using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// The outcome of resolving a Jellyfin user.
/// </summary>
/// <param name="User">The user, when successful.</param>
/// <param name="Error">The error, when not.</param>
public sealed record OidcUserResult(User? User, OidcLoginError? Error)
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The result.</returns>
    public static OidcUserResult Success(User user) => new(user, null);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns>The result.</returns>
    public static OidcUserResult Fail(OidcLoginError error) => new(null, error);
}
