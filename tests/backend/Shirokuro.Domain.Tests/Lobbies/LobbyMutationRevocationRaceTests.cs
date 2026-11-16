using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Real HTTP mutation + concurrent logout on TestServer, using a deliberately
/// delayed already-committed lobby command. No network LB/TLS claims.
/// </summary>
public sealed class LobbyMutationRevocationRaceTests
{
    private const string Room = "room-cccccccccccccccccccccccccccccccc";
    // Every fixture signs in two guests; issuing a shared ticket now proves
    // persistence with one GET each before regular cookie authentication.
    private const int IssuanceReadbacks = 2;

    private sealed class ManualDrain : IReadinessDrainSignal
    {
        private int _started;
        public bool IsDraining => Volatile.Read(ref _started) != 0;
        public void Start() => Interlocked.Exchange(ref _started, 1);
    }

    private sealed class CompletedButHeldLobby(
        GuestLobbyCommands commands, LobbyRosterReader reader,
        InMemoryLobbyCoordinator coordinator) : IAsyncGuestLobby
    {
        private readonly LocalGuestLobbyRuntime _local =
            new(commands, reader, coordinator);
        public TaskCompletionSource<bool> Committed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CreatedRoomId { get; private set; }

        private async Task<LobbyRosterView> Hold(
            Task<LobbyRosterView> command, CancellationToken cancellationToken)
        {
            var roster = await command;
            CreatedRoomId ??= roster.LobbyId;
            Committed.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return roster;
        }

        public Task<LobbyRosterView> CreateAsync(string actor,
            IReadOnlyList<string>? teams, string? team,
            CancellationToken cancellationToken = default) =>
            Hold(_local.CreateAsync(actor, teams, team, cancellationToken),
                cancellationToken);
        public Task<LobbyRosterView> JoinAsync(string room, string actor,
            string? team, long? revision,
            CancellationToken cancellationToken = default) =>
            Hold(_local.JoinAsync(room, actor, team, revision, cancellationToken),
                cancellationToken);
        public Task<LobbyRosterView> ChooseHeroAsync(string room, string actor,
            string? hero, long revision,
            CancellationToken cancellationToken = default) =>
            Hold(_local.ChooseHeroAsync(room, actor, hero, revision, cancellationToken),
                cancellationToken);
        public Task<LobbyRosterView> ChangeTeamAsync(string room, string actor,
            string? team, long revision,
            CancellationToken cancellationToken = default) =>
            Hold(_local.ChangeTeamAsync(room, actor, team, revision, cancellationToken),
                cancellationToken);
        public Task LeaveAsync(string room, string actor, long revision,
            CancellationToken cancellationToken = default) =>
            _local.LeaveAsync(room, actor, revision, cancellationToken);
        public Task<LobbyRosterView> ForMemberAsync(string room, string actor,
            CancellationToken cancellationToken = default) =>
            _local.ForMemberAsync(room, actor, cancellationToken);
        public Task<LobbyRosterView> PeekForMemberAsync(string room, string actor,
            CancellationToken cancellationToken = default) =>
            _local.PeekForMemberAsync(room, actor, cancellationToken);
        public Task<int> RemovePlayerFromAllRoomsAsync(string actor,
            CancellationToken cancellationToken = default) =>
            _local.RemovePlayerFromAllRoomsAsync(actor, cancellationToken);
    }

