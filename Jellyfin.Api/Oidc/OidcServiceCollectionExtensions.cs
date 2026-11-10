using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Registers the OpenID Connect services. This is the fork's single hook into server startup.
/// </summary>
public static class OidcServiceCollectionExtensions
{
    // MediaBrowser.Controller.Extensions.ConfigurationExtensions.AddressOverrideKey.
    private const string AddressOverrideKey = MediaBrowser.Controller.Extensions.ConfigurationExtensions.AddressOverrideKey;

    /// <summary>
    /// Reads <c>JELLYFIN_OIDC_*</c> and registers the OIDC services. Invalid configuration throws,
    /// which stops the server from starting.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (for Jellyfin's published server URL).</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="OidcConfigurationException">The configuration is invalid.</exception>
    public static IServiceCollection AddJellyfinOidc(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddJellyfinOidc(OidcOptions.FromEnvironment(configuration[AddressOverrideKey]));
    }

    /// <summary>
    /// Registers the OIDC services with the given options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The options.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddJellyfinOidc(this IServiceCollection services, OidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OidcLoginProtector>();
        services.AddSingleton(sp => new OidcLinkStore(sp.GetRequiredService<IApplicationPaths>().ConfigurationDirectoryPath));
        services.AddSingleton<OidcClient>();
        services.AddSingleton<OidcUserService>();

        // Always registered, so users linked earlier keep failing password login even while OIDC is switched off.
        services.AddSingleton<IAuthenticationProvider, OidcAuthenticationProvider>();

        services.AddSingleton<OidcMvcFilter>();
        services.Configure<MvcOptions>(o => o.Filters.AddService<OidcMvcFilter>());
        services.AddHostedService<OidcHostedService>();
        return services;
    }
}
