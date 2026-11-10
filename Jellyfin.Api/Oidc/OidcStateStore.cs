using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// In-memory, short-lived, single-use store for pending authorization requests
/// (state, nonce, PKCE verifier), each bound to a browser cookie.
/// </summary>
public sealed class OidcStateStore
{
    /// <summary>
    /// The maximum number of pending logins kept at once.
    /// </summary>
    public const int Capacity = 1000;

    /// <summary>
    /// How long a pending login stays valid.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcStateStore"/> class.
    /// </summary>
    /// <param name="timeProvider">The time provider.</param>
    public OidcStateStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets the number of stored entries, including expired ones not yet purged.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Creates a new pending login.
    /// </summary>
    /// <remarks>
    /// When the store is full, expired entries are purged first and then the oldest entries are evicted,
    /// so a flood of login requests cannot lock everyone out for the whole lifetime of an entry.
    /// </remarks>
    /// <returns>The pending login.</returns>
    public OidcPendingLogin Create()
    {
        var now = _timeProvider.GetUtcNow();
        if (_entries.Count >= Capacity)
        {
            Purge(now);
        }

        var login = new OidcPendingLogin(RandomToken(), RandomToken(), RandomToken(), RandomToken());
        _entries[login.State] = new Entry(login, Hash(login.BrowserBinding), now + Lifetime);
        return login;
    }

    /// <summary>
    /// Removes and returns a pending login if it exists, has not expired and belongs to this browser.
    /// A state value can only ever be redeemed once, whether or not redemption succeeds.
    /// </summary>
    /// <param name="state">The <c>state</c> returned by the provider.</param>
    /// <param name="browserBinding">The binding value from the browser cookie.</param>
    /// <returns>The pending login, or <c>null</c>.</returns>
    public OidcPendingLogin? Take(string? state, string? browserBinding)
    {
        if (string.IsNullOrEmpty(state) || state.Length > 256 || !_entries.TryRemove(state, out var entry))
        {
            return null;
        }

        if (entry.Expires <= _timeProvider.GetUtcNow())
        {
            return null;
        }

        if (string.IsNullOrEmpty(browserBinding)
            || !CryptographicOperations.FixedTimeEquals(entry.BindingHash, Hash(browserBinding)))
        {
            return null;
        }

        return entry.Login;
    }

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

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Purge(DateTimeOffset now)
    {
        foreach (var key in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
        {
            _entries.TryRemove(key, out _);
        }

        var excess = _entries.Count - (Capacity - (Capacity / 10));
        if (excess > 0)
        {
            foreach (var key in _entries.OrderBy(e => e.Value.Expires).Take(excess).Select(e => e.Key).ToList())
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(OidcPendingLogin Login, byte[] BindingHash, DateTimeOffset Expires);
}
