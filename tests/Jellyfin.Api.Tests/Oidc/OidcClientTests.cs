using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Oidc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public sealed class OidcClientTests : IDisposable
{
    private const string Issuer = "https://idp.example.org/app/";
    private const string ClientId = "jellyfin";
    private const string Nonce = "the-nonce";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _otherRsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly OpenIdConnectConfiguration _config;
    private readonly OidcClient _client;

    public OidcClientTests()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = "k1" };
        _config = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            AuthorizationEndpoint = "https://idp.example.org/authorize?tenant=x",
            TokenEndpoint = "https://idp.example.org/token"
        };
        _config.SigningKeys.Add(_key);
        _config.IdTokenSigningAlgValuesSupported.Add(SecurityAlgorithms.RsaSha256);

        var options = new OidcOptions
        {
            Enabled = true,
            Issuer = Issuer,
            ClientId = ClientId,
            ClientSecret = "a-client-secret-that-is-long-enough-for-hs256",
            Scopes = ["openid", "profile"],
            GroupsClaim = "groups"
        };

        // Discovery is never reached in these tests: a refresh attempt fails closed.
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new FailingHandler()));
        _client = new OidcClient(options, factory.Object, NullLogger<OidcClient>.Instance);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        _otherRsa.Dispose();
    }

    private static Dictionary<string, object> Claims(Action<Dictionary<string, object>>? change = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = ClientId,
            ["sub"] = "user-1",
            ["nonce"] = Nonce,
            ["preferred_username"] = "alice",
            ["groups"] = new[] { "media", "admins" }
        };
        change?.Invoke(claims);
        return claims;
    }

    private static string Token(SecurityKey key, string algorithm, Dictionary<string, object> claims, DateTime? expires = null)
        => new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, algorithm)
        });

    private string ValidToken(Action<Dictionary<string, object>>? change = null) => Token(_key, SecurityAlgorithms.RsaSha256, Claims(change));

    [Fact]
    public async Task ValidToken_IsAccepted()
    {
        var claims = await _client.ValidateIdTokenAsync(_config, ValidToken(), Nonce, CancellationToken.None);
        var identity = _client.CreateIdentity(_config.Issuer, claims.GetSingle("sub")!, claims);

        Assert.Equal(Issuer, identity.Issuer);
        Assert.Equal("user-1", identity.Subject);
        Assert.Equal("alice", identity.Username);
        Assert.Equal(new[] { "media", "admins" }, identity.Groups);
    }

    [Fact]
    public async Task HmacToken_SignedWithClientSecret_IsAccepted()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-client-secret-that-is-long-enough-for-hs256"));
        var token = Token(key, SecurityAlgorithms.HmacSha256, Claims());
        _config.IdTokenSigningAlgValuesSupported.Add(SecurityAlgorithms.HmacSha256);
        Assert.NotNull(await _client.ValidateIdTokenAsync(_config, token, Nonce, CancellationToken.None));
    }

    [Fact]
    public async Task HmacToken_RejectedUnlessAdvertised()
    {
        // A leaked client secret must not be enough to mint ID tokens for a provider that signs with RSA.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-client-secret-that-is-long-enough-for-hs256"));
        var token = Token(key, SecurityAlgorithms.HmacSha256, Claims());
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, token, Nonce, CancellationToken.None));

        _config.IdTokenSigningAlgValuesSupported.Clear();
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, token, Nonce, CancellationToken.None));
    }

    [Fact]
    public async Task RsaToken_RejectedWhenOnlyHmacAdvertised()
    {
        _config.IdTokenSigningAlgValuesSupported.Clear();
        _config.IdTokenSigningAlgValuesSupported.Add(SecurityAlgorithms.HmacSha256);
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, ValidToken(), Nonce, CancellationToken.None));
    }

    [Fact]
    public async Task WrongNonce_IsRejected()
        => await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, ValidToken(), "other", CancellationToken.None));

    [Fact]
    public async Task MissingNonce_IsRejected()
        => await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, ValidToken(c => c.Remove("nonce")), Nonce, CancellationToken.None));

    [Fact]
    public async Task WrongAudience_IsRejected()
        => await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, ValidToken(c => c["aud"] = "other"), Nonce, CancellationToken.None));

    [Fact]
    public async Task WrongIssuer_IsRejected()
        => await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, ValidToken(c => c["iss"] = "https://evil.example.org/"), Nonce, CancellationToken.None));

    [Fact]
    public async Task MultipleAudiences_RequireMatchingAzp()
    {
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, ValidToken(c => c["aud"] = new[] { ClientId, "other" }), Nonce, CancellationToken.None));
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, ValidToken(c => c["azp"] = "other"), Nonce, CancellationToken.None));
        Assert.NotNull(await _client.ValidateIdTokenAsync(
            _config,
            ValidToken(c =>
            {
                c["aud"] = new[] { ClientId, "other" };
                c["azp"] = ClientId;
            }),
            Nonce,
            CancellationToken.None));
    }

    [Fact]
    public async Task Expired_IsRejected()
    {
        var token = Token(_key, SecurityAlgorithms.RsaSha256, Claims(), DateTime.UtcNow.AddMinutes(-10));
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, token, Nonce, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownKey_IsRejected()
    {
        var token = Token(new RsaSecurityKey(_otherRsa) { KeyId = "k2" }, SecurityAlgorithms.RsaSha256, Claims());
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, token, Nonce, CancellationToken.None));
    }

    [Fact]
    public async Task WrongKeyWithSameKid_IsRejected()
    {
        var token = Token(new RsaSecurityKey(_otherRsa) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256, Claims());
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, token, Nonce, CancellationToken.None));
    }

    [Fact]
    public async Task UnsignedToken_IsRejected()
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Claims = Claims(), Expires = DateTime.UtcNow.AddMinutes(5) });
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, token, Nonce, CancellationToken.None));
    }

    [Fact]
    public async Task TamperedPayload_IsRejected()
    {
        var parts = ValidToken().Split('.');
        var payload = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(Base64UrlEncoder.Decode(parts[1]).Replace("alice", "admin", StringComparison.Ordinal)));
        var token = parts[0] + "." + payload + "." + parts[2];
        await Assert.ThrowsAsync<OidcProtocolException>(() => _client.ValidateIdTokenAsync(_config, token, Nonce, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://idp/app/", "https://idp/app/", true)]
    [InlineData("https://idp/app", "https://idp/app/", true)]
    [InlineData("https://idp/app/", "https://idp/app", true)]
    [InlineData("https://idp/app", "https://idp/other", false)]
    [InlineData("https://idp/app", "https://IDP/app", false)]
    [InlineData("https://idp/app", null, false)]
    public void IssuerMatches(string configured, string? discovered, bool expected)
        => Assert.Equal(expected, OidcClient.IssuerMatches(configured, discovered));

    [Fact]
    public void AuthorizationUrl_HasPkceStateNonce()
    {
        var store = new OidcStateStore(TimeProvider.System);
        var login = store.Create();
        var url = new Uri(_client.BuildAuthorizationUrl(_config, "https://jf.example.org/oidc/callback", login));
        var query = QueryHelpers.ParseQuery(url.Query);

        Assert.Equal("idp.example.org", url.Host);
        Assert.Equal("x", query["tenant"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal("https://jf.example.org/oidc/callback", query["redirect_uri"]);
        Assert.Equal("openid profile", query["scope"]);
        Assert.Equal(login.State, query["state"]);
        Assert.Equal(login.Nonce, query["nonce"]);
        Assert.Equal(OidcStateStore.CodeChallenge(login.CodeVerifier), query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.DoesNotContain(login.CodeVerifier, url.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(login.BrowserBinding, url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffData_CannotBreakOutOfScript()
    {
        var data = OidcController.HandoffData("sid", "</script><script>alert(1)</script>", "uid", "tok'\"&", "dev");
        Assert.DoesNotContain("<", data, StringComparison.Ordinal);
        Assert.DoesNotContain(">", data, StringComparison.Ordinal);
        Assert.DoesNotContain("'", data, StringComparison.Ordinal);
        Assert.DoesNotContain("&", data, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorMessages_DoNotDependOnInput()
    {
        foreach (var error in Enum.GetValues<OidcLoginError>())
        {
            Assert.False(string.IsNullOrEmpty(OidcController.ErrorMessage(error)));
        }

        Assert.False(string.IsNullOrEmpty(OidcController.ErrorMessage(null)));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no network in tests");
    }
}
