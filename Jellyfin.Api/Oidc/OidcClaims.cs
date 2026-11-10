using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// The claims of an ID token or userinfo response, flattened to lists of strings.
/// </summary>
public sealed class OidcClaims
{
    private readonly Dictionary<string, IReadOnlyList<string>> _claims;

    private OidcClaims(Dictionary<string, IReadOnlyList<string>> claims)
    {
        _claims = claims;
    }

    /// <summary>
    /// Builds the claim set from a JSON object (a JWT payload or a userinfo response).
    /// Strings are kept as is, arrays contribute each scalar element, numbers and booleans use their JSON text.
    /// Objects and nulls are ignored.
    /// </summary>
    /// <param name="json">The JSON object.</param>
    /// <returns>The claims.</returns>
    public static OidcClaims FromJson(JsonElement json)
    {
        var claims = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (json.ValueKind != JsonValueKind.Object)
        {
            return new OidcClaims(claims);
        }

        foreach (var property in json.EnumerateObject())
        {
            var values = new List<string>();
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    AddScalar(values, item);
                }
            }
            else
            {
                AddScalar(values, property.Value);
            }

            claims[property.Name] = values;
        }

        return new OidcClaims(claims);
    }

    /// <summary>
    /// Builds the claim set from a JSON object string.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The claims.</returns>
    public static OidcClaims FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromJson(doc.RootElement);
    }

    /// <summary>
    /// Returns whether the claim is present.
    /// </summary>
    /// <param name="name">The claim name.</param>
    /// <returns><c>true</c> if present.</returns>
    public bool Has(string name) => _claims.ContainsKey(name);

    /// <summary>
    /// Gets all non-empty values of a claim.
    /// </summary>
    /// <param name="name">The claim name.</param>
    /// <returns>The values.</returns>
    public IReadOnlyList<string> GetAll(string name)
        => _claims.TryGetValue(name, out var values) ? values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() : [];

    /// <summary>
    /// Gets the single value of a claim.
    /// </summary>
    /// <param name="name">The claim name.</param>
    /// <returns>The value, or <c>null</c> if the claim is missing, empty or has several values.</returns>
    public string? GetSingle(string name)
    {
        var values = GetAll(name);
        return values.Count == 1 ? values[0] : null;
    }

    /// <summary>
    /// Combines two claim sets; claims in <paramref name="primary"/> win over claims in <paramref name="fallback"/>.
    /// </summary>
    /// <param name="primary">The preferred claims (the ID token).</param>
    /// <param name="fallback">Claims used only where <paramref name="primary"/> lacks them (userinfo).</param>
    /// <returns>The merged claims.</returns>
    public static OidcClaims Merge(OidcClaims primary, OidcClaims fallback)
    {
        var claims = new Dictionary<string, IReadOnlyList<string>>(fallback._claims, StringComparer.Ordinal);
        foreach (var (name, values) in primary._claims)
        {
            claims[name] = values;
        }

        return new OidcClaims(claims);
    }

    private static void AddScalar(List<string> values, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                values.Add(element.GetString()!);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                values.Add(element.GetRawText());
                break;
            default:
                break;
        }
    }
}
