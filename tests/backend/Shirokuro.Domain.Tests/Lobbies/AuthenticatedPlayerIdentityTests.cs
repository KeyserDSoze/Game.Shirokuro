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


    [Fact]
    public void UnauthenticatedSecondaryIdentityCannotProvideThePlayerId()
    {
        var signedInWithoutId = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "anonymous-cookie")
        ], "Cookies");
        var untrustedSecondary = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "alice")
        ]);
        var principal = new ClaimsPrincipal([signedInWithoutId, untrustedSecondary]);

        Assert.Null(AuthenticatedPlayerIdentity.FromPrincipal(principal));
    }

    [Fact]
    public void MultipleAuthenticatedIdentitiesAreAmbiguousEvenIfOnlyOneHasAnId()
    {
        var signedIn = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "alice")
        ], "Cookies");
        var anotherSignedIn = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "bob")
        ], "Other-Identity");
        Assert.Null(AuthenticatedPlayerIdentity.FromPrincipal(
            new ClaimsPrincipal([signedIn, anotherSignedIn])));
    }

    private static ClaimsPrincipal Build(string id) => new(
        new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Cookies"));
}
