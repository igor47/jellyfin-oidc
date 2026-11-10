using System;
using System.Threading.Tasks;
using Jellyfin.Api.Oidc;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Authentication;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public class OidcAuthenticationProviderTests
{
    private readonly OidcAuthenticationProvider _provider;

    public OidcAuthenticationProviderTests()
    {
        _provider = new OidcAuthenticationProvider(new FakeCrypto());
    }

    private static User OidcUser() => new("alice", OidcAuthenticationProvider.ProviderId, "reset");

    [Fact]
    public void DefaultProviderId_MatchesJellyfin()
        => Assert.Equal(OidcAuthenticationProvider.DefaultProviderId, typeof(DefaultAuthenticationProvider).FullName);

    [Fact]
    public async Task Authenticate_AlwaysFails()
    {
        await Assert.ThrowsAsync<AuthenticationException>(() => _provider.Authenticate("alice", string.Empty));
        await Assert.ThrowsAsync<AuthenticationException>(() => _provider.Authenticate("alice", "pw", OidcUser()));
    }

    [Fact]
    public async Task ChangePassword_ByAdmin_HandsUserBackToDefaultProvider()
    {
        var user = OidcUser();
        await _provider.ChangePassword(user, "new-password");
        Assert.Equal(OidcAuthenticationProvider.DefaultProviderId, user.AuthenticationProviderId);
        Assert.NotNull(user.Password);
    }

    [Fact]
    public async Task ChangePassword_Empty_KeepsUserOidcOnlyWithRandomPassword()
    {
        var user = OidcUser();
        await _provider.ChangePassword(user, string.Empty);
        Assert.Equal(OidcAuthenticationProvider.ProviderId, user.AuthenticationProviderId);

        // Never null: an empty password would let anyone in if the provider ever became the default one.
        Assert.False(string.IsNullOrEmpty(user.Password));
    }
}
