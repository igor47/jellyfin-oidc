using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Maps a validated OpenID Connect identity to a Jellyfin user: group checks, linking, creation,
/// administrator sync and the same account checks as password login.
/// </summary>
public sealed partial class OidcUserService : IDisposable
{
    /// <summary>
    /// The longest username accepted from the provider.
    /// </summary>
    public const int MaxUsernameLength = 128;

    private readonly OidcOptions _options;
    private readonly OidcLinkStore _links;
    private readonly IUserManager _userManager;
    private readonly INetworkManager _networkManager;
    private readonly ILogger<OidcUserService> _logger;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcUserService"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="links">The link store.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="networkManager">The network manager.</param>
    /// <param name="logger">The logger.</param>
    public OidcUserService(
        OidcOptions options,
        OidcLinkStore links,
        IUserManager userManager,
        INetworkManager networkManager,
        ILogger<OidcUserService> logger)
    {
        _options = options;
        _links = links;
        _userManager = userManager;
        _networkManager = networkManager;
        _logger = logger;
    }

    /// <summary>
    /// Turns a claim value into a valid Jellyfin username. Characters Jellyfin does not allow in
    /// usernames are replaced with <c>_</c> and surrounding whitespace is removed.
    /// </summary>
    /// <param name="claim">The claim value.</param>
    /// <returns>The username, or <c>null</c> if no valid username can be derived.</returns>
    public static string? SanitizeUsername(string? claim)
    {
        if (string.IsNullOrWhiteSpace(claim))
        {
            return null;
        }

        var name = InvalidUsernameCharsRegex().Replace(claim.Normalize(System.Text.NormalizationForm.FormC), "_").Trim();
        if (name.Length == 0
            || name.Length > MaxUsernameLength
            || name.All(c => c == '_' || c == '.')
            || !ValidUsernameRegex().IsMatch(name))
        {
            return null;
        }

        return name;
    }

    /// <summary>
    /// Returns whether the identity may log in at all.
    /// </summary>
    /// <param name="identity">The identity.</param>
    /// <returns><c>true</c> if allowed.</returns>
    public bool IsAllowed(OidcIdentity identity)
        => _options.AllowedGroups.Count == 0 || identity.Groups.Any(g => _options.AllowedGroups.Contains(g, StringComparer.Ordinal));

    /// <summary>
    /// Returns whether the identity should be an administrator, or <c>null</c> if administrator status is not managed.
    /// </summary>
    /// <param name="identity">The identity.</param>
    /// <returns>The desired administrator status.</returns>
    public bool? ShouldBeAdmin(OidcIdentity identity)
        => _options.AdminGroups.Count == 0 ? null : identity.Groups.Any(g => _options.AdminGroups.Contains(g, StringComparer.Ordinal));

    /// <summary>
    /// Resolves (and if needed links or creates) the Jellyfin user for an identity and checks that it may log in.
    /// </summary>
    /// <param name="identity">The validated identity.</param>
    /// <param name="remoteEndPoint">The client IP address.</param>
    /// <returns>The result.</returns>
    public async Task<OidcUserResult> ResolveAsync(OidcIdentity identity, string remoteEndPoint)
    {
        if (!IsAllowed(identity))
        {
            _logger.LogWarning("OIDC login denied for subject {Subject}: not in an allowed group", identity.Subject);
            return OidcUserResult.Fail(OidcLoginError.NotInAllowedGroup);
        }

        User? user;
        await _resolveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await FindOrCreateAsync(identity).ConfigureAwait(false);
            if (result.User is null)
            {
                return result;
            }

            user = await SyncAdminAsync(result.User, identity).ConfigureAwait(false);
        }
        finally
        {
            _resolveLock.Release();
        }

        // The same account checks UserManager.AuthenticateUser applies to password logins.
        if (user.HasPermission(PermissionKind.IsDisabled))
        {
            _logger.LogWarning("OIDC login denied for {UserName}: account disabled", user.Username);
            return OidcUserResult.Fail(OidcLoginError.AccountDisabled);
        }

        if (!user.HasPermission(PermissionKind.EnableRemoteAccess) && !_networkManager.IsInLocalNetwork(remoteEndPoint))
        {
            _logger.LogWarning("OIDC login denied for {UserName}: remote access disabled (IP: {IP})", user.Username, remoteEndPoint);
            return OidcUserResult.Fail(OidcLoginError.RemoteAccessDenied);
        }

        if (!user.IsParentalScheduleAllowed())
        {
            _logger.LogWarning("OIDC login denied for {UserName}: outside the allowed schedule", user.Username);
            return OidcUserResult.Fail(OidcLoginError.ScheduleDenied);
        }

