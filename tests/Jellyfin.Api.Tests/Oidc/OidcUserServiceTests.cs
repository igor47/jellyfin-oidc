using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Oidc;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public sealed class OidcUserServiceTests : IDisposable
{
    private const string Issuer = "https://idp.example.org";
    private const string DefaultProvider = OidcAuthenticationProvider.DefaultProviderId;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oidc-users-" + Guid.NewGuid().ToString("N"));
    private readonly List<User> _users = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<INetworkManager> _networkManager = new();
    private readonly OidcLinkStore _links;

    public OidcUserServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _links = new OidcLinkStore(_dir);

        _userManager.Setup(m => m.GetUsers()).Returns(() => _users.ToList());
        _userManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => _users.FirstOrDefault(u => u.Id.Equals(id)));
        _userManager.Setup(m => m.GetUserByName(It.IsAny<string>()))
            .Returns((string name) => _users.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase)));
        _userManager.Setup(m => m.CreateUserAsync(It.IsAny<string>())).ReturnsAsync((string name) => AddUser(name));
        _userManager.Setup(m => m.UpdateUserAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _userManager.Setup(m => m.DeleteUserAsync(It.IsAny<Guid>())).Returns((Guid id) =>
        {
            _users.RemoveAll(u => u.Id.Equals(id));
            return Task.CompletedTask;
        });
        _networkManager.Setup(m => m.IsInLocalNetwork(It.IsAny<string>())).Returns(false);
    }

    public void Dispose()
    {
        _links.Dispose();
        Directory.Delete(_dir, true);
    }

    private User AddUser(string name, bool admin = false, string? password = null)
    {
        var user = new User(name, DefaultProvider, "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider") { Password = password };
        user.AddDefaultPermissions();
        user.SetPermission(PermissionKind.IsAdministrator, admin);
        _users.Add(user);
        return user;
    }

    private OidcUserService CreateService(Func<OidcOptions, OidcOptions>? configure = null)
    {
        var options = new OidcOptions { Enabled = true, Issuer = Issuer, ClientId = "c", ClientSecret = "s" };
        options = configure?.Invoke(options) ?? options;
        return new OidcUserService(options, _links, _userManager.Object, _networkManager.Object, NullLogger<OidcUserService>.Instance);
    }

    private static OidcIdentity Identity(string sub, string? username, params string[] groups) => new(Issuer, sub, username, groups);

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
    public async Task NewUser_IsCreatedLinkedAndPasswordLocked()
    {
        var service = CreateService();

        var result = await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        var user = Assert.Single(_users);
        Assert.Same(user, result.User);
        Assert.Equal("alice", user.Username);
        Assert.Equal(OidcAuthenticationProvider.ProviderId, user.AuthenticationProviderId);
        Assert.True(user.HasPermission(PermissionKind.EnableAllFolders));
        Assert.False(user.HasPermission(PermissionKind.IsAdministrator));
        Assert.Equal(user.Id, await _links.FindUserAsync(Issuer, "sub-1"));
    }

    [Fact]
    public async Task LinkedUser_IsFoundBySubject_EvenIfUsernameClaimChanges()
    {
        var service = CreateService();
        await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        var result = await service.ResolveAsync(Identity("sub-1", "alice-renamed"), "1.2.3.4");

        Assert.Equal("alice", result.User!.Username);
        Assert.Single(_users);
    }

    [Fact]
    public async Task LinkedUser_WithAdminSetPassword_KeepsDefaultProvider()
    {
        var service = CreateService();
        var user = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;

        // What OidcAuthenticationProvider.ChangePassword does when an administrator sets a password.
        user.AuthenticationProviderId = DefaultProvider;
        user.Password = "hash";

        var again = await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");
        Assert.Equal(DefaultProvider, again.User!.AuthenticationProviderId);
    }

    [Fact]
    public async Task ExistingUnlinkedUser_IsNotAdopted_ByDefault()
    {
        var existing = AddUser("Alice", password: "hash");
        var service = CreateService();

        var result = await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        Assert.Null(result.User);
        Assert.Equal(OidcLoginError.UsernameTaken, result.Error);
        Assert.Single(_users);
        Assert.Equal(DefaultProvider, existing.AuthenticationProviderId);
        Assert.Null(await _links.FindUserAsync(Issuer, "sub-1"));
        _userManager.Verify(m => m.CreateUserAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExistingUnlinkedUser_IsAdopted_WhenLinkingEnabled()
    {
        var existing = AddUser("Alice", password: "hash");
        var service = CreateService(o => new OidcOptions { Enabled = true, Issuer = o.Issuer, ClientId = "c", ClientSecret = "s", LinkExistingByUsername = true });

        var result = await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        Assert.Same(existing, result.User);
        Assert.Equal(OidcAuthenticationProvider.ProviderId, existing.AuthenticationProviderId);
        Assert.Equal("hash", existing.Password);
        Assert.Equal(existing.Id, await _links.FindUserAsync(Issuer, "sub-1"));
    }

    [Fact]
    public async Task ExistingUserLinkedToAnotherIdentity_IsNeverAdopted()
    {
        var service = CreateService(o => new OidcOptions { Enabled = true, Issuer = o.Issuer, ClientId = "c", ClientSecret = "s", LinkExistingByUsername = true });
        var first = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;

        var result = await service.ResolveAsync(Identity("sub-2", "ALICE"), "1.2.3.4");

        Assert.Equal(OidcLoginError.UsernameTaken, result.Error);
        Assert.Equal(first.Id, await _links.FindUserAsync(Issuer, "sub-1"));
        Assert.Null(await _links.FindUserAsync(Issuer, "sub-2"));
    }

    [Fact]
    public async Task SanitizedCollision_IsRejected()
    {
        var service = CreateService();
        await service.ResolveAsync(Identity("sub-1", "a/b"), "1.2.3.4");

        var result = await service.ResolveAsync(Identity("sub-2", "a\\b"), "1.2.3.4");

        Assert.Equal(OidcLoginError.UsernameTaken, result.Error);
        Assert.Single(_users);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("..")]
    public async Task UnusableUsername_IsRejected(string? username)
    {
        var service = CreateService();
        var result = await service.ResolveAsync(Identity("sub-1", username), "1.2.3.4");
        Assert.Equal(OidcLoginError.InvalidUsername, result.Error);
        Assert.Empty(_users);
    }

    [Fact]
    public async Task AutoCreateDisabled_RejectsUnknownUser()
    {
        var service = CreateService(o => new OidcOptions { Enabled = true, Issuer = o.Issuer, ClientId = "c", ClientSecret = "s", AutoCreateUsers = false });
        var result = await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");
        Assert.Equal(OidcLoginError.NoAccount, result.Error);
        Assert.Empty(_users);
    }

    [Fact]
    public async Task DeletedLinkedUser_IsRecreated()
    {
        var service = CreateService();
        var first = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;
        _users.Clear();

        var result = await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");

        Assert.NotNull(result.User);
        Assert.NotEqual(first.Id, result.User!.Id);
        Assert.Equal(result.User.Id, await _links.FindUserAsync(Issuer, "sub-1"));
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "other" }, false)]
    [InlineData(new[] { "other", "media" }, true)]
    [InlineData(new[] { "Media" }, false)]
    public async Task AllowedGroups_AreEnforcedBeforeAnyUserIsCreated(string[] groups, bool allowed)
    {
        var service = CreateService(o => new OidcOptions { Enabled = true, Issuer = o.Issuer, ClientId = "c", ClientSecret = "s", AllowedGroups = ["media", "admins"] });

        var result = await service.ResolveAsync(Identity("sub-1", "alice", groups), "1.2.3.4");

        Assert.Equal(allowed, result.User is not null);
        Assert.Equal(allowed ? null : OidcLoginError.NotInAllowedGroup, result.Error);
        Assert.Equal(allowed ? 1 : 0, _users.Count);
    }

    [Fact]
    public async Task AdminGroups_PromoteAndDemoteOnEveryLogin()
    {
        AddUser("root", admin: true);
        var service = CreateService(o => new OidcOptions { Enabled = true, Issuer = o.Issuer, ClientId = "c", ClientSecret = "s", AdminGroups = ["admins"] });

        var user = (await service.ResolveAsync(Identity("sub-1", "alice", "admins"), "1.2.3.4")).User!;
        Assert.True(user.HasPermission(PermissionKind.IsAdministrator));

        user = (await service.ResolveAsync(Identity("sub-1", "alice", "users"), "1.2.3.4")).User!;
        Assert.False(user.HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task AdminGroups_Unset_LeaveAdminStatusAlone()
    {
        var service = CreateService();
        var user = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;
        user.SetPermission(PermissionKind.IsAdministrator, true);

        user = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;
        Assert.True(user.HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task AdminGroups_NeverDemoteTheLastAdministrator()
    {
        var service = CreateService(o => new OidcOptions { Enabled = true, Issuer = o.Issuer, ClientId = "c", ClientSecret = "s", AdminGroups = ["admins"] });
        var user = (await service.ResolveAsync(Identity("sub-1", "alice", "admins"), "1.2.3.4")).User!;
        Assert.True(user.HasPermission(PermissionKind.IsAdministrator));

        user = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;
        Assert.True(user.HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task DisabledUser_IsRejected()
    {
        var service = CreateService();
        var user = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;
        user.SetPermission(PermissionKind.IsDisabled, true);

        var result = await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4");
        Assert.Equal(OidcLoginError.AccountDisabled, result.Error);
    }

    [Fact]
    public async Task RemoteAccessDisabled_IsRejectedOnlyFromOutside()
    {
        var service = CreateService();
        var user = (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).User!;
        user.SetPermission(PermissionKind.EnableRemoteAccess, false);

        Assert.Equal(OidcLoginError.RemoteAccessDenied, (await service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4")).Error);

        _networkManager.Setup(m => m.IsInLocalNetwork("192.168.1.5")).Returns(true);
        Assert.NotNull((await service.ResolveAsync(Identity("sub-1", "alice"), "192.168.1.5")).User);
    }

    [Fact]
    public async Task FailedProviderSwitch_RemovesTheNewUser()
    {
        _userManager.Setup(m => m.UpdateUserAsync(It.IsAny<User>())).ThrowsAsync(new InvalidOperationException("db"));
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(Identity("sub-1", "alice"), "1.2.3.4"));
        Assert.Empty(_users);
        Assert.Null(await _links.FindUserAsync(Issuer, "sub-1"));
    }
}
