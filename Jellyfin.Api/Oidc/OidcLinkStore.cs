using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Persists (issuer, subject) to Jellyfin user id links in a JSON file in the config directory.
/// No database schema is touched, so the database stays compatible with stock Jellyfin.
/// </summary>
public sealed class OidcLinkStore : IDisposable
{
    /// <summary>
    /// The file name used in the config directory.
    /// </summary>
    public const string FileName = "oidc-links.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<OidcLink>? _links;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcLinkStore"/> class.
    /// </summary>
    /// <param name="configDirectory">The Jellyfin config directory.</param>
    public OidcLinkStore(string configDirectory)
    {
        _path = Path.Combine(configDirectory, FileName);
    }

    /// <summary>
    /// Gets the path of the backing file.
    /// </summary>
    public string FilePath => _path;

    /// <summary>
    /// Finds the user linked to an identity.
    /// </summary>
    /// <param name="issuer">The issuer.</param>
    /// <param name="subject">The subject.</param>
    /// <returns>The linked user id, or <c>null</c>.</returns>
    public async Task<Guid?> FindUserAsync(string issuer, string subject)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return Load().FirstOrDefault(l => Matches(l, issuer, subject))?.UserId;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns whether any identity is linked to a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> if linked.</returns>
    public async Task<bool> IsUserLinkedAsync(Guid userId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return Load().Any(l => l.UserId.Equals(userId));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Links an identity to a user, replacing any previous link for that identity.
    /// </summary>
    /// <param name="issuer">The issuer.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="userId">The user id.</param>
    /// <returns>A task.</returns>
    public async Task LinkAsync(string issuer, string subject, Guid userId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var links = Load().Where(l => !Matches(l, issuer, subject)).ToList();
            links.Add(new OidcLink { Issuer = issuer, Subject = subject, UserId = userId, Created = DateTime.UtcNow });
            Save(links);
            _links = links;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes the link for an identity.
    /// </summary>
    /// <param name="issuer">The issuer.</param>
    /// <param name="subject">The subject.</param>
    /// <returns>A task.</returns>
    public async Task UnlinkAsync(string issuer, string subject)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var links = Load().Where(l => !Matches(l, issuer, subject)).ToList();
            Save(links);
            _links = links;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private static bool Matches(OidcLink link, string issuer, string subject)
        => string.Equals(link.Issuer, issuer, StringComparison.Ordinal)
           && string.Equals(link.Subject, subject, StringComparison.Ordinal);

    private List<OidcLink> Load()
    {
        if (_links is not null)
        {
            return _links;
        }

        if (!File.Exists(_path))
        {
            _links = [];
            return _links;
        }

        // A corrupt file throws: logins fail rather than silently losing every link and re-creating users.
        var file = JsonSerializer.Deserialize<LinkFile>(File.ReadAllText(_path), _jsonOptions)
            ?? throw new InvalidDataException(_path + " is empty or invalid.");
        var links = file.Links ?? [];
        if (links.Any(l => string.IsNullOrEmpty(l.Issuer) || string.IsNullOrEmpty(l.Subject) || l.UserId.Equals(Guid.Empty)))
        {
            throw new InvalidDataException(_path + " contains an incomplete link.");
        }

        _links = links;
        return _links;
    }

    private void Save(List<OidcLink> links)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new LinkFile { Version = 1, Links = links }, _jsonOptions));
        File.Move(tmp, _path, true);
    }

    private sealed class LinkFile
    {
        public int Version { get; set; }

        public List<OidcLink>? Links { get; set; }
    }
}
