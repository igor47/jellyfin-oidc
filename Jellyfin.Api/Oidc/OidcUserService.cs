using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
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
    private readonly ISessionManager _sessionManager;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly INetworkManager _networkManager;
    private readonly ILogger<OidcUserService> _logger;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _creating = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcUserService"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="links">The link store.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="cryptoProvider">The crypto provider.</param>
    /// <param name="networkManager">The network manager.</param>
    /// <param name="logger">The logger.</param>
    public OidcUserService(
        OidcOptions options,
        OidcLinkStore links,
        IUserManager userManager,
        ISessionManager sessionManager,
        ICryptoProvider cryptoProvider,
        INetworkManager networkManager,
        ILogger<OidcUserService> logger)
    {
        _options = options;
        _links = links;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _cryptoProvider = cryptoProvider;
        _networkManager = networkManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether a user is being created right now.
    /// </summary>
    public bool IsAnyUserBeingCreated => !_creating.IsEmpty;

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
    /// Returns a random password with 256 bits of entropy. Nobody ever learns it; it only makes sure an account
    /// never has an empty password, whatever authentication provider it ends up with.
    /// </summary>
    /// <returns>The password.</returns>
    public static string RandomPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Returns whether a user with this name is being created by an OIDC login right now. Jellyfin's
    /// <c>UserManager.CreateUserAsync</c> creates users without a password, so password login must be refused for
    /// that name until the random password is set.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <returns><c>true</c> if it is.</returns>
    public bool IsBeingCreated(string? username)
        => !string.IsNullOrWhiteSpace(username) && _creating.ContainsKey(NormalizeName(username));

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
        User user;
        await _resolveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsAllowed(identity))
            {
                _logger.LogWarning("OIDC login denied for subject {Subject}: not in an allowed group", identity.Subject);
                await RevokeLinkedUserAsync(identity).ConfigureAwait(false);
                return OidcUserResult.Fail(OidcLoginError.NotInAllowedGroup);
            }

            var result = await FindOrCreateAsync(identity).ConfigureAwait(false);
            if (result.User is null)
            {
                return result;
            }

            user = await SyncAdminAsync(result.User.Id, identity).ConfigureAwait(false);
        }
        finally
        {
            _resolveLock.Release();
        }

        // The same account checks UserManager.AuthenticateUser applies to password logins.
        if (user.HasPermission(PermissionKind.IsDisabled))
        {
            _logger.LogWarning("OIDC login denied for {UserName}: account disabled; revoking its sessions", user.Username);
            await _sessionManager.RevokeUserTokens(user.Id, null!).ConfigureAwait(false);
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

        return OidcUserResult.Success(user);
    }

    /// <inheritdoc />
    public void Dispose() => _resolveLock.Dispose();

    // The same normalisation Jellyfin uses for username uniqueness and lookup (User.NormalizedUsername,
    // UserManager.GetUserByName/CreateUserAsync): ToUpperInvariant. Matching with it can never find a user
    // Jellyfin itself would consider a different name.
    private static string NormalizeName(string username) => username.ToUpperInvariant();

    private async Task RevokeLinkedUserAsync(OidcIdentity identity)
    {
        if (await _links.FindUserAsync(identity.Issuer, identity.Subject).ConfigureAwait(false) is Guid id
            && _userManager.GetUserById(id) is not null)
        {
            _logger.LogWarning("Revoking all sessions of user {UserId}: its identity is no longer allowed to sign in", id);
            await _sessionManager.RevokeUserTokens(id, null!).ConfigureAwait(false);
        }
    }

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

            // Never silently re-create: the operator deleted this account, possibly on purpose.
            _logger.LogError(
                "OIDC login refused for subject {Subject} (issuer {Issuer}): it is linked to Jellyfin user {UserId}, which no longer exists. "
                + "Remove that entry from {LinkFile} to allow the account to be re-created",
                identity.Subject,
                identity.Issuer,
                id,
                _links.FilePath);
            return OidcUserResult.Fail(OidcLoginError.LinkedUserMissing);
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

        // GetUserByName compares NormalizedUsername with ToUpperInvariant, Jellyfin's own uniqueness rule.
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

            // Adoption: whoever held this account before (possibly someone who renamed their account to squat the
            // name) must keep nothing. Lock it to OIDC with a new random password, then revoke every existing token,
            // all before the link is persisted and before any administrator status is granted.
            var adopted = await LockToOidcAsync(existing.Id).ConfigureAwait(false);
            await _sessionManager.RevokeUserTokens(adopted.Id, null!).ConfigureAwait(false);
            await _links.LinkAsync(identity.Issuer, identity.Subject, adopted.Id).ConfigureAwait(false);
            _logger.LogInformation(
                "OIDC subject {Subject} linked to existing user {UserName}; its password was replaced and its sessions revoked",
                identity.Subject,
                adopted.Username);
            return OidcUserResult.Success(adopted);
        }

        if (!_options.AutoCreateUsers)
        {
            _logger.LogWarning("OIDC login denied for subject {Subject}: no linked user and automatic user creation is disabled", identity.Subject);
            return OidcUserResult.Fail(OidcLoginError.NoAccount);
        }

        var key = NormalizeName(username);
        _creating[key] = 0;
        try
        {
            // Default policy, same as a user created in the dashboard (all libraries, hidden from the login screen).
            var created = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
            try
            {
                // CreateUserAsync leaves the password empty: set a random one right away through the upstream path
                // (the new user still has the default provider), then switch to the OIDC provider.
                await _userManager.ChangePassword(created.Id, RandomPassword()).ConfigureAwait(false);
                created = await LockToOidcAsync(created.Id).ConfigureAwait(false);
                await _links.LinkAsync(identity.Issuer, identity.Subject, created.Id).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await _userManager.DeleteUserAsync(created.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not remove partially created user {UserName}", username);
                }

                throw;
            }

            _logger.LogInformation("OIDC subject {Subject} created user {UserName}", identity.Subject, created.Username);
            return OidcUserResult.Success(created);
        }
        finally
        {
            _creating.TryRemove(key, out _);
        }
    }

    // Sets a random password hash and the OIDC provider in one update, on a freshly loaded copy of the user.
    private async Task<User> LockToOidcAsync(Guid userId)
    {
        var user = _userManager.GetUserById(userId) ?? throw new InvalidOperationException("User disappeared while linking.");
        user.Password = _cryptoProvider.CreatePasswordHash(RandomPassword()).ToString();
        user.AuthenticationProviderId = OidcAuthenticationProvider.ProviderId;
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        return _userManager.GetUserById(userId) ?? user;
    }

    private async Task<User> SyncAdminAsync(Guid userId, OidcIdentity identity)
    {
        // Load right before writing, so the write cannot revert a concurrent change made in the dashboard.
        var user = _userManager.GetUserById(userId) ?? throw new InvalidOperationException("User disappeared while signing in.");
        var shouldBeAdmin = ShouldBeAdmin(identity);
        if (shouldBeAdmin is not bool admin || admin == user.HasPermission(PermissionKind.IsAdministrator))
        {
            return user;
        }

        if (!admin && _userManager.GetUsers().Count(u => u.HasPermission(PermissionKind.IsAdministrator)) <= 1)
        {
            _logger.LogWarning("Not removing administrator status from {UserName}: it is the only administrator", user.Username);
            return user;
        }

        user.SetPermission(PermissionKind.IsAdministrator, admin);
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        _logger.LogInformation("OIDC set administrator status of {UserName} to {IsAdmin}", user.Username, admin);
        return _userManager.GetUserById(userId) ?? user;
    }

    [GeneratedRegex(@"[^\w\ \-'._@+]")]
    private static partial Regex InvalidUsernameCharsRegex();

    // Same as UserManager.ValidUsernameRegex.
    [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")]
    private static partial Regex ValidUsernameRegex();
}
