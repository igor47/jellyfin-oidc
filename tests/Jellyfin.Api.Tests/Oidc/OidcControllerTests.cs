using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Oidc;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public sealed class OidcControllerTests : IDisposable
{
    private const string Discovery = """
        {"issuer":"https://idp.example.org","authorization_endpoint":"https://idp.example.org/authorize",
         "token_endpoint":"https://idp.example.org/token","jwks_uri":"https://idp.example.org/jwks",
         "id_token_signing_alg_values_supported":["RS256"]}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oidc-controller-" + Guid.NewGuid().ToString("N"));
    private readonly OidcOptions _options = new()
    {
        Enabled = true,
        Issuer = "https://idp.example.org",
        ClientId = "jellyfin",
        ClientSecret = "secret",
        AllowAnyUser = true,
        ExternalBaseUrl = "https://media.example.org/jf"
    };

    private readonly OidcLoginProtector _protector;
    private readonly OidcLinkStore _links;
    private readonly FakeUserStore _store = new();
    private readonly Mock<ISessionManager> _sessionManager = new();

    public OidcControllerTests()
    {
        _protector = new OidcLoginProtector(_options, TimeProvider.System);
        _links = new OidcLinkStore(_dir);
    }

    public void Dispose()
    {
        _protector.Dispose();
        _links.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private OidcController CreateController(HttpContext httpContext)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new DiscoveryHandler()));
        var client = new OidcClient(_options, factory.Object, NullLogger<OidcClient>.Instance);
        var userService = new OidcUserService(
            _options,
            _links,
            _store.CreateUserManagerMock().Object,
            _sessionManager.Object,
            new FakeCrypto(),
            Mock.Of<INetworkManager>(),
            NullLogger<OidcUserService>.Instance);
        return new OidcController(_options, _protector, client, userService, _sessionManager.Object, Mock.Of<IServerApplicationHost>(), NullLogger<OidcController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static DefaultHttpContext RequestFromEvilHost()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("evil.example.org");
        context.Request.PathBase = "/elsewhere";
        return context;
    }

    private DefaultHttpContext CallbackRequest(string? cookie)
    {
        var context = RequestFromEvilHost();
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = _protector.CookieName + "=" + cookie;
        }

        return context;
    }

    private static string SetCookie(HttpContext context) => string.Join("\n", context.Response.Headers.SetCookie.ToArray());

    [Fact]
    public async Task Login_SetsHardenedCookie_AndUsesConfiguredBaseUrl()
    {
        var context = RequestFromEvilHost();
        var result = await CreateController(context).Login();

        var redirect = Assert.IsType<RedirectResult>(result);
        var query = QueryHelpers.ParseQuery(new Uri(redirect.Url).Query);
        Assert.StartsWith("https://idp.example.org/authorize?", redirect.Url, StringComparison.Ordinal);
        Assert.Equal("https://media.example.org/jf/oidc/callback", query["redirect_uri"]);

        var cookie = SetCookie(context).ToLowerInvariant();
        Assert.StartsWith(_protector.CookieName.ToLowerInvariant() + "=", cookie, StringComparison.Ordinal);
        Assert.StartsWith("__host-", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/;", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=lax", cookie, StringComparison.Ordinal);
        Assert.Contains("max-age=600", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", cookie, StringComparison.Ordinal);
        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString(), StringComparison.Ordinal);

        // The cookie carries the state that was sent to the provider.
        var value = context.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        Assert.NotNull(_protector.Unprotect(value, query["state"]));
    }

    [Fact]
    public async Task Callback_WithoutCookie_IsRejected()
    {
        var context = CallbackRequest(null);
        var result = Assert.IsType<ContentResult>(await CreateController(context).Callback("code", "state", null));
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.DoesNotContain("accessToken", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_StateNotMatchingCookie_IsRejected_AndCookieCleared()
    {
        var login = _protector.Create();
        var context = CallbackRequest(_protector.Protect(login));

        var result = Assert.IsType<ContentResult>(await CreateController(context).Callback("code", "another-state", null));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("expires=thu, 01 jan 1970", SetCookie(context).ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_ProviderError_IsNotReflected()
    {
        var login = _protector.Create();
        var context = CallbackRequest(_protector.Protect(login));

        var result = Assert.IsType<ContentResult>(await CreateController(context).Callback(null, login.State, "<script>alert(1)</script>"));

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        Assert.DoesNotContain("<script>", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", result.Content, StringComparison.Ordinal);
        Assert.Contains("href=\"/jf/web/\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", context.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task Callback_WithoutCode_IsRejected()
    {
        var login = _protector.Create();
        var context = CallbackRequest(_protector.Protect(login));

        var result = Assert.IsType<ContentResult>(await CreateController(context).Callback(null, login.State, null));

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    [Fact]
    public async Task Disabled_EndpointsAreNotFound()
    {
        var controller = new OidcController(
            new OidcOptions(),
            _protector,
            new OidcClient(new OidcOptions(), Mock.Of<IHttpClientFactory>(), NullLogger<OidcClient>.Instance),
            null!,
            _sessionManager.Object,
            Mock.Of<IServerApplicationHost>(),
            NullLogger<OidcController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        Assert.IsType<NotFoundResult>(await controller.Login());
        Assert.IsType<NotFoundResult>(await controller.Callback("c", "s", null));
    }

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/.well-known/openid-configuration" => Discovery,
                "/jwks" => """{"keys":[]}""",
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
