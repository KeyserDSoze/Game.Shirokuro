using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Hubs;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class LobbyRosterTransportTests
{
    private const string RoomA = "room-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RoomB = "room-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RoomMissing = "room-00000000000000000000000000000000";
    private const string RoomSecret = "room-cccccccccccccccccccccccccccccccc";
    private const string RoomShared = "room-dddddddddddddddddddddddddddddddd";
    // Real canonical guest identities; a signed legacy account cookie is no
    // longer an authenticated identity under the sole guest cookie scheme.
    private const string GuestAlice = "guest-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GuestBob = "guest-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GuestCara = "guest-cccccccccccccccccccccccccccccccc";

    private static WebApplicationFactory<Program> CreateServer(
        IAsyncGuestLobby? overrideLobby = null, IReadinessDrainSignal? drain = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_ =>
                {
                    var coordinator = new InMemoryLobbyCoordinator();
                    coordinator.Create(RoomA, ["white", "black"]);
                    coordinator.Join(RoomA, GuestAlice, "white", 0);
                    coordinator.Join(RoomA, GuestBob, "black", 1);
                    coordinator.ChooseHero(RoomA, GuestAlice, "kira", 2);
                    coordinator.Create(RoomB, ["white", "black"]);
                    coordinator.Join(RoomB, GuestCara, "white", 0);
                    return coordinator;
                });
                if (overrideLobby is not null)
                {
                    services.RemoveAll<IAsyncGuestLobby>();
                    services.AddSingleton(overrideLobby);
                }
                if (drain is not null)
                {
                    services.RemoveAll<IReadinessDrainSignal>();
                    services.AddSingleton(drain);
                }
            }));

    private static HttpClient ClientFor(
        WebApplicationFactory<Program> factory,
        params Claim[] claims)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);
        var signed = options.TicketDataFormat.Protect(ticket);
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + signed);
        return client;
    }

    private static HttpClient MemberClientFor(
        WebApplicationFactory<Program> server, string playerId)
    {
        // Use a REAL local guest registry entry rather than the historical
        // shortcut of signing an arbitrary account-like NameIdentifier.
        // This keeps the roster/SignalR authorization tests representative
        // under the new guest-only cookie policy.
        var registry = server.Services.GetRequiredService<GuestSessionRegistry>();
        var token = registry.Register(playerId,
            TimeProvider.System.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(playerId, token);
        return ClientFor(server, principal.Claims.ToArray());
    }

    private sealed class ContendedLobby : IAsyncGuestLobby
    {
        public int Reads { get; private set; }
        public TaskCompletionSource<LobbyRosterView>? Pending { get; set; }
        public TaskCompletionSource<LobbyRosterView>? PendingPeek { get; set; }
        public TaskCompletionSource<bool> ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LobbyRosterView> ForMemberAsync(string roomId, string trustedPlayerId,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            ReadStarted.TrySetResult(true);
            // Simulate the Redis runtime's bounded heartbeat CAS retry
            // exhaustion AFTER auth/member routing, without real contention
            // races or an arbitrary InvalidOperationException catch-all.
            return Pending?.Task ?? Task.FromException<LobbyRosterView>(
                new LobbyReadContentionException());
        }
        public Task<LobbyRosterView> PeekForMemberAsync(string roomId,
            string trustedPlayerId, CancellationToken cancellationToken = default)
        {
            Reads++;
            ReadStarted.TrySetResult(true);
            return PendingPeek?.Task ?? Task.FromException<LobbyRosterView>(
                new LobbySnapshotUnavailableException());
        }

        public Task<LobbyRosterView> CreateAsync(string trustedPlayerId,
            IReadOnlyList<string>? teamIds, string? initialTeam,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> JoinAsync(string roomId, string trustedPlayerId,
            string? teamId, long? expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ChooseHeroAsync(string roomId, string trustedPlayerId,
            string? heroId, long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ChangeTeamAsync(string roomId, string trustedPlayerId,
            string? teamId, long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task LeaveAsync(string roomId, string trustedPlayerId,
            long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> RemovePlayerFromAllRoomsAsync(string trustedPlayerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ManualRosterDrain : IReadinessDrainSignal
    {
        private int _stopping;
        public bool IsDraining => Volatile.Read(ref _stopping) != 0;
        public void Stop() => Interlocked.Exchange(ref _stopping, 1);
    }

    [Theory]
    [InlineData("roster")]
    [InlineData("roster/peek")]
    public async Task AuthenticatedInFlightHttpRosterCannotPublishAfterDrain(
        string relativePath)
    {
        var pending = new TaskCompletionSource<LobbyRosterView>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lobby = new ContendedLobby();
        if (relativePath == "roster")
            lobby.Pending = pending;
        else
            lobby.PendingPeek = pending;

        var drain = new ManualRosterDrain();
        using var server = CreateServer(lobby, drain);
        using var guest = MemberClientFor(server, GuestAlice);
        var view = new LobbyRosterReader(
            server.Services.GetRequiredService<InMemoryLobbyCoordinator>())
            .ForMember(RoomA, GuestAlice);

        // Test both the explicit heartbeat and the passive conditional GET.
        // Without the post-await fence they would return 200 and 304.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/lobbies/{RoomA}/{relativePath}");
        request.Headers.TryAddWithoutValidation("If-None-Match",
            "\"rev-" + view.Revision + "\"");
        var inFlight = guest.SendAsync(request);
        await lobby.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, lobby.Reads);

        drain.Stop();
        pending.SetResult(view);
        using (var rejected = await inFlight)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
            Assert.False(rejected.Headers.Contains("ETag"));
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
            var body = await rejected.Content.ReadAsStringAsync();
            Assert.DoesNotContain(RoomA, body, StringComparison.Ordinal);
            Assert.DoesNotContain(GuestAlice, body, StringComparison.Ordinal);
            Assert.DoesNotContain("participants", body,
                StringComparison.OrdinalIgnoreCase);
        }
        using (var afterStop = await guest.GetAsync(
                   $"/api/lobbies/{RoomA}/{relativePath}"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                afterStop.StatusCode);
            Assert.Equal("no-store",
                afterStop.Headers.CacheControl?.ToString());
        }
        Assert.Equal(1, lobby.Reads); // No further storage access.
        using var health = await guest.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // The HTTP handler must never publish a roster or even a 304 ETag
    // whose first guest auth succeeded before a concurrent revocation.
    [Theory]
    [InlineData("roster")]
    [InlineData("roster/peek")]
    public async Task RevokedGuestCannotReceiveInFlightHttpRosterOrNotModified(
        string relativePath)
    {
        using var capture = new MultiplayerMetricCapture();
        var pending = new TaskCompletionSource<LobbyRosterView>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lobby = new ContendedLobby();
        if (relativePath == "roster")
            lobby.Pending = pending;
        else
            lobby.PendingPeek = pending;

        using var server = CreateServer(lobby);
        var registry = server.Services.GetRequiredService<GuestSessionRegistry>();
        var token = registry.Register(GuestAlice,
            TimeProvider.System.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(GuestAlice, token);
        using var guest = ClientFor(server, principal.Claims.ToArray());
        var roster = new LobbyRosterReader(
            server.Services.GetRequiredService<InMemoryLobbyCoordinator>())
            .ForMember(RoomA, GuestAlice);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/lobbies/{RoomA}/{relativePath}");
        request.Headers.TryAddWithoutValidation("If-None-Match",
            "\"rev-" + roster.Revision + "\"");
        var inFlight = guest.SendAsync(request);
        await lobby.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, lobby.Reads);

        Assert.True(registry.Revoke(principal));
        pending.SetResult(roster);
        using var response = await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.False(response.Headers.Contains("ETag"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(RoomA, body, StringComparison.Ordinal);
        Assert.DoesNotContain(GuestBob, body, StringComparison.Ordinal);
        Assert.DoesNotContain("participants", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, lobby.Reads); // No second roster query after revocation.
        Assert.Contains(capture.For(MultiplayerMetrics.RosterPostReadAuthorityName),
            measurement => measurement.Tags["surface"] == (relativePath == "roster" ? "http-refresh" : "http-peek") &&
                           measurement.Tags["outcome"] == "inactive");
    }

    private sealed class InterruptibleGuestCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _values = new();
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public bool FailReads { get; set; }

        public byte[]? Get(string key)
        {
            Interlocked.Increment(ref _readCount);
            if (FailReads)
                throw new IOException("private shared session authority unavailable");
            return _values.TryGetValue(key, out var value) ? value : null;
        }

        public Task<byte[]?> GetAsync(string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Get(key));
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => _values[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Set(key, value, options);
            return Task.CompletedTask;
        }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public void Remove(string key) => _values.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Remove(key);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("roster")]
    [InlineData("roster/peek")]
    public async Task GuestAuthorityOutageAfterInFlightRosterReturnsOpaque503(
        string relativePath)
    {
        using var capture = new MultiplayerMetricCapture();
        var cache = new InterruptibleGuestCache();
        var registry = new GuestSessionRegistry(TimeProvider.System, cache);
        var pending = new TaskCompletionSource<LobbyRosterView>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lobby = new ContendedLobby();
        if (relativePath == "roster")
            lobby.Pending = pending;
        else
            lobby.PendingPeek = pending;
        using var server = CreateServer(lobby).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<GuestSessionRegistry>();
                services.AddSingleton(registry);
            }));
        var token = await registry.RegisterAsync(GuestAlice,
            TimeProvider.System.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(GuestAlice, token);
        using var guest = ClientFor(server, principal.Claims.ToArray());
        var roster = new LobbyRosterReader(
            server.Services.GetRequiredService<InMemoryLobbyCoordinator>())
            .ForMember(RoomA, GuestAlice);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/lobbies/{RoomA}/{relativePath}");
        request.Headers.TryAddWithoutValidation("If-None-Match",
            "\"rev-" + roster.Revision + "\"");
        var inFlight = guest.SendAsync(request);
        await lobby.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, cache.ReadCount); // Auth middleware checked real authority.
        cache.FailReads = true; // Remote Redis outage before post-read check.
        pending.SetResult(roster);

        using var response = await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.False(response.Headers.Contains("ETag"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(RoomA, body, StringComparison.Ordinal);
        Assert.DoesNotContain(GuestBob, body, StringComparison.Ordinal);
        Assert.DoesNotContain("participants", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private shared", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, cache.ReadCount); // Exactly one post-read authority lookup.
        Assert.Equal(1, lobby.Reads);
        Assert.Contains(capture.For(MultiplayerMetrics.RosterPostReadAuthorityName),
            measurement => measurement.Tags["surface"] == (relativePath == "roster" ? "http-refresh" : "http-peek") &&
                           measurement.Tags["outcome"] == "unavailable");
    }

    private sealed class PausedRosterWithLogout(
        LobbyRosterReader reader, InMemoryLobbyCoordinator coordinator)
        : IAsyncGuestLobby
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseRead { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task<LobbyRosterView> ReadAsync(
            string roomId, string trustedPlayerId, bool passive,
            CancellationToken cancellationToken)
        {
            // Capture a genuinely member-authorized snapshot BEFORE logout.
            // The underlying storage response arrives only after release.
            var view = passive
                ? reader.PeekForMember(roomId, trustedPlayerId)
                : reader.ForMember(roomId, trustedPlayerId);
            if (trustedPlayerId == GuestAlice)
            {
                ReadStarted.TrySetResult(true);
                await ReleaseRead.Task.WaitAsync(cancellationToken);
            }
            return view;
        }

        public Task<LobbyRosterView> ForMemberAsync(string roomId,
            string trustedPlayerId, CancellationToken cancellationToken = default)
            => ReadAsync(roomId, trustedPlayerId, false, cancellationToken);
        public Task<LobbyRosterView> PeekForMemberAsync(string roomId,
            string trustedPlayerId, CancellationToken cancellationToken = default)
            => ReadAsync(roomId, trustedPlayerId, true, cancellationToken);
        public Task<int> RemovePlayerFromAllRoomsAsync(string trustedPlayerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(coordinator.RemovePlayerFromAllRooms(trustedPlayerId));
        }
        public Task<LobbyRosterView> CreateAsync(string trustedPlayerId,
            IReadOnlyList<string>? teamIds, string? initialTeam,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<LobbyRosterView> JoinAsync(string roomId,
            string trustedPlayerId, string? teamId, long? expectedRevision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<LobbyRosterView> ChooseHeroAsync(string roomId,
            string trustedPlayerId, string? heroId, long expectedRevision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<LobbyRosterView> ChangeTeamAsync(string roomId,
            string trustedPlayerId, string? teamId, long expectedRevision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task LeaveAsync(string roomId, string trustedPlayerId,
            long expectedRevision, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("roster")]
    [InlineData("roster/peek")]
    public async Task ConcurrentRealHttpLogoutRevokesInFlightRosterButOtherGuestCanRead(
        string relativePath)
    {
        using var capture = new MultiplayerMetricCapture();
        using var server = CreateServer().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAsyncGuestLobby>();
                services.AddSingleton<PausedRosterWithLogout>();
                services.AddSingleton<IAsyncGuestLobby>(services =>
                    services.GetRequiredService<PausedRosterWithLogout>());
            }));
        var paused = server.Services.GetRequiredService<PausedRosterWithLogout>();
        using var alice = MemberClientFor(server, GuestAlice);
        using var bob = MemberClientFor(server, GuestBob);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // The conditional validator matches the snapshot revision. The
        // passive route must not report 304 after the issuing guest logs out.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/lobbies/{RoomA}/{relativePath}");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"rev-3\"");
        var inFlight = alice.SendAsync(request, timeout.Token);
        await paused.ReadStarted.Task.WaitAsync(timeout.Token);

        using var logout = new HttpRequestMessage(HttpMethod.Delete,
            "/api/sessions/guest");
        logout.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        logout.Headers.TryAddWithoutValidation(
            WebMutationGuard.RequestHeaderName, WebMutationGuard.RequestHeaderValue);
        using var loggedOut = await alice.SendAsync(logout, timeout.Token);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        Assert.True(loggedOut.Headers.CacheControl?.NoStore == true);

        paused.ReleaseRead.SetResult(true);
        using var stale = await inFlight.WaitAsync(timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.True(stale.Headers.CacheControl?.NoStore == true);
        Assert.False(stale.Headers.Contains("ETag"));
        Assert.False(stale.Headers.Contains("Set-Cookie"));
        var deniedBody = await stale.Content.ReadAsStringAsync(timeout.Token);
        Assert.DoesNotContain(RoomA, deniedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(GuestAlice, deniedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(GuestBob, deniedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("participants", deniedBody,
            StringComparison.OrdinalIgnoreCase);

        using var revoked = await alice.GetAsync("/api/sessions/me", timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.True(revoked.Headers.CacheControl?.NoStore == true);

        using var surviving = await bob.GetAsync(
            $"/api/lobbies/{RoomA}/{relativePath}", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, surviving.StatusCode);
        Assert.True(surviving.Headers.CacheControl?.NoStore == true);
        using var json = JsonDocument.Parse(
            await surviving.Content.ReadAsStringAsync(timeout.Token));
        var roster = json.RootElement;
        Assert.Equal(4, roster.GetProperty("revision").GetInt64());
        var member = Assert.Single(roster.GetProperty("participants").EnumerateArray());
        Assert.Equal(GuestBob, member.GetProperty("playerId").GetString());
        Assert.DoesNotContain(GuestAlice, roster.GetRawText());
        using var otherSession = await bob.GetAsync("/api/sessions/me", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, otherSession.StatusCode);
        var surface = relativePath == "roster" ? "http-refresh" : "http-peek";
        var metrics = capture.For(MultiplayerMetrics.RosterPostReadAuthorityName);
        Assert.Contains(metrics, m => m.Tags["surface"] == surface &&
            m.Tags["outcome"] == "inactive");
        Assert.Contains(metrics, m => m.Tags["surface"] == surface &&
            m.Tags["outcome"] == "active");
    }

    [Fact]
    public async Task SignalRDoesNotExposeUndecodableRedisRoomOrUnverifiedRoster()
    {
        var database = System.Reflection.DispatchProxy.Create<
            StackExchange.Redis.IDatabase,
            RedisLobbyCommandTimeoutTests.CorruptSnapshotDatabase>();
        var corrupt = (RedisLobbyCommandTimeoutTests.CorruptSnapshotDatabase)(object)database;
        corrupt.Raw = System.Text.Encoding.UTF8.GetBytes("{bad-snapshot}");
        var clock = TimeProvider.System;
        var runtime = new RedisGuestLobbyRuntime(
            new RedisLobbySnapshotStore(database, clock), clock);
        var hub = new LobbyRosterHub(
            new LobbyRosterReader(new InMemoryLobbyCoordinator()),
            lobby: runtime)
        {
            Context = new FakeHubContext(Principal("alice"))
        };

        var error = await Assert.ThrowsAsync<HubException>(
            () => hub.GetRoster(RoomA));
        Assert.Equal("Lobby unavailable.", error.Message);
        Assert.Equal(1, corrupt.Reads);
        Assert.Equal(0, corrupt.Mutations);
    }

    [Fact]
    public async Task ContendedAuthenticatedHttpRosterReturnsUncachedSanitized503()
    {
        var contended = new ContendedLobby();
        using var server = CreateServer(contended);
        using var alice = MemberClientFor(server, GuestAlice);

        using var response = await alice.GetAsync($"/api/lobbies/{RoomA}/roster");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("contention", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Redis", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, contended.Reads);
    }

    [Fact]
    public async Task FailedAuthorizedPassiveRosterCannotBecomeNotModifiedOnMatchingEtag()
    {
        var unavailable = new ContendedLobby();
        using var server = CreateServer(unavailable);
        using var alice = MemberClientFor(server, GuestAlice);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/lobbies/{RoomA}/roster/peek");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"rev-3\"");
        using var response = await alice.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("ETag"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, unavailable.Reads);
    }

    [Fact]
    public async Task ContendedSignalRRosterKeepsExistingOpaqueUnavailableError()
    {
        var contended = new ContendedLobby();
        var hub = new LobbyRosterHub(
            new LobbyRosterReader(new InMemoryLobbyCoordinator()),
            lobby: contended)
        {
            Context = new FakeHubContext(Principal("alice"))
        };
        var error = await Assert.ThrowsAsync<HubException>(
            () => hub.GetRoster(RoomA));
        Assert.Equal("Lobby unavailable.", error.Message);
        Assert.Equal(1, contended.Reads);
    }

    [Fact]
    public async Task AnonymousOrSpoofedHttpIdentityCannotReadRoster()
    {
        using var server = CreateServer();
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var anonymous = await client.GetAsync($"/api/lobbies/{RoomA}/roster");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Add("X-Player-Id", "alice");
        client.DefaultRequestHeaders.Add("X-User-Id", "alice");
        var spoofed = await client.GetAsync($"/api/lobbies/{RoomA}/roster?playerId=alice");
        Assert.Equal(HttpStatusCode.Unauthorized, spoofed.StatusCode);
    }

    [Fact]
    public async Task SignedInMemberCanReadOnlyExplicitRosterData()
    {
        using var server = CreateServer();
        using var alice = MemberClientFor(server, GuestAlice);
        var response = await alice.GetAsync($"/api/lobbies/{RoomA}/roster");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(RoomA, root.GetProperty("lobbyId").GetString());
        Assert.Equal(3, root.GetProperty("revision").GetInt64());
        Assert.Equal(2, root.GetProperty("participants").GetArrayLength());
        Assert.Equal(GuestAlice,
            root.GetProperty("participants")[0].GetProperty("playerId").GetString());
        Assert.Equal("kira",
            root.GetProperty("participants")[0].GetProperty("heroId").GetString());
        Assert.DoesNotContain("plannedDestination", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateRoute", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("matchState", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtherMemberIdInQueryCannotOverrideSignedIdentity()
    {
        using var server = CreateServer();
        using var bob = MemberClientFor(server, GuestBob);
        var response = await bob.GetAsync($"/api/lobbies/{RoomB}/roster?playerId=cara");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownLobbyAndUnauthorizedMembershipHaveSameNotFoundResponse()
    {
        using var server = CreateServer();
        using var bob = MemberClientFor(server, GuestBob);
        var absent = await bob.GetAsync($"/api/lobbies/{RoomMissing}/roster");
        var denied = await bob.GetAsync($"/api/lobbies/{RoomB}/roster");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(absent.StatusCode, denied.StatusCode);
        Assert.Equal(await absent.Content.ReadAsStringAsync(),
                     await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignedInButAmbiguousOrMissingIdentityFailsClosed()
    {
        using var server = CreateServer();
        using var noId = ClientFor(server, new Claim(ClaimTypes.Name, "alice"));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await noId.GetAsync($"/api/lobbies/{RoomA}/roster")).StatusCode);

        using var duplicateId = ClientFor(server,
            new Claim(ClaimTypes.NameIdentifier, "alice"),
            new Claim(ClaimTypes.NameIdentifier, "bob"));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await duplicateId.GetAsync($"/api/lobbies/{RoomA}/roster")).StatusCode);
    }

    [Fact]
    public async Task ForgedCookieCannotImpersonateAMember()
    {
        using var server = CreateServer();
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", "Shirokuro.Session=alice");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/lobbies/{RoomA}/roster")).StatusCode);
    }

    [Fact]
    public async Task BrowserSignalRRequiresTrustedOriginButAllowsViteAndNonBrowserClients()
    {
        using var server = CreateServer();
        using var member = MemberClientFor(server, GuestAlice);
        const string negotiate = "/hubs/lobbies/negotiate?negotiateVersion=1";

        // A browser's cross-origin Vite development client is explicitly
        // supported. No X-Shirokuro-Request mutation header is required.
        foreach (var allowed in new[] { "http://localhost", "http://localhost:5173",
                                        "http://127.0.0.1:5173" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, negotiate);
            request.Headers.TryAddWithoutValidation("Origin", allowed);
            using var response = await member.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Existing non-browser SignalR clients may omit Origin entirely.
        using (var noOrigin = await member.PostAsync(negotiate, null))
            Assert.Equal(HttpStatusCode.OK, noOrigin.StatusCode);

        foreach (var hostile in new[] { "https://evil.example", "null",
                                        "http://localhost:5173/path",
                                        "http://attacker.localhost:5173",
                                        "http://localhost:5173, http://evil.example" })
        {
            foreach (var (method, path) in new[]
                     {
                         (HttpMethod.Post, negotiate),
                         (HttpMethod.Get, "/hubs/lobbies")
                     })
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.TryAddWithoutValidation("Origin", hostile);
                using var denied = await member.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
                Assert.False(denied.Headers.Contains("Set-Cookie"));
            }
        }

        // The browser hub fence does not spill onto ordinary member-scoped
        // HTTP reads or the unauthenticated liveness endpoint.
        using var me = await member.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var health = await member.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task SignalRNegotiateRequiresAuthenticationEvenWithSpoofedHeaders()
    {
        using var server = CreateServer();
        using var anonymous = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        anonymous.DefaultRequestHeaders.Add("X-Player-Id", "alice");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync("/hubs/lobbies/negotiate?negotiateVersion=1", null)).StatusCode);

        using var alice = MemberClientFor(server, GuestAlice);
        var negotiate = await alice.PostAsync("/hubs/lobbies/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var json = await negotiate.Content.ReadAsStringAsync();
        Assert.Contains("connectionId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicHealthAndBoardPreviewEndpointsRemainUnauthenticated()
    {
        using var server = CreateServer();
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/preview/board")).StatusCode);
    }

    [Fact]
    public void HubHasNoCallerControlledPlayerIdOrStateMutations()
    {
        var methods = typeof(LobbyRosterHub).GetMethods(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.DeclaredOnly);
        // SignalR lifecycle overrides are not caller-invokable hub methods.
        // No new player-controlled room subscription or mutation was added.
        Assert.Equal(new[] { "GetRoster", "OnConnectedAsync", "OnDisconnectedAsync" },
            methods.Select(method => method.Name).OrderBy(name => name,
                StringComparer.Ordinal));
        var method = Assert.Single(methods, m => m.Name == "GetRoster");
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal("lobbyId", parameter.Name);
        Assert.Equal(typeof(string), parameter.ParameterType);
        Assert.NotNull(Attribute.GetCustomAttribute(
            typeof(LobbyRosterHub), typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute)));
    }

    [Fact]
    public async Task SignalRMethodReauthorizesMemberAndRejectsSpoofedContextUserIdentifier()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomA, ["white", "black"]);
        coordinator.Join(RoomA, "alice", "white", 0);
        coordinator.Join(RoomA, "bob", "black", 1);
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator));

        // The caller's authenticated principal, not a transport-supplied or
        // caller-controlled identifier, is the authoritative identity.
        hub.Context = new FakeHubContext(Principal("alice"), "spoofed-bob");
        var roster = await hub.GetRoster(RoomA);
        Assert.Equal(RoomA, roster.LobbyId);
        Assert.Equal(2, roster.Participants.Count);

        hub.Context = new FakeHubContext(Principal("outsider"), "alice");
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomA))).Message);

        hub.Context = new FakeHubContext(Principal("alice"));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomMissing))).Message);

        hub.Context = new FakeHubContext(new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomA))).Message);

        hub.Context = new FakeHubContext(Principal("bob"));
        Assert.Equal(RoomA, (await hub.GetRoster(RoomA)).LobbyId);
    }


    [Fact]
    public async Task HubRejectsMalformedIdsWithUniformNotFoundError()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomA, ["white", "black"]);
        coordinator.Join(RoomA, "alice", "white", 0);
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator))
        {
            Context = new FakeHubContext(Principal("alice"))
        };
        Assert.Equal(RoomA, (await hub.GetRoster(RoomA)).LobbyId);

        foreach (var bad in new[]
                 {
                     "", "room-1", "room-" + new string('A', 32),
                     "room-" + new string('a', 31),
                     "room-" + new string('a', 33),
                     "room-" + new string('a', 31) + "/",
                     " room-" + new string('a', 32),
                     "room-" + new string('a', 31) + "é"
                 })
        {
            var error = await Assert.ThrowsAsync<HubException>(
                () => hub.GetRoster(bad));
            Assert.Equal("Lobby unavailable.", error.Message);
        }
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(
                () => hub.GetRoster(RoomMissing))).Message);
    }

    [Fact]
    public async Task ExistingSignalRConnectionCannotReadAfterGuestLogoutRevocation()
    {
        using var capture = new MultiplayerMetricCapture();
        var playerId = "guest-" + new string('a', 32);
        var clock = TimeProvider.System;
        var registry = new GuestSessionRegistry(clock);
        var token = registry.Register(playerId, clock.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(playerId, token);

        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomSecret, ["white", "black"]);
        coordinator.Join(RoomSecret, playerId, "white", 0);

        // The same Context.User survives as a cached principal across a
        // long-lived socket; the registry must be consulted per invocation.
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator), registry)
        {
            Context = new FakeHubContext(principal)
        };
        Assert.Equal(RoomSecret, (await hub.GetRoster(RoomSecret)).LobbyId);
        Assert.Contains(capture.For(MultiplayerMetrics.RosterPostReadAuthorityName),
            m => m.Tags["surface"] == "signalr" &&
                 m.Tags["outcome"] == "active");
        Assert.True(registry.Revoke(principal));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomSecret))).Message);

        // A valid ticket with a session ID no longer registered also fails.
        var newHub = new LobbyRosterHub(new LobbyRosterReader(coordinator), registry)
        {
            Context = new FakeHubContext(principal)
        };
        await Assert.ThrowsAsync<HubException>(() => newHub.GetRoster(RoomSecret));
    }

    [Fact]
    public async Task ExistingHubRejectsSessionRevokedByAnotherRegistry()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var provider = services.BuildServiceProvider();
        var shared = provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
        var clock = TimeProvider.System;
        var onA = new GuestSessionRegistry(clock, shared);
        var onB = new GuestSessionRegistry(clock, shared);
        var id = GuestSessions.NewPlayerId();
        var token = await onA.RegisterAsync(id, clock.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(id, token);

        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomShared, ["white", "black"]);
        coordinator.Join(RoomShared, id, "white", 0);
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator), onA)
        {
            Context = new FakeHubContext(principal)
        };

        Assert.Equal(RoomShared, (await hub.GetRoster(RoomShared)).LobbyId);
        Assert.True(await onB.RevokeAsync(principal));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomShared))).Message);
    }

    [Fact]
    public async Task RemoteGuestRevocationDuringInFlightHubReadRejectsStaleRoster()
    {
        using var capture = new MultiplayerMetricCapture();
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var provider = services.BuildServiceProvider();
        var shared = provider.GetRequiredService<
            Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
        var clock = TimeProvider.System;
        var firstNodeSessions = new GuestSessionRegistry(clock, shared);
        var secondNodeSessions = new GuestSessionRegistry(clock, shared);
        var playerId = GuestSessions.NewPlayerId();
        var token = await firstNodeSessions.RegisterAsync(
            playerId, clock.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(playerId, token);

        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomShared, ["white", "black"]);
        coordinator.Join(RoomShared, playerId, "white", 0);
        var reader = new LobbyRosterReader(coordinator);
        // Snapshot was authorized when the read started, but must not be
        // delivered once a second node has revoked the session.
        var stale = reader.ForMember(RoomShared, playerId);
        var pending = new TaskCompletionSource<LobbyRosterView>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lobby = new ContendedLobby { Pending = pending };
        var hub = new LobbyRosterHub(reader, firstNodeSessions, lobby: lobby)
        {
            Context = new FakeHubContext(principal)
        };

        var inFlight = hub.GetRoster(RoomShared);
        await lobby.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, lobby.Reads);
        Assert.True(await secondNodeSessions.RevokeAsync(principal));
        pending.SetResult(stale);

        var denied = await Assert.ThrowsAsync<HubException>(
            () => inFlight.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Lobby unavailable.", denied.Message);
        // The room read was not retried after revocation and no cached
        // local guest registry entry can override the shared authority.
        Assert.Equal(1, lobby.Reads);
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(
                () => hub.GetRoster(RoomShared))).Message);
        Assert.Equal(1, lobby.Reads);
        Assert.Contains(capture.For(MultiplayerMetrics.RosterPostReadAuthorityName),
            m => m.Tags["surface"] == "signalr" &&
                 m.Tags["outcome"] == "inactive");
    }

    [Fact]
    public async Task SignalRPostReadAuthorityOutageRemainsOpaqueAndCountsUnavailable()
    {
        using var capture = new MultiplayerMetricCapture();
        var cache = new InterruptibleGuestCache();
        var registry = new GuestSessionRegistry(TimeProvider.System, cache);
        var token = await registry.RegisterAsync(GuestAlice,
            TimeProvider.System.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(GuestAlice, token);
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomA, ["white", "black"]);
        coordinator.Join(RoomA, GuestAlice, "white", 0);
        var reader = new LobbyRosterReader(coordinator);
        var oldRoster = reader.ForMember(RoomA, GuestAlice);
        var pending = new TaskCompletionSource<LobbyRosterView>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lobby = new ContendedLobby { Pending = pending };
        var hub = new LobbyRosterHub(reader, registry, lobby: lobby)
        {
            Context = new FakeHubContext(principal)
        };

        var inFlight = hub.GetRoster(RoomA);
        await lobby.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, cache.ReadCount);
        cache.FailReads = true; // Simulated remote authority outage after read started.
        pending.SetResult(oldRoster);

        var denied = await Assert.ThrowsAsync<HubException>(
            () => inFlight.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Lobby unavailable.", denied.Message);
        Assert.Equal(2, cache.ReadCount);
        Assert.Equal(1, lobby.Reads);
        Assert.Contains(capture.For(MultiplayerMetrics.RosterPostReadAuthorityName),
            m => m.Tags["surface"] == "signalr" &&
                 m.Tags["outcome"] == "unavailable");
    }

    [Fact]
    public void SessionCookieSettingsHaveStrictSecurityDefaults()
    {
        using var server = CreateServer();
        // Trigger host initialization before obtaining its configured options.
        using var client = server.CreateClient();
        var cookie = server.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal("Shirokuro.Session", cookie.Cookie.Name);
        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, cookie.Cookie.SameSite);
        Assert.False(cookie.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(8), cookie.ExpireTimeSpan);
    }

    private sealed class StoppingHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    [Fact]
    public async Task ThrowingPreStopObserverCannotBlockSignalRSocketDrain()
    {
        using var metrics = new MultiplayerMetricCapture();
        using var host = new StoppingHostLifetime();
        using var signal = new HostLifetimeReadinessDrainSignal(host);
        using var tracker = new LobbyConnectionDrainRegistry(signal, host);
        var socket = new FakeHubContext(Principal("alice"),
            connectionId: "faulty-prestop-observer");
        var hub = new LobbyRosterHub(new LobbyRosterReader(
            new InMemoryLobbyCoordinator()), drain: signal,
            connections: tracker) { Context = socket };
        await hub.OnConnectedAsync();
        Assert.Equal(0, socket.AbortCount);

        // Registration occurs AFTER the registry subscriber; in .NET
        // CancellationTokenSource invokes callbacks synchronously and
        // can throw an AggregateException from any failing subscriber.
        // That exception must not escape a real OS SIGUSR1 callback.
        using var faulty = signal.PreStopStarted.Register(
            () => throw new InvalidOperationException(
                "Test-only broken preStop subscriber"));
        signal.BeginPreStopDrain();

        Assert.True(signal.IsDraining);
        Assert.True(tracker.IsDraining);
        Assert.False(host.ApplicationStopping.IsCancellationRequested);
        Assert.Equal(1, socket.AbortCount);
        var attempts = metrics.For(
            MultiplayerMetrics.SignalRDrainAbortAttemptsName);
        Assert.Single(attempts, item => item.Value == 1 &&
            item.Tags.Count == 1 &&
            item.Tags.TryGetValue("reason", out var reason) &&
            reason == "prestop");

        signal.BeginPreStopDrain();
        host.StopApplication();
        Assert.Equal(1, socket.AbortCount);
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(
                () => hub.GetRoster(RoomA))).Message);
    }

    [Fact]
    public async Task PreStopSocketMetricsCountOnlyRegistryAbortAttempts()
    {
        using var capture = new MultiplayerMetricCapture();
        using var host = new StoppingHostLifetime();
        using var drain = new HostLifetimeReadinessDrainSignal(host);
        using var tracker = new LobbyConnectionDrainRegistry(drain, host);
        var reader = new LobbyRosterReader(new InMemoryLobbyCoordinator());
        var connected = new FakeHubContext(Principal("alice"),
            connectionId: "observed-socket");
        var disconnected = new FakeHubContext(Principal("bob"),
            connectionId: "untracked-socket");
        var liveHub = new LobbyRosterHub(reader,
            drain: drain, connections: tracker) { Context = connected };
        var lostHub = new LobbyRosterHub(reader,
            drain: drain, connections: tracker) { Context = disconnected };
        await liveHub.OnConnectedAsync();
        await lostHub.OnConnectedAsync();
        await lostHub.OnDisconnectedAsync(null);

        drain.BeginPreStopDrain();
        drain.BeginPreStopDrain();
        host.StopApplication();
        Assert.Equal(1, connected.AbortCount);
        Assert.Equal(0, disconnected.AbortCount);
        Assert.Contains(capture.For(MultiplayerMetrics.SignalRDrainAbortAttemptsName),
            item => item.Value == 1 && item.Tags.Count == 1 &&
                item.Tags.TryGetValue("reason", out var reason) &&
                reason == "prestop");

        var late = new FakeHubContext(Principal("alice"),
            connectionId: "after-prestop");
        Assert.False(tracker.TryTrack(late));
        Assert.Equal(1, late.AbortCount);
        Assert.Contains(capture.For(MultiplayerMetrics.SignalRDrainAbortAttemptsName),
            item => item.Value == 1 && item.Tags.Count == 1 &&
                item.Tags.TryGetValue("reason", out var reason) &&
                reason == "late-connection");
    }

    [Fact]
    public async Task HostShutdownSocketMetricsUseFallbackReason()
    {
        using var capture = new MultiplayerMetricCapture();
        using var host = new StoppingHostLifetime();
        using var drain = new HostLifetimeReadinessDrainSignal(host);
        using var tracker = new LobbyConnectionDrainRegistry(drain, host);
        var context = new FakeHubContext(Principal("alice"),
            connectionId: "shutdown-only");
        var hub = new LobbyRosterHub(new LobbyRosterReader(
            new InMemoryLobbyCoordinator()), drain: drain,
            connections: tracker) { Context = context };
        await hub.OnConnectedAsync();
        host.StopApplication();
        Assert.Equal(1, context.AbortCount);
        Assert.Contains(capture.For(MultiplayerMetrics.SignalRDrainAbortAttemptsName),
            item => item.Value == 1 && item.Tags.Count == 1 &&
                item.Tags.TryGetValue("reason", out var reason) &&
                reason == "host-shutdown");
    }

    private sealed class PausingDrainSignal(
        HostLifetimeReadinessDrainSignal actual) :
        IReadinessDrainSignal, IPreStopDrainNotification
    {
        private int _reads;
        private int _pauseAt = int.MaxValue;

        public TaskCompletionSource<bool> ReadPaused { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ResumeRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken PreStopStarted => actual.PreStopStarted;

        public bool IsDraining
        {
            get
            {
                var read = Interlocked.Increment(ref _reads);
                if (read == Volatile.Read(ref _pauseAt))
                {
                    // This is the post-dictionary-insert drain check,
                    // not a clock/timing guess: the test triggers the
                    // lifecycle transition while this read is paused.
                    ReadPaused.TrySetResult(true);
                    ResumeRead.Task.GetAwaiter().GetResult();
                }
                return actual.IsDraining;
            }
        }

        public void PauseAfterReads(int additionalReads) =>
            Volatile.Write(ref _pauseAt,
                Volatile.Read(ref _reads) + additionalReads);
    }

    [Fact]
    public async Task PreStopDuringPostInsertTrackAbortsSocketOnce()
    {
        using var capture = new MultiplayerMetricCapture();
        using var host = new StoppingHostLifetime();
        using var signal = new HostLifetimeReadinessDrainSignal(host);
        var drain = new PausingDrainSignal(signal);
        using var tracker = new LobbyConnectionDrainRegistry(drain, host);
        var socket = new FakeHubContext(Principal("alice"),
            connectionId: "post-insert-prestop");

        // One read in registry ctor, then 2 in TryTrack: initial admission
        // and the post-insert check. Pause at the latter, when the socket
        // is already visible to the registry's drain callback.
        drain.PauseAfterReads(2);
        var pending = Task.Run(() => tracker.TryTrack(socket));
        await drain.ReadPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            signal.BeginPreStopDrain();
            Assert.True(tracker.IsDraining);
            Assert.False(host.ApplicationStopping.IsCancellationRequested);
            Assert.Equal(1, socket.AbortCount);
        }
        finally
        {
            drain.ResumeRead.TrySetResult(true);
        }

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, socket.AbortCount);
        signal.BeginPreStopDrain();
        host.StopApplication();
        Assert.Equal(1, socket.AbortCount);
        Assert.Contains(
            capture.For(MultiplayerMetrics.SignalRDrainAbortAttemptsName),
            item => item.Value == 1 && item.Tags.Count == 1 &&
                    item.Tags.TryGetValue("reason", out var reason) &&
                    reason == "prestop");
    }

    [Fact]
    public async Task HostShutdownDuringHubConnectPostInsertCannotDoubleAbort()
    {
        using var capture = new MultiplayerMetricCapture();
        using var host = new StoppingHostLifetime();
        using var signal = new HostLifetimeReadinessDrainSignal(host);
        var drain = new PausingDrainSignal(signal);
        using var tracker = new LobbyConnectionDrainRegistry(drain, host);
        var socket = new FakeHubContext(Principal("alice"),
            connectionId: "raced-hub-connect");
        var hub = new LobbyRosterHub(new LobbyRosterReader(
            new InMemoryLobbyCoordinator()), drain: drain,
            connections: tracker) { Context = socket };

        // The Hub's first drain check, registry IsDraining, TryTrack's
        // pre-check and finally the post-insert check are 4 reads.
        // Force SIGTERM after the context is inserted, while the Hub's
        // callback awaits the result of TryTrack.
        drain.PauseAfterReads(4);
        var pending = Task.Run(() => hub.OnConnectedAsync());
        await drain.ReadPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            host.StopApplication();
            Assert.True(signal.IsDraining);
            Assert.Equal(1, socket.AbortCount);
        }
        finally
        {
            drain.ResumeRead.TrySetResult(true);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, socket.AbortCount); // Both registry and Hub raced.
        Assert.Contains(
            capture.For(MultiplayerMetrics.SignalRDrainAbortAttemptsName),
            item => item.Value == 1 && item.Tags.Count == 1 &&
                    item.Tags.TryGetValue("reason", out var reason) &&
                    reason == "host-shutdown");
    }

    [Fact]
    public async Task DisconnectedAfterInsertionIsNotAbortedByConcurrentPreStop()
    {
        using var host = new StoppingHostLifetime();
        using var signal = new HostLifetimeReadinessDrainSignal(host);
        var drain = new PausingDrainSignal(signal);
        using var tracker = new LobbyConnectionDrainRegistry(drain, host);
        var socket = new FakeHubContext(Principal("alice"),
            connectionId: "post-insert-disconnected");

        drain.PauseAfterReads(2);
        var pending = Task.Run(() => tracker.TryTrack(socket));
        await drain.ReadPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // A disconnect observed by the registry before the drain
            // snapshot must win the atomic entry state transition.
            tracker.Untrack(socket);
            signal.BeginPreStopDrain();
            Assert.True(tracker.IsDraining);
            Assert.Equal(0, socket.AbortCount);
        }
        finally
        {
            drain.ResumeRead.TrySetResult(true);
        }
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, socket.AbortCount);
        host.StopApplication();
        Assert.Equal(0, socket.AbortCount);
    }

    [Fact]
    public async Task PreStopAbortsOnlyActiveSocketsBeforeSigtermAndRejectsReconnects()
    {
        using var host = new StoppingHostLifetime();
        using var drain = new HostLifetimeReadinessDrainSignal(host);
        using var tracker = new LobbyConnectionDrainRegistry(drain, host);
        var reader = new LobbyRosterReader(new InMemoryLobbyCoordinator());

        var active = new FakeHubContext(Principal("alice"),
            connectionId: "prestop-active");
        var departed = new FakeHubContext(Principal("bob"),
            connectionId: "prestop-disconnected");
        var activeHub = new LobbyRosterHub(reader,
            drain: drain, connections: tracker) { Context = active };
        var departedHub = new LobbyRosterHub(reader,
            drain: drain, connections: tracker) { Context = departed };
        await activeHub.OnConnectedAsync();
        await departedHub.OnConnectedAsync();
        await departedHub.OnDisconnectedAsync(null);

        Assert.False(host.ApplicationStopping.IsCancellationRequested);
        Assert.False(tracker.IsDraining);
        Assert.Equal(0, active.AbortCount);
        drain.BeginPreStopDrain();

        Assert.True(drain.IsDraining);
        Assert.True(tracker.IsDraining);
        Assert.False(host.ApplicationStopping.IsCancellationRequested);
        Assert.Equal(1, active.AbortCount);
        Assert.Equal(0, departed.AbortCount); // No stale transport abort.

        // A duplicate SIGUSR1, followed by the ordinary SIGTERM fallback,
        // must not abort previously tracked sockets multiple times.
        drain.BeginPreStopDrain();
        host.StopApplication();
        Assert.Equal(1, active.AbortCount);
        Assert.Equal(0, departed.AbortCount);

        var late = new FakeHubContext(Principal("alice"),
            connectionId: "prestop-late");
        Assert.False(tracker.TryTrack(late));
        Assert.Equal(1, late.AbortCount);
        var lateViaHub = new FakeHubContext(Principal("bob"),
            connectionId: "prestop-late-hub");
        var lateHub = new LobbyRosterHub(reader,
            drain: drain, connections: tracker) { Context = lateViaHub };
        await lateHub.OnConnectedAsync();
        Assert.Equal(1, lateViaHub.AbortCount);
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(
                () => activeHub.GetRoster(RoomA))).Message);
    }

    [Fact]
    public async Task PreStopDuringInFlightHubRosterDeniesResultAndClosesSocket()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomA, ["white", "black"]);
        coordinator.Join(RoomA, "alice", "white", 0);
        var reader = new LobbyRosterReader(coordinator);
        using var host = new StoppingHostLifetime();
        using var drain = new HostLifetimeReadinessDrainSignal(host);
        using var tracker = new LobbyConnectionDrainRegistry(drain, host);
        var pending = new TaskCompletionSource<LobbyRosterView>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lobby = new ContendedLobby { Pending = pending };
        var context = new FakeHubContext(Principal("alice"),
            connectionId: "prestop-in-flight");
        var hub = new LobbyRosterHub(reader, lobby: lobby,
            drain: drain, connections: tracker) { Context = context };
        await hub.OnConnectedAsync();

        var inFlight = hub.GetRoster(RoomA);
        await lobby.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, lobby.Reads);
        Assert.Equal(0, context.AbortCount);

        // The async Redis roster result is already in flight but the node
        // enters preStop (not SIGTERM). No private result can be published.
        drain.BeginPreStopDrain();
        Assert.False(host.ApplicationStopping.IsCancellationRequested);
        Assert.Equal(1, context.AbortCount);
        pending.SetResult(reader.ForMember(RoomA, "alice"));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => inFlight)).Message);

        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(
                () => hub.GetRoster(RoomA))).Message);
        Assert.Equal(1, lobby.Reads); // No further Redis/member lookup.
        Assert.Equal(1, context.AbortCount);
    }

    [Fact]
    public void PreStopBeforeRegistryConstructionRejectsLateSocket()
    {
        using var host = new StoppingHostLifetime();
        using var drain = new HostLifetimeReadinessDrainSignal(host);
        drain.BeginPreStopDrain();

        // Lazy DI: no connection registry existed when SIGUSR1 arrived.
        // CancellationToken.Register invokes the callback immediately;
        // fresh sockets must still be rejected before SIGTERM.
        using var tracker = new LobbyConnectionDrainRegistry(drain, host);
        Assert.True(tracker.IsDraining);
        Assert.False(host.ApplicationStopping.IsCancellationRequested);
        var context = new FakeHubContext(Principal("alice"),
            connectionId: "prestop-already-draining");
        Assert.False(tracker.TryTrack(context));
        Assert.Equal(1, context.AbortCount);
        host.StopApplication();
        Assert.Equal(1, context.AbortCount);
    }

    [Fact]
    public async Task ShutdownAbortsTrackedLobbySocketsAndRejectsNewConnections()
    {
        using var host = new StoppingHostLifetime();
        using var drain = new HostLifetimeReadinessDrainSignal(host);
        using var tracker = new LobbyConnectionDrainRegistry(drain, host);
        var reader = new LobbyRosterReader(new InMemoryLobbyCoordinator());

        var first = new FakeHubContext(Principal("alice"),
            connectionId: "socket-1");
        var gone = new FakeHubContext(Principal("bob"),
            connectionId: "socket-2");
        var firstHub = new LobbyRosterHub(reader,
            drain: drain, connections: tracker) { Context = first };
        var goneHub = new LobbyRosterHub(reader,
            drain: drain, connections: tracker) { Context = gone };

        await firstHub.OnConnectedAsync();
        await goneHub.OnConnectedAsync();
        Assert.Equal(0, first.AbortCount);
        Assert.Equal(0, gone.AbortCount);
        await goneHub.OnDisconnectedAsync(null);

        host.StopApplication();
        Assert.True(drain.IsDraining);
        Assert.Equal(1, first.AbortCount);
        Assert.Equal(0, gone.AbortCount); // Disconnected contexts are removed.

        // CancellationToken callback executes once; repeated host shutdown
        // and repeated checks cannot create further abort side effects.
        host.StopApplication();
        Assert.Equal(1, first.AbortCount);

        var late = new FakeHubContext(Principal("alice"),
            connectionId: "socket-late");
        Assert.False(tracker.TryTrack(late));
        Assert.Equal(1, late.AbortCount);
        var rejectedHubContext = new FakeHubContext(Principal("alice"),
            connectionId: "socket-post-stop");
        var rejectedHub = new LobbyRosterHub(reader,
            drain: drain, connections: tracker)
        {
            Context = rejectedHubContext
        };
        await rejectedHub.OnConnectedAsync();
        Assert.Equal(1, rejectedHubContext.AbortCount);
        await firstHub.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task ExistingHubRefusesRosterBeforeAndAfterShutdownDuringInFlightRead()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomA, ["white", "black"]);
        coordinator.Join(RoomA, "alice", "white", 0);
        var reader = new LobbyRosterReader(coordinator);
        using var host = new StoppingHostLifetime();
        using var drain = new HostLifetimeReadinessDrainSignal(host);
        var pending = new TaskCompletionSource<LobbyRosterView>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lobby = new ContendedLobby { Pending = pending };
        var hub = new LobbyRosterHub(reader, lobby: lobby, drain: drain)
        {
            Context = new FakeHubContext(Principal("alice"))
        };

        var read = hub.GetRoster(RoomA);
        Assert.Equal(1, lobby.Reads);
        host.StopApplication();
        pending.SetResult(reader.ForMember(RoomA, "alice"));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => read)).Message);

        // Once stopping was observed, no subsequent roster request may
        // touch session or lobby persistence, even with a valid room ID.
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(
                () => hub.GetRoster(RoomA))).Message);
        Assert.Equal(1, lobby.Reads);
    }

    private static ClaimsPrincipal Principal(string playerId) =>
        new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, playerId)
        ], CookieAuthenticationDefaults.AuthenticationScheme));

    private sealed class FakeHubContext(
        ClaimsPrincipal user,
        string? spoofedUserIdentifier = null,
        string connectionId = "test-connection") : HubCallerContext
    {
        private int _abortCount;
        public int AbortCount => Volatile.Read(ref _abortCount);
        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => spoofedUserIdentifier;
        public override ClaimsPrincipal User => user;
        public override IDictionary<object, object?> Items { get; } =
            new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Interlocked.Increment(ref _abortCount);
    }
}
