using System;
using System.Linq;
using Jellyfin.Api.Oidc;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public class OidcStateStoreTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly OidcStateStore _store;

    public OidcStateStoreTests()
    {
        _store = new OidcStateStore(_time);
    }

    [Fact]
    public void Take_WithMatchingBinding_ReturnsLoginOnce()
    {
        var login = _store.Create();

        Assert.Equal(login, _store.Take(login.State, login.BrowserBinding));
        Assert.Null(_store.Take(login.State, login.BrowserBinding));
    }

    [Fact]
    public void Create_GeneratesDistinctRandomValues()
    {
        var login = _store.Create();
        Assert.Equal(4, new[] { login.State, login.Nonce, login.CodeVerifier, login.BrowserBinding }.Distinct().Count());
        Assert.All(new[] { login.State, login.Nonce, login.CodeVerifier, login.BrowserBinding }, v => Assert.Equal(43, v.Length));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("someone-elses-cookie")]
    public void Take_WithWrongBinding_FailsAndConsumesState(string? binding)
    {
        var login = _store.Create();

        Assert.Null(_store.Take(login.State, binding));
        Assert.Null(_store.Take(login.State, login.BrowserBinding));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void Take_UnknownState_Fails(string? state)
    {
        _store.Create();
        Assert.Null(_store.Take(state, "x"));
    }

    [Fact]
    public void Take_Expired_Fails()
    {
        var login = _store.Create();
        _time.Advance(OidcStateStore.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Null(_store.Take(login.State, login.BrowserBinding));
    }

    [Fact]
    public void Take_JustBeforeExpiry_Succeeds()
    {
        var login = _store.Create();
        _time.Advance(OidcStateStore.Lifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(_store.Take(login.State, login.BrowserBinding));
    }

    [Fact]
    public void Create_WhenFull_StaysBoundedAndEvictsOldest()
    {
        var first = _store.Create();
        _time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < OidcStateStore.Capacity * 2; i++)
        {
            _store.Create();
        }

        Assert.True(_store.Count <= OidcStateStore.Capacity);
        Assert.Null(_store.Take(first.State, first.BrowserBinding));

        var latest = _store.Create();
        Assert.NotNull(_store.Take(latest.State, latest.BrowserBinding));
    }

    [Fact]
    public void Create_WhenFull_PurgesExpiredFirst()
    {
        for (var i = 0; i < OidcStateStore.Capacity; i++)
        {
            _store.Create();
        }

        _time.Advance(OidcStateStore.Lifetime + TimeSpan.FromSeconds(1));
        var fresh = _store.Create();
        Assert.Equal(1, _store.Count);
        Assert.NotNull(_store.Take(fresh.State, fresh.BrowserBinding));
    }

    [Fact]
    public void CodeChallenge_MatchesRfc7636Example()
    {
        // RFC 7636 appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OidcStateStore.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
