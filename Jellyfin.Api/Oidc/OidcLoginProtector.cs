using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Keeps pending logins (state, nonce, PKCE verifier, expiry) in a cookie encrypted and authenticated with
/// AES-256-GCM under a random per-process key. Nothing is stored on the server, so flooding
/// <c>/oidc/login</c> cannot evict anyone's login; logins in flight fail across a server restart.
/// </summary>
public sealed class OidcLoginProtector : IDisposable
{
    /// <summary>
    /// How long a pending login stays valid.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxCookieLength = 2048;

    private readonly AesGcm _aes;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _associatedData;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcLoginProtector"/> class.
    /// </summary>
    /// <param name="options">The options; <see cref="OidcOptions.ExternalBaseUrl"/> decides the cookie name and flags.</param>
    /// <param name="timeProvider">The time provider.</param>
    public OidcLoginProtector(OidcOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider;
        _aes = new AesGcm(RandomNumberGenerator.GetBytes(32), TagSize);

        var baseUri = new Uri(string.IsNullOrEmpty(options.ExternalBaseUrl) ? "http://localhost" : options.ExternalBaseUrl);
        BasePath = baseUri.AbsolutePath.TrimEnd('/');
        IsSecure = baseUri.Scheme == Uri.UriSchemeHttps;

        // Unique per base path, so two instances on one host under different base URLs do not collide.
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BasePath)))[..8];
        CookieName = (IsSecure ? "__Host-" : string.Empty) + "jellyfin_oidc_" + suffix;

        // Binds the ciphertext to this cookie name: a cookie minted for another instance does not decrypt.
        _associatedData = Encoding.UTF8.GetBytes(CookieName);
    }

    /// <summary>
    /// Gets the cookie name: <c>__Host-jellyfin_oidc_&lt;hash of base path&gt;</c> for an https base URL,
    /// <c>jellyfin_oidc_&lt;hash of base path&gt;</c> for http.
    /// </summary>
    public string CookieName { get; }

    /// <summary>
    /// Gets the path of the external base URL, without a trailing slash.
    /// </summary>
    public string BasePath { get; }

    /// <summary>
    /// Gets a value indicating whether the external base URL is https.
    /// </summary>
    public bool IsSecure { get; }

    /// <summary>
    /// Computes the PKCE S256 code challenge for a verifier.
    /// </summary>
    /// <param name="verifier">The code verifier.</param>
    /// <returns>The code challenge.</returns>
    public static string CodeChallenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// Generates a URL-safe random token with 256 bits of entropy.
    /// </summary>
    /// <returns>The token.</returns>
    public static string RandomToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Creates a new pending login.
    /// </summary>
    /// <returns>The pending login.</returns>
    public OidcPendingLogin Create()
        => new(RandomToken(), RandomToken(), RandomToken(), _timeProvider.GetUtcNow() + Lifetime);

    /// <summary>
    /// Returns the cookie options. With an https base URL the cookie follows the <c>__Host-</c> rules
    /// (Secure, Path=/, no Domain); with http it is scoped to <c>&lt;base path&gt;/oidc</c>.
    /// </summary>
    /// <param name="maxAge">The max age, or <c>null</c> when deleting.</param>
    /// <returns>The options.</returns>
    public CookieOptions CookieOptions(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,

        // Lax: the cookie must come along on the top-level GET redirect back from the provider.
        SameSite = SameSiteMode.Lax,
        Secure = IsSecure,
        Path = IsSecure ? "/" : BasePath + "/oidc",
        Domain = null,
        MaxAge = maxAge,
        IsEssential = true
    };

    /// <summary>
    /// Encrypts a pending login into a cookie value.
    /// </summary>
    /// <param name="login">The pending login.</param>
    /// <returns>The cookie value.</returns>
    public string Protect(OidcPendingLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            login.State,
            login.Nonce,
            login.CodeVerifier,
            login.Expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        });

        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        _aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length), _associatedData);
        return Base64UrlEncode(output);
    }

    /// <summary>
    /// Decrypts a cookie value and checks it against the <c>state</c> returned by the provider.
    /// Fails closed: any tampering, decryption or parse error, expiry or state mismatch returns <c>null</c>.
    /// </summary>
    /// <param name="cookie">The cookie value.</param>
    /// <param name="state">The <c>state</c> query parameter of the callback.</param>
    /// <returns>The pending login, or <c>null</c>.</returns>
    public OidcPendingLogin? Unprotect(string? cookie, string? state)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length > MaxCookieLength || string.IsNullOrEmpty(state) || state.Length > 256)
        {
            return null;
        }

        OidcPendingLogin login;
        try
        {
            var data = Base64UrlDecode(cookie);
            if (data.Length <= NonceSize + TagSize)
            {
                return null;
            }

            var plaintext = new byte[data.Length - NonceSize - TagSize];
            _aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, plaintext.Length), data.AsSpan(NonceSize + plaintext.Length), plaintext, _associatedData);
            var fields = JsonSerializer.Deserialize<string[]>(plaintext);
            if (fields is null || fields.Length != 4 || Array.Exists(fields, string.IsNullOrEmpty))
            {
                return null;
            }

            login = new OidcPendingLogin(
                fields[0],
                fields[1],
                fields[2],
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or OverflowException or ArgumentException)
        {
            return null;
        }

        if (login.Expires <= _timeProvider.GetUtcNow())
        {
            return null;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(login.State), Encoding.UTF8.GetBytes(state)) ? login : null;
    }

    /// <inheritdoc />
    public void Dispose() => _aes.Dispose();

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException()
        };
        return Convert.FromBase64String(s);
    }
}