    private sealed class FailingSessionCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _data = new();
        private int _gets;
        public int Reads => Volatile.Read(ref _gets);
        public bool FailReads { get; set; }
        public Action<int>? OnRead { get; set; }
        public byte[]? Get(string key)
        {
            var current = Interlocked.Increment(ref _gets);
            OnRead?.Invoke(current);
            if (FailReads)
                throw new IOException("private redis/session connection detail");
            return _data.TryGetValue(key, out var result) ? result : null;
        }
        public Task<byte[]?> GetAsync(string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Get(key));
        }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => _data[key] = value;
        public Task SetAsync(string key, byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Set(key, value, options);
            return Task.CompletedTask;
        }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _data.TryRemove(key, out _);
        public Task RemoveAsync(string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private static WebApplicationFactory<Program> Server(
        FailingSessionCache? cache = null, ManualDrain? drain = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAsyncGuestLobby>();
                services.AddSingleton<CompletedButHeldLobby>();
                services.AddSingleton<IAsyncGuestLobby>(sp =>
                    sp.GetRequiredService<CompletedButHeldLobby>());
                if (cache is not null)
                {
                    services.RemoveAll<IDistributedCache>();
                    services.AddSingleton<IDistributedCache>(cache);
                }
                if (drain is not null)
                {
                    services.RemoveAll<IReadinessDrainSignal>();
                    services.AddSingleton<IReadinessDrainSignal>(drain);
                }
            }));

    private static HttpClient Client(WebApplicationFactory<Program> server,
        string? cookie = null)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        if (cookie is not null)
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static HttpRequestMessage Mutation(HttpMethod method, string path, object? body,
        bool withBody = true)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        request.Headers.TryAddWithoutValidation(
            WebMutationGuard.RequestHeaderName, WebMutationGuard.RequestHeaderValue);
        if (withBody)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<(string PlayerId, string Cookie)> SignIn(
        HttpClient visitor, CancellationToken cancellationToken)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest",
            body: null, withBody: false);
        using var response = await visitor.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return (json.RootElement.GetProperty("playerId").GetString()!,
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0]);
    }

    private static HttpRequestMessage Operation(string name) => name switch
    {
        "create" => Mutation(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" }),
        "join" => Mutation(HttpMethod.Post, $"/api/lobbies/{Room}/join",
            new { teamId = "black", expectedRevision = 1 }),
        "hero" => Mutation(HttpMethod.Patch, $"/api/lobbies/{Room}/hero",
            new { heroId = "kira", expectedRevision = 2 }),
        "team" => Mutation(HttpMethod.Patch, $"/api/lobbies/{Room}/team",
            new { teamId = "black", expectedRevision = 2 }),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static void PrepareRoom(
        InMemoryLobbyCoordinator coordinator, string operation,
        string actor, string survivor)
    {
        coordinator.Create(Room, ["white", "black"]);
        if (operation == "join" || operation == "create")
        {
            coordinator.Join(Room, survivor, "white", 0);
            return;
        }
        coordinator.Join(Room, actor, "white", 0);
        coordinator.Join(Room, survivor, "black", 1);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("join")]
    [InlineData("hero")]
    [InlineData("team")]
    public async Task CompletedRosterMutationCannotReturnPrivateResultAfterRealHttpLogout(
        string operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = Server();
        using var actorVisitor = Client(server);
        using var survivorVisitor = Client(server);
        var actorIdentity = await SignIn(actorVisitor, timeout.Token);
        var survivorIdentity = await SignIn(survivorVisitor, timeout.Token);
        using var actor = Client(server, actorIdentity.Cookie);
        using var survivor = Client(server, survivorIdentity.Cookie);
        var coordinator = server.Services.GetRequiredService<InMemoryLobbyCoordinator>();
        PrepareRoom(coordinator, operation, actorIdentity.PlayerId, survivorIdentity.PlayerId);
        var paused = server.Services.GetRequiredService<CompletedButHeldLobby>();

        using var request = Operation(operation);
        var inFlight = actor.SendAsync(request, timeout.Token);
        await paused.Committed.Task.WaitAsync(timeout.Token);

        // The command has already mutated real lobby state, but has not
        // returned its body or (for create) a new private room Location.
        using var logout = Mutation(HttpMethod.Delete,
            "/api/sessions/guest", body: null, withBody: false);
        using var loggedOut = await actor.SendAsync(logout, timeout.Token);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        Assert.True(loggedOut.Headers.CacheControl?.NoStore == true);

        paused.Release.TrySetResult(true);
        using var denied = await inFlight.WaitAsync(timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl?.NoStore == true);
        Assert.False(denied.Headers.Contains("Location"));
        Assert.False(denied.Headers.Contains("ETag"));
        Assert.False(denied.Headers.Contains("Set-Cookie"));
        var body = await denied.Content.ReadAsStringAsync(timeout.Token);
        Assert.DoesNotContain(Room, body, StringComparison.Ordinal);
        Assert.DoesNotContain(actorIdentity.PlayerId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(survivorIdentity.PlayerId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("participants", body, StringComparison.OrdinalIgnoreCase);

        using var revoked = await actor.GetAsync("/api/sessions/me", timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        // A different signed guest keeps their existing room and sees the
        // cleanup result, never the signed-out actor.
        using var rosterReply = await survivor.GetAsync(
            $"/api/lobbies/{Room}/roster", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, rosterReply.StatusCode);
        using var parsed = JsonDocument.Parse(
            await rosterReply.Content.ReadAsStringAsync(timeout.Token));
        var participants = parsed.RootElement.GetProperty("participants");
        var survivorOnly = Assert.Single(participants.EnumerateArray());
        Assert.Equal(survivorIdentity.PlayerId,
            survivorOnly.GetProperty("playerId").GetString());

        if (operation == "create")
        {
            Assert.NotNull(paused.CreatedRoomId);
            Assert.NotEqual(Room, paused.CreatedRoomId);
            using var absent = await survivor.GetAsync(
                $"/api/lobbies/{paused.CreatedRoomId}/roster", timeout.Token);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("join")]
    [InlineData("hero")]
    [InlineData("team")]
    public async Task SessionAuthorityOutageAfterCommittedMutationReturns503WithoutRoster(
        string operation)
    {
        var cache = new FailingSessionCache();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = Server(cache);
        using var actorVisitor = Client(server);
        using var survivorVisitor = Client(server);
        var actorIdentity = await SignIn(actorVisitor, timeout.Token);
        var survivorIdentity = await SignIn(survivorVisitor, timeout.Token);
        using var actor = Client(server, actorIdentity.Cookie);
        var coordinator = server.Services.GetRequiredService<InMemoryLobbyCoordinator>();
        PrepareRoom(coordinator, operation, actorIdentity.PlayerId, survivorIdentity.PlayerId);
        var paused = server.Services.GetRequiredService<CompletedButHeldLobby>();

        using var request = Operation(operation);
        var inFlight = actor.SendAsync(request, timeout.Token);
        await paused.Committed.Task.WaitAsync(timeout.Token);
        Assert.True(cache.Reads >= IssuanceReadbacks + 1); // Pre-handler auth validated the cookie.

        cache.FailReads = true;
        paused.Release.TrySetResult(true);
        using var denied = await inFlight.WaitAsync(timeout.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl?.NoStore == true);
        Assert.False(denied.Headers.Contains("Location"));
        Assert.False(denied.Headers.Contains("ETag"));
        Assert.False(denied.Headers.Contains("Set-Cookie"));
        var body = await denied.Content.ReadAsStringAsync(timeout.Token);
        Assert.DoesNotContain(Room, body, StringComparison.Ordinal);
        Assert.DoesNotContain(actorIdentity.PlayerId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(survivorIdentity.PlayerId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("participants", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private redis", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(cache.Reads >= IssuanceReadbacks + 2); // Post-command guest authority lookup.
    }
    [Theory]
    [InlineData("create")]
    [InlineData("join")]
    [InlineData("hero")]
    [InlineData("team")]
    public async Task ActiveGuestReceivesCommittedRosterOnlyAfterSecondSessionCheck(
        string operation)
    {
        var cache = new FailingSessionCache();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = Server(cache);
        using var actorVisitor = Client(server);
        using var survivorVisitor = Client(server);
        var actorIdentity = await SignIn(actorVisitor, timeout.Token);
        var survivorIdentity = await SignIn(survivorVisitor, timeout.Token);
        using var actor = Client(server, actorIdentity.Cookie);
        var coordinator = server.Services.GetRequiredService<InMemoryLobbyCoordinator>();
        PrepareRoom(coordinator, operation, actorIdentity.PlayerId, survivorIdentity.PlayerId);
        var paused = server.Services.GetRequiredService<CompletedButHeldLobby>();

        using var request = Operation(operation);
        var inFlight = actor.SendAsync(request, timeout.Token);
        await paused.Committed.Task.WaitAsync(timeout.Token);
        Assert.Equal(IssuanceReadbacks + 1, cache.Reads); // Cookie middleware, not post-command yet.
        paused.Release.TrySetResult(true);

        using var result = await inFlight.WaitAsync(timeout.Token);
        Assert.Equal(operation == "create" ? HttpStatusCode.Created : HttpStatusCode.OK,
            result.StatusCode);
        Assert.True(result.Headers.CacheControl?.NoStore == true);
        Assert.Equal(IssuanceReadbacks + 2, cache.Reads); // Exactly one additional authority lookup.
        using var json = JsonDocument.Parse(
            await result.Content.ReadAsStringAsync(timeout.Token));
        Assert.Contains(json.RootElement.GetProperty("participants").EnumerateArray(),
            member => member.GetProperty("playerId").GetString() ==
                      actorIdentity.PlayerId);
        if (operation == "create")
            Assert.Equal(
                $"/api/lobbies/{paused.CreatedRoomId}/roster",
                result.Headers.Location?.OriginalString);
        else
            Assert.False(result.Headers.Contains("Location"));
    }

    [Fact]
    public async Task RejectedMutationDoesNotSpendAdditionalGuestAuthorityRead()
    {
        var cache = new FailingSessionCache();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = Server(cache);
        using var actorVisitor = Client(server);
        using var survivorVisitor = Client(server);
        var actorIdentity = await SignIn(actorVisitor, timeout.Token);
        var survivorIdentity = await SignIn(survivorVisitor, timeout.Token);
        using var actor = Client(server, actorIdentity.Cookie);
        PrepareRoom(server.Services.GetRequiredService<InMemoryLobbyCoordinator>(),
            "hero", actorIdentity.PlayerId, survivorIdentity.PlayerId);

        using var request = Mutation(HttpMethod.Patch,
            $"/api/lobbies/{Room}/hero",
            new { heroId = "not-a-registered-hero", expectedRevision = 2 });
        using var response = await actor.SendAsync(request, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(IssuanceReadbacks + 1, cache.Reads); // Failed before post-mutation roster check.
        Assert.False(response.Headers.Contains("Location"));
        Assert.True(response.Headers.CacheControl?.NoStore == true);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("join")]
    [InlineData("hero")]
    [InlineData("team")]
    public async Task PreStopAfterCommittedMutationDeniesRosterWithoutAnotherGuestRedisLookup(
        string operation)
    {
        var cache = new FailingSessionCache();
        var drain = new ManualDrain();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = Server(cache, drain);
        using var actorVisitor = Client(server);
        using var survivorVisitor = Client(server);
        var actorIdentity = await SignIn(actorVisitor, timeout.Token);
        var survivorIdentity = await SignIn(survivorVisitor, timeout.Token);
        using var actor = Client(server, actorIdentity.Cookie);
        var coordinator = server.Services.GetRequiredService<InMemoryLobbyCoordinator>();
        PrepareRoom(coordinator, operation, actorIdentity.PlayerId, survivorIdentity.PlayerId);
        var paused = server.Services.GetRequiredService<CompletedButHeldLobby>();

        using var request = Operation(operation);
        var inFlight = actor.SendAsync(request, timeout.Token);
        await paused.Committed.Task.WaitAsync(timeout.Token);
        Assert.Equal(IssuanceReadbacks + 1, cache.Reads); // The issuing guest passed cookie auth.
        drain.Start(); // preStop happens after the command already committed.
        paused.Release.TrySetResult(true);

        using var denied = await inFlight.WaitAsync(timeout.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl?.NoStore == true);
        Assert.False(denied.Headers.Contains("Location"));
        Assert.False(denied.Headers.Contains("ETag"));
        Assert.False(denied.Headers.Contains("Set-Cookie"));
        var body = await denied.Content.ReadAsStringAsync(timeout.Token);
        Assert.DoesNotContain(Room, body, StringComparison.Ordinal);
        Assert.DoesNotContain(actorIdentity.PlayerId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(survivorIdentity.PlayerId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("participants", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IssuanceReadbacks + 1, cache.Reads); // Drain short-circuits the post-read Redis lookup.

        // Drain does not undo an earlier legitimate command or remove a
        // different guest's membership. Verify state directly, since
        // member-only HTTP GETs are intentionally 503 while draining.
        var reader = server.Services.GetRequiredService<LobbyRosterReader>();
        var committedRoom = operation == "create" ? paused.CreatedRoomId! : Room;
        var committed = reader.ForMember(committedRoom, actorIdentity.PlayerId);
        Assert.Contains(committed.Participants,
            member => member.PlayerId == actorIdentity.PlayerId);
        var survivorRoom = reader.ForMember(Room, survivorIdentity.PlayerId);
        Assert.Contains(survivorRoom.Participants,
            member => member.PlayerId == survivorIdentity.PlayerId);
        if (operation == "create")
            Assert.DoesNotContain(survivorRoom.Participants,
                member => member.PlayerId == actorIdentity.PlayerId);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("join")]
    [InlineData("hero")]
    [InlineData("team")]
    public async Task PreStopDuringFinalGuestCheckOverridesActiveSession(
        string operation)
    {
        var cache = new FailingSessionCache();
        var drain = new ManualDrain();
        // This callback executes at the exact second registry lookup,
        // after the initial signed cookie was authenticated and the lobby
        // command was released. The result itself remains Active.
        cache.OnRead = count =>
        {
            if (count == IssuanceReadbacks + 2) drain.Start();
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = Server(cache, drain);
        using var actorVisitor = Client(server);
        using var survivorVisitor = Client(server);
        var actorIdentity = await SignIn(actorVisitor, timeout.Token);
        var survivorIdentity = await SignIn(survivorVisitor, timeout.Token);
        using var actor = Client(server, actorIdentity.Cookie);
        PrepareRoom(server.Services.GetRequiredService<InMemoryLobbyCoordinator>(),
            operation, actorIdentity.PlayerId, survivorIdentity.PlayerId);
        var paused = server.Services.GetRequiredService<CompletedButHeldLobby>();

        using var request = Operation(operation);
        var inFlight = actor.SendAsync(request, timeout.Token);
        await paused.Committed.Task.WaitAsync(timeout.Token);
        Assert.False(drain.IsDraining);
        Assert.Equal(IssuanceReadbacks + 1, cache.Reads);
        paused.Release.TrySetResult(true);

        using var denied = await inFlight.WaitAsync(timeout.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.True(drain.IsDraining);
        Assert.Equal(IssuanceReadbacks + 2, cache.Reads);
        Assert.True(denied.Headers.CacheControl?.NoStore == true);
        Assert.False(denied.Headers.Contains("Location"));
        Assert.False(denied.Headers.Contains("ETag"));
        Assert.False(denied.Headers.Contains("Set-Cookie"));
        var body = await denied.Content.ReadAsStringAsync(timeout.Token);
        Assert.DoesNotContain(Room, body, StringComparison.Ordinal);
        Assert.DoesNotContain(actorIdentity.PlayerId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(survivorIdentity.PlayerId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("participants", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("room-", body, StringComparison.OrdinalIgnoreCase);
    }

}
