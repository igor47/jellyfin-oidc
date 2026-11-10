using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Model.Branding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Global MVC filter that keeps the fork's changes to upstream controllers out of upstream files:
/// it rejects the password endpoints when password login is disabled, and augments the branding responses.
/// Actions are matched by controller type and <c>nameof</c>, so an upstream rename breaks the build
/// instead of silently re-enabling password login.
/// </summary>
public sealed class OidcMvcFilter : IAsyncActionFilter, IAsyncResultFilter
{
    /// <summary>
    /// The <see cref="UserController"/> actions rejected when password login is disabled.
    /// </summary>
    public static readonly IReadOnlySet<string> PasswordActions = new HashSet<string>(StringComparer.Ordinal)
    {
#pragma warning disable CS0618 // Type or member is obsolete: the legacy routes must be blocked too.
        nameof(UserController.AuthenticateUser),
        nameof(UserController.UpdateUserPasswordLegacy),
#pragma warning restore CS0618
        nameof(UserController.AuthenticateUserByName),
        nameof(UserController.UpdateUserPassword),
        nameof(UserController.ForgotPassword),
        nameof(UserController.ForgotPasswordPin)
    };

    private readonly OidcOptions _options;
    private readonly ILogger<OidcMvcFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcMvcFilter"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger.</param>
    public OidcMvcFilter(OidcOptions options, ILogger<OidcMvcFilter> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Returns whether an action is a password endpoint rejected when password login is disabled.
    /// </summary>
    /// <param name="descriptor">The action descriptor.</param>
    /// <returns><c>true</c> if it is.</returns>
    public static bool IsPasswordAction(ControllerActionDescriptor? descriptor)
        => descriptor is not null
           && descriptor.ControllerTypeInfo.AsType() == typeof(UserController)
           && PasswordActions.Contains(descriptor.MethodInfo.Name);

    /// <inheritdoc />
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (_options.DisablePasswordLogin && IsPasswordAction(context.ActionDescriptor as ControllerActionDescriptor))
        {
            _logger.LogInformation(
                "Rejected {Action} from {IP}: password login is disabled",
                ((ControllerActionDescriptor)context.ActionDescriptor).MethodInfo.Name,
                context.HttpContext.Connection.RemoteIpAddress);
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Password login is disabled on this server."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return Task.CompletedTask;
        }

        return next();
    }

    /// <inheritdoc />
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult result
            && context.ActionDescriptor is ControllerActionDescriptor descriptor
            && descriptor.ControllerTypeInfo.AsType() == typeof(BrandingController))
        {
            if (string.Equals(descriptor.MethodInfo.Name, nameof(BrandingController.GetBrandingOptions), StringComparison.Ordinal)
                && result.Value is BrandingOptionsDto dto)
            {
                // The controller builds a fresh DTO per request; the stored branding is not touched.
                dto.LoginDisclaimer = OidcBranding.AugmentDisclaimer(dto.LoginDisclaimer, _options, OidcUrls.LoginPath(_options, context.HttpContext.Request));
                dto.CustomCss = OidcBranding.AugmentCss(dto.CustomCss, _options);
            }
            else if (string.Equals(descriptor.MethodInfo.Name, nameof(BrandingController.GetBrandingCss), StringComparison.Ordinal)
                     && result.Value is string css)
            {
                result.Value = OidcBranding.AugmentCss(css, _options) ?? string.Empty;
            }
        }

        return next();
    }
}
