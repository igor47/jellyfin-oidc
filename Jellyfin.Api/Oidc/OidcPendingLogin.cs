using System;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// A login started at <c>/oidc/login</c> and not yet completed. It lives only in the browser,
/// in an encrypted and authenticated cookie (see <see cref="OidcLoginProtector"/>).
/// </summary>
/// <param name="State">The <c>state</c> parameter.</param>
/// <param name="Nonce">The <c>nonce</c> parameter.</param>
/// <param name="CodeVerifier">The PKCE code verifier.</param>
/// <param name="Expires">When the login expires.</param>
public sealed record OidcPendingLogin(string State, string Nonce, string CodeVerifier, DateTimeOffset Expires);
