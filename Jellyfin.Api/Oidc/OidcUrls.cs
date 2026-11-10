using System;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Computes the externally visible URLs of the OIDC endpoints.
/// </summary>
public static class OidcUrls
{
    /// <summary>
    /// Returns the external base URL (scheme, host, port and Jellyfin base path; no trailing slash):
    /// <c>JELLYFIN_OIDC_REDIRECT_BASE_URL</c>, else Jellyfin's published server URL, else the request's
    /// scheme and host plus Jellyfin's base URL.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="publishedServerUrl">Jellyfin's published server URL setting, if any.</param>
    /// <param name="request">The request.</param>
    /// <returns>The base URL.</returns>
    public static string BaseUrl(OidcOptions options, string? publishedServerUrl, HttpRequest request)
    {
        if (!string.IsNullOrEmpty(options.RedirectBaseUrl))
        {
            return options.RedirectBaseUrl;
        }

        if (!string.IsNullOrWhiteSpace(publishedServerUrl)
            && Uri.TryCreate(publishedServerUrl.Trim(), UriKind.Absolute, out var published)
            && (published.Scheme == Uri.UriSchemeHttps || published.Scheme == Uri.UriSchemeHttp))
        {
            return publishedServerUrl.Trim().TrimEnd('/');
        }

        return request.Scheme + "://" + request.Host.ToUriComponent() + request.PathBase.ToUriComponent();
    }

    /// <summary>
    /// Returns the redirect URI registered at the provider.
    /// </summary>
    /// <param name="baseUrl">The base URL from <see cref="BaseUrl"/>.</param>
    /// <returns>The redirect URI.</returns>
    public static string CallbackUrl(string baseUrl) => baseUrl + "/oidc/callback";

    /// <summary>
    /// Returns the path the login button submits to. It is a path (not a URL) so the button works from
    /// whichever address the browser uses; with <c>REDIRECT_BASE_URL</c> set, that URL's path is used.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="request">The request.</param>
    /// <returns>The path.</returns>
    public static string LoginPath(OidcOptions options, HttpRequest request)
    {
        var basePath = !string.IsNullOrEmpty(options.RedirectBaseUrl)
            ? new Uri(options.RedirectBaseUrl).AbsolutePath.TrimEnd('/')
            : request.PathBase.ToUriComponent();
        return basePath + "/oidc/login";
    }
}
