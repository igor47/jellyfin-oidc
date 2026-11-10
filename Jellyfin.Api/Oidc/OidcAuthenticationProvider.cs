using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.Cryptography;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Authentication provider assigned to users created or linked by OpenID Connect.
/// Password authentication always fails for these users. When an administrator sets a
/// (non-empty) password, the user is handed back to Jellyfin's default provider so the
/// new password works.
/// </summary>
public class OidcAuthenticationProvider : IAuthenticationProvider, IRequiresResolvedUser
{
    /// <summary>
    /// The full type name of Jellyfin's default authentication provider (in Jellyfin.Server.Implementations,
    /// which this assembly does not reference).
    /// </summary>
    public const string DefaultProviderId = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

    private readonly ICryptoProvider _cryptoProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcAuthenticationProvider"/> class.
    /// </summary>
    /// <param name="cryptoProvider">The crypto provider.</param>
    public OidcAuthenticationProvider(ICryptoProvider cryptoProvider)
    {
        _cryptoProvider = cryptoProvider;
    }

    /// <summary>
    /// Gets the provider id stored in <see cref="User.AuthenticationProviderId"/>.
    /// </summary>
    public static string ProviderId => typeof(OidcAuthenticationProvider).FullName!;

    /// <inheritdoc />
    public string Name => "OpenID Connect";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        => throw new AuthenticationException("This account signs in with OpenID Connect.");

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
        => throw new AuthenticationException("This account signs in with OpenID Connect.");

    /// <inheritdoc />
    public Task ChangePassword(User user, string newPassword)
    {
        // An empty password would make the account password-less (anyone could log in by name if the provider
        // ever went back to the default one), so a "reset" stores a random password and keeps the account OIDC-only.
        if (string.IsNullOrEmpty(newPassword))
        {
            user.Password = _cryptoProvider.CreatePasswordHash(OidcUserService.RandomPassword()).ToString();
            return Task.CompletedTask;
        }

        user.Password = _cryptoProvider.CreatePasswordHash(newPassword).ToString();
        user.AuthenticationProviderId = DefaultProviderId;
        return Task.CompletedTask;
    }
}
