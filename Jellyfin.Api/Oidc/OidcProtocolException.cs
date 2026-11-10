using System;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Thrown when the OpenID Connect exchange with the provider fails or returns something invalid.
/// </summary>
public class OidcProtocolException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OidcProtocolException"/> class.
    /// </summary>
    public OidcProtocolException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcProtocolException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public OidcProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcProtocolException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public OidcProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
