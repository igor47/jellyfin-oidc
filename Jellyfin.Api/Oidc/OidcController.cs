using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// The OpenID Connect login endpoints.
/// </summary>
[ApiController]
[Route("oidc")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public class OidcController : ControllerBase
{
    /// <summary>
    /// The client name used for sessions created here; it is what the stock web client reports.
    /// </summary>
    public const string WebClientName = "Jellyfin Web";

    private readonly OidcOptions _options;
    private readonly OidcLoginProtector _protector;
    private readonly OidcClient _client;
    private readonly OidcUserService _userService;
    private readonly ISessionManager _sessionManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<OidcController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcController"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="protector">The pending login cookie protector.</param>
    /// <param name="client">The OIDC client.</param>
    /// <param name="userService">The user service.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="appHost">The application host.</param>
    /// <param name="logger">The logger.</param>
    public OidcController(
        OidcOptions options,
        OidcLoginProtector protector,
        OidcClient client,
        OidcUserService userService,
        ISessionManager sessionManager,
        IServerApplicationHost appHost,
        ILogger<OidcController> logger)
    {
        _options = options;
        _protector = protector;
        _client = client;
        _userService = userService;
        _sessionManager = sessionManager;
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>
    /// Starts the authorization code flow and redirects to the provider.
    /// </summary>
    /// <returns>A redirect to the provider, or an error page.</returns>
    [HttpGet("login")]
    public async Task<ActionResult> Login()
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        OpenIdConnectConfiguration config;
        try
        {
            config = await _client.GetConfigurationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OidcProtocolException ex)
        {
            _logger.LogError("OIDC login could not start: {Reason} {Inner}", ex.Message, ex.InnerException?.Message);
            return ErrorPage(OidcLoginError.ProviderError);
        }

        var login = _protector.Create();
        Response.Cookies.Append(_protector.CookieName, _protector.Protect(login), _protector.CookieOptions(OidcLoginProtector.Lifetime));
        SetNoStore();
        return Redirect(_client.BuildAuthorizationUrl(config, OidcUrls.CallbackUrl(_options), login));
    }

    /// <summary>
    /// Completes the flow: validates the response, resolves the user, creates a session and hands it to the web client.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="state">The state.</param>
    /// <param name="error">The error code, if the provider refused.</param>
    /// <returns>The hand-off page, or an error page.</returns>
    [HttpGet("callback")]
    public async Task<ActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        // The cookie is cleared whatever happens, so a pending login can be completed at most once in this browser.
        Request.Cookies.TryGetValue(_protector.CookieName, out var cookie);
        Response.Cookies.Delete(_protector.CookieName, _protector.CookieOptions(null));

        var login = _protector.Unprotect(cookie, state);
        if (login is null)
        {
            _logger.LogWarning("OIDC callback rejected: missing, expired, tampered or foreign login cookie, or state mismatch (IP: {IP})", HttpContext.GetNormalizedRemoteIP());
            return ErrorPage(OidcLoginError.InvalidState);
        }

        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("OIDC provider returned error {Error}", SafeErrorCode(error));
            return ErrorPage(OidcLoginError.ProviderError);
        }

        if (string.IsNullOrEmpty(code) || code.Length > 4096)
        {
            _logger.LogWarning("OIDC callback without a usable code");
            return ErrorPage(OidcLoginError.ProviderError);
        }

        OidcIdentity identity;
        try
        {
            identity = await _client.RedeemCodeAsync(code, OidcUrls.CallbackUrl(_options), login, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OidcProtocolException ex)
        {
            _logger.LogWarning("OIDC login failed: {Reason} {Inner}", ex.Message, ex.InnerException?.GetType().Name);
            return ErrorPage(OidcLoginError.ProviderError);
        }

        var remoteIp = HttpContext.GetNormalizedRemoteIP().ToString();
        OidcUserResult result;
        try
        {
            result = await _userService.ResolveAsync(identity, remoteIp).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(ex, "OIDC login failed while resolving the Jellyfin user for subject {Subject}", identity.Subject);
            return ErrorPage(null);
        }

        if (result.User is null)
        {
            return ErrorPage(result.Error);
        }

        var deviceId = OidcLoginProtector.RandomToken();
        AuthenticationResult session;
        try
        {
            session = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = result.User.Id,
                Username = result.User.Username,
                App = WebClientName,
                AppVersion = _appHost.ApplicationVersionString,
                DeviceId = deviceId,
                DeviceName = "Web Browser",
                RemoteEndPoint = remoteIp
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SecurityException or AuthenticationException)
        {
            _logger.LogWarning("OIDC login for {UserName} refused by the session manager: {Reason}", result.User.Username, ex.Message);
            return ErrorPage(OidcLoginError.SessionDenied);
        }

        _logger.LogInformation("OIDC login succeeded for {UserName} (IP: {IP})", result.User.Username, remoteIp);
        return HandoffPage(session, deviceId);
    }

    /// <summary>
    /// Returns the user-facing message for a login error. Never includes request or provider data.
    /// </summary>
    /// <param name="error">The error, or <c>null</c> for an internal error.</param>
    /// <returns>The message.</returns>
    public static string ErrorMessage(OidcLoginError? error) => error switch
    {
        OidcLoginError.InvalidState => "This sign-in link has expired or was already used. Please start again.",
        OidcLoginError.ProviderError => "The identity provider did not complete the sign-in.",
        OidcLoginError.NotInAllowedGroup => "Your account is not allowed to use this server.",
        OidcLoginError.InvalidUsername => "Your account has no usable username for this server.",
        OidcLoginError.UsernameTaken => "A different account with your username already exists on this server. Ask the administrator to link it.",
        OidcLoginError.NoAccount => "You do not have an account on this server. Ask the administrator to create one.",
        OidcLoginError.LinkedUserMissing => "Your account on this server is not available. Ask the administrator.",
        OidcLoginError.AccountDisabled => "Your account on this server is disabled.",
        OidcLoginError.RemoteAccessDenied => "Your account may only be used from the local network.",
        OidcLoginError.ScheduleDenied => "Your account may not be used at this time.",
        OidcLoginError.SessionDenied => "A session could not be started for your account.",
        _ => "Sign-in failed because of a server error."
    };

    /// <summary>
    /// Builds the hand-off page script data.
    /// </summary>
    /// <param name="serverId">The server id.</param>
    /// <param name="serverName">The server name.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="deviceId">The device id.</param>
    /// <returns>A JSON object literal safe to embed in a script element.</returns>
    public static string HandoffData(string serverId, string serverName, string userId, string accessToken, string deviceId)
    {
        // The default encoder escapes <, >, &, quotes and non-ASCII, so the result cannot close the script element.
        return JsonSerializer.Serialize(new
        {
            serverId,
            serverName,
            userId,
            accessToken,
            deviceId
        });
    }

    private static string SafeErrorCode(string error)
    {
        var code = new string(error.Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_').Take(64).ToArray());
        return code.Length == 0 ? "invalid" : code;
    }

    private void SetNoStore()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.XFrameOptions = "DENY";
    }

    private ContentResult Page(int statusCode, string title, string body, string? script, string nonce)
    {
        SetNoStore();
        Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'nonce-" + nonce + "'; style-src 'nonce-" + nonce + "'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

        var html = new StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<meta name=\"referrer\" content=\"no-referrer\">")
            .Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>")
            .Append("<style nonce=\"").Append(nonce).Append("\">")
            .Append("body{background:#101010;color:#ddd;font-family:sans-serif;display:flex;align-items:center;justify-content:center;min-height:90vh;margin:0}")
            .Append("main{max-width:30em;padding:1em;text-align:center}a{color:#00a4dc}")
            .Append("</style></head><body><main>")
            .Append(body)
            .Append("</main>");
        if (script is not null)
        {
            html.Append("<script nonce=\"").Append(nonce).Append("\">").Append(script).Append("</script>");
        }

        html.Append("</body></html>");
        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = "text/html; charset=utf-8",
            Content = html.ToString()
        };
    }

    private ContentResult ErrorPage(OidcLoginError? error)
    {
        var status = error switch
        {
            OidcLoginError.InvalidState => StatusCodes.Status400BadRequest,
            OidcLoginError.ProviderError => StatusCodes.Status502BadGateway,
            null => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status403Forbidden
        };

        var webUrl = WebUtility.HtmlEncode(OidcUrls.WebPath(_options));
        var body = "<h1>Sign-in failed</h1><p>" + WebUtility.HtmlEncode(ErrorMessage(error)) + "</p><p><a href=\"" + webUrl + "\">Back to Jellyfin</a></p>";
        return Page(status, "Sign-in failed", body, null, OidcLoginProtector.RandomToken());
    }

    private ContentResult HandoffPage(AuthenticationResult session, string deviceId)
    {
        var data = HandoffData(
            session.ServerId,
            _appHost.FriendlyName,
            session.User.Id.ToString("N"),
            session.AccessToken,
            deviceId);

        // Writes the session where the stock web client (jellyfin-apiclient Credentials and appSettings) reads it:
        // localStorage "jellyfin_credentials" (Servers[]), "_deviceId2" (the device id the token was issued for) and
        // "enableAutoLogin" (otherwise the client drops the token on connect). The server address is computed the
        // way the web client computes it, from the URL the browser actually used.
        var script = "(function(){var d=" + data + ";"
            + "var p=location.pathname,i=p.toLowerCase().lastIndexOf('/oidc/callback');"
            + "var addr=location.origin+(i>=0?p.substring(0,i):'');"
            + "var c;try{c=JSON.parse(localStorage.getItem('jellyfin_credentials')||'{}')||{};}catch(e){c={};}"
            + "if(!Array.isArray(c.Servers)){c.Servers=[];}"
            + "var s=null;for(var k=0;k<c.Servers.length;k++){if(c.Servers[k]&&c.Servers[k].Id===d.serverId){s=c.Servers[k];}}"
            + "if(!s){s={Id:d.serverId};c.Servers.push(s);}"
            + "s.Name=d.serverName;s.ManualAddress=addr;s.LastConnectionMode=2;s.DateLastAccessed=Date.now();"
            + "s.UserId=d.userId;s.AccessToken=d.accessToken;"
            + "localStorage.setItem('jellyfin_credentials',JSON.stringify(c));"
            + "localStorage.setItem('_deviceId2',d.deviceId);"
            + "localStorage.setItem('enableAutoLogin','true');"
            + "location.replace(addr+'/web/');})();";

        var body = "<p>Signing you in&hellip;</p><noscript><p>JavaScript is required to finish signing in.</p></noscript>";
        return Page(StatusCodes.Status200OK, "Signing in", body, script, OidcLoginProtector.RandomToken());
    }
}
