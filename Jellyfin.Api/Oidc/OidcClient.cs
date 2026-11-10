using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// The relying-party side of the OpenID Connect authorization code flow: discovery, the authorization
/// request, the code exchange, ID token validation and (when needed) the userinfo request.
/// </summary>
public class OidcClient
{
    private static readonly TimeSpan _clockSkew = TimeSpan.FromMinutes(2);

    private static readonly string[] _allowedAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.HmacSha256, SecurityAlgorithms.HmacSha384, SecurityAlgorithms.HmacSha512
    ];

    private readonly OidcOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OidcClient> _logger;
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> _configurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcClient"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public OidcClient(OidcOptions options, IHttpClientFactory httpClientFactory, ILogger<OidcClient> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configurationManager = new Lazy<ConfigurationManager<OpenIdConnectConfiguration>>(CreateConfigurationManager);
    }

    /// <summary>
    /// Gets the provider configuration from the discovery document (cached, refreshed periodically).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The configuration.</returns>
    /// <exception cref="OidcProtocolException">Discovery failed or the document is inconsistent.</exception>
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        OpenIdConnectConfiguration config;
        try
        {
            config = await _configurationManager.Value.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OidcProtocolException("OpenID Connect discovery failed.", ex);
        }

        if (!IssuerMatches(_options.Issuer, config.Issuer))
        {
            throw new OidcProtocolException("The discovery document's issuer does not match the configured issuer.");
        }

        if (string.IsNullOrEmpty(config.AuthorizationEndpoint) || string.IsNullOrEmpty(config.TokenEndpoint))
        {
            throw new OidcProtocolException("The discovery document lacks an authorization or token endpoint.");
        }

        return config;
    }

    /// <summary>
    /// Returns whether a discovered issuer matches the configured one. Per OpenID Connect Discovery they must
    /// be identical; a single trailing slash difference is tolerated because operators often omit it.
    /// </summary>
    /// <param name="configured">The configured issuer.</param>
    /// <param name="discovered">The discovered issuer.</param>
    /// <returns><c>true</c> if they match.</returns>
    public static bool IssuerMatches(string configured, string? discovered)
        => !string.IsNullOrEmpty(discovered)
           && string.Equals(configured.TrimEnd('/'), discovered.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>
    /// Builds the authorization request URL.
    /// </summary>
    /// <param name="config">The provider configuration.</param>
    /// <param name="redirectUri">The redirect URI.</param>
    /// <param name="login">The pending login.</param>
    /// <returns>The URL.</returns>
    public string BuildAuthorizationUrl(OpenIdConnectConfiguration config, string redirectUri, OidcPendingLogin login)
        => QueryHelpers.AddQueryString(config.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', _options.Scopes),
            ["state"] = login.State,
            ["nonce"] = login.Nonce,
            ["code_challenge"] = OidcStateStore.CodeChallenge(login.CodeVerifier),
            ["code_challenge_method"] = "S256"
        });

    /// <summary>
    /// Exchanges the authorization code, validates the ID token and returns the identity.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="redirectUri">The redirect URI used in the authorization request.</param>
    /// <param name="login">The pending login.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="OidcProtocolException">Anything about the exchange or the token is not right.</exception>
    public async Task<OidcIdentity> RedeemCodeAsync(string code, string redirectUri, OidcPendingLogin login, CancellationToken cancellationToken)
    {
        var config = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var tokens = await ExchangeCodeAsync(config, code, redirectUri, login.CodeVerifier, cancellationToken).ConfigureAwait(false);

        var idToken = GetString(tokens, "id_token") ?? throw new OidcProtocolException("The token response has no id_token.");
        var claims = await ValidateIdTokenAsync(config, idToken, login.Nonce, cancellationToken).ConfigureAwait(false);
        var subject = claims.GetSingle("sub");
        if (string.IsNullOrEmpty(subject) || subject.Length > 255)
        {
            throw new OidcProtocolException("The ID token has no usable sub claim.");
        }

        var needsUserInfo = !claims.Has(_options.UsernameClaim)
            || (!claims.Has(_options.GroupsClaim) && (_options.AllowedGroups.Count > 0 || _options.AdminGroups.Count > 0));
        if (needsUserInfo && !string.IsNullOrEmpty(config.UserInfoEndpoint) && GetString(tokens, "access_token") is string accessToken)
        {
            var userInfo = await GetUserInfoAsync(config, accessToken, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(userInfo.GetSingle("sub"), subject, StringComparison.Ordinal))
            {
                throw new OidcProtocolException("The userinfo sub does not match the ID token.");
            }

            claims = OidcClaims.Merge(claims, userInfo);
        }

        return CreateIdentity(config.Issuer, subject, claims);
    }

    /// <summary>
    /// Builds the identity from validated claims.
    /// </summary>
    /// <param name="issuer">The issuer.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="claims">The claims.</param>
    /// <returns>The identity.</returns>
    public OidcIdentity CreateIdentity(string issuer, string subject, OidcClaims claims)
        => new(issuer, subject, claims.GetSingle(_options.UsernameClaim), claims.GetAll(_options.GroupsClaim));

    /// <summary>
    /// Validates an ID token: signature (JWKS, or the client secret for HMAC), issuer, audience, authorized party,
    /// lifetime and nonce.
    /// </summary>
    /// <param name="config">The provider configuration.</param>
    /// <param name="idToken">The ID token.</param>
    /// <param name="nonce">The expected nonce.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The token's claims.</returns>
    /// <exception cref="OidcProtocolException">The token is not valid.</exception>
    public async Task<OidcClaims> ValidateIdTokenAsync(OpenIdConnectConfiguration config, string idToken, string nonce, CancellationToken cancellationToken)
    {
        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(idToken, CreateValidationParameters(config)).ConfigureAwait(false);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // The provider may have rotated its keys: refresh the discovery document once and retry.
            _configurationManager.Value.RequestRefresh();
            config = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            result = await handler.ValidateTokenAsync(idToken, CreateValidationParameters(config)).ConfigureAwait(false);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
        {
            throw result.Exception is null
                ? new OidcProtocolException("The ID token is not valid.")
                : new OidcProtocolException("The ID token is not valid: " + result.Exception.GetType().Name, result.Exception);
        }

        var claims = OidcClaims.FromJson(Base64UrlEncoder.Decode(token.EncodedPayload));

        var tokenNonce = claims.GetSingle("nonce");
        if (tokenNonce is null || !string.Equals(tokenNonce, nonce, StringComparison.Ordinal))
        {
            throw new OidcProtocolException("The ID token nonce does not match.");
        }

        var audiences = claims.GetAll("aud");
        var authorizedParty = claims.GetSingle("azp");
        if ((audiences.Count > 1 || claims.Has("azp")) && !string.Equals(authorizedParty, _options.ClientId, StringComparison.Ordinal))
        {
            throw new OidcProtocolException("The ID token azp does not match the client id.");
        }

        return claims;
    }

    /// <summary>
    /// Creates the token validation parameters for a provider configuration.
    /// </summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The parameters.</returns>
    public TokenValidationParameters CreateValidationParameters(OpenIdConnectConfiguration config)
    {
        // Only algorithms the provider advertises for ID tokens. If it advertises none, asymmetric ones only.
        var advertised = config.IdTokenSigningAlgValuesSupported;
        var algorithms = advertised.Count > 0
            ? _allowedAlgorithms.Where(a => advertised.Contains(a)).ToArray()
            : _allowedAlgorithms.Where(a => !a.StartsWith("HS", StringComparison.Ordinal)).ToArray();

        var keys = new List<SecurityKey>(config.SigningKeys);

        // OpenID Connect Core 10.1: HMAC-signed ID tokens use the client secret as the key. Accepted only when the
        // provider advertises an HMAC algorithm, and never with a secret shorter than 256 bits.
        var secretBytes = Encoding.UTF8.GetBytes(_options.ClientSecret);
        if (algorithms.Any(a => a.StartsWith("HS", StringComparison.Ordinal)) && secretBytes.Length >= 32)
        {
            keys.Add(new SymmetricSecurityKey(secretBytes));
        }

        return new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidateIssuer = true,
            ValidAudience = _options.ClientId,
            ValidateAudience = true,
            IssuerSigningKeys = keys,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = _clockSkew,
            ValidAlgorithms = algorithms,
            TryAllIssuerSigningKeys = true
        };
    }

    private static string? GetString(JsonElement json, string name)
        => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FormEncode(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    private ConfigurationManager<OpenIdConnectConfiguration> CreateConfigurationManager()
    {
        var metadataAddress = _options.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        var retriever = new HttpDocumentRetriever(_httpClientFactory.CreateClient(NamedClient.Default))
        {
            RequireHttps = metadataAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        };

        return new ConfigurationManager<OpenIdConnectConfiguration>(metadataAddress, new OpenIdConnectConfigurationRetriever(), retriever);
    }

    private async Task<JsonElement> ExchangeCodeAsync(
        OpenIdConnectConfiguration config,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, config.TokenEndpoint);
        var methods = config.TokenEndpointAuthMethodsSupported;
        if (methods.Count > 0 && !methods.Contains("client_secret_basic") && methods.Contains("client_secret_post"))
        {
            form["client_id"] = _options.ClientId;
            form["client_secret"] = _options.ClientSecret;
        }
        else
        {
            // RFC 6749 2.3.1: client id and secret are form-encoded before being base64-encoded.
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(FormEncode(_options.ClientId) + ":" + FormEncode(_options.ClientSecret)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        return await SendForJsonAsync(request, "token", cancellationToken).ConfigureAwait(false);
    }

    private async Task<OidcClaims> GetUserInfoAsync(OpenIdConnectConfiguration config, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, config.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        var json = await SendForJsonAsync(request, "userinfo", cancellationToken).ConfigureAwait(false);
        return OidcClaims.FromJson(json);
    }

    private async Task<JsonElement> SendForJsonAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OidcProtocolException("The " + what + " request failed.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The body may echo request parameters; only log the status and the OAuth error code.
                string? error = null;
                try
                {
                    using var errorDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    error = GetString(errorDoc.RootElement, "error");
                }
                catch (JsonException)
                {
                }

                _logger.LogWarning("OIDC {Endpoint} request returned {StatusCode} (error: {Error})", what, (int)response.StatusCode, error ?? "none");
                throw new OidcProtocolException("The " + what + " request was rejected.");
            }

            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                return doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new OidcProtocolException("The " + what + " response is not JSON.", ex);
            }
        }
    }
}
