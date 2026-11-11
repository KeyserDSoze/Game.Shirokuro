using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// A revoked cookie cannot repeat logout, so post-revocation cleanup needs
/// durable retry rather than relying on the browser to retry the request.
/// </summary>
public sealed class GuestLogoutRecoveryTests
{
    private sealed class TestIndex(TimeProvider clock) : IGuestExpiryIndex
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (string PlayerId, DateTimeOffset Due)> _items = new();
        public bool FailRevokedSchedule { get; set; }
        public bool FailAck { get; set; }
        public int RevokedSchedules { get; private set; }
        public int Acknowledgements { get; private set; }

        public Task ScheduleAsync(string playerId, DateTimeOffset expiresUtc,
            CancellationToken cancellationToken = default,
            string? trustedSessionToken = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                _items.Add(Guid.NewGuid().ToString("N"), (playerId, expiresUtc));
            return Task.CompletedTask;
        }

        public Task<string> ScheduleRevokedAsync(string playerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRevokedSchedule)
                throw new IOException("private Redis cleanup queue outage");
            var member = Guid.NewGuid().ToString("N");
            lock (_gate)
            {
                _items.Add(member, (playerId, clock.GetUtcNow()));
                RevokedSchedules++;
            }
            return Task.FromResult(member);
        }

        public Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                return Task.FromResult<IReadOnlyList<DueGuestExpiry>>(
                    _items.Where(x => x.Value.Due <= clock.GetUtcNow())
                        .Select(x => new DueGuestExpiry(x.Key, x.Value.PlayerId))
                        .Take(RedisGuestExpiryIndex.BatchSize)
                        .ToArray());
        }

        public Task AcknowledgeAsync(string member,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailAck)
                throw new IOException("private Redis ack outage");
            lock (_gate)
            {
                _items.Remove(member);
                Acknowledgements++;
            }
            return Task.CompletedTask;
        }

        public int DueCount
        {
            get { lock (_gate) return _items.Count(x => x.Value.Due <= clock.GetUtcNow()); }
        }
    }

    private sealed class ToggleLobby(IAsyncGuestLobby inner) : IAsyncGuestLobby
    {
        public bool FailCleanup { get; set; }
        public int CleanupCalls { get; private set; }

        public Task<LobbyRosterView> CreateAsync(string player, IReadOnlyList<string>? teams,
            string? team, CancellationToken token = default) =>
            inner.CreateAsync(player, teams, team, token);
        public Task<LobbyRosterView> JoinAsync(string id, string player, string? team,
            long? revision, CancellationToken token = default) =>
            inner.JoinAsync(id, player, team, revision, token);
        public Task<LobbyRosterView> ChooseHeroAsync(string id, string player,
            string? hero, long revision, CancellationToken token = default) =>
            inner.ChooseHeroAsync(id, player, hero, revision, token);
        public Task<LobbyRosterView> ChangeTeamAsync(string id, string player,
            string? team, long revision, CancellationToken token = default) =>
            inner.ChangeTeamAsync(id, player, team, revision, token);
        public Task LeaveAsync(string id, string player, long revision,
            CancellationToken token = default) =>
            inner.LeaveAsync(id, player, revision, token);
        public Task<LobbyRosterView> ForMemberAsync(string id, string player,
            CancellationToken token = default) =>
            inner.ForMemberAsync(id, player, token);

        public Task<int> RemovePlayerFromAllRoomsAsync(string player,
            CancellationToken token = default)
        {
            CleanupCalls++;
            if (FailCleanup)
                throw new IOException("lobby CAS storage unavailable");
            return inner.RemovePlayerFromAllRoomsAsync(player, token);
        }
    }

    private sealed class TestFence : IGuestRevocationFence
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }

        public Task FenceGuestAsync(string id, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Fail)
                throw new IOException("sensitive Redis fence exception");
            Assert.StartsWith("guest-", id, StringComparison.Ordinal);
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static (WebApplicationFactory<Program> Server, TestIndex Index,
        ToggleLobby Lobby, TestFence Fence) Setup()
    {
        var clock = TimeProvider.System;
        var index = new TestIndex(clock);
        var fence = new TestFence();
        var coordinator = new InMemoryLobbyCoordinator(clock);
        var reader = new LobbyRosterReader(coordinator);
        var commands = new GuestLobbyCommands(coordinator, reader);
        var lobby = new ToggleLobby(new LocalGuestLobbyRuntime(commands, reader, coordinator));

        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGuestExpiryIndex>();
                services.AddSingleton<IGuestExpiryIndex>(index);
                services.RemoveAll<IAsyncGuestLobby>();
                services.AddSingleton<IAsyncGuestLobby>(lobby);
                services.RemoveAll<IGuestRevocationFence>();
                services.AddSingleton<IGuestRevocationFence>(fence);
                services.RemoveAll<InMemoryLobbyCoordinator>();
                services.AddSingleton(coordinator);
            });
        });
        return (server, index, lobby, fence);
    }

    private static HttpClient Client(WebApplicationFactory<Program> server,
        string? cookie = null)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        if (cookie is not null)
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static HttpRequestMessage Mutate(HttpMethod method, string route,
        object? payload = null)
    {
        var req = new HttpRequestMessage(method, route);
        req.Headers.Add("Origin", "https://game.example");
        req.Headers.Add("X-Shirokuro-Request", "1");
        if (payload is not null)
            req.Content = JsonContent.Create(payload);
        return req;
    }

    private static async Task<(string Id, string Cookie)> Issue(HttpClient client)
    {
        using var req = Mutate(HttpMethod.Post, "/api/sessions/guest");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("playerId").GetString()!,
            Assert.Single(resp.Headers.GetValues("Set-Cookie")).Split(';')[0]);
    }

    private static async Task<string> Room(HttpClient client)
    {
        using var req = Mutate(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" });
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("lobbyId").GetString()!;
    }

    private static async Task<HttpResponseMessage> Logout(HttpClient client)
    {
        using var req = Mutate(HttpMethod.Delete, "/api/sessions/guest");
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task RevokedGuestOutageReturns503AndOtherNodeWorkerCleansDueWork()
    {
        var (server, index, lobby, fence) = Setup();
        using (server)
        using (var visitor = Client(server))
        {
            var (alice, cookie) = await Issue(visitor);
            var roomOwner = Client(server, cookie);
            using (roomOwner)
            {
                var room = await Room(roomOwner);
                lobby.FailCleanup = true;
                using (var response = await Logout(roomOwner))
                {
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                    Assert.Contains("expires=", Assert.Single(
                        response.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
                    Assert.True(response.Headers.CacheControl?.NoStore == true);
                    Assert.DoesNotContain(alice, await response.Content.ReadAsStringAsync());
                }
                Assert.Equal(1, fence.Calls);
                Assert.Equal(1, index.RevokedSchedules);
                Assert.Equal(1, index.DueCount);
                Assert.Equal(1, lobby.CleanupCalls);

                using (var oldTicket = await roomOwner.GetAsync("/api/sessions/me"))
                    Assert.Equal(HttpStatusCode.Unauthorized, oldTicket.StatusCode);
                Assert.Equal(1, server.Services.GetRequiredService<InMemoryLobbyCoordinator>()
                    .StoredRoomCount);

                // Emulates a different node picking up the encrypted due job.
                lobby.FailCleanup = false;
                var worker = new GuestSessionExpiryWorker(
                    server.Services.GetRequiredService<GuestSessionRegistry>(),
                    server.Services.GetRequiredService<InMemoryLobbyCoordinator>(),
                    TimeProvider.System, lobby, index, fence);
                Assert.Equal(1, await worker.SweepOnceAsync());
                Assert.Equal(0, await worker.SweepOnceAsync());
                Assert.Equal(0, index.DueCount);
                Assert.Equal(0, server.Services.GetRequiredService<InMemoryLobbyCoordinator>()
                    .StoredRoomCount);
            }
        }
    }

    [Fact]
    public async Task QueueOutageDoesNotPreventDirectLogoutCleanup()
    {
        var (server, index, lobby, fence) = Setup();
        using (server)
        using (var visitor = Client(server))
        {
            var (_, cookie) = await Issue(visitor);
            using var player = Client(server, cookie);
            await Room(player);
            index.FailRevokedSchedule = true;
            using var result = await Logout(player);
            Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
            Assert.Contains("expires=", Assert.Single(
                result.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, lobby.CleanupCalls);
            Assert.Equal(0, index.DueCount);
            Assert.Equal(0, server.Services.GetRequiredService<InMemoryLobbyCoordinator>()
                .StoredRoomCount);
            using var rejected = await player.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
    }

    [Fact]
    public async Task QueueAndLobbyOutageNeverReportSuccessfulCleanup()
    {
        var (server, index, lobby, fence) = Setup();
        using (server)
        using (var visitor = Client(server))
        {
            var (_, cookie) = await Issue(visitor);
            using var player = Client(server, cookie);
            await Room(player);
            index.FailRevokedSchedule = true;
            lobby.FailCleanup = true;
            using var result = await Logout(player);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
            Assert.Contains("expires=", Assert.Single(
                result.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, index.RevokedSchedules);
            Assert.Equal(1, lobby.CleanupCalls);
            Assert.Equal(1, server.Services.GetRequiredService<InMemoryLobbyCoordinator>()
                .StoredRoomCount);
            using var rejected = await player.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
    }

    [Fact]
    public async Task RedisFenceOutageStillRevokesAndRetriesWithoutAcknowledgingCleanup()
    {
        var (server, index, lobby, fence) = Setup();
        using (server)
        using (var visitor = Client(server))
        {
            var (_, cookie) = await Issue(visitor);
            using var player = Client(server, cookie);
            await Room(player);

            // Revocation succeeds but the fence write fails. Even if direct
            // cleanup succeeds, the API must NOT claim a fully fenced logout,
            // and the due item must survive to retry fencing on another node.
            fence.Fail = true;
            using (var response = await Logout(player))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Contains("expires=", Assert.Single(response.Headers.GetValues("Set-Cookie")),
                    StringComparison.OrdinalIgnoreCase);
                Assert.True(response.Headers.CacheControl?.NoStore == true);
            }
            Assert.Equal(0, fence.Calls);
            Assert.Equal(1, lobby.CleanupCalls);
            Assert.Equal(1, index.DueCount);
            using (var staleCookie = await player.GetAsync("/api/sessions/me"))
                Assert.Equal(HttpStatusCode.Unauthorized, staleCookie.StatusCode);

            // Failing the fence in the worker must retain work without ack.
            var worker = new GuestSessionExpiryWorker(
                server.Services.GetRequiredService<GuestSessionRegistry>(),
                server.Services.GetRequiredService<InMemoryLobbyCoordinator>(),
                TimeProvider.System, lobby, index, fence);
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SweepOnceAsync());
            Assert.Equal(1, index.DueCount);

            fence.Fail = false;
            Assert.Equal(0, await worker.SweepOnceAsync());
            Assert.Equal(0, index.DueCount);
            Assert.Equal(1, fence.Calls);
        }
    }

    [Fact]
    public async Task FailedAckStillReturnsSuccessfulLogoutAndIsIdempotentlyRetried()
    {
        var (server, index, lobby, fence) = Setup();
        using (server)
        using (var visitor = Client(server))
        {
            var (_, cookie) = await Issue(visitor);
            using var player = Client(server, cookie);
            await Room(player);
            index.FailAck = true;
            using var result = await Logout(player);
            Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
            Assert.Equal(1, index.DueCount);
            Assert.Equal(0, server.Services.GetRequiredService<InMemoryLobbyCoordinator>()
                .StoredRoomCount);

            index.FailAck = false;
            var worker = new GuestSessionExpiryWorker(
                server.Services.GetRequiredService<GuestSessionRegistry>(),
                server.Services.GetRequiredService<InMemoryLobbyCoordinator>(),
                TimeProvider.System, lobby, index, fence);
            Assert.Equal(0, await worker.SweepOnceAsync());
            Assert.Equal(0, index.DueCount);
            Assert.True(index.Acknowledgements > 0);
        }
    }
}
