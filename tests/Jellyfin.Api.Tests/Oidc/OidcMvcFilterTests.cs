using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Oidc;
using MediaBrowser.Model.Branding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public class OidcMvcFilterTests
{
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
        var filter = new OidcMvcFilter(options, NullLogger<OidcMvcFilter>.Instance);
        var context = ExecutingContext(descriptor);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object()));
        });
        return (nextCalled, context.Result);
    }

    public static TheoryData<string> PasswordActions() => new(OidcMvcFilter.PasswordActions);

    [Fact]
    public void PasswordActions_CoverAllPasswordEndpoints()
    {
        // Every action of UserController that takes or changes a password must be in the list.
        var expected = new[]
        {
            "AuthenticateUser", "AuthenticateUserByName", "UpdateUserPassword", "UpdateUserPasswordLegacy", "ForgotPassword", "ForgotPasswordPin"
        };
        Assert.Equal(expected.OrderBy(x => x), OidcMvcFilter.PasswordActions.OrderBy(x => x));
        Assert.All(expected, name => Assert.NotNull(typeof(UserController).GetMethod(name)));
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
    public async Task QuickConnectController_PasswordDisabled_Runs()
    {
        var (nextCalled, _) = await RunAction(new OidcOptions { DisablePasswordLogin = true }, Descriptor<QuickConnectController>(nameof(QuickConnectController.InitiateQuickConnect)));
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task BrandingResult_IsAugmented()
    {
        var options = new OidcOptions { Enabled = true, ProviderName = "IdP", DisablePasswordLogin = true };
        var filter = new OidcMvcFilter(options, NullLogger<OidcMvcFilter>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = "/jf";
        var actionContext = new ActionContext(httpContext, new RouteData(), Descriptor<BrandingController>(nameof(BrandingController.GetBrandingOptions)));
        var dto = new BrandingOptionsDto { LoginDisclaimer = "hi", CustomCss = ".a{}", SplashscreenEnabled = true };
        var context = new ResultExecutingContext(actionContext, new List<IFilterMetadata>(), new ObjectResult(dto), new object());

        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), context.Result, new object())));

        var served = Assert.IsType<BrandingOptionsDto>(((ObjectResult)context.Result).Value);
        Assert.Contains("action=\"/jf/oidc/login\"", served.LoginDisclaimer, System.StringComparison.Ordinal);
        Assert.Contains("manualLoginForm", served.CustomCss, System.StringComparison.Ordinal);
        Assert.True(served.SplashscreenEnabled);
    }

    [Fact]
    public async Task BrandingCssResult_IsAugmented()
    {
        var options = new OidcOptions { DisablePasswordLogin = true };
        var filter = new OidcMvcFilter(options, NullLogger<OidcMvcFilter>.Instance);
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), Descriptor<BrandingController>(nameof(BrandingController.GetBrandingCss)));
        var context = new ResultExecutingContext(actionContext, new List<IFilterMetadata>(), new ObjectResult(string.Empty), new object());

        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), context.Result, new object())));

        Assert.Contains("manualLoginForm", (string)((ObjectResult)context.Result).Value!, System.StringComparison.Ordinal);
    }
}
