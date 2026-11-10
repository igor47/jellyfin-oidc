using System;
using Jellyfin.Api.Oidc;
using MediaBrowser.Model.Branding;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public class OidcBrandingTests
{
    private static readonly OidcOptions _enabled = new() { Enabled = true, ProviderName = "Authentik" };
    private static readonly OidcOptions _enabledNoPassword = new() { Enabled = true, ProviderName = "Authentik", DisablePasswordLogin = true };
    private static readonly OidcOptions _disabled = new();

    [Fact]
    public void Disclaimer_Enabled_PrependsSameTabButton()
    {
        var html = OidcBranding.AugmentDisclaimer("Operator text", _enabled, "/jf/oidc/login")!;

        Assert.StartsWith(OidcBranding.HtmlBegin, html, StringComparison.Ordinal);
        Assert.Contains("<form class=\"oidcLoginForm\" action=\"/jf/oidc/login\" method=\"get\">", html, StringComparison.Ordinal);
        Assert.Contains("class=\"raised block emby-button button-submit oidcLoginButton\"", html, StringComparison.Ordinal);
        Assert.Contains("Sign in with Authentik", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
        Assert.EndsWith("Operator text", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Disclaimer_ProviderNameAndPathAreEncoded()
    {
        var options = new OidcOptions { Enabled = true, ProviderName = "<img src=x onerror=alert(1)>" };
        var html = OidcBranding.AugmentDisclaimer(null, options, "/a\"b/oidc/login")!;

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/a&quot;b/oidc/login\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Operator text")]
    public void Disclaimer_Disabled_IsUnchanged(string? stored)
        => Assert.Equal(stored, OidcBranding.AugmentDisclaimer(stored, _disabled, "/oidc/login"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Operator text\n\nwith **markdown**")]
    public void Disclaimer_RoundTripsThroughStrip(string? stored)
    {
        var served = OidcBranding.AugmentDisclaimer(stored, _enabled, "/oidc/login");
        var saved = OidcBranding.Strip(served, OidcBranding.HtmlBegin, OidcBranding.HtmlEnd);
        Assert.Equal(stored ?? string.Empty, saved ?? string.Empty);

        // Serving branding that was saved with the additions does not duplicate them.
        Assert.Equal(served, OidcBranding.AugmentDisclaimer(served, _enabled, "/oidc/login"));
    }

    [Fact]
    public void Css_PasswordDisabled_HidesFormsOnLoginPageOnly()
    {
        var css = OidcBranding.AugmentCss(".x{}", _enabledNoPassword)!;

        Assert.StartsWith(".x{}\n" + OidcBranding.CssBegin, css, StringComparison.Ordinal);
        Assert.Contains("#loginPage .manualLoginForm", css, StringComparison.Ordinal);
        Assert.Contains("#loginPage .visualLoginForm", css, StringComparison.Ordinal);
        Assert.Contains("#loginPage .btnForgotPassword", css, StringComparison.Ordinal);
        Assert.Contains("#loginPage .btnManual", css, StringComparison.Ordinal);
        Assert.DoesNotContain("btnQuick", css, StringComparison.Ordinal);
        Assert.EndsWith(OidcBranding.CssEnd, css, StringComparison.Ordinal);
    }

    [Fact]
    public void Css_PasswordEnabled_DoesNotHideForms()
    {
        var css = OidcBranding.AugmentCss(null, _enabled)!;
        Assert.DoesNotContain("manualLoginForm", css, StringComparison.Ordinal);
        Assert.Contains("oidcLoginForm", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Css_PasswordDisabledWithoutOidc_StillHidesForms()
    {
        var css = OidcBranding.AugmentCss(null, new OidcOptions { DisablePasswordLogin = true })!;
        Assert.Contains("manualLoginForm", css, StringComparison.Ordinal);
        Assert.DoesNotContain("oidcLoginForm", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".x{}")]
    [InlineData(".x{}\n")]
    public void Css_RoundTripsThroughStrip(string? stored)
    {
        var served = OidcBranding.AugmentCss(stored, _enabledNoPassword);
        Assert.Equal(stored ?? string.Empty, OidcBranding.Strip(served, OidcBranding.CssBegin, OidcBranding.CssEnd) ?? string.Empty);
        Assert.Equal(served, OidcBranding.AugmentCss(served, _enabledNoPassword));
        Assert.Equal(stored, OidcBranding.AugmentCss(stored, _disabled));
    }

    [Fact]
    public void Strip_DanglingMarker_KeepsOperatorText()
        => Assert.Equal("before after", OidcBranding.Strip("before " + OidcBranding.HtmlBegin + "after", OidcBranding.HtmlBegin, OidcBranding.HtmlEnd));

    [Fact]
    public void Clean_RemovesAdditionsFromSavedBranding()
    {
        var options = new BrandingOptions
        {
            LoginDisclaimer = OidcBranding.AugmentDisclaimer("hello", _enabled, "/oidc/login"),
            CustomCss = OidcBranding.AugmentCss(".x{}", _enabledNoPassword),
            SplashscreenEnabled = true
        };

        OidcHostedService.Clean(options);

        Assert.Equal("hello", options.LoginDisclaimer);
        Assert.Equal(".x{}", options.CustomCss);
        Assert.True(options.SplashscreenEnabled);
    }
}
