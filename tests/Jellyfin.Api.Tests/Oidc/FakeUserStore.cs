using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Moq;

namespace Jellyfin.Api.Tests.Oidc;

/// <summary>
/// An in-memory user store that, like the real UserManager, hands out copies: only what is written back
/// through UpdateUserAsync/ChangePassword is persisted.
/// </summary>
internal sealed class FakeUserStore
{
    public const string DefaultProvider = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

    private readonly Dictionary<Guid, User> _users = new();

    public Func<string, Task>? OnCreate { get; set; }

    public Exception? FailUpdates { get; set; }

    public IReadOnlyCollection<User> All => _users.Values.Select(Copy).ToList();

    public static User Copy(User u)
    {
        var c = new User(u.Username, u.AuthenticationProviderId, u.PasswordResetProviderId)
        {
            Id = u.Id,
            Password = u.Password,
            InternalId = u.InternalId
        };
        foreach (var p in u.Permissions)
        {
            c.SetPermission(p.Kind, p.Value);
        }

        return c;
    }

    public User Add(string name, bool admin = false, string? password = null)
    {
        var user = new User(name, DefaultProvider, "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider") { Password = password };
        user.AddDefaultPermissions();
        user.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, admin);
        _users[user.Id] = Copy(user);
        return Copy(user);
    }

    public User? Get(Guid id) => _users.TryGetValue(id, out var u) ? Copy(u) : null;

    public User? GetByName(string name)
#pragma warning disable CA1862 // Mirrors UserManager.GetUserByName: NormalizedUsername == name.ToUpperInvariant().
        => _users.Values.Where(u => u.NormalizedUsername == name.ToUpperInvariant()).Select(Copy).FirstOrDefault();
#pragma warning restore CA1862

    public async Task<User> Create(string name)
    {
        if (GetByName(name) is not null)
        {
            throw new ArgumentException("exists");
        }

        // Like UserManager.CreateUserAsync: default provider, no password.
        var user = Add(name);
        if (OnCreate is not null)
        {
            await OnCreate(name).ConfigureAwait(false);
        }

        return user;
    }

    public Task Update(User user)
    {
        if (FailUpdates is not null)
        {
            throw FailUpdates;
        }

        if (!_users.ContainsKey(user.Id))
        {
            throw new InvalidOperationException("no such user");
        }

        _users[user.Id] = Copy(user);
        return Task.CompletedTask;
    }

    public Task ChangePassword(Guid id, string password)
    {
        // What the default provider does: store a hash of a non-empty password.
        _users[id].Password = string.IsNullOrEmpty(password) ? null : "default-hash";
        return Task.CompletedTask;
    }

    public Task Delete(Guid id)
    {
        _users.Remove(id);
        return Task.CompletedTask;
    }

    public Mock<IUserManager> CreateUserManagerMock()
    {
        var userManager = new Mock<IUserManager>();
        userManager.Setup(m => m.GetUsers()).Returns(() => All);
        userManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => Get(id));
        userManager.Setup(m => m.GetUserByName(It.IsAny<string>())).Returns((string name) => GetByName(name));
        userManager.Setup(m => m.CreateUserAsync(It.IsAny<string>())).Returns((string name) => Create(name));
        userManager.Setup(m => m.UpdateUserAsync(It.IsAny<User>())).Returns((User u) => Update(u));
        userManager.Setup(m => m.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>())).Returns((Guid id, string pw) => ChangePassword(id, pw));
        userManager.Setup(m => m.DeleteUserAsync(It.IsAny<Guid>())).Returns((Guid id) => Delete(id));
        return userManager;
    }

    // Simulates a change made concurrently in the dashboard, directly in the store.
    public void Mutate(Guid id, Action<User> change) => change(_users[id]);
}
