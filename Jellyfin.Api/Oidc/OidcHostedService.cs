using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Branding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// Logs the OIDC configuration at startup and guards stored branding.
/// The dashboard's branding page loads <c>/Branding/Configuration</c> (which carries the fork's additions)
/// and saves it back. This removes the additions before the branding is stored, so stored branding stays
/// exactly what the operator wrote and nothing is duplicated.
/// </summary>
public sealed class OidcHostedService : IHostedService
{
    private readonly IServerConfigurationManager _configurationManager;
    private readonly OidcOptions _options;
    private readonly ILogger<OidcHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcHostedService"/> class.
    /// </summary>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger.</param>
    public OidcHostedService(IServerConfigurationManager configurationManager, OidcOptions options, ILogger<OidcHostedService> logger)
    {
        _configurationManager = configurationManager;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Removes the fork's additions from branding options in place.
    /// </summary>
    /// <param name="options">The branding options.</param>
    public static void Clean(BrandingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.LoginDisclaimer = OidcBranding.Strip(options.LoginDisclaimer, OidcBranding.HtmlBegin, OidcBranding.HtmlEnd);
        options.CustomCss = OidcBranding.Strip(options.CustomCss, OidcBranding.CssBegin, OidcBranding.CssEnd);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _configurationManager.NamedConfigurationUpdating += OnUpdating;

        if (_options.Enabled)
        {
            _logger.LogInformation(
                "OpenID Connect login enabled (issuer {Issuer}, client {ClientId}, auto-create {AutoCreate}, link existing by username {LinkExisting})",
                _options.Issuer,
                _options.ClientId,
                _options.AutoCreateUsers,
                _options.LinkExistingByUsername);
            _logger.LogInformation("OIDC redirect URI: {RedirectUri}", OidcUrls.CallbackUrl(_options));
            if (_options.AllowAnyUser && _options.AutoCreateUsers && _options.AllowedGroups.Count == 0)
            {
                _logger.LogWarning("JELLYFIN_OIDC_ALLOW_ANY_USER is set: anyone the identity provider authenticates gets a Jellyfin account");
            }

            if (_options.Issuer.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("The OIDC issuer uses plain http; use https outside of testing");
            }
        }

        if (_options.DisablePasswordLogin)
        {
            _logger.LogWarning("Password login is disabled (JELLYFIN_OIDC_DISABLE_PASSWORD_LOGIN). Unset it and restart to log in with a password");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _configurationManager.NamedConfigurationUpdating -= OnUpdating;
        return Task.CompletedTask;
    }

    private void OnUpdating(object? sender, ConfigurationUpdateEventArgs e)
    {
        if (e.NewConfiguration is BrandingOptions options)
        {
            Clean(options);
        }
    }
}
