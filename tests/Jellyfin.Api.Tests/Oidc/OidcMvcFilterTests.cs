using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Oidc;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Branding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public class OidcMvcFilterTests
{
    // Actions that bind a password but are not password logins, reviewed by hand: administrators creating a user with
    // an initial password, the first-run wizard setting the first administrator's password, and an administrator
    // storing the credentials of a Live TV listings provider.
    private static readonly string[] _reviewedPasswordActions =
    [
        "UserController." + nameof(UserController.CreateUserByName),
        "StartupController." + nameof(StartupController.UpdateStartupUser),
        "LiveTvController." + nameof(LiveTvController.AddListingProvider)
    ];

    private static readonly Regex _passwordName = new("^(pw|pin|password)$|password|^(current|new)pw$", RegexOptions.IgnoreCase);

    private static ControllerActionDescriptor Descriptor<T>(string method)
        => new()
        {
            ControllerTypeInfo = typeof(T).GetTypeInfo(),
            MethodInfo = typeof(T).GetMethod(method)!,
            ControllerName = typeof(T).Name,
            ActionName = method
        };

    private static ActionExecutingContext ExecutingContext(ActionDescriptor descriptor)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    }

    private static async Task<(bool NextCalled, IActionResult? Result)> RunAction(OidcOptions options, ActionDescriptor descriptor)
    {
        var filter = CreateFilter(options);
        var context = ExecutingContext(descriptor);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object()));
        });
        return (nextCalled, context.Result);
    }

    private static OidcMvcFilter CreateFilter(OidcOptions options, OidcUserService? userService = null)
        => new(options, userService ?? CreateUserService(options, Mock.Of<IUserManager>()), NullLogger<OidcMvcFilter>.Instance);

    private static OidcUserService CreateUserService(OidcOptions options, IUserManager userManager)
    {
        return new OidcUserService(
            options,
            new OidcLinkStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oidc-filter-" + Guid.NewGuid().ToString("N"))),
            userManager,
            Mock.Of<ISessionManager>(),
            new FakeCrypto(),
            Mock.Of<INetworkManager>(),
            NullLogger<OidcUserService>.Instance);
    }

    private static bool BindsPassword(ParameterInfo parameter)
    {
        if (_passwordName.IsMatch(parameter.Name ?? string.Empty))
        {
            return true;
        }

        var type = parameter.ParameterType;
        return type.Assembly == typeof(UserController).Assembly
               && type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => _passwordName.IsMatch(p.Name));
    }

    private static List<string> ActionsBindingPasswords()
        => typeof(UserController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any() && m.GetParameters().Any(BindsPassword))
                .Select(m => t.Name + "." + m.Name))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    public static TheoryData<string> PasswordActions() => new(OidcMvcFilter.PasswordActions);

    [Fact]
    public void EveryActionBindingAPassword_IsBlockedOrReviewed()
    {
        var found = ActionsBindingPasswords();
        var blocked = OidcMvcFilter.PasswordActions.Select(a => "UserController." + a).ToList();

        // A new upstream endpoint that takes a password, PIN or password DTO fails this test until it is classified.
        Assert.Empty(found.Except(blocked).Except(_reviewedPasswordActions));

        // And the detector does see every blocked action, so the check above is not vacuous.
        Assert.Empty(blocked.Except(found));
        Assert.All(_reviewedPasswordActions, a => Assert.Contains(a, found));
    }

    [Theory]
    [MemberData(nameof(PasswordActions))]
    public async Task PasswordAction_Disabled_IsRejected(string action)
    {
        var (nextCalled, result) = await RunAction(new OidcOptions { DisablePasswordLogin = true }, Descriptor<UserController>(action));

        Assert.False(nextCalled);
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Theory]
    [MemberData(nameof(PasswordActions))]
    public async Task PasswordAction_Enabled_Runs(string action)
    {
        var (nextCalled, result) = await RunAction(new OidcOptions { Enabled = true }, Descriptor<UserController>(action));
        Assert.True(nextCalled);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(nameof(UserController.AuthenticateWithQuickConnect))]
    [InlineData(nameof(UserController.GetCurrentUser))]
    [InlineData(nameof(UserController.CreateUserByName))]
    public async Task OtherUserActions_PasswordDisabled_Run(string action)
    {
        var (nextCalled, _) = await RunAction(new OidcOptions { DisablePasswordLogin = true }, Descriptor<UserController>(action));
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task PasswordLogin_ForUserBeingCreatedByOidc_IsRejected()
    {
        // Jellyfin creates users without a password; between creation and the random password, an empty password
        // must not log in, even with password login enabled.
        var options = new OidcOptions { Enabled = true, AutoCreateUsers = true };
        var store = new FakeUserStore();
        var userService = CreateUserService(options, store.CreateUserManagerMock().Object);
        var filter = CreateFilter(options, userService);
        IActionResult? duringCreation = null;
        IActionResult? otherUser = null;
        store.OnCreate = async _ =>
        {
            duringCreation = await RunLogin(filter, "ALICE");
            otherUser = await RunLogin(filter, "bob");
        };

        await userService.ResolveAsync(new OidcIdentity("https://idp", "sub", "alice", []), "1.2.3.4");

        Assert.IsType<UnauthorizedResult>(duringCreation);
        Assert.Null(otherUser);
        Assert.Null(await RunLogin(filter, "alice"));
    }

    private static async Task<IActionResult?> RunLogin(OidcMvcFilter filter, string username)
    {
        var context = ExecutingContext(Descriptor<UserController>(nameof(UserController.AuthenticateUserByName)));
        context.ActionArguments["request"] = new Jellyfin.Api.Models.UserDtos.AuthenticateUserByName { Username = username, Pw = string.Empty };
        await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object())));
        return context.Result;
    }

    [Fact]
    public async Task QuickConnectController_PasswordDisabled_Runs()
    {
        var (nextCalled, _) = await RunAction(new OidcOptions { DisablePasswordLogin = true }, Descriptor<QuickConnectController>(nameof(QuickConnectController.InitiateQuickConnect)));
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task BrandingResult_IsAugmented()
    {
        var options = new OidcOptions { Enabled = true, ProviderName = "IdP", DisablePasswordLogin = true, ExternalBaseUrl = "https://media.example.org/jf" };
        var filter = CreateFilter(options);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = "/ignored";
        httpContext.Request.Host = new HostString("evil.example.org");
        var actionContext = new ActionContext(httpContext, new RouteData(), Descriptor<BrandingController>(nameof(BrandingController.GetBrandingOptions)));
        var dto = new BrandingOptionsDto { LoginDisclaimer = "hi", CustomCss = ".a{}", SplashscreenEnabled = true };
        var context = new ResultExecutingContext(actionContext, new List<IFilterMetadata>(), new ObjectResult(dto), new object());

        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), context.Result, new object())));

        var served = Assert.IsType<BrandingOptionsDto>(((ObjectResult)context.Result).Value);
        Assert.Contains("action=\"/jf/oidc/login\"", served.LoginDisclaimer, StringComparison.Ordinal);
        Assert.Contains("manualLoginForm", served.CustomCss, StringComparison.Ordinal);
        Assert.True(served.SplashscreenEnabled);
    }

    [Fact]
    public async Task BrandingCssResult_IsAugmented()
    {
        var options = new OidcOptions { DisablePasswordLogin = true };
        var filter = CreateFilter(options);
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), Descriptor<BrandingController>(nameof(BrandingController.GetBrandingCss)));
        var context = new ResultExecutingContext(actionContext, new List<IFilterMetadata>(), new ObjectResult(string.Empty), new object());

        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), context.Result, new object())));

        Assert.Contains("manualLoginForm", (string)((ObjectResult)context.Result).Value!, StringComparison.Ordinal);
    }
}
