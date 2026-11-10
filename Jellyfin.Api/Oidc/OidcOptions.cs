using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Api.Oidc;

/// <summary>
/// OpenID Connect settings, read once at startup from <c>JELLYFIN_OIDC_*</c> environment variables.
/// </summary>
public sealed class OidcOptions
{
    /// <summary>
    /// The prefix shared by every environment variable read by this class.
    /// </summary>
    public const string Prefix = "JELLYFIN_OIDC_";

    /// <summary>
    /// Gets a value indicating whether OpenID Connect login is enabled.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the issuer URL as configured. The discovery document's <c>issuer</c> must match it, ignoring a trailing slash.
    /// </summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>
    /// Gets the client id.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the client secret.
    /// </summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>
    /// Gets the requested scopes.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = ["openid", "profile", "email"];

    /// <summary>
    /// Gets the claim used as the Jellyfin username.
    /// </summary>
    public string UsernameClaim { get; init; } = "preferred_username";

    /// <summary>
    /// Gets the claim holding group names.
    /// </summary>
    public string GroupsClaim { get; init; } = "groups";

    /// <summary>
    /// Gets the groups allowed to log in; empty means everyone.
    /// </summary>
    public IReadOnlyList<string> AllowedGroups { get; init; } = [];

    /// <summary>
    /// Gets the groups whose members are administrators; empty means administrator status is not managed.
    /// </summary>
    public IReadOnlyList<string> AdminGroups { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether unknown users are created on first login.
    /// </summary>
    public bool AutoCreateUsers { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether an existing, unlinked user with the same name is adopted on first login.
    /// </summary>
    public bool LinkExistingByUsername { get; init; }

    /// <summary>
    /// Gets the provider name shown on the login button.
    /// </summary>
    public string ProviderName { get; init; } = "SSO";

    /// <summary>
    /// Gets a value indicating whether username/password authentication is rejected.
    /// </summary>
    public bool DisablePasswordLogin { get; init; }

    /// <summary>
    /// Gets the external base URL (no trailing slash), or <c>null</c> to derive it per request.
    /// </summary>
    public string? RedirectBaseUrl { get; init; }

    /// <summary>
    /// Reads and validates the options from the process environment.
    /// </summary>
    /// <returns>The options.</returns>
    public static OidcOptions FromEnvironment() => Parse(Environment.GetEnvironmentVariable, File.ReadAllText);

    /// <summary>
    /// Reads and validates the options.
    /// </summary>
    /// <param name="getVariable">Returns the value of an environment variable, or <c>null</c>.</param>
    /// <param name="readFile">Reads a whole file (used for <c>CLIENT_SECRET_FILE</c>).</param>
    /// <returns>The options.</returns>
    /// <exception cref="OidcConfigurationException">The configuration is invalid.</exception>
    public static OidcOptions Parse(Func<string, string?> getVariable, Func<string, string> readFile)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        ArgumentNullException.ThrowIfNull(readFile);

        string? Get(string name)
        {
            var value = getVariable(Prefix + name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        bool GetBool(string name, bool defaultValue)
        {
            var value = Get(name);
            if (value is null)
            {
                return defaultValue;
            }

            return value.ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" => false,
                _ => throw new OidcConfigurationException(string.Format(CultureInfo.InvariantCulture, "{0}{1} must be true or false, got '{2}'.", Prefix, name, value))
            };
        }

        var enabled = GetBool("ENABLED", false);
        var disablePasswordLogin = GetBool("DISABLE_PASSWORD_LOGIN", false);
        if (!enabled)
        {
            // Password login can be switched off on its own (Quick Connect and API keys keep working),
            // but nothing else is read or validated while OIDC is off.
            return new OidcOptions { Enabled = false, DisablePasswordLogin = disablePasswordLogin };
        }

        var issuer = Get("ISSUER") ?? throw new OidcConfigurationException(Prefix + "ISSUER is required when " + Prefix + "ENABLED is true.");
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            || (issuerUri.Scheme != Uri.UriSchemeHttps && issuerUri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(issuerUri.Query)
            || !string.IsNullOrEmpty(issuerUri.Fragment))
        {
            throw new OidcConfigurationException(Prefix + "ISSUER must be an absolute http(s) URL without query or fragment.");
        }

        var clientId = Get("CLIENT_ID") ?? throw new OidcConfigurationException(Prefix + "CLIENT_ID is required when " + Prefix + "ENABLED is true.");

        var secret = Get("CLIENT_SECRET");
        var secretFile = Get("CLIENT_SECRET_FILE");
        if (secret is not null && secretFile is not null)
        {
            throw new OidcConfigurationException("Set only one of " + Prefix + "CLIENT_SECRET and " + Prefix + "CLIENT_SECRET_FILE.");
        }

        if (secretFile is not null)
        {
            try
            {
                secret = readFile(secretFile).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new OidcConfigurationException(Prefix + "CLIENT_SECRET_FILE could not be read: " + ex.Message, ex);
            }
        }

        if (string.IsNullOrEmpty(secret))
        {
            throw new OidcConfigurationException(Prefix + "CLIENT_SECRET or " + Prefix + "CLIENT_SECRET_FILE is required when " + Prefix + "ENABLED is true.");
        }

        var scopes = (Get("SCOPES") ?? "openid profile email")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!scopes.Contains("openid", StringComparer.Ordinal))
        {
            throw new OidcConfigurationException(Prefix + "SCOPES must include 'openid'.");
        }

        string? redirectBase = Get("REDIRECT_BASE_URL");
        if (redirectBase is not null)
        {
            if (!Uri.TryCreate(redirectBase, UriKind.Absolute, out var redirectUri)
                || (redirectUri.Scheme != Uri.UriSchemeHttps && redirectUri.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(redirectUri.Query)
                || !string.IsNullOrEmpty(redirectUri.Fragment)
                || !string.IsNullOrEmpty(redirectUri.UserInfo))
            {
                throw new OidcConfigurationException(Prefix + "REDIRECT_BASE_URL must be an absolute http(s) URL without query, fragment or credentials.");
            }

            redirectBase = redirectBase.TrimEnd('/');
        }

        return new OidcOptions
        {
            Enabled = true,
            Issuer = issuer,
            ClientId = clientId,
            ClientSecret = secret,
            Scopes = scopes,
            UsernameClaim = Get("USERNAME_CLAIM") ?? "preferred_username",
            GroupsClaim = Get("GROUPS_CLAIM") ?? "groups",
            AllowedGroups = ParseList(Get("ALLOWED_GROUPS")),
            AdminGroups = ParseList(Get("ADMIN_GROUPS")),
            AutoCreateUsers = GetBool("AUTO_CREATE_USERS", true),
            LinkExistingByUsername = GetBool("LINK_EXISTING_BY_USERNAME", false),
            ProviderName = Get("PROVIDER_NAME") ?? "SSO",
            DisablePasswordLogin = disablePasswordLogin,
            RedirectBaseUrl = redirectBase
        };
    }

    /// <summary>
    /// Hides secret values in the environment variable dump Jellyfin logs at startup. Variables whose name
    /// contains SECRET, PASSWORD or TOKEN are replaced, except <c>*_FILE</c> variables, which hold paths.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The value to log.</returns>
    public static object RedactForLog(string name, object value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.EndsWith("_FILE", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
               || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
               || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
            ? "***"
            : value;
    }

    private static string[] ParseList(string? value)
        => value is null
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
}