        // ISessionManager.AuthenticateDirect does not record the login; password logins do.
        user.LastLoginDate = DateTime.UtcNow;
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        return OidcUserResult.Success(_userManager.GetUserById(user.Id) ?? user);
    }

    /// <inheritdoc />
    public void Dispose() => _resolveLock.Dispose();

    private async Task<OidcUserResult> FindOrCreateAsync(OidcIdentity identity)
    {
        var linkedId = await _links.FindUserAsync(identity.Issuer, identity.Subject).ConfigureAwait(false);
        if (linkedId is Guid id)
        {
            var linked = _userManager.GetUserById(id);
            if (linked is not null)
            {
                return OidcUserResult.Success(linked);
            }

            // The Jellyfin user was deleted: forget the link and treat this as a first login.
            _logger.LogInformation("OIDC link for subject {Subject} points to deleted user {UserId}; removing it", identity.Subject, id);
            await _links.UnlinkAsync(identity.Issuer, identity.Subject).ConfigureAwait(false);
        }

        var username = SanitizeUsername(identity.Username);
        if (username is null)
        {
            _logger.LogWarning(
                "OIDC login denied for subject {Subject}: claim {Claim} is missing or not a usable username",
                identity.Subject,
                _options.UsernameClaim);
            return OidcUserResult.Fail(OidcLoginError.InvalidUsername);
        }

        var existing = _userManager.GetUserByName(username);
        if (existing is not null)
        {
            if (!_options.LinkExistingByUsername)
            {
                _logger.LogWarning(
                    "OIDC login denied for subject {Subject}: user {UserName} already exists and linking existing users is disabled",
                    identity.Subject,
                    existing.Username);
                return OidcUserResult.Fail(OidcLoginError.UsernameTaken);
            }

            if (await _links.IsUserLinkedAsync(existing.Id).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "OIDC login denied for subject {Subject}: user {UserName} is already linked to another identity",
                    identity.Subject,
                    existing.Username);
                return OidcUserResult.Fail(OidcLoginError.UsernameTaken);
            }

            // Switch the provider before persisting the link, so a failure can never leave a linked user with password login.
            existing = await AssignOidcProviderAsync(existing).ConfigureAwait(false);
            await _links.LinkAsync(identity.Issuer, identity.Subject, existing.Id).ConfigureAwait(false);
            _logger.LogInformation("OIDC subject {Subject} linked to existing user {UserName}", identity.Subject, existing.Username);
            return OidcUserResult.Success(existing);
        }

        if (!_options.AutoCreateUsers)
        {
            _logger.LogWarning("OIDC login denied for subject {Subject}: no linked user and automatic user creation is disabled", identity.Subject);
            return OidcUserResult.Fail(OidcLoginError.NoAccount);
        }

        // Default policy, same as a user created in the dashboard (all libraries, hidden from the login screen).
        var created = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
        try
        {
            // A new user has no password; switch it to the OIDC provider right away so it never accepts an empty one.
            created = await AssignOidcProviderAsync(created).ConfigureAwait(false);
            await _links.LinkAsync(identity.Issuer, identity.Subject, created.Id).ConfigureAwait(false);
        }
        catch
        {
            await _userManager.DeleteUserAsync(created.Id).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation("OIDC subject {Subject} created user {UserName}", identity.Subject, created.Username);
        return OidcUserResult.Success(created);
    }

    // Only done when linking: if an administrator later sets a password, the user moves back to the
    // default provider (see OidcAuthenticationProvider.ChangePassword) and that must stick.
    private async Task<User> AssignOidcProviderAsync(User user)
    {
        if (string.Equals(user.AuthenticationProviderId, OidcAuthenticationProvider.ProviderId, StringComparison.Ordinal))
        {
            return user;
        }

        user.AuthenticationProviderId = OidcAuthenticationProvider.ProviderId;
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        return _userManager.GetUserById(user.Id) ?? user;
    }

    private async Task<User> SyncAdminAsync(User user, OidcIdentity identity)
    {
        var changed = false;
        var shouldBeAdmin = ShouldBeAdmin(identity);
        if (shouldBeAdmin is bool admin && admin != user.HasPermission(PermissionKind.IsAdministrator))
        {
            if (!admin && _userManager.GetUsers().Count(u => u.HasPermission(PermissionKind.IsAdministrator)) <= 1)
            {
                _logger.LogWarning(
                    "Not removing administrator status from {UserName}: it is the only administrator",
                    user.Username);
            }
            else
            {
                user.SetPermission(PermissionKind.IsAdministrator, admin);
                changed = true;
                _logger.LogInformation("OIDC set administrator status of {UserName} to {IsAdmin}", user.Username, admin);
            }
        }

        if (changed)
        {
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            user = _userManager.GetUserById(user.Id) ?? user;
        }

        return user;
    }

    [GeneratedRegex(@"[^\w\ \-'._@+]")]
    private static partial Regex InvalidUsernameCharsRegex();

    // Same as UserManager.ValidUsernameRegex.
    [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")]
    private static partial Regex ValidUsernameRegex();
}
