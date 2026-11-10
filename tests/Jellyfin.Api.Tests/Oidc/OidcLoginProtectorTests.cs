using System;
using System.Linq;
using Jellyfin.Api.Oidc;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public sealed class OidcLoginProtectorTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly OidcLoginProtector _protector;

    public OidcLoginProtectorTests()
    {
        _protector = new OidcLoginProtector(new OidcOptions { Enabled = true, ExternalBaseUrl = "https://media.example.org" }, _time);
    }

    public void Dispose() => _protector.Dispose();

    [Fact]
    public void RoundTrip_WithMatchingState()
    {
        var login = _protector.Create();
        var cookie = _protector.Protect(login);

        Assert.Equal(login, _protector.Unprotect(cookie, login.State));
        Assert.DoesNotContain(login.CodeVerifier, cookie, StringComparison.Ordinal);
        Assert.DoesNotContain(login.Nonce, cookie, StringComparison.Ordinal);
        Assert.DoesNotContain(login.State, cookie, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_GeneratesDistinctRandomValues()
    {
        var login = _protector.Create();
        var values = new[] { login.State, login.Nonce, login.CodeVerifier };
        Assert.Equal(3, values.Distinct().Count());
        Assert.All(values, v => Assert.Equal(43, v.Length));
        Assert.Equal(_time.GetUtcNow() + OidcLoginProtector.Lifetime, login.Expires);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other-state")]
    public void StateMismatch_Fails(string? state)
    {
        var login = _protector.Create();
        Assert.Null(_protector.Unprotect(_protector.Protect(login), state));
    }

    [Fact]
    public void CookieFromAnotherLogin_Fails()
    {
        // Binding: the state returned to this browser must be the one in this browser's cookie.
        var mine = _protector.Create();
        var theirs = _protector.Create();
        Assert.Null(_protector.Unprotect(_protector.Protect(theirs), mine.State));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("AAAA")]
    public void GarbageCookie_Fails(string? cookie)
        => Assert.Null(_protector.Unprotect(cookie, "state"));

    [Fact]
    public void TamperedCookie_Fails()
    {
        var login = _protector.Create();
        var cookie = _protector.Protect(login).ToCharArray();
        var i = cookie.Length / 2;
        cookie[i] = cookie[i] == 'A' ? 'B' : 'A';
        Assert.Null(_protector.Unprotect(new string(cookie), login.State));
    }

    [Fact]
    public void CookieFromAnotherProcessOrInstance_Fails()
    {
        var login = _protector.Create();
        using var other = new OidcLoginProtector(new OidcOptions { Enabled = true, ExternalBaseUrl = "https://media.example.org" }, _time);
        Assert.Null(other.Unprotect(_protector.Protect(login), login.State));
    }

    [Fact]
    public void Expired_Fails()
    {
        var login = _protector.Create();
        var cookie = _protector.Protect(login);
        _time.Advance(OidcLoginProtector.Lifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(_protector.Unprotect(cookie, login.State));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(_protector.Unprotect(cookie, login.State));
    }

    [Fact]
    public void Https_UsesHostPrefixedCookie()
    {
        Assert.StartsWith("__Host-jellyfin_oidc_", _protector.CookieName, StringComparison.Ordinal);
        var options = _protector.CookieOptions(OidcLoginProtector.Lifetime);
        Assert.True(options.Secure);
        Assert.True(options.HttpOnly);
        Assert.Equal("/", options.Path);
        Assert.Null(options.Domain);
        Assert.Equal(SameSiteMode.Lax, options.SameSite);
        Assert.Equal(TimeSpan.FromMinutes(10), options.MaxAge);
    }

    [Fact]
    public void Http_UsesPlainCookieScopedToBasePath()
    {
        using var protector = new OidcLoginProtector(new OidcOptions { Enabled = true, ExternalBaseUrl = "http://jf.lan:8096/jf" }, _time);
        Assert.StartsWith("jellyfin_oidc_", protector.CookieName, StringComparison.Ordinal);
        var options = protector.CookieOptions(null);
        Assert.False(options.Secure);
        Assert.Equal("/jf/oidc", options.Path);
    }

    [Fact]
    public void CookieName_IsUniquePerBasePath()
    {
        using var a = new OidcLoginProtector(new OidcOptions { ExternalBaseUrl = "https://media.example.org/a" }, _time);
        using var b = new OidcLoginProtector(new OidcOptions { ExternalBaseUrl = "https://media.example.org/b" }, _time);
        Assert.NotEqual(a.CookieName, b.CookieName);
        Assert.NotEqual(a.CookieName, _protector.CookieName);
    }

    [Fact]
    public void CodeChallenge_MatchesRfc7636Example()
    {
        // RFC 7636 appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OidcLoginProtector.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }
}
