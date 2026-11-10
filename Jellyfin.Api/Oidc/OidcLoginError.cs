namespace Jellyfin.Api.Oidc;

/// <summary>
/// Why an OpenID Connect login was refused.
/// </summary>
public enum OidcLoginError
{
    /// <summary>The login request expired, was already used, or came from another browser.</summary>
    InvalidState,

    /// <summary>The provider returned an error or the token could not be obtained or validated.</summary>
    ProviderError,

    /// <summary>The user is not in any of the allowed groups.</summary>
    NotInAllowedGroup,

    /// <summary>The username claim is missing or unusable.</summary>
    InvalidUsername,

    /// <summary>A Jellyfin user with that name exists and may not be linked.</summary>
    UsernameTaken,

    /// <summary>No linked user and automatic creation is disabled.</summary>
    NoAccount,

    /// <summary>The Jellyfin account is disabled.</summary>
    AccountDisabled,

    /// <summary>Remote access is disabled for the account.</summary>
    RemoteAccessDenied,

    /// <summary>The parental schedule does not allow access now.</summary>
    ScheduleDenied,

    /// <summary>The session could not be created (device or session limits).</summary>
    SessionDenied
}
