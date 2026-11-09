using System.Security.Claims;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class AuthenticatedPlayerIdentityTests
{
    [Fact]
    public void ValidSessionProvidesExactlyOneTrustedNameIdentifier()
    {
        var principal = Build("alice");
        Assert.Equal("alice", AuthenticatedPlayerIdentity.FromPrincipal(principal));
    }

    [Fact]
    public void AnonymousAndUnauthenticatedPrincipalCannotAssertAnIdentity()
    {
        Assert.Null(AuthenticatedPlayerIdentity.FromPrincipal(null));
        Assert.Null(AuthenticatedPlayerIdentity.FromPrincipal(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")]))));
    }

    [Fact]
    public void SignedInPrincipalWithoutNameIdentifierIsRejected()
    {
        Assert.Null(AuthenticatedPlayerIdentity.FromPrincipal(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "Cookies"))));
    }

    [Fact]
    public void DuplicateNameIdentifierClaimsAreAmbiguousAndRejected()
    {
        Assert.Null(AuthenticatedPlayerIdentity.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "alice"),
            new Claim(ClaimTypes.NameIdentifier, "bob")
        ], "Cookies"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" alice")]
    [InlineData("alice ")]
    public void EmptyOrPaddedIdentityIsRejected(string id)
    {
        Assert.Null(AuthenticatedPlayerIdentity.FromPrincipal(Build(id)));
    }

    private static ClaimsPrincipal Build(string id) => new(
        new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Cookies"));
}

