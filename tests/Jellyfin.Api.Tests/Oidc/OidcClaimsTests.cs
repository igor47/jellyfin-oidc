using Jellyfin.Api.Oidc;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public class OidcClaimsTests
{
    [Fact]
    public void FromJson_FlattensScalarsAndArrays()
    {
        var claims = OidcClaims.FromJson("""
            {"sub":"abc","groups":["a","b",3,null,{"x":1}],"single":"g","n":42,"t":true,"o":{"k":"v"},"z":null,"empty":""}
            """);

        Assert.Equal("abc", claims.GetSingle("sub"));
        Assert.Equal(new[] { "a", "b", "3" }, claims.GetAll("groups"));
        Assert.Equal(new[] { "g" }, claims.GetAll("single"));
        Assert.Equal("42", claims.GetSingle("n"));
        Assert.Equal("true", claims.GetSingle("t"));
        Assert.Empty(claims.GetAll("o"));
        Assert.Empty(claims.GetAll("z"));
        Assert.Empty(claims.GetAll("empty"));
        Assert.Empty(claims.GetAll("missing"));
        Assert.True(claims.Has("o"));
        Assert.False(claims.Has("missing"));
    }

    [Fact]
    public void GetSingle_MultipleValues_ReturnsNull()
    {
        var claims = OidcClaims.FromJson("""{"preferred_username":["a","b"]}""");
        Assert.Null(claims.GetSingle("preferred_username"));
    }

    [Fact]
    public void FromJson_NonObject_IsEmpty()
    {
        Assert.False(OidcClaims.FromJson("[1,2]").Has("0"));
    }

    [Fact]
    public void Merge_PrimaryWins()
    {
        var idToken = OidcClaims.FromJson("""{"sub":"1","preferred_username":"tok"}""");
        var userInfo = OidcClaims.FromJson("""{"sub":"1","preferred_username":"info","groups":["g"]}""");
        var merged = OidcClaims.Merge(idToken, userInfo);

        Assert.Equal("tok", merged.GetSingle("preferred_username"));
        Assert.Equal(new[] { "g" }, merged.GetAll("groups"));
    }
}
