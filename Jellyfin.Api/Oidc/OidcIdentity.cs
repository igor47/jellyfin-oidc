using System.Collections.Generic;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// A validated identity returned by the provider.
/// </summary>
/// <param name="Issuer">The issuer (<c>iss</c>).</param>
/// <param name="Subject">The subject (<c>sub</c>).</param>
/// <param name="Username">The raw value of the username claim, or <c>null</c> if missing.</param>
/// <param name="Groups">The groups.</param>
public sealed record OidcIdentity(string Issuer, string Subject, string? Username, IReadOnlyList<string> Groups);
