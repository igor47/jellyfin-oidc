using System;
using System.Net;
using System.Text;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Adds the login button and the password-form hiding CSS to the branding returned to clients.
/// The injected text is wrapped in markers so it can always be recognised and removed again
/// (for example when the dashboard saves branding it previously loaded with the additions).
/// </summary>
public static class OidcBranding
{
    /// <summary>Start marker in the login disclaimer (an HTML comment).</summary>
    public const string HtmlBegin = "<!-- jellyfin-oidc:begin -->";

    /// <summary>End marker in the login disclaimer.</summary>
    public const string HtmlEnd = "<!-- jellyfin-oidc:end -->";

    /// <summary>Start marker in the custom CSS.</summary>
    public const string CssBegin = "/* jellyfin-oidc:begin */";

    /// <summary>End marker in the custom CSS.</summary>
    public const string CssEnd = "/* jellyfin-oidc:end */";

    /// <summary>
    /// Returns the login disclaimer with the "Sign in with ..." button prepended when OIDC is enabled.
    /// </summary>
    /// <param name="stored">The operator's disclaimer.</param>
    /// <param name="options">The options.</param>
    /// <param name="loginPath">The absolute path of <c>/oidc/login</c>, including Jellyfin's base URL.</param>
    /// <returns>The disclaimer to serve.</returns>
    public static string? AugmentDisclaimer(string? stored, OidcOptions options, string loginPath)
    {
        var clean = Strip(stored, HtmlBegin, HtmlEnd);
        if (!options.Enabled)
        {
            return clean;
        }

        // The stock login page renders the disclaimer with markdown-it (html enabled) and DOMPurify, and turns
        // every <a> into a new-tab link; a GET form survives sanitising and navigates in the same tab.
        // The blank lines make markdown-it treat the comment and the form as separate raw HTML blocks.
        var button = new StringBuilder()
            .Append(HtmlBegin).Append("\n\n")
            .Append("<form class=\"oidcLoginForm\" action=\"").Append(WebUtility.HtmlEncode(loginPath)).Append("\" method=\"get\">")
            .Append("<button type=\"submit\" class=\"raised block emby-button button-submit oidcLoginButton\">")
            .Append("Sign in with ").Append(WebUtility.HtmlEncode(options.ProviderName))
            .Append("</button></form>\n\n")
            .Append(HtmlEnd).Append("\n\n");

        return button.Append(clean).ToString();
    }

    /// <summary>
    /// Returns the custom CSS with the fork's rules appended.
    /// </summary>
    /// <param name="stored">The operator's CSS.</param>
    /// <param name="options">The options.</param>
    /// <returns>The CSS to serve.</returns>
    public static string? AugmentCss(string? stored, OidcOptions options)
    {
        var clean = Strip(stored, CssBegin, CssEnd);
        if (!options.Enabled && !options.DisablePasswordLogin)
        {
            return clean;
        }

        var css = new StringBuilder(clean ?? string.Empty);
        if (css.Length > 0)
        {
            css.Append('\n');
        }

        css.Append(CssBegin).Append('\n');
        if (options.Enabled)
        {
            // Put the provider button first on the login page.
            css.Append("#loginPage .readOnlyContent { display: flex; flex-direction: column; }\n")
                .Append("#loginPage .loginDisclaimerContainer { order: -1; margin-top: 0; margin-bottom: 1em; }\n")
                .Append("#loginPage .loginDisclaimer { width: 100%; }\n")
                .Append("#loginPage .oidcLoginForm { margin: 0; }\n");
        }

        if (options.DisablePasswordLogin)
        {
            css.Append("#loginPage .manualLoginForm, #loginPage .visualLoginForm, #loginPage .btnManual, #loginPage .btnForgotPassword { display: none !important; }\n");
        }

        css.Append(CssEnd);
        return css.ToString();
    }

    /// <summary>
    /// Removes every marked block (markers included) from a string.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="begin">The start marker.</param>
    /// <param name="end">The end marker.</param>
    /// <returns>The value without injected blocks.</returns>
    public static string? Strip(string? value, string begin, string end)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var start = value.IndexOf(begin, StringComparison.Ordinal);
        while (start >= 0)
        {
            var stop = value.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0)
            {
                // A dangling start marker: drop it alone, keep the operator's text.
                value = value.Remove(start, begin.Length);
            }
            else
            {
                stop += end.Length;
                while (stop < value.Length && (value[stop] == '\n' || value[stop] == '\r'))
                {
                    stop++;
                }

                // Also drop the newline AugmentCss puts in front of the block.
                if (start > 0 && value[start - 1] == '\n')
                {
                    start--;
                }

                value = value.Remove(start, stop - start);
            }

            start = value.IndexOf(begin, StringComparison.Ordinal);
        }

        return value;
    }
}
