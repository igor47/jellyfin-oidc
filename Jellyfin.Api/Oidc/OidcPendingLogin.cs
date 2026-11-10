namespace Jellyfin.Api.Oidc;

/// <summary>
/// A login started at <c>/oidc/login</c> and not yet completed.
/// </summary>
/// <param name="State">The <c>state</c> parameter.</param>
/// <param name="Nonce">The <c>nonce</c> parameter.</param>
/// <param name="CodeVerifier">The PKCE code verifier.</param>
/// <param name="BrowserBinding">The secret stored in the browser cookie.</param>
public sealed record OidcPendingLogin(string State, string Nonce, string CodeVerifier, string BrowserBinding);
