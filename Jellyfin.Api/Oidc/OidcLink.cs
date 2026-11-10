using System;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// A link between an OpenID Connect identity and a Jellyfin user.
/// </summary>
public sealed class OidcLink
{
    /// <summary>
    /// Gets or sets the issuer (<c>iss</c>).
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subject (<c>sub</c>).
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets when the link was created.
    /// </summary>
    public DateTime Created { get; set; }
}
