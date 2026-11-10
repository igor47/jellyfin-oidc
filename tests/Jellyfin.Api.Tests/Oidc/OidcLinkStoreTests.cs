using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Api.Oidc;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public sealed class OidcLinkStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oidc-links-" + Guid.NewGuid().ToString("N"));

    public OidcLinkStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Find_WithoutFile_ReturnsNull()
    {
        using var store = new OidcLinkStore(_dir);
        Assert.Null(await store.FindUserAsync("https://idp", "sub"));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task Link_PersistsAcrossInstances()
    {
        var userId = Guid.NewGuid();
        using (var store = new OidcLinkStore(_dir))
        {
            await store.LinkAsync("https://idp", "sub-1", userId);
        }

        using var reloaded = new OidcLinkStore(_dir);
        Assert.Equal(userId, await reloaded.FindUserAsync("https://idp", "sub-1"));
        Assert.True(await reloaded.IsUserLinkedAsync(userId));
        Assert.False(await reloaded.IsUserLinkedAsync(Guid.NewGuid()));
        Assert.Equal(Path.Combine(_dir, "oidc-links.json"), reloaded.FilePath);
    }

    [Fact]
    public async Task Find_IsKeyedByIssuerAndSubject_Exactly()
    {
        using var store = new OidcLinkStore(_dir);
        var userId = Guid.NewGuid();
        await store.LinkAsync("https://idp", "Sub", userId);

        Assert.Null(await store.FindUserAsync("https://idp", "sub"));
        Assert.Null(await store.FindUserAsync("https://other", "Sub"));
        Assert.Null(await store.FindUserAsync("https://idp/", "Sub"));
        Assert.Equal(userId, await store.FindUserAsync("https://idp", "Sub"));
    }

    [Fact]
    public async Task Link_ReplacesExistingLinkForIdentity()
    {
        using var store = new OidcLinkStore(_dir);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await store.LinkAsync("https://idp", "sub", first);
        await store.LinkAsync("https://idp", "sub", second);

        Assert.Equal(second, await store.FindUserAsync("https://idp", "sub"));
        Assert.False(await store.IsUserLinkedAsync(first));
    }

    [Fact]
    public async Task Unlink_RemovesLink()
    {
        using var store = new OidcLinkStore(_dir);
        var userId = Guid.NewGuid();
        await store.LinkAsync("https://idp", "sub", userId);
        await store.UnlinkAsync("https://idp", "sub");

        using var reloaded = new OidcLinkStore(_dir);
        Assert.Null(await reloaded.FindUserAsync("https://idp", "sub"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"version\":1,\"links\":[{\"issuer\":\"https://idp\",\"subject\":\"\",\"userId\":\"6f1b1a8e-5d1c-4f61-9a43-6f2a8b0e4c11\"}]}")]
    public async Task CorruptFile_FailsClosed(string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, OidcLinkStore.FileName), content, TestContext.Current.CancellationToken);
        using var store = new OidcLinkStore(_dir);
        await Assert.ThrowsAnyAsync<Exception>(() => store.FindUserAsync("https://idp", "sub"));
    }
}
