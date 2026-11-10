using System;
using System.Threading.Tasks;
using Jellyfin.Api.Oidc;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.Cryptography;
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
    public async Task ChangePassword_Empty_KeepsUserOidcOnly()
    {
        var user = OidcUser();
        await _provider.ChangePassword(user, string.Empty);
        Assert.Equal(OidcAuthenticationProvider.ProviderId, user.AuthenticationProviderId);
        Assert.Null(user.Password);
    }

    private sealed class FakeCrypto : ICryptoProvider
    {
        public string DefaultHashMethod => "PBKDF2-SHA512";

        public PasswordHash CreatePasswordHash(ReadOnlySpan<char> password) => new(DefaultHashMethod, new byte[] { 1, 2, 3 });

        public bool Verify(PasswordHash hash, ReadOnlySpan<char> password) => false;

        public byte[] GenerateSalt() => new byte[] { 4 };

        public byte[] GenerateSalt(int length) => new byte[length];
    }
}
