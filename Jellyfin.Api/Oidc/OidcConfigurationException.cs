using System;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Thrown at startup when the <c>JELLYFIN_OIDC_*</c> configuration is invalid.
/// </summary>
public class OidcConfigurationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OidcConfigurationException"/> class.
    /// </summary>
    public OidcConfigurationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public OidcConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public OidcConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
