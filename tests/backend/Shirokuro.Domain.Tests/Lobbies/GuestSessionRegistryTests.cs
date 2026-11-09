using System.Security.Claims;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class GuestSessionRegistryTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-09T21:00:00+00:00");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    [Fact]
    public void RegisteredSessionIsValidOnlyForItsSignedPlayerIdentity()
    {
        var clock = new ManualClock();
        var registry = new GuestSessionRegistry(clock);
        var playerId = "guest-" + new string('a', 32);
        var token = registry.Register(playerId, clock.GetUtcNow().AddHours(1));
        Assert.Matches("^[0-9a-f]{32}$", token);
        var principal = GuestSessions.CreatePrincipal(playerId, token);
        Assert.True(registry.IsActive(principal));

        Assert.False(registry.IsActive(GuestSessions.CreatePrincipal(
            "guest-" + new string('b', 32), token)));
        Assert.False(registry.IsActive(GuestSessions.CreatePrincipal(
            playerId, new string('c', 32))));
        Assert.False(registry.IsActive(null));
    }

    [Fact]
    public void RevocationIsImmediateIdempotentAndCannotRevokeAnotherPlayersToken()
    {
        var clock = new ManualClock();
        var registry = new GuestSessionRegistry(clock);
        var id = "guest-" + new string('a', 32);
        var token = registry.Register(id, clock.GetUtcNow().AddHours(1));
        var valid = GuestSessions.CreatePrincipal(id, token);
        var other = GuestSessions.CreatePrincipal("guest-" + new string('b', 32), token);

        Assert.False(registry.Revoke(other));
        Assert.True(registry.IsActive(valid));
        Assert.True(registry.Revoke(valid));
        Assert.False(registry.IsActive(valid));
        Assert.False(registry.Revoke(valid));
    }

    [Fact]
    public void ManualClockExpiryAndSweepAreDeterministic()
    {
        var clock = new ManualClock();
        var registry = new GuestSessionRegistry(clock);
        var id = "guest-" + new string('a', 32);
        var token = registry.Register(id, clock.GetUtcNow().AddMinutes(30));
        var principal = GuestSessions.CreatePrincipal(id, token);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(registry.IsActive(principal));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(registry.IsActive(principal));
        Assert.Equal(1, registry.PruneExpired());
        Assert.Equal(0, registry.PruneExpired());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            registry.Register(id, clock.GetUtcNow()));
    }

    [Fact]
    public void LegacySessionWithoutServerTokenNeverCountsAsRegistered()
    {
        var id = "guest-" + new string('a', 32);
        var claim = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(GuestSessions.SessionKindClaim, GuestSessions.GuestKind)
        ], "Cookies"));
        Assert.False(new GuestSessionRegistry(new ManualClock()).IsActive(claim));
    }

    [Fact]
    public async Task ConcurrentRegistrationAndRevocationRemainConsistent()
    {
        var clock = new ManualClock();
        var registry = new GuestSessionRegistry(clock);
        var expires = clock.GetUtcNow().AddHours(1);
        var ids = await Task.WhenAll(Enumerable.Range(0, 80).Select(i => Task.Run(() =>
        {
            var playerId = "guest-" + i.ToString("x32");
            var token = registry.Register(playerId, expires);
            var principal = GuestSessions.CreatePrincipal(playerId, token);
            if (!registry.IsActive(principal))
                return (Token: token, Revoked: false);
            return (Token: token, Revoked: registry.Revoke(principal));
        })));
        Assert.Equal(80, ids.Select(x => x.Token).Distinct().Count());
        Assert.All(ids, x => Assert.True(x.Revoked));
        Assert.Equal(0, registry.PruneExpired());
    }

    [Fact]
    public void DuplicateOrInvalidSignedSessionClaimsAreRejected()
    {
        var clock = new ManualClock();
        var registry = new GuestSessionRegistry(clock);
        var player = "guest-" + new string('a', 32);
        var token = registry.Register(player, clock.GetUtcNow().AddHours(1));
        var ambiguous = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, player),
            new Claim(GuestSessions.SessionKindClaim, GuestSessions.GuestKind),
            new Claim(GuestSessions.SessionIdClaim, token),
            new Claim(GuestSessions.SessionIdClaim, token)
        ], "Cookies"));
        Assert.False(registry.IsActive(ambiguous));
        Assert.False(registry.Revoke(ambiguous));
        Assert.Throws<ArgumentException>(() =>
            GuestSessions.CreatePrincipal(player, "not-a-session"));
    }
}

