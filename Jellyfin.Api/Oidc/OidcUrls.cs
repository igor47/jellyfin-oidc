using System;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// The externally visible URLs of the OIDC endpoints, all derived from the configured
/// <see cref="OidcOptions.ExternalBaseUrl"/> and never from request headers.
/// </summary>
public static class OidcUrls
{
    /// <summary>
    /// Returns the redirect URI registered at the provider.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <returns>The redirect URI.</returns>
    public static string CallbackUrl(OidcOptions options) => options.ExternalBaseUrl + "/oidc/callback";

    /// <summary>
    /// Returns the path the login button submits to (a path, so the button works from whichever host the browser uses).
    /// </summary>
    /// <param name="options">The options.</param>
    /// <returns>The path.</returns>
    public static string LoginPath(OidcOptions options) => BasePath(options) + "/oidc/login";

    /// <summary>
    /// Returns the path of the web client.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <returns>The path.</returns>
    public static string WebPath(OidcOptions options) => BasePath(options) + "/web/";

    private static string BasePath(OidcOptions options)
        => string.IsNullOrEmpty(options.ExternalBaseUrl) ? string.Empty : new Uri(options.ExternalBaseUrl).AbsolutePath.TrimEnd('/');
}
