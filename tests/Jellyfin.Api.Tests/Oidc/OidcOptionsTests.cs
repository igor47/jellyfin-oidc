using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Api.Oidc;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public class OidcOptionsTests
{
    private static Dictionary<string, string?> ValidEnv() => new()
    {
        ["JELLYFIN_OIDC_ENABLED"] = "true",
        ["JELLYFIN_OIDC_ISSUER"] = "https://auth.example.org/application/o/jellyfin/",
        ["JELLYFIN_OIDC_CLIENT_ID"] = "jellyfin",
        ["JELLYFIN_OIDC_CLIENT_SECRET"] = "s3cret"
    };

    private static OidcOptions Parse(Dictionary<string, string?> env, Func<string, string>? readFile = null)
        => OidcOptions.Parse(name => env.GetValueOrDefault(name), readFile ?? (_ => throw new FileNotFoundException()));

    [Fact]
    public void Parse_Empty_IsDisabledWithDefaults()
    {
        var options = Parse(new());
        Assert.False(options.Enabled);
        Assert.False(options.DisablePasswordLogin);
    }

    [Fact]
    public void Parse_DisabledIgnoresOtherSettings_ButHonoursPasswordSwitch()
    {
        var options = Parse(new()
        {
            ["JELLYFIN_OIDC_ENABLED"] = "false",
            ["JELLYFIN_OIDC_ISSUER"] = "not a url",
            ["JELLYFIN_OIDC_DISABLE_PASSWORD_LOGIN"] = "true"
        });
        Assert.False(options.Enabled);
        Assert.True(options.DisablePasswordLogin);
    }

    [Fact]
    public void Parse_Valid_UsesDefaults()
    {
        var options = Parse(ValidEnv());
        Assert.True(options.Enabled);
        Assert.Equal("https://auth.example.org/application/o/jellyfin/", options.Issuer);
        Assert.Equal("jellyfin", options.ClientId);
        Assert.Equal("s3cret", options.ClientSecret);
        Assert.Equal(new[] { "openid", "profile", "email" }, options.Scopes);
        Assert.Equal("preferred_username", options.UsernameClaim);
        Assert.Equal("groups", options.GroupsClaim);
        Assert.Empty(options.AllowedGroups);
        Assert.Empty(options.AdminGroups);
        Assert.True(options.AutoCreateUsers);
        Assert.False(options.LinkExistingByUsername);
        Assert.Equal("SSO", options.ProviderName);
        Assert.False(options.DisablePasswordLogin);
        Assert.Null(options.RedirectBaseUrl);
    }

    [Fact]
    public void Parse_AllSettings()
    {
        var env = ValidEnv();
        env["JELLYFIN_OIDC_SCOPES"] = "openid  groups profile";
        env["JELLYFIN_OIDC_USERNAME_CLAIM"] = "nickname";
        env["JELLYFIN_OIDC_GROUPS_CLAIM"] = "roles";
        env["JELLYFIN_OIDC_ALLOWED_GROUPS"] = " media , admins,,media";
        env["JELLYFIN_OIDC_ADMIN_GROUPS"] = "admins";
        env["JELLYFIN_OIDC_AUTO_CREATE_USERS"] = "no";
        env["JELLYFIN_OIDC_LINK_EXISTING_BY_USERNAME"] = "1";
        env["JELLYFIN_OIDC_PROVIDER_NAME"] = "Authentik";
        env["JELLYFIN_OIDC_DISABLE_PASSWORD_LOGIN"] = "TRUE";
        env["JELLYFIN_OIDC_REDIRECT_BASE_URL"] = "https://media.example.org/jf/";

        var options = Parse(env);
        Assert.Equal(new[] { "openid", "groups", "profile" }, options.Scopes);
        Assert.Equal("nickname", options.UsernameClaim);
        Assert.Equal("roles", options.GroupsClaim);
        Assert.Equal(new[] { "media", "admins" }, options.AllowedGroups);
        Assert.Equal(new[] { "admins" }, options.AdminGroups);
        Assert.False(options.AutoCreateUsers);
        Assert.True(options.LinkExistingByUsername);
        Assert.Equal("Authentik", options.ProviderName);
        Assert.True(options.DisablePasswordLogin);
        Assert.Equal("https://media.example.org/jf", options.RedirectBaseUrl);
    }

    [Theory]
    [InlineData("JELLYFIN_OIDC_ISSUER")]
    [InlineData("JELLYFIN_OIDC_CLIENT_ID")]
    [InlineData("JELLYFIN_OIDC_CLIENT_SECRET")]
    public void Parse_MissingRequired_Throws(string name)
    {
        var env = ValidEnv();
        env[name] = "   ";
        Assert.Throws<OidcConfigurationException>(() => Parse(env));
    }

    [Theory]
    [InlineData("JELLYFIN_OIDC_ENABLED", "maybe")]
    [InlineData("JELLYFIN_OIDC_AUTO_CREATE_USERS", "y")]
    [InlineData("JELLYFIN_OIDC_ISSUER", "auth.example.org")]
    [InlineData("JELLYFIN_OIDC_ISSUER", "ftp://auth.example.org")]
    [InlineData("JELLYFIN_OIDC_ISSUER", "https://auth.example.org/?x=1")]
    [InlineData("JELLYFIN_OIDC_SCOPES", "profile email")]
    [InlineData("JELLYFIN_OIDC_REDIRECT_BASE_URL", "/relative")]
    [InlineData("JELLYFIN_OIDC_REDIRECT_BASE_URL", "https://user:pw@media.example.org")]
    [InlineData("JELLYFIN_OIDC_REDIRECT_BASE_URL", "https://media.example.org/#x")]
    public void Parse_Invalid_Throws(string name, string value)
    {
        var env = ValidEnv();
        env[name] = value;
        Assert.Throws<OidcConfigurationException>(() => Parse(env));
    }

    [Fact]
    public void Parse_SecretFile_IsReadAndTrimmed()
    {
        var env = ValidEnv();
        env.Remove("JELLYFIN_OIDC_CLIENT_SECRET");
        env["JELLYFIN_OIDC_CLIENT_SECRET_FILE"] = "/run/secrets/oidc";
        var options = Parse(env, path => path == "/run/secrets/oidc" ? "from-file\n" : throw new FileNotFoundException());
        Assert.Equal("from-file", options.ClientSecret);
    }

    [Fact]
    public void Parse_SecretAndSecretFile_Throws()
    {
        var env = ValidEnv();
        env["JELLYFIN_OIDC_CLIENT_SECRET_FILE"] = "/run/secrets/oidc";
        Assert.Throws<OidcConfigurationException>(() => Parse(env, _ => "x"));
    }

    [Fact]
    public void Parse_UnreadableSecretFile_Throws()
    {
        var env = ValidEnv();
        env.Remove("JELLYFIN_OIDC_CLIENT_SECRET");
        env["JELLYFIN_OIDC_CLIENT_SECRET_FILE"] = "/nope";
        Assert.Throws<OidcConfigurationException>(() => Parse(env));
    }

    [Fact]
    public void Parse_EmptySecretFile_Throws()
    {
        var env = ValidEnv();
        env.Remove("JELLYFIN_OIDC_CLIENT_SECRET");
        env["JELLYFIN_OIDC_CLIENT_SECRET_FILE"] = "/run/secrets/oidc";
        Assert.Throws<OidcConfigurationException>(() => Parse(env, _ => " \n"));
    }

    [Theory]
    [InlineData("JELLYFIN_OIDC_CLIENT_SECRET", "***")]
    [InlineData("jellyfin_oidc_client_secret", "***")]
    [InlineData("JELLYFIN_SOME_PASSWORD", "***")]
    [InlineData("JELLYFIN_OIDC_CLIENT_SECRET_FILE", "value")]
    [InlineData("JELLYFIN_OIDC_ISSUER", "value")]
    [InlineData("JELLYFIN_CONFIG_DIR", "value")]
    public void RedactForLog(string name, string expected)
        => Assert.Equal(expected, OidcOptions.RedactForLog(name, "value"));
}
