using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Oidc;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public sealed class OidcUserServiceTests : IDisposable
{
    private const string Issuer = "https://idp.example.org";
    private const string DefaultProvider = FakeUserStore.DefaultProvider;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oidc-users-" + Guid.NewGuid().ToString("N"));
    private readonly FakeUserStore _store = new();
    private readonly Mock<IUserManager> _userManager;
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<INetworkManager> _networkManager = new();
    private readonly OidcLinkStore _links;

    public OidcUserServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _links = new OidcLinkStore(_dir);

        _userManager = _store.CreateUserManagerMock();
        _sessionManager.Setup(m => m.RevokeUserTokens(It.IsAny<Guid>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _networkManager.Setup(m => m.IsInLocalNetwork(It.IsAny<string>())).Returns(false);
    }

    public void Dispose()
    {
        _links.Dispose();
        Directory.Delete(_dir, true);
    }

    private OidcUserService CreateService(bool link = false, bool autoCreate = true, string[]? allowed = null, string[]? admins = null)
    {
        var options = new OidcOptions
        {
            Enabled = true,
            Issuer = Issuer,
            ClientId = "c",
            ClientSecret = "s",
            LinkExistingByUsername = link,
            AutoCreateUsers = autoCreate,
            AllowedGroups = allowed ?? [],
            AdminGroups = admins ?? []
        };
        return new OidcUserService(options, _links, _userManager.Object, _sessionManager.Object, new FakeCrypto(), _networkManager.Object, NullLogger<OidcUserService>.Instance);
    }

    private static OidcIdentity Identity(string sub, string? username, params string[] groups) => new(Issuer, sub, username, groups);

    private User Stored(Guid id) => _store.Get(id) ?? throw new InvalidOperationException("not stored");

    private void VerifyRevoked(Guid id, Func<Times> times) => _sessionManager.Verify(m => m.RevokeUserTokens(id, It.IsAny<string>()), times);

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("  Alice Smith ", "Alice Smith")]
    [InlineData("alice@example.org", "alice@example.org")]
    [InlineData("o'brien-jr.+x_y", "o'brien-jr.+x_y")]
    [InlineData("al/ice", "al_ice")]
    [InlineData("a<script>", "a_script_")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("Jürgen", "Jürgen")]
    [InlineData("用户", "用户")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(".", null)]
    [InlineData("..", null)]
    [InlineData("///", null)]
    public void SanitizeUsername(string? claim, string? expected)
        => Assert.Equal(expected, OidcUserService.SanitizeUsername(claim));

    [Fact]
    public void SanitizeUsername_TooLong_IsRejected()
    {
        Assert.Null(OidcUserService.SanitizeUsername(new string('a', OidcUserService.MaxUsernameLength + 1)));
        Assert.NotNull(OidcUserService.SanitizeUsername(new string('a', OidcUserService.MaxUsernameLength)));
    }

    [Fact]
    public async Task NewUser_IsPersistedLinkedAndPasswordLocked()
    {
        var result = await CreateService().ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        var user = Stored(result.User!.Id);
        Assert.Equal("alice", user.Username);
        Assert.Equal(OidcAuthenticationProvider.ProviderId, user.AuthenticationProviderId);
        Assert.False(string.IsNullOrEmpty(user.Password));
        Assert.True(user.HasPermission(PermissionKind.EnableAllFolders));
        Assert.False(user.HasPermission(PermissionKind.IsAdministrator));
        Assert.Equal(user.Id, await _links.FindUserAsync(Issuer, "sub-1"));
        Assert.Single(_store.All);
    }

    [Fact]
    public async Task NewUser_GetsAPasswordBeforeTheProviderSwitch()
    {
        await CreateService().ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");
        _userManager.Verify(m => m.ChangePassword(It.IsAny<Guid>(), It.Is<string>(p => p.Length >= 32)), Times.Once);
    }

    [Fact]
    public async Task NewUser_NameIsMarkedAsBeingCreated_UntilLocked()
    {
        var service = CreateService();
        bool? duringCreation = null;
        _store.OnCreate = name =>
        {
            duringCreation = service.IsBeingCreated("ALICE") && service.IsAnyUserBeingCreated;
            return Task.CompletedTask;
        };

        await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        Assert.True(duringCreation);
        Assert.False(service.IsBeingCreated("alice"));
        Assert.False(service.IsAnyUserBeingCreated);
    }

    [Fact]
    public async Task LinkedUser_IsFoundBySubject_EvenIfUsernameClaimChanges()
    {
        var service = CreateService();
        await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        var result = await service.ResolveAsync(Identity("sub-1", "alice-renamed"), "1.2.3.4");

        Assert.Equal("alice", result.User!.Username);
        Assert.Single(_store.All);
    }

    [Fact]
    public async Task LinkedUser_WithAdminSetPassword_KeepsDefaultProvider()
    {
        var service = CreateService();
        var id = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!.Id;

        // What OidcAuthenticationProvider.ChangePassword does when an administrator sets a password.
        _store.Mutate(id, u =>
        {
            u.AuthenticationProviderId = DefaultProvider;
            u.Password = "admin-set";
        });

        await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");
        Assert.Equal(DefaultProvider, Stored(id).AuthenticationProviderId);
        Assert.Equal("admin-set", Stored(id).Password);
    }

    [Fact]
    public async Task ExistingUnlinkedUser_IsNotAdopted_ByDefault()
    {
        var existing = _store.Add("Alice", password: "hash");

        var result = await CreateService().ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        Assert.Equal(OidcLoginError.UsernameTaken, result.Error);
        Assert.Single(_store.All);
        Assert.Equal(DefaultProvider, Stored(existing.Id).AuthenticationProviderId);
        Assert.Equal("hash", Stored(existing.Id).Password);
        Assert.Null(await _links.FindUserAsync(Issuer, "sub-1"));
        VerifyRevoked(existing.Id, Times.Never);
        _userManager.Verify(m => m.CreateUserAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Adoption_ReplacesPasswordAndRevokesTokens_BeforePromotion()
    {
        // The squatting scenario: a local user renamed to an admin's IdP username before the admin's first login.
        _store.Add("root", admin: true);
        var squatter = _store.Add("Alice", password: "squatter-hash");
        var promotedWhenRevoked = (bool?)null;
        _sessionManager.Setup(m => m.RevokeUserTokens(squatter.Id, It.IsAny<string>()))
            .Callback(() => promotedWhenRevoked = Stored(squatter.Id).HasPermission(PermissionKind.IsAdministrator))
            .Returns(Task.CompletedTask);

        var result = await CreateService(link: true, admins: ["admins"]).ResolveAsync(Identity("sub-1", "alice", "admins"), "1.2.3.4");

        Assert.Equal(squatter.Id, result.User!.Id);
        var stored = Stored(squatter.Id);
        Assert.Equal(OidcAuthenticationProvider.ProviderId, stored.AuthenticationProviderId);
        Assert.NotEqual("squatter-hash", stored.Password);
        Assert.False(string.IsNullOrEmpty(stored.Password));
        Assert.True(stored.HasPermission(PermissionKind.IsAdministrator));
        VerifyRevoked(squatter.Id, Times.Once);
        Assert.False(promotedWhenRevoked);
        Assert.Equal(squatter.Id, await _links.FindUserAsync(Issuer, "sub-1"));
    }

    [Fact]
    public async Task Adoption_UsesJellyfinsOwnNameComparison()
    {
        // Jellyfin's uniqueness rule is ToUpperInvariant: "ſam" and "SAM" are the same user to Jellyfin,
        // so matching them is consistent with what Jellyfin would refuse to create anyway.
        var existing = _store.Add("sam");

        var result = await CreateService(link: true).ResolveAsync(Identity("sub-1", "\u017Fam"), "1.2.3.4");

        Assert.Equal(existing.Id, result.User!.Id);
        _userManager.Verify(m => m.GetUserByName("\u017Fam"), Times.Once);
    }

    [Fact]
    public async Task ExistingUserLinkedToAnotherIdentity_IsNeverAdopted()
    {
        var service = CreateService(link: true);
        var first = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;
        _sessionManager.Invocations.Clear();

        var result = await service.ResolveAsync(Identity("sub-2", "ALICE"), "1.2.3.4");

        Assert.Equal(OidcLoginError.UsernameTaken, result.Error);
        Assert.Equal(first.Id, await _links.FindUserAsync(Issuer, "sub-1"));
        Assert.Null(await _links.FindUserAsync(Issuer, "sub-2"));
        VerifyRevoked(first.Id, Times.Never);
    }

    [Fact]
    public async Task SanitizedCollision_IsRejected()
    {
        var service = CreateService();
        await service.ResolveAsync(Identity("sub-1", "a/b"), "1.2.3.4");

        var result = await service.ResolveAsync(Identity("sub-2", "a\\b"), "1.2.3.4");

        Assert.Equal(OidcLoginError.UsernameTaken, result.Error);
        Assert.Single(_store.All);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("..")]
    public async Task UnusableUsername_IsRejected(string? username)
    {
        var result = await CreateService().ResolveAsync(Identity("sub-1", username), "1.2.3.4");
        Assert.Equal(OidcLoginError.InvalidUsername, result.Error);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task AutoCreateDisabled_RejectsUnknownUser()
    {
        var result = await CreateService(autoCreate: false).ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");
        Assert.Equal(OidcLoginError.NoAccount, result.Error);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task DeletedLinkedUser_IsNotRecreated()
    {
        var service = CreateService();
        var first = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;
        await _store.Delete(first.Id);

        var result = await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        Assert.Equal(OidcLoginError.LinkedUserMissing, result.Error);
        Assert.Empty(_store.All);
        Assert.Equal(first.Id, await _links.FindUserAsync(Issuer, "sub-1"));
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "other" }, false)]
    [InlineData(new[] { "other", "media" }, true)]
    [InlineData(new[] { "Media" }, false)]
    public async Task AllowedGroups_AreEnforcedBeforeAnyUserIsCreated(string[] groups, bool allowed)
    {
        var result = await CreateService(allowed: ["media", "admins"]).ResolveAsync(Identity("sub-1", "alice", groups), "1.2.3.4");

        Assert.Equal(allowed, result.User is not null);
        Assert.Equal(allowed ? null : OidcLoginError.NotInAllowedGroup, result.Error);
        Assert.Equal(allowed ? 1 : 0, _store.All.Count);
    }

    [Fact]
    public async Task LinkedUserLeavingAllowedGroups_IsDeniedAndRevoked()
    {
        var service = CreateService(allowed: ["media"]);
        var id = (await service.ResolveAsync(Identity("sub-1", "alice", "media"), "1.2.3.4")).User!.Id;

        var result = await service.ResolveAsync(Identity("sub-1", "alice", "other"), "1.2.3.4");

        Assert.Equal(OidcLoginError.NotInAllowedGroup, result.Error);
        VerifyRevoked(id, Times.Once);
    }

    [Fact]
    public async Task AdminGroups_PromoteAndDemoteOnEveryLogin_Persisted()
    {
        _store.Add("root", admin: true);
        var service = CreateService(admins: ["admins"]);

        var id = (await service.ResolveAsync(Identity("sub-1", "alice", "admins"), "1.2.3.4")).User!.Id;
        Assert.True(Stored(id).HasPermission(PermissionKind.IsAdministrator));

        await service.ResolveAsync(Identity("sub-1", "alice", "users"), "1.2.3.4");
        Assert.False(Stored(id).HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task AdminGroups_Unset_LeaveAdminStatusAlone()
    {
        var service = CreateService();
        var id = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!.Id;
        _store.Mutate(id, u => u.SetPermission(PermissionKind.IsAdministrator, true));

        await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");
        Assert.True(Stored(id).HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task AdminGroups_NeverDemoteTheLastAdministrator()
    {
        var service = CreateService(admins: ["admins"]);
        var id = (await service.ResolveAsync(Identity("sub-1", "alice", "admins"), "1.2.3.4")).User!.Id;
        Assert.True(Stored(id).HasPermission(PermissionKind.IsAdministrator));

        await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");
        Assert.True(Stored(id).HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task Login_DoesNotRevertConcurrentDashboardChanges()
    {
        var service = CreateService(admins: ["admins"]);
        _store.Add("root", admin: true);
        var id = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!.Id;

        // An administrator disables the account; a login that writes (admin sync) must not re-enable it.
        _store.Mutate(id, u => u.SetPermission(PermissionKind.IsDisabled, true));
        var result = await service.ResolveAsync(Identity("sub-1", "alice", "admins"), "1.2.3.4");

        Assert.Equal(OidcLoginError.AccountDisabled, result.Error);
        Assert.True(Stored(id).HasPermission(PermissionKind.IsDisabled));
    }

    [Fact]
    public async Task DisabledUser_IsRejectedAndRevoked()
    {
        var service = CreateService();
        var id = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!.Id;
        _store.Mutate(id, u => u.SetPermission(PermissionKind.IsDisabled, true));

        var result = await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        Assert.Equal(OidcLoginError.AccountDisabled, result.Error);
        VerifyRevoked(id, Times.Once);
    }

    [Fact]
    public async Task RemoteAccessDisabled_IsRejectedOnlyFromOutside()
    {
        var service = CreateService();
        var id = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!.Id;
        _store.Mutate(id, u => u.SetPermission(PermissionKind.EnableRemoteAccess, false));

        Assert.Equal(OidcLoginError.RemoteAccessDenied, (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).Error);

        _networkManager.Setup(m => m.IsInLocalNetwork("192.168.1.5")).Returns(true);
        Assert.NotNull((await service.ResolveAsync(Identity("sub-1", "alice"), "192.168.1.5")).User);
    }

    [Fact]
    public async Task FailedProviderSwitch_RemovesTheNewUser()
    {
        _store.FailUpdates = new InvalidOperationException("db");
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4"));
        Assert.Empty(_store.All);
        Assert.Null(await _links.FindUserAsync(Issuer, "sub-1"));
        Assert.False(service.IsBeingCreated("alice"));
    }
}
