using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Security;
using Shirokuro.Api.Readiness;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// A browser's invalid mutation intent must be rejected by endpoint metadata
/// before guest cookie validation can read Redis, even with a signed cookie.
/// </summary>
public sealed class PreAuthenticationMutationGuardTests
{
    /// <summary>
    /// A real HTTP body stream: transport sends all but the final JSON byte
    /// before pausing. Endpoint middleware must already have authenticated
    /// the cookie (and acquired create quota) while JSON binding is stuck.
    /// </summary>
    private sealed class PausedJsonContent : HttpContent
    {
        private readonly byte[] _payload;

        public PausedJsonContent(string json)
        {
            _payload = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        public TaskCompletionSource<bool> FirstChunkSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ResumeBody { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync(
                _payload.AsMemory(0, _payload.Length - 1), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            FirstChunkSent.TrySetResult(true);
            await ResumeBody.Task.WaitAsync(cancellationToken);
            await stream.WriteAsync(
                _payload.AsMemory(_payload.Length - 1, 1), cancellationToken);
        }
    }

    private sealed class CountingCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _data = new();
        private int _reads;
        private int _writes;
        private int _removes;
        public int Reads => Volatile.Read(ref _reads);
        public int Writes => Volatile.Read(ref _writes);
        public int Removes => Volatile.Read(ref _removes);
        public int StoredGuestKeys => _data.Keys.Count(key =>
            key.StartsWith("guest-session:", StringComparison.Ordinal));
        public bool Unavailable { get; set; }
        public bool RemoveUnavailable { get; set; }
        // Pause an in-handler Redis SET until ApplicationStopping is raised.
        public TaskCompletionSource<bool>? SetStarted { get; set; }
        public TaskCompletionSource<bool>? ResumeSet { get; set; }
        // Optional async gate for an AUTHENTICATED cookie lookup already in
        // flight before ApplicationStopping; never delay normal fixtures.
        public TaskCompletionSource<bool>? ReadStarted { get; set; }
        public TaskCompletionSource<bool>? ResumeRead { get; set; }
        public void ExpireAll() => _data.Clear();

        public byte[]? Get(string key) =>
            _data.TryGetValue(key, out var value) ? value : null;

        public async Task<byte[]?> GetAsync(string key,
            CancellationToken token = default)
        {
            Interlocked.Increment(ref _reads);
            ReadStarted?.TrySetResult(true);
            if (ResumeRead is { } gate)
                await gate.Task.WaitAsync(token);
            if (Unavailable)
                throw new IOException("Private Redis endpoint and password");
            return Get(key);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Interlocked.Increment(ref _writes);
            _data[key] = value;
        }

        public async Task SetAsync(string key, byte[] value,
            DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            SetStarted?.TrySetResult(true);
            if (ResumeSet is { } gate)
                await gate.Task.WaitAsync(token);
            Set(key, value, options);
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) =>
            Task.CompletedTask;

        public void Remove(string key)
        {
            Interlocked.Increment(ref _removes);
            if (RemoveUnavailable)
                throw new IOException("Private Redis rollback endpoint");
            _data.TryRemove(key, out _);
        }
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingLimiter : IGuestMutationLimiter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Allowed { get; set; } = true;
        public bool Unavailable { get; set; }
        // Control a quota already acquired by the ingress middleware but
        // not yet completed. A permit may be spent; no room may be created.
        public TaskCompletionSource<bool>? PermitStarted { get; set; }
        public TaskCompletionSource<GuestMutationDecision>? PendingPermit { get; set; }

        public Task<GuestMutationDecision> AcquireAsync(
            GuestMutationRateLimit rule, IPAddress? clientIp,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            PermitStarted?.TrySetResult(true);
            if (PendingPermit is { } gate)
                return gate.Task.WaitAsync(cancellationToken);
            if (Unavailable)
                throw new IOException("Sensitive Redis connection details");
            return Task.FromResult(new GuestMutationDecision(Allowed, 60_000));
        }
    }

    private static readonly (HttpMethod Method, string Path)[] MutationRoutes =
    [
        (HttpMethod.Post, "/api/sessions/guest"),
        (HttpMethod.Delete, "/api/sessions/guest"),
        (HttpMethod.Post, "/api/lobbies"),
        (HttpMethod.Post, "/api/lobbies/room-00000000000000000000000000000000/join"),
        (HttpMethod.Patch, "/api/lobbies/room-00000000000000000000000000000000/hero"),
        (HttpMethod.Patch, "/api/lobbies/room-00000000000000000000000000000000/team"),
        (HttpMethod.Delete, "/api/lobbies/room-00000000000000000000000000000000/membership")
    ];

    private sealed class ManualDrainSignal : IReadinessDrainSignal
    {
        private int _stopping;
        public bool IsDraining => Volatile.Read(ref _stopping) != 0;
        public void Stop() => Interlocked.Exchange(ref _stopping, 1);
    }

    private sealed class PausingExpiryIndex : IGuestExpiryIndex
    {
        private int _scheduled;
        public int Scheduled => Volatile.Read(ref _scheduled);
        public TaskCompletionSource<bool> ScheduleStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ResumeSchedule { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ScheduleAsync(string trustedPlayerId,
            DateTimeOffset expiresUtc,
            CancellationToken cancellationToken = default,
            string? trustedSessionToken = null)
        {
            Interlocked.Increment(ref _scheduled);
            ScheduleStarted.TrySetResult(true);
            await ResumeSchedule.Task.WaitAsync(cancellationToken);
        }

        public Task<string> ScheduleRevokedAsync(string trustedPlayerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("unused");
        public Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DueGuestExpiry>>(Array.Empty<DueGuestExpiry>());
        public Task AcknowledgeAsync(string protectedMember,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ReadyProbe : IDeploymentReadiness
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private static WebApplicationFactory<Program> Production(
        CountingCache cache, CountingLimiter limiter, bool stubReadiness = false,
        string? recoveryCutoff = null, IReadinessDrainSignal? drain = null,
        IGuestExpiryIndex? expiryIndex = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder, cache);
            // The operator cutoff is sampled from builder.Configuration
            // during Program startup, before late app-config callbacks.
            // Supply it as an early WebHost setting as well as a late
            // provider, like the shared DP key-ring test configuration.
            if (recoveryCutoff is not null)
                builder.UseSetting(GuestSessionRecoveryCutoff.ConfigurationKey,
                    recoveryCutoff);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                };
                if (recoveryCutoff is not null)
                    settings[GuestSessionRecoveryCutoff.ConfigurationKey] = recoveryCutoff;
                config.AddInMemoryCollection(settings);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGuestMutationLimiter>();
                services.AddSingleton<IGuestMutationLimiter>(limiter);
                if (expiryIndex is not null)
                {
                    services.RemoveAll<IGuestExpiryIndex>();
                    services.AddSingleton(expiryIndex);
                }
                if (drain is not null)
                {
                    services.RemoveAll<IReadinessDrainSignal>();
                    services.AddSingleton(drain);
                }
                if (stubReadiness)
                {
                    services.RemoveAll<IDeploymentReadiness>();
                    services.AddSingleton<IDeploymentReadiness, ReadyProbe>();
                }
            });
        });

    private static HttpClient Client(
        WebApplicationFactory<Program> server, bool development = false,
        string? cookie = null)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(development
                ? "http://localhost" : "https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static HttpRequestMessage Mutation(
        HttpMethod method, string path, string? origin, bool withIntent = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        if (withIntent)
            request.Headers.Add(WebMutationGuard.RequestHeaderName,
                WebMutationGuard.RequestHeaderValue);
        return request;
    }

    private static async Task<string> Issue(
        HttpClient client, string origin)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest", origin);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    private static void AssertEarlyForbidden(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    private static async Task AssertPostAuthDrainResponseAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Retry-After"));
        Assert.False(response.Headers.Contains("ETag"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("guest-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("room-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("redis", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RoomCreateSlowJsonCompletingAfterDrainDoesNotStartLobbyMutation()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, drain: drain);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);

        var rooms = server.Services.GetRequiredService<
            Shirokuro.Application.Lobbies.InMemoryLobbyCoordinator>();
        Assert.Equal(0, rooms.StoredRoomCount);
        cache.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        limiter.PermitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var body = new PausedJsonContent(
            """{"teamIds":["white","black"],"teamId":"white"}""");
        using var create = Mutation(HttpMethod.Post, "/api/lobbies",
            "https://game.example");
        create.Content = body;
        var request = guest.SendAsync(create);
        // The actual cookie Redis lookup and room quota must complete
        // BEFORE the last body byte arrives; both drain middleware ran.
        await Task.WhenAll(body.FirstChunkSent.Task, cache.ReadStarted.Task,
            limiter.PermitStarted.Task).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, cache.Reads);
        Assert.Equal(2, limiter.Calls); // Guest issuance and room creation.
        Assert.Equal(0, rooms.StoredRoomCount);

        drain.Stop();
        body.ResumeBody.SetResult(true);
        using var rejected = await request.WaitAsync(TimeSpan.FromSeconds(15));
        await AssertPostAuthDrainResponseAsync(rejected);
        Assert.Null(rejected.Headers.Location);
        Assert.Equal(0, rooms.StoredRoomCount);
        Assert.Equal(1, cache.Writes); // Existing signed guest only.
        Assert.Equal(2, limiter.Calls); // Quota already consumed once.
    }

    [Fact]
    public async Task RoomJoinSlowJsonCompletingAfterDrainDoesNotAdmitNewMember()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, drain: drain);
        using var anonymous = Client(server);
        var ownerCookie = await Issue(anonymous, "https://game.example");
        var joinerCookie = await Issue(anonymous, "https://game.example");
        using var owner = Client(server, cookie: ownerCookie);
        using var joiner = Client(server, cookie: joinerCookie);
        using var create = Mutation(HttpMethod.Post, "/api/lobbies",
            "https://game.example");
        create.Content = new StringContent(
            """{"teamIds":["white","black"],"teamId":"white"}""",
            Encoding.UTF8, "application/json");
        using var created = await owner.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var json = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var roomId = json.RootElement.GetProperty("lobbyId").GetString()!;
        var rooms = server.Services.GetRequiredService<
            Shirokuro.Application.Lobbies.InMemoryLobbyCoordinator>();
        var before = rooms.Get(roomId);
        Assert.Single(before.Participants);
        Assert.Equal(1, before.Revision);
        var initialReads = cache.Reads;
        var initialQuota = limiter.Calls;

        cache.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var body = new PausedJsonContent(
            """{"teamId":"black","expectedRevision":1}""");
        using var join = Mutation(HttpMethod.Post,
            "/api/lobbies/" + roomId + "/join", "https://game.example");
        join.Content = body;
        var request = joiner.SendAsync(join);
        await Task.WhenAll(body.FirstChunkSent.Task,
            cache.ReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(initialReads + 1, cache.Reads);
        Assert.Equal(initialQuota, limiter.Calls);
        Assert.Equal(before.Revision, rooms.Get(roomId).Revision);

        drain.Stop();
        body.ResumeBody.SetResult(true);
        using var rejected = await request.WaitAsync(TimeSpan.FromSeconds(15));
        await AssertPostAuthDrainResponseAsync(rejected);
        Assert.Null(rejected.Headers.Location);
        var after = rooms.Get(roomId);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Single(after.Participants);
        Assert.Equal(before.Participants[0].PlayerId, after.Participants[0].PlayerId);
        Assert.Equal(initialQuota, limiter.Calls);
        Assert.Equal(2, cache.Writes);
    }

    [Fact]
    public async Task SlowJsonBodyStillCreatesRoomNormallyWithoutDrain()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        using var body = new PausedJsonContent(
            """{"teamIds":["white","black"],"teamId":"white"}""");
        using var request = Mutation(HttpMethod.Post, "/api/lobbies",
            "https://game.example");
        request.Content = body;
        var pending = guest.SendAsync(request);
        await body.FirstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(15));
        body.ResumeBody.SetResult(true);
        using var created = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var json = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var roomId = json.RootElement.GetProperty("lobbyId").GetString()!;
        var rooms = server.Services.GetRequiredService<
            Shirokuro.Application.Lobbies.InMemoryLobbyCoordinator>();
        Assert.Equal(1, rooms.StoredRoomCount);
        Assert.Equal(1, rooms.Get(roomId).Revision);
        Assert.Equal(2, limiter.Calls);
    }

    [Fact]
    public async Task GuestRegistrationRedisSetCompletingDuringDrainRevokesUnpublishedTicket()
    {
        var cache = new CountingCache
        {
            SetStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ResumeSet = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, drain: drain);
        using var visitor = Client(server);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "https://game.example");
        var inFlight = visitor.SendAsync(request);
        await cache.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // Both middleware fences have passed, the handler is awaiting
        // its own Redis SET. The confirmed SET must NOT produce a cookie.
        drain.Stop();
        cache.ResumeSet.SetResult(true);
        using var result = await inFlight.WaitAsync(TimeSpan.FromSeconds(15));
        await AssertPostAuthDrainResponseAsync(result);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, cache.Reads); // Targeted registry rollback lookup.
        Assert.Equal(1, cache.Removes);
        Assert.Equal(0, cache.StoredGuestKeys);
        Assert.Equal(0, server.Services.GetRequiredService<
            GuestSessionRegistry>().StoredSessionCount);
    }

    [Fact]
    public async Task GuestExpiryScheduleCompletingDuringDrainRevokesUnpublishedTicket()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        var index = new PausingExpiryIndex();
        using var server = Production(cache, limiter, drain: drain,
            expiryIndex: index);
        using var visitor = Client(server);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "https://game.example");
        var inFlight = visitor.SendAsync(request);
        await index.ScheduleStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, cache.StoredGuestKeys);
        Assert.Equal(1, index.Scheduled);

        // Durable expiry work was started before shutdown. Once it finishes
        // there must be no new Set-Cookie or successful guest identity.
        drain.Stop();
        index.ResumeSchedule.SetResult(true);
        using var result = await inFlight.WaitAsync(TimeSpan.FromSeconds(15));
        await AssertPostAuthDrainResponseAsync(result);
        Assert.Equal(1, cache.Reads);
        Assert.Equal(1, cache.Removes);
        Assert.Equal(0, cache.StoredGuestKeys);
        Assert.Equal(0, server.Services.GetRequiredService<
            GuestSessionRegistry>().StoredSessionCount);
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task GuestDrainRollbackOutageNeverLeaksUnpublishedCookieOrRedisDetails()
    {
        var cache = new CountingCache
        {
            SetStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ResumeSet = new(TaskCreationOptions.RunContinuationsAsynchronously),
            RemoveUnavailable = true
        };
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, drain: drain);
        using var visitor = Client(server);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "https://game.example");
        var inFlight = visitor.SendAsync(request);
        await cache.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        drain.Stop();
        cache.ResumeSet.SetResult(true);
        using var result = await inFlight.WaitAsync(TimeSpan.FromSeconds(15));
        await AssertPostAuthDrainResponseAsync(result);
        Assert.Equal(1, cache.Removes); // Attempted, NOT confirmed.
        Assert.Equal(1, cache.StoredGuestKeys);
        Assert.Equal(1, server.Services.GetRequiredService<
            GuestSessionRegistry>().StoredSessionCount);
        // The unconfirmed orphan has only the server-side random token,
        // never a browser cookie. Production Redis will expire its TTL.
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task GuestIssuanceWaitingForQuotaCannotMintCookieAfterDrainStarts()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, drain: drain);
        using var visitor = Client(server);

        limiter.PermitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        limiter.PendingPermit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "https://game.example");
        var inFlight = visitor.SendAsync(request);
        await limiter.PermitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The pre-auth fence already passed before the limiter completed.
        drain.Stop();
        limiter.PendingPermit.SetResult(new GuestMutationDecision(true, 60_000));
        using var result = await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertPostAuthDrainResponseAsync(result);
        Assert.Equal(1, limiter.Calls);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(0, cache.Writes); // No signed ticket persisted.
    }

    [Fact]
    public async Task RoomCreationWaitingForQuotaCannotMutateLobbyAfterDrainStarts()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, drain: drain);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        var rooms = server.Services.GetRequiredService<
            Shirokuro.Application.Lobbies.InMemoryLobbyCoordinator>();
        Assert.Equal(0, rooms.StoredRoomCount);

        limiter.PermitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        limiter.PendingPermit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var request = Mutation(HttpMethod.Post, "/api/lobbies",
            "https://game.example");
        request.Content = new StringContent(
            """{"teamIds":["white","black"],"teamId":"white"}""",
            Encoding.UTF8, "application/json");
        var inFlight = guest.SendAsync(request);
        await limiter.PermitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The cookie was authorized BEFORE the quota gate; a late permit
        // does not authorize a newly created room on a draining node.
        Assert.Equal(1, cache.Reads);
        drain.Stop();
        limiter.PendingPermit.SetResult(new GuestMutationDecision(true, 60_000));
        using var result = await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertPostAuthDrainResponseAsync(result);
        Assert.Equal(0, rooms.StoredRoomCount);
        Assert.Equal(2, limiter.Calls); // One issuance + one room permit.
        Assert.Equal(1, cache.Writes); // Only the pre-drain guest ticket.
    }

    [Fact]
    public async Task JoinWaitingForCookieAuthorityCannotChangeRosterAfterDrainStarts()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, drain: drain);
        using var anonymous = Client(server);
        var ownerCookie = await Issue(anonymous, "https://game.example");
        var joinerCookie = await Issue(anonymous, "https://game.example");
        using var owner = Client(server, cookie: ownerCookie);
        using var joiner = Client(server, cookie: joinerCookie);
        using var create = Mutation(HttpMethod.Post, "/api/lobbies",
            "https://game.example");
        create.Content = new StringContent(
            """{"teamIds":["white","black"],"teamId":"white"}""",
            Encoding.UTF8, "application/json");
        using var created = await owner.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var roomId = createdJson.RootElement.GetProperty("lobbyId").GetString()!;
        var rooms = server.Services.GetRequiredService<
            Shirokuro.Application.Lobbies.InMemoryLobbyCoordinator>();
        var before = rooms.Get(roomId);
        Assert.Equal(1, before.Revision);
        Assert.Single(before.Participants);
        var readsBefore = cache.Reads;
        var quotasBefore = limiter.Calls;

        cache.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.ResumeRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var join = Mutation(HttpMethod.Post,
            "/api/lobbies/" + roomId + "/join", "https://game.example");
        join.Content = new StringContent(
            """{"teamId":"black","expectedRevision":1}""",
            Encoding.UTF8, "application/json");
        var inFlight = joiner.SendAsync(join);
        await cache.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(readsBefore + 1, cache.Reads);

        drain.Stop();
        cache.ResumeRead.SetResult(true); // Redis confirms valid old cookie.
        using var result = await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertPostAuthDrainResponseAsync(result);
        var after = rooms.Get(roomId);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Single(after.Participants);
        Assert.Equal(before.Participants[0].PlayerId, after.Participants[0].PlayerId);
        Assert.Equal(quotasBefore, limiter.Calls);
        Assert.Equal(2, cache.Writes);
    }

    [Fact]
    public async Task DrainingJoinRejectsBeforeGuestRedisWithoutMaskingRequestGuards()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, stubReadiness: true,
            drain: drain);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        using (var active = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);

        var reads = cache.Reads;
        Assert.True(reads > 0);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);
        const string roomId = "room-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var joinPath = "/api/lobbies/" + roomId + "/join";

        drain.Stop();
        cache.Unavailable = true;
        limiter.Unavailable = true;
        foreach (var client in new[] { anonymous, guest })
        {
            using var request = Mutation(HttpMethod.Post, joinPath,
                "https://game.example");
            request.Content = new StringContent(
                """{"teamId":"black","expectedRevision":1}""",
                Encoding.UTF8, "application/json");
            using var rejected = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
            Assert.False(rejected.Headers.Contains("Retry-After"));
            Assert.False(rejected.Headers.Contains("ETag"));
            Assert.DoesNotContain(roomId,
                await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(reads, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // Route, room ID, origin and declared body checks still happen
        // before host drain, without revealing the real lobby or cookie.
        using (var malformed = Mutation(HttpMethod.Post,
                   "/api/lobbies/bad/join", "https://game.example"))
        {
            malformed.Content = new StringContent("{}",
                Encoding.UTF8, "application/json");
            using var rejected = await guest.SendAsync(malformed);
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        }
        using (var wrong = Mutation(HttpMethod.Put, joinPath,
                   "https://game.example"))
        using (var rejected = await guest.SendAsync(wrong))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, rejected.StatusCode);
        using (var hostile = Mutation(HttpMethod.Post, joinPath,
                   "https://attacker.example"))
        {
            hostile.Content = new StringContent("{}",
                Encoding.UTF8, "application/json");
            using var rejected = await guest.SendAsync(hostile);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }
        using (var badMedia = Mutation(HttpMethod.Post, joinPath,
                   "https://game.example"))
        {
            badMedia.Content = new StringContent("invalid",
                Encoding.UTF8, "text/plain");
            using var rejected = await guest.SendAsync(badMedia);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode);
        }
        using (var tooLarge = Mutation(HttpMethod.Post, joinPath,
                   "https://game.example"))
        {
            tooLarge.Content = new StringContent(new string('x', 5000),
                Encoding.UTF8, "application/json");
            using var rejected = await guest.SendAsync(tooLarge);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        }
        Assert.Equal(reads, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        using (var health = await guest.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var ready = await guest.GetAsync("/api/ready"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        // The join fence does not skip authentication on other mutations.
        using var hero = Mutation(HttpMethod.Patch,
            "/api/lobbies/" + roomId + "/hero", "https://game.example");
        hero.Content = new StringContent(
            """{"heroId":"kira","expectedRevision":1}""",
            Encoding.UTF8, "application/json");
        using var denied = await guest.SendAsync(hero);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.Equal(reads + 1, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task DevelopmentJoinCannotAdmitThirdGuestAfterDrainButLeaveStillWorks()
    {
        var drain = new ManualDrainSignal();
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IReadinessDrainSignal>();
                    services.AddSingleton<IReadinessDrainSignal>(drain);
                });
            });
        using var anonymous = Client(server, development: true);
        var ownerCookie = await Issue(anonymous, "http://localhost");
        var secondCookie = await Issue(anonymous, "http://localhost");
        var thirdCookie = await Issue(anonymous, "http://localhost");
        using var owner = Client(server, development: true, cookie: ownerCookie);
        using var second = Client(server, development: true, cookie: secondCookie);
        using var third = Client(server, development: true, cookie: thirdCookie);

        using var me = await owner.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var ownerJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        var ownerId = ownerJson.RootElement.GetProperty("playerId").GetString()!;
        using var create = Mutation(HttpMethod.Post, "/api/lobbies", "http://localhost");
        create.Content = new StringContent(
            """{"teamIds":["white","black"],"teamId":"white"}""",
            Encoding.UTF8, "application/json");
        using var created = await owner.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var roomId = createdJson.RootElement.GetProperty("lobbyId").GetString()!;

        var joinPath = "/api/lobbies/" + roomId + "/join";
        using (var join = Mutation(HttpMethod.Post, joinPath, "http://localhost"))
        {
            join.Content = new StringContent(
                """{"teamId":"black","expectedRevision":1}""",
                Encoding.UTF8, "application/json");
            using var joined = await second.SendAsync(join);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        var lobby = server.Services.GetRequiredService<
            Shirokuro.Api.Lobbies.IAsyncGuestLobby>();
        var before = await lobby.PeekForMemberAsync(roomId, ownerId);
        Assert.Equal(2, before.Revision);
        Assert.Equal(2, before.Participants.Count);

        drain.Stop();
        using (var join = Mutation(HttpMethod.Post, joinPath, "http://localhost"))
        {
            join.Content = new StringContent(
                """{"teamId":"black","expectedRevision":2}""",
                Encoding.UTF8, "application/json");
            using var rejected = await third.SendAsync(join);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
            Assert.False(rejected.Headers.Contains("Retry-After"));
        }

        var unchanged = await lobby.PeekForMemberAsync(roomId, ownerId);
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Equal(2, unchanged.Participants.Count);
        // Existing members must be able to leave and release capacity
        // even though a new participant is refused by the draining node.
        using (var leave = Mutation(HttpMethod.Delete,
                   "/api/lobbies/" + roomId + "/membership", "http://localhost"))
        {
            leave.Content = new StringContent(
                """{"expectedRevision":2}""",
                Encoding.UTF8, "application/json");
            using var left = await second.SendAsync(leave);
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }
        var after = await lobby.PeekForMemberAsync(roomId, ownerId);
        Assert.Equal(3, after.Revision);
        Assert.Single(after.Participants);
        Assert.Equal(ownerId, after.Participants[0].PlayerId);

        using var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest",
            "http://localhost");
        using var revoked = await owner.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
    }

    [Fact]
    public async Task DrainingNodeRejectsOnlyNewAdmissionsBeforeGuestRedisAndQuota()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, stubReadiness: true,
            drain: drain);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        using (var active = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);

        var reads = cache.Reads;
        Assert.True(reads > 0);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // The real signed guest would need the shared registry. Neither
        // that validation nor the per-IP Redis limiter may run after drain.
        drain.Stop();
        cache.Unavailable = true;
        limiter.Unavailable = true;
        foreach (var client in new[] { anonymous, guest })
        {
            foreach (var path in new[] { "/api/sessions/guest", "/api/lobbies" })
            {
                using var request = Mutation(HttpMethod.Post, path,
                    "https://game.example");
                if (path == "/api/lobbies")
                    request.Content = new StringContent(
                        """{"teamIds":["white","black"],"teamId":"white"}""",
                        Encoding.UTF8, "application/json");
                using var rejected = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
                Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
                Assert.False(rejected.Headers.Contains("Set-Cookie"));
                Assert.False(rejected.Headers.Contains("Retry-After"));
                Assert.DoesNotContain("Private Redis",
                    await rejected.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }
        }
        Assert.Equal(reads, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // Established routing and pre-auth validation retain their
        // precedence. The shutdown fence must not hide malformed requests.
        using (var unknown = await guest.GetAsync("/api/no-such-endpoint"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal("no-store", unknown.Headers.CacheControl?.ToString());
        }
        using (var method = await guest.GetAsync("/api/lobbies"))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, method.StatusCode);
            Assert.Equal("no-store", method.Headers.CacheControl?.ToString());
        }
        using (var hostile = Mutation(HttpMethod.Post, "/api/sessions/guest",
                   "https://attacker.example"))
        using (var rejected = await guest.SendAsync(hostile))
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using (var body = Mutation(HttpMethod.Post, "/api/sessions/guest",
                   "https://game.example"))
        {
            body.Content = new StringContent("{}", Encoding.UTF8,
                "application/json");
            using var rejected = await guest.SendAsync(body);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using (var media = Mutation(HttpMethod.Post, "/api/lobbies",
                   "https://game.example"))
        {
            media.Content = new StringContent("not-json", Encoding.UTF8,
                "text/plain");
            using var rejected = await guest.SendAsync(media);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode);
        }
        Assert.Equal(reads, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        using (var health = await guest.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var ready = await guest.GetAsync("/api/ready"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        // A non-admission private route still checks its guest authority.
        using var identity = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, identity.StatusCode);
        Assert.Equal(reads + 1, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task DevelopmentDrainRejectsNewAdmissionsButAllowsExistingLogout()
    {
        var drain = new ManualDrainSignal();
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IReadinessDrainSignal>();
                    services.AddSingleton<IReadinessDrainSignal>(drain);
                });
            });
        using var anonymous = Client(server, development: true);
        var cookie = await Issue(anonymous, "http://localhost");
        using var guest = Client(server, development: true, cookie: cookie);

        // An already-accepted room exists before the drain. Its creator
        // must retain the ability to log out and remove its memberships.
        using (var creation = Mutation(HttpMethod.Post, "/api/lobbies",
                   "http://localhost"))
        {
            creation.Content = new StringContent(
                """{"teamIds":["white","black"],"teamId":"white"}""",
                Encoding.UTF8, "application/json");
            using var created = await guest.SendAsync(creation);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        drain.Stop();
        foreach (var client in new[] { anonymous, guest })
        {
            foreach (var path in new[] { "/api/sessions/guest", "/api/lobbies" })
            {
                using var request = Mutation(HttpMethod.Post, path,
                    "http://localhost");
                if (path == "/api/lobbies")
                    request.Content = new StringContent(
                        """{"teamIds":["white","black"],"teamId":"white"}""",
                        Encoding.UTF8, "application/json");
                using var rejected = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
                Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
                Assert.False(rejected.Headers.Contains("Set-Cookie"));
                Assert.False(rejected.Headers.Contains("Retry-After"));
            }
        }

        // Drain is not a blanket authorization bypass or forced logout:
        // authenticated reads and revocation remain available to the player.
        using (var existing = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest",
                   "http://localhost"))
        using (var revoked = await guest.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using (var oldCookie = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, oldCookie.StatusCode);
        using (var health = await guest.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var ready = await guest.GetAsync("/api/ready"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task DrainingLobbyHubRejectsNegotiateAndUpgradeBeforeGuestRedis()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, stubReadiness: true,
            drain: drain);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        var negotiatePath = "/hubs/lobbies/negotiate?negotiateVersion=1";

        // Before shutdown a real authenticated guest may negotiate normally.
        using (var open = await guest.PostAsync(negotiatePath, null))
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        var reads = cache.Reads;
        Assert.True(reads > 0);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        cache.Unavailable = true;
        drain.Stop();
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, negotiatePath),
                     (HttpMethod.Get, "/hubs/lobbies"),
                     (HttpMethod.Post, negotiatePath)
                 })
        {
            using var request = new HttpRequestMessage(method, path);
            using var rejected = await guest.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("no-store",
                rejected.Headers.CacheControl?.ToString());
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
            Assert.False(rejected.Headers.Contains("Retry-After"));
        }

        // No extra signed-cookie authority GET or quota read on shutdown.
        Assert.Equal(reads, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // Unrelated paths keep their normal 404, not a broad hub 503.
        foreach (var path in new[]
                 {
                     "/hubs/lobbies-extra",
                     "/hubs/not-a-hub",
                     "/hubs/lobbies/unknown"
                 })
        {
            using var missing = await guest.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("no-store", missing.Headers.CacheControl?.ToString());
        }
        Assert.Equal(reads, cache.Reads);

        // Existing liveness, readiness and public board endpoints are
        // independent of whether the SignalR transport accepts new sockets.
        using (var health = await guest.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var board = await guest.GetAsync("/api/preview/board"))
            Assert.Equal(HttpStatusCode.OK, board.StatusCode);
        using (var ready = await guest.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("no-store", ready.Headers.CacheControl?.ToString());
        }
        Assert.Equal(reads, cache.Reads);

        // Actual protected API routes are NOT globally exempted during
        // shutdown. A signed real guest still fails closed on Redis outage.
        using var privateRead = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, privateRead.StatusCode);
        Assert.Equal(reads + 1, cache.Reads);
    }

    [Fact]
    public async Task DrainingHttpRosterReadsReturn503BeforeSignedGuestRedisLookup()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        var drain = new ManualDrainSignal();
        using var server = Production(cache, limiter, stubReadiness: true,
            drain: drain);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);

        // The actual canonical signed guest still authenticates normally.
        using (var active = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var reads = cache.Reads;
        Assert.True(reads > 0);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // Drain and Redis loss together: the *route-owned* pre-auth fence
        // must respond before even checking the attached valid guest cookie.
        cache.Unavailable = true;
        drain.Stop();
        const string roomId = "room-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        foreach (var path in new[]
                 {
                     $"/api/lobbies/{roomId}/roster",
                     $"/api/lobbies/{roomId}/roster/peek",
                     $"/api/lobbies/{roomId}/roster/peek"
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("If-None-Match", "\"rev-3\"");
            using var rejected = await guest.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
            Assert.False(rejected.Headers.Contains("ETag"));
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
            Assert.False(rejected.Headers.Contains("Retry-After"));
            var payload = await rejected.Content.ReadAsStringAsync();
            Assert.DoesNotContain("participants", payload,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(roomId, payload, StringComparison.Ordinal);
        }
        // No Redis registry lookups, session writes or rate limit allowance
        // consumed for any shutdown read, even with a stolen signed cookie.
        Assert.Equal(reads, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // The normal framework/route guards remain authoritative.
        using (var invalid = await guest.GetAsync("/api/lobbies/invalid/roster"))
        {
            Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
            Assert.Equal("no-store", invalid.Headers.CacheControl?.ToString());
        }
        using (var unknown = await guest.GetAsync("/api/lobbies/not-a-route"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal("no-store", unknown.Headers.CacheControl?.ToString());
        }
        using (var wrongMethod = await guest.SendAsync(
                   new HttpRequestMessage(HttpMethod.Put,
                       $"/api/lobbies/{roomId}/roster")))
        {
            // TestServer's synthetic ASP.NET Core fallback returns 405 but
            // does not necessarily expose an Allow header. Browser-level
            // method tests assert Allow separately where it is available.
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
            Assert.Equal("no-store", wrongMethod.Headers.CacheControl?.ToString());
            Assert.False(wrongMethod.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(reads, cache.Reads);

        using (var health = await guest.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var ready = await guest.GetAsync("/api/ready"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(reads, cache.Reads);

        // Shutdown does not exempt actual protected non-roster endpoints
        // from authoritative Redis guest validation during an outage.
        using var identity = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, identity.StatusCode);
        Assert.Equal(reads + 1, cache.Reads);
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task UnsupportedSignedCookieFailsClosedBeforeRedisEvenDuringOutage()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var guestCookie = await Issue(anonymous, "https://game.example");
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        var options = server.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var unsupported = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "old-verified-account")],
            CookieAuthenticationDefaults.AuthenticationScheme));
        var ticket = new AuthenticationTicket(
            unsupported, CookieAuthenticationDefaults.AuthenticationScheme);
        var cookie = options.Cookie.Name + "=" +
                     options.TicketDataFormat.Protect(ticket);

        // A signed but unsupported account ticket must not become a valid
        // account, nor automatically receive a replacement guest identity.
        // Redis is unavailable yet no read may be attempted for this shape.
        using var capture = new MultiplayerMetricCapture();
        cache.Unavailable = true;
        using var unsupportedClient = Client(server, cookie: cookie);
        using (var privateRead = await unsupportedClient.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, privateRead.StatusCode);
            Assert.Equal("no-store", privateRead.Headers.CacheControl?.ToString());
            Assert.False(privateRead.Headers.Contains("Set-Cookie"));
        }
        using (var issuanceRequest = Mutation(
                   HttpMethod.Post, "/api/sessions/guest", "https://game.example"))
        using (var rejected = await unsupportedClient.SendAsync(issuanceRequest))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
        }
        using (var negotiate = await unsupportedClient.PostAsync(
                   "/hubs/lobbies/negotiate?negotiateVersion=1", null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        // The issuance request uses the server's normal pre-auth technical
        // admission quota, but it must not reach session registration.
        Assert.Equal(2, limiter.Calls);

        // Public probes never read guest Redis, even with a rejected ticket.
        using (var health = await unsupportedClient.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(0, cache.Reads);

        // A real, previously issued guest still fails closed at 503 during
        // the *same* Redis outage, not 401 or an unsupported-account bypass.
        using var realGuest = Client(server, cookie: guestCookie);
        using (var outage = await realGuest.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
            Assert.Equal("no-store", outage.Headers.CacheControl?.ToString());
        }
        Assert.Equal(1, cache.Reads);
        cache.Unavailable = false;
        using var active = await realGuest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, cache.Writes);

        // The only exported attribute is a fixed enum value; never include
        // the signed non-guest NameIdentifier, cookie, IP or raw request path.
        var decisions = capture.For(MultiplayerMetrics.GuestCookieAuthorityName);
        Assert.Contains(decisions, item => item.Value == 1 &&
            item.Tags.Count == 1 && item.Tags["outcome"] == "unsupported-ticket");
        Assert.Contains(decisions, item => item.Value == 1 &&
            item.Tags["outcome"] == "unavailable");
        Assert.Contains(decisions, item => item.Value == 1 &&
            item.Tags["outcome"] == "active");
        Assert.All(decisions, item =>
        {
            Assert.Equal(new[] { "outcome" }, item.Tags.Keys);
            Assert.Contains(item.Tags["outcome"],
                new[] { "active", "inactive", "unavailable",
                         "recovery-cutoff", "unsupported-ticket" });
            Assert.DoesNotContain("old-verified-account",
                string.Join(",", item.Tags.Values), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SignedGuestCookieMetricsDistinguishActiveOutageAndConfirmedRevocation()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        using var capture = new MultiplayerMetricCapture();

        using (var health = await anonymous.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var cookie = await Issue(anonymous, "https://game.example");
        // A process-wide MeterListener also observes independent tests.
        // The fake cache, not the global meter queue, proves that this
        // anonymous issuance performed no cookie authority reads.
        Assert.Equal(0, cache.Reads);

        using var guest = Client(server, cookie: cookie);
        using (var accepted = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, cache.Reads);

        // Public metadata excludes cookie registry validation, even when
        // the browser sends a perfectly signed guest ticket.
        using (var health = await guest.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var preview = await guest.GetAsync("/api/preview/board"))
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        // Early malformed lobby routing never triggers cookie authentication.
        using (var badRoom = await guest.GetAsync("/api/lobbies/bad/roster"))
            Assert.Equal(HttpStatusCode.NotFound, badRoom.StatusCode);
        Assert.Equal(1, cache.Reads);

        cache.Unavailable = true;
        using (var failed = await guest.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal("no-store", failed.Headers.CacheControl?.ToString());
            Assert.False(failed.Headers.Contains("Set-Cookie"));
        }

        cache.Unavailable = false;
        cache.ExpireAll(); // Redis confirms the ticket no longer exists.
        using (var revoked = await guest.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.Equal("no-store", revoked.Headers.CacheControl?.ToString());
        }
        Assert.Equal(3, cache.Reads);
        Assert.Equal(1, cache.Writes); // No automatic replacement identity.
        Assert.Equal(1, limiter.Calls);

        var outcomes = capture.For(MultiplayerMetrics.GuestCookieAuthorityName);
        // MeterListener is process-wide: other concurrently running xUnit
        // tests may legitimately emit the same instrument. Assert the
        // three real statuses, not a global count across test classes;
        // cache.Reads above proves exactly three actual lookups here.
        foreach (var state in new[] { "active", "unavailable", "inactive" })
            Assert.Contains(outcomes, item => item.Value == 1 &&
                item.Tags.Count == 1 && item.Tags["outcome"] == state);
        Assert.All(outcomes, item =>
        {
            Assert.Equal(new[] { "outcome" }, item.Tags.Keys);
            Assert.Contains(item.Tags["outcome"],
                new[] { "active", "inactive", "unavailable",
                        "recovery-cutoff", "unsupported-ticket" });
            Assert.DoesNotContain("guest-", item.Tags["outcome"], StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task DisasterRecoveryCutoffRejectsSignedCookieWithoutRedisAuthorityLookup()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var issuing = Production(cache, limiter);
        using var anonymous = Client(issuing);
        var cookie = await Issue(anonymous, "https://game.example");
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);

        // Same persistent Data Protection ring, different node configured
        // with a future operator cutoff. Redis still holds this original
        // session, but it MUST NOT be consulted or authenticated.
        using var recoveryNode = Production(cache, limiter,
            recoveryCutoff: "2099-01-01T00:00:00Z");
        using var capture = new MultiplayerMetricCapture();
        using var guest = Client(recoveryNode, cookie: cookie);
        using (var rejected = await guest.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        var events = capture.For(MultiplayerMetrics.GuestCookieAuthorityName);
        // The MeterListener is shared across concurrent xUnit test cases.
        // One matching event plus zero cache GETs proves this node took
        // the cutoff path; unrelated tests may emit other outcomes.
        Assert.Contains(events, item => item.Value == 1 &&
            item.Tags.Count == 1 && item.Tags["outcome"] == "recovery-cutoff");
        Assert.All(events, item =>
        {
            Assert.Equal(new[] { "outcome" }, item.Tags.Keys);
            Assert.Contains(item.Tags["outcome"],
                new[] { "active", "inactive", "unavailable",
                        "recovery-cutoff", "unsupported-ticket" });
        });

        // The old node can still independently validate the same cookie;
        // the cutoff is a deployment-wide operator setting, not a Redis
        // delete. Rollout consistency remains an operational requirement.
        using var original = Client(issuing, cookie: cookie);
        using (var active = await original.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Equal(1, cache.Reads);
        Assert.Contains(capture.For(MultiplayerMetrics.GuestCookieAuthorityName),
            item => item.Value == 1 && item.Tags["outcome"] == "active");
    }

    [Fact]
    public async Task GuestIssuanceQuotaRunsBeforeSignedCookieRedisAuthority()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(1, limiter.Calls); // Only the genuine guest admission.
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);

        // The signed guest cookie would require an authoritative Redis
        // read. A denied quota MUST be returned first, even during outage,
        // with no session validation or cookie replacement.
        cache.Unavailable = true;
        limiter.Allowed = false;
        using (var deniedRequest = Mutation(HttpMethod.Post,
                   "/api/sessions/guest", "https://game.example"))
        using (var denied = await guest.SendAsync(deniedRequest))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.Equal("60", Assert.Single(
                denied.Headers.GetValues("Retry-After")));
            Assert.False(denied.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(2, limiter.Calls);

        limiter.Unavailable = true;
        using (var outageRequest = Mutation(HttpMethod.Post,
                   "/api/sessions/guest", "https://game.example"))
        using (var outage = await guest.SendAsync(outageRequest))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
            Assert.Equal("no-store", outage.Headers.CacheControl?.ToString());
            Assert.False(outage.Headers.Contains("Set-Cookie"));
            Assert.False(outage.Headers.Contains("Retry-After"));
            Assert.DoesNotContain("Sensitive Redis",
                await outage.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(3, limiter.Calls);

        // A genuine permit now reaches cookie validation and fails closed
        // on the unavailable registry rather than minting a new identity.
        limiter.Unavailable = false;
        limiter.Allowed = true;
        using (var allowedRequest = Mutation(HttpMethod.Post,
                   "/api/sessions/guest", "https://game.example"))
        using (var authUnavailable = await guest.SendAsync(allowedRequest))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                authUnavailable.StatusCode);
            Assert.Equal("no-store",
                authUnavailable.Headers.CacheControl?.ToString());
            Assert.False(authUnavailable.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(1, cache.Reads);
        Assert.Equal(4, limiter.Calls);
        // An outage cannot create a replacement session even when Redis
        // would accept a SET for a new ticket but not a GET for the old one.
        Assert.Equal(1, cache.Writes);

        // A separate room-creation POST still validates the signed cookie
        // BEFORE attempting its distinct quota, even under registry outage.
        using (var roomRequest = Mutation(HttpMethod.Post,
                   "/api/lobbies", "https://game.example"))
        using (var blockedRoom = await guest.SendAsync(roomRequest))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                blockedRoom.StatusCode);
        }
        Assert.Equal(2, cache.Reads);
        Assert.Equal(4, limiter.Calls);

        // Recovery preserves idempotence: no new cookie or new guest ID.
        cache.Unavailable = false;
        using var recoveredRequest = Mutation(HttpMethod.Post,
            "/api/sessions/guest", "https://game.example");
        using var recovered = await guest.SendAsync(recoveredRequest);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.False(recovered.Headers.Contains("Set-Cookie"));
        Assert.Equal("no-store", recovered.Headers.CacheControl?.ToString());
        Assert.Equal(3, cache.Reads);
        Assert.Equal(5, limiter.Calls);
        Assert.Equal(1, cache.Writes);

        // A confirmed inactive ticket is different from an unavailable
        // authority: the explicit user action may issue a fresh guest.
        cache.ExpireAll();
        using var reissueRequest = Mutation(HttpMethod.Post,
            "/api/sessions/guest", "https://game.example");
        using var reissued = await guest.SendAsync(reissueRequest);
        Assert.Equal(HttpStatusCode.OK, reissued.StatusCode);
        Assert.True(reissued.Headers.Contains("Set-Cookie"));
        Assert.Equal(2, cache.Writes);
        Assert.Equal(4, cache.Reads);
        Assert.Equal(6, limiter.Calls);
    }

    [Fact]
    public async Task ProductionRejectsEveryBadMutationBeforeRedisCookieReadOrQuota()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // A valid cookie would otherwise trigger a Redis lookup and 503
        // when Redis is unavailable. These rejections must stay 403 before
        // Redis is contacted, and must not consume shared mutation budget.
        cache.Unavailable = true;
        foreach (var (method, path) in MutationRoutes)
        {
            foreach (var (origin, intent) in new (string?, bool)[]
            {
                (null, true),
                ("https://evil.example", true),
                ("https://game.example", false)
            })
            {
                using var request = Mutation(method, path, origin, intent);
                using var denied = await guest.SendAsync(request);
                AssertEarlyForbidden(denied);
                Assert.Equal(0, cache.Reads);
                Assert.Equal(1, limiter.Calls);
            }
        }

        using (var request = Mutation(
            HttpMethod.Patch,
            "/api/lobbies/room-00000000000000000000000000000000/hero",
            "https://game.example"))
        using (var unavailable = await guest.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
            Assert.False(unavailable.Headers.Contains("Set-Cookie"));
            Assert.DoesNotContain("Private Redis",
                await unavailable.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // Read-only endpoints never acquire Origin requirements; protected
        // reads still require Redis, but liveness stays independent.
        using (var read = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
        Assert.Equal(2, cache.Reads);
        // A genuinely anonymous liveness check never needs guest Redis.
        // Requests carrying a cookie can still attempt authentication even
        // on public routes; that separate behavior is outside this guard.
        using (var health = await anonymous.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(2, cache.Reads);

        cache.Unavailable = false;
        using var recovered = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(3, cache.Reads);
    }

    private sealed class FakeBodyDetectionFeature(bool canHaveBody) :
        IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuestIssuanceBodyGuardUsesHttp2BodyDetectionWithoutHeaders(
        bool canHaveBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Protocol = "HTTP/2";
        context.Features.Set<IHttpRequestBodyDetectionFeature>(
            new FakeBodyDetectionFeature(canHaveBody));
        Assert.Null(context.Request.ContentLength);
        Assert.False(context.Request.Headers.ContainsKey("Transfer-Encoding"));
        Assert.Equal(!canHaveBody, GuestSessionBodyGuard.Allows(context));

        // A caller-supplied Content-Length: 0 is permitted only when the
        // transport reports that no body frame can follow.
        context.Request.ContentLength = 0;
        Assert.Equal(!canHaveBody, GuestSessionBodyGuard.Allows(context));
    }

    [Fact]
    public void GuestIssuanceBodyGuardDeniesFramedBodiesWithoutReadingThem()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestBodyDetectionFeature>(
            new FakeBodyDetectionFeature(false));
        Assert.True(GuestSessionBodyGuard.Allows(context));
        context.Request.ContentLength = 1;
        Assert.False(GuestSessionBodyGuard.Allows(context));
        context.Request.ContentLength = null;
        context.Request.Headers.TransferEncoding = "chunked";
        Assert.False(GuestSessionBodyGuard.Allows(context));
    }

    [Fact]
    public void GuestLogoutHttp2BodyDetectionRejectsWithoutLengthHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "DELETE";
        context.Request.Protocol = "HTTP/2";
        context.Features.Set<IHttpRequestBodyDetectionFeature>(
            new FakeBodyDetectionFeature(true));
        Assert.Null(context.Request.ContentLength);
        Assert.False(context.Request.Headers.ContainsKey("Transfer-Encoding"));
        Assert.False(GuestSessionBodyGuard.Allows(context));
        context.Features.Set<IHttpRequestBodyDetectionFeature>(
            new FakeBodyDetectionFeature(false));
        Assert.True(GuestSessionBodyGuard.Allows(context));
    }

    [Fact]
    public async Task LogoutBodyIsRejectedBeforeGuestRedisAuthOrRevocation()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // The same signed guest cannot be verified during Redis outage.
        // A malformed DELETE must still be rejected at ingress with 400
        // before any Redis GET or irreversible lobby/session revocation.
        cache.Unavailable = true;
        using (var json = Mutation(HttpMethod.Delete,
                   "/api/sessions/guest", "https://game.example"))
        {
            json.Content = new StringContent(
                "{}", Encoding.UTF8, "application/json");
            using var denied = await guest.SendAsync(json);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie"));
            Assert.False(denied.Headers.Contains("Retry-After"));
        }

        // HTTP/1.1 chunked framing is invalid even for a zero-byte payload.
        using (var chunked = Mutation(HttpMethod.Delete,
                   "/api/sessions/guest", "https://game.example"))
        {
            chunked.Headers.TransferEncodingChunked = true;
            chunked.Content = new ByteArrayContent([]);
            using var denied = await guest.SendAsync(chunked);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie"));
        }

        // Origin policy remains first even when the body is malformed.
        using (var hostile = Mutation(HttpMethod.Delete,
                   "/api/sessions/guest", "https://evil.example"))
        {
            hostile.Content = new StringContent("{}", Encoding.UTF8,
                "application/json");
            using var forbidden = await guest.SendAsync(hostile);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal("no-store", forbidden.Headers.CacheControl?.ToString());
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // The legitimate bodyless DELETE reaches real auth. During an
        // outage it fails closed (503), not 400 or a pretend sign-out.
        using (var valid = Mutation(HttpMethod.Delete,
                   "/api/sessions/guest", "https://game.example"))
        using (var unavailable = await guest.SendAsync(valid))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
            Assert.False(unavailable.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(1, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // The malformed DELETE must not sign out the guest. Recovery of
        // Redis authority should preserve the original identity/ticket.
        cache.Unavailable = false;
        using var recovered = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        using var identity = JsonDocument.Parse(
            await recovered.Content.ReadAsStringAsync());
        Assert.StartsWith("guest-",
            identity.RootElement.GetProperty("playerId").GetString());
        Assert.Equal("guest",
            identity.RootElement.GetProperty("kind").GetString());
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task GuestIssuanceBodyWithValidOriginRejectsBeforeGuestRedisOrQuota()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        cache.Unavailable = true;
        // If this reaches cookie validation, Redis would be unavailable
        // and report 503; if it reaches admission, quota would increment.
        using var body = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "https://game.example");
        body.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var denied = await guest.SendAsync(body);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        Assert.False(denied.Headers.Contains("Set-Cookie"));
        Assert.False(denied.Headers.Contains("Retry-After"));
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // Valid bodyless reads remain authenticated and fail closed when
        // the shared authority is unavailable, unlike this early 400.
        using var me = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, me.StatusCode);
        Assert.Equal(1, cache.Reads);
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task OversizedLobbyBodyRejectsBeforeRedisTicketOrQuotaInProduction()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        cache.Unavailable = true;
        // Every DTO-bearing lobby mutation has the server-owned body marker.
        // A large declared body is rejected without trying guest Redis,
        // including on the room creation endpoint with its separate quota.
        foreach (var (method, path) in MutationRoutes.Skip(2))
        {
            using var oversized = Mutation(method, path, "https://game.example");
            oversized.Content = new StringContent(
                new string('x', (int)GuestLobbyMutationBodyLimit.MaxBytes + 1),
                Encoding.UTF8, "application/json");
            Assert.Equal(GuestLobbyMutationBodyLimit.MaxBytes + 1,
                oversized.Content.Headers.ContentLength);
            using var denied = await guest.SendAsync(oversized);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie"));
            Assert.False(denied.Headers.Contains("Retry-After"));
            Assert.Equal(0, cache.Reads);
            Assert.Equal(1, limiter.Calls);
        }

        // An exactly-on-boundary body is not rejected by the early guard:
        // it still needs the authoritative Redis guest validation.
        using var boundary = Mutation(HttpMethod.Post, "/api/lobbies",
            "https://game.example");
        boundary.Content = new StringContent(
            new string('x', (int)GuestLobbyMutationBodyLimit.MaxBytes),
            Encoding.UTF8, "application/json");
        using var unavailable = await guest.SendAsync(boundary);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
        Assert.Equal(1, cache.Reads);
        Assert.Equal(1, limiter.Calls); // Auth rejected before creation quota.
    }

    private sealed class FakeBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }
        public long? MaxRequestBodySize { get; set; }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/vnd.shirokuro+json", true)]
    [InlineData("text/plain", false)]
    [InlineData("application/x-www-form-urlencoded", false)]
    [InlineData("multipart/form-data", false)]
    [InlineData("application/json, text/plain", false)]
    public void LobbyMediaTypeGuardMatchesJsonBinderForExplicitHeaders(
        string? contentType, bool allowed)
    {
        var context = new DefaultHttpContext();
        if (contentType is not null)
            context.Request.Headers.ContentType = contentType;
        Assert.Equal(allowed, GuestLobbyMutationMediaTypeGuard.Allows(context));
    }

    [Theory]
    [InlineData("POST", "/api/lobbies", true, false)]
    [InlineData("post", "/API/LOBBIES/", true, false)]
    [InlineData("POST", "/api/lobbies/room-00000000000000000000000000000000/join", true, false)]
    [InlineData("PATCH", "/api/lobbies/room-00000000000000000000000000000000/hero", true, false)]
    [InlineData("PATCH", "/api/lobbies/room-00000000000000000000000000000000/team", true, false)]
    [InlineData("DELETE", "/api/lobbies/room-00000000000000000000000000000000/membership", true, false)]
    [InlineData("POST", "/api/lobbies/not-a-room/join", true, true)]
    [InlineData("GET", "/api/lobbies", false, false)]
    [InlineData("DELETE", "/api/lobbies", false, false)]
    [InlineData("POST", "/api/lobbies/room-00000000000000000000000000000000/hero", false, false)]
    [InlineData("POST", "/api/lobbies/room-00000000000000000000000000000000/join/extra", false, false)]
    [InlineData("POST", "/api/lobbies/room-00000000000000000000000000000000/roster", false, false)]
    [InlineData("POST", "/api/sessions/guest", false, false)]
    public void Synthetic415FallbackIsLimitedToKnownLobbyMutations(
        string method, string path, bool expected, bool invalidRoom)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.ContentType = "text/plain";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask, new EndpointMetadataCollection(),
            "415 HTTP Unsupported Media Type"));

        Assert.Equal(expected,
            GuestLobbyMutationMediaTypeGuard.IsFrameworkUnsupportedLobbyRoute(
                context, out var actualInvalidRoom));
        Assert.Equal(invalidRoom, actualInvalidRoom);
    }

    [Theory]
    [InlineData("application/json", "415 HTTP Unsupported Media Type")]
    [InlineData("application/vnd.shirokuro+json", "415 HTTP Unsupported Media Type")]
    [InlineData("text/plain", "real lobby endpoint")]
    public void Synthetic415FallbackRequiresBothFrameworkEndpointAndNonJson(
        string contentType, string displayName)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/lobbies";
        context.Request.Headers.ContentType = contentType;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask, new EndpointMetadataCollection(),
            displayName));

        Assert.False(
            GuestLobbyMutationMediaTypeGuard.IsFrameworkUnsupportedLobbyRoute(
                context, out var invalidRoom));
        Assert.False(invalidRoom);
    }

    [Fact]
    public void Synthetic415FallbackDoesNotClaimMissingContentTypeOrEndpoint()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/lobbies";

        // A missing Content-Type is handled by the normal ASP.NET binder.
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask, new EndpointMetadataCollection(),
            "415 HTTP Unsupported Media Type"));
        Assert.False(
            GuestLobbyMutationMediaTypeGuard.IsFrameworkUnsupportedLobbyRoute(
                context, out var invalidRoom));
        Assert.False(invalidRoom);

        // An explicit unsupported type without the framework fallback must
        // not be mistaken for a DTO-bound lobby route.
        context.Request.Headers.ContentType = "text/plain";
        context.SetEndpoint(null);
        Assert.False(
            GuestLobbyMutationMediaTypeGuard.IsFrameworkUnsupportedLobbyRoute(
                context, out invalidRoom));
        Assert.False(invalidRoom);
    }

    [Theory]
    [InlineData("/api/lobbies", true)]
    [InlineData("/API/LOBBIES", true)]
    [InlineData("/api", true)]
    [InlineData("/hubs/lobbies", true)]
    [InlineData("/HUBS/OTHER", true)]
    [InlineData("/apix/lobbies", false)]
    [InlineData("/hubs-other/lobbies", false)]
    [InlineData("/preview", false)]
    public void Synthetic405GuardRecognizesOnlyFrameworkApiAndHubFallback(
        string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(),
            "405 HTTP Method Not Supported"));

        Assert.Equal(expected,
            ApiMethodMismatchGuard.IsFrameworkMethodNotAllowed(context));
    }

    [Fact]
    public void Synthetic405GuardNeverBypassesOrdinaryEndpoints()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/lobbies";

        Assert.False(ApiMethodMismatchGuard.IsFrameworkMethodNotAllowed(context));
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(),
            "real lobby endpoint"));
        Assert.False(ApiMethodMismatchGuard.IsFrameworkMethodNotAllowed(context));

        // Even a coincidental display name cannot skip authentication if
        // the endpoint carries any real server-owned metadata.
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new WebMutationGuardRequired()),
            "405 HTTP Method Not Supported"));
        Assert.False(ApiMethodMismatchGuard.IsFrameworkMethodNotAllowed(context));
    }

    [Fact]
    public async Task Synthetic405PreservesAllowWithoutSignedGuestRedisLookup()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        using var capture = new MultiplayerMetricCapture();
        cache.Unavailable = true;
        // POST is the registered method for room creation. A request with
        // the wrong method uses the framework's synthetic 405 and Allow
        // header, not a real lobby handler or cookie Redis authentication.
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, "/api/lobbies");
            using var rejected = await guest.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, rejected.StatusCode);
            Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
            // TestServer's HttpClient abstraction does not consistently expose
            // the framework-generated Allow header, even though 405 is correct.
            // Assert its value whenever surfaced here; the real HTTP browser
            // Playwright regression independently requires Allow: POST.
            if (rejected.Headers.TryGetValues("Allow", out var values))
                Assert.Contains("POST", string.Join(",", values));
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
            Assert.False(rejected.Headers.Contains("Retry-After"));
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // An actual protected endpoint must still revalidate the signed
        // cookie against Redis, fail closed, then recover unchanged.
        using (var unavailable = await guest.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                unavailable.StatusCode);
            Assert.Equal("no-store",
                unavailable.Headers.CacheControl?.ToString());
        }
        Assert.Equal(1, cache.Reads);
        cache.Unavailable = false;
        using var recovered = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // A method mismatch is counted at the early routing fence, not as
        // an unavailable guest-cookie check. Real session requests above
        // still report their own Redis authority outcome.
        var ingress = capture.For(MultiplayerMetrics.IngressRejectionsName);
        Assert.Contains(ingress, x => x.Value == 1 &&
            x.Tags.Count == 1 && x.Tags["reason"] == "method-not-allowed");
        Assert.All(ingress, x => Assert.Single(x.Tags));
        Assert.Contains(capture.For(MultiplayerMetrics.GuestCookieAuthorityName),
            x => x.Tags["outcome"] == "unavailable");
    }

    [Fact]
    public async Task UnmatchedApiAndHubPathsRejectBeforeSignedGuestCookieRedisLookup()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        using var capture = new MultiplayerMetricCapture();
        cache.Unavailable = true;
        // Routing has no endpoint for any of these namespace-bound paths.
        // The explicit non-JSON body must not turn a missing route into the
        // synthetic 415 fallback reserved for five actual DTO commands.
        foreach (var path in new[]
                 {
                     "/api",
                     "/api/not-a-route",
                     "/API/NOT-A-ROUTE",
                     "/api/lobbies/not-a-room/join/extra",
                     "/hubs/not-a-hub"
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var missing = await guest.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("no-store", missing.Headers.CacheControl?.ToString());
            Assert.False(missing.Headers.Contains("Set-Cookie"));
            Assert.False(missing.Headers.Contains("Retry-After"));
        }

        using (var request = Mutation(
                   HttpMethod.Post, "/api/lobbies/not-a-room/join/extra",
                   "https://game.example"))
        {
            request.Content = new StringContent(
                "{}", Encoding.UTF8, "text/plain");
            using var missing = await guest.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("no-store", missing.Headers.CacheControl?.ToString());
            Assert.False(missing.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // Routing does match this path but not the GET method: its synthetic
        // 405 endpoint must keep the framework's method semantics. It must
        // not be treated as an unmatched route or a JSON media-type 415.
        using (var wrongMethod = await anonymous.GetAsync("/api/lobbies"))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // Only a real, authorized API route asks the Redis-backed cookie
        // authority; it must remain fail-closed during the same outage.
        using (var protectedRead = await guest.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                protectedRead.StatusCode);
            Assert.Equal("no-store",
                protectedRead.Headers.CacheControl?.ToString());
        }
        Assert.Equal(1, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        cache.Unavailable = false;
        using var restored = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, cache.Writes);

        // The counter uses closed server-provided labels only, irrespective
        // of which unknown URL or hostile room suffix was requested.
        var ingress = capture.For(MultiplayerMetrics.IngressRejectionsName);
        Assert.Contains(ingress, x => x.Value == 1 &&
            x.Tags.Count == 1 && x.Tags["reason"] == "unknown-route");
        Assert.Contains(ingress, x => x.Value == 1 &&
            x.Tags.Count == 1 && x.Tags["reason"] == "method-not-allowed");
        Assert.All(ingress, x =>
        {
            Assert.Single(x.Tags);
            Assert.DoesNotContain("not-a-route",
                string.Join(",", x.Tags.Values), StringComparison.Ordinal);
            Assert.DoesNotContain("not-a-room",
                string.Join(",", x.Tags.Values), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task NonJsonContentTypeOnReadEndpointsDoesNotTriggerLobbyFallback()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // A JSON media-type rejection must never mask the Redis authority
        // failure on read-only endpoints protected by the signed guest cookie.
        cache.Unavailable = true;
        foreach (var path in new[]
                 {
                     "/api/sessions/me",
                     "/api/lobbies/room-00000000000000000000000000000000/roster"
                 })
        {
            using var read = new HttpRequestMessage(HttpMethod.Get, path);
            read.Content = new StringContent("{}", Encoding.UTF8, "text/plain");
            using var denied = await guest.SendAsync(read);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // Public liveness remains independent of both cookie authority and
        // the JSON mutation guard, even with a non-JSON content-type header.
        using var health = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        health.Content = new StringContent("{}", Encoding.UTF8, "text/plain");
        using var healthy = await anonymous.SendAsync(health);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal(2, cache.Reads);
    }

    [Fact]
    public async Task NonJsonLobbyMutationsRejectBeforeGuestRedisAndRoomQuota()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        cache.Unavailable = true;
        // All five DTO-bound mutations share the server-owned metadata.
        foreach (var (method, path) in MutationRoutes.Skip(2))
        foreach (var contentType in new[]
                 { "text/plain", "application/x-www-form-urlencoded" })
        {
            using var nonJson = Mutation(method, path, "https://game.example");
            nonJson.Content = new StringContent("{}", Encoding.UTF8, contentType);
            using var denied = await guest.SendAsync(nonJson);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie"));
            Assert.False(denied.Headers.Contains("Retry-After"));
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);

        // CSRF Origin still wins over an unsupported media type. Size 413
        // also wins, and neither attempt can read Redis or consume quota.
        using (var hostile = Mutation(HttpMethod.Post, "/api/lobbies",
                   "https://evil.example"))
        {
            hostile.Content = new StringContent("{}", Encoding.UTF8, "text/plain");
            using var denied = await guest.SendAsync(hostile);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        }
        using (var tooLarge = Mutation(HttpMethod.Post, "/api/lobbies",
                   "https://game.example"))
        {
            tooLarge.Content = new StringContent(
                new string('x', (int)GuestLobbyMutationBodyLimit.MaxBytes + 1),
                Encoding.UTF8, "text/plain");
            using var denied = await guest.SendAsync(tooLarge);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, denied.StatusCode);
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // The framework 415 fallback must not bypass canonical room-ID
        // validation even when both fields are invalid.
        using (var badRoom = Mutation(HttpMethod.Post,
                   "/api/lobbies/not-a-room/join", "https://game.example"))
        {
            badRoom.Content = new StringContent("{}", Encoding.UTF8, "text/plain");
            using var denied = await guest.SendAsync(badRoom);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        }
        Assert.Equal(0, cache.Reads);

        // Both JSON media types must pass this syntactic guard. Redis is
        // intentionally unavailable, so existing auth fails closed at 503.
        foreach (var contentType in new[]
                 { "application/json", "application/vnd.shirokuro+json" })
        {
            using var json = Mutation(HttpMethod.Post, "/api/lobbies",
                "https://game.example");
            json.Content = new StringContent("{}", Encoding.UTF8, contentType);
            using var unavailable = await guest.SendAsync(json);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
        }
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        cache.Unavailable = false;
        using var me = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(3, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(1, limiter.Calls);
    }

    [Theory]
    [InlineData(null, 4096L)]
    [InlineData(30_000L, 4096L)]
    [InlineData(2048L, 2048L)]
    public void LobbyBodyLimitCapsStreamingRequestsWithoutRelaxingHostLimit(
        long? configured, long expected)
    {
        var context = new DefaultHttpContext();
        var feature = new FakeBodySizeFeature { MaxRequestBodySize = configured };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        Assert.True(GuestLobbyMutationBodyLimit.TryApply(context));
        Assert.Equal(expected, feature.MaxRequestBodySize);
    }

    [Fact]
    public void LobbyBodyLimitDoesNotMutateReadOnlyFeature()
    {
        var context = new DefaultHttpContext();
        var feature = new FakeBodySizeFeature
        {
            IsReadOnly = true,
            MaxRequestBodySize = 8192
        };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        Assert.True(GuestLobbyMutationBodyLimit.TryApply(context));
        Assert.Equal(8192, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task DevelopmentRejectsAllBadCookieMutationsButAcceptsValidReads()
    {
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var anonymous = Client(server, development: true);
        var cookie = await Issue(anonymous, "http://localhost");
        using var guest = Client(server, development: true, cookie: cookie);

        foreach (var (method, path) in MutationRoutes)
        {
            using var invalid = Mutation(method, path, "http://attacker.example");
            using var denied = await guest.SendAsync(invalid);
            AssertEarlyForbidden(denied);
        }
        using (var absentIntent = Mutation(
            HttpMethod.Delete, "/api/sessions/guest", "http://localhost",
            withIntent: false))
        using (var denied = await guest.SendAsync(absentIntent))
            AssertEarlyForbidden(denied);

        using (var me = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using (var badRoom = Mutation(
            HttpMethod.Patch, "/api/lobbies/not-a-room/hero",
            "http://localhost"))
        using (var response = await guest.SendAsync(badRoom))
        {
            // Valid Origin passes the new middleware and reaches normal
            // handler input validation/membership checks.
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CookieAuthAnonymousPrivateReadsAreNeverCacheable(
        bool development)
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = development
            ? new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Development"))
            : Production(cache, limiter);
        using var anonymous = Client(server, development: development);

        // These 401s are generated by RequireAuthorization BEFORE the
        // endpoint sets Cache-Control. Neither response may be cached or
        // set a guest cookie.
        foreach (var path in new[]
                 {
                     "/api/sessions/me",
                     "/api/lobbies/room-00000000000000000000000000000000/roster"
                 })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("guest-", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(0, limiter.Calls);
    }

    [Fact]
    public async Task CookieAuthForbidIsNeverCacheableEvenWithoutEndpointHandler()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var bootstrap = Client(server);
        // Starting TestServer resolves the actual configured cookie auth
        // handler. ForbidAsync deliberately skips any API handler, proving
        // that the event itself installs no-store.
        using (var health = await bootstrap.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var context = new DefaultHttpContext
        {
            RequestServices = server.Services
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("game.example");

        await context.ForbidAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal(0, cache.Reads);
        Assert.Equal(0, limiter.Calls);
    }

    [Fact]
    public async Task MalformedRoomIdsNeverSpendGuestRedisOrMutationQuota()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // The same signed guest whose Redis ticket cannot be checked is
        // attached to each request. Impossible room codes must be rejected
        // by SERVER-OWNED endpoint metadata before cookie authentication.
        cache.Unavailable = true;
        foreach (var bad in new[]
                 {
                     "room-1", "room-" + new string('A', 32),
                     "room-" + new string('a', 31),
                     "room-" + new string('a', 33),
                     "room-" + new string('a', 31) + "-bad"
                 })
        {
            using (var roster = await guest.GetAsync(
                       "/api/lobbies/" + bad + "/roster"))
            {
                Assert.Equal(HttpStatusCode.NotFound, roster.StatusCode);
                Assert.Equal("no-store", roster.Headers.CacheControl?.ToString());
                Assert.False(roster.Headers.Contains("Set-Cookie"));
            }

            foreach (var (method, suffix) in new[]
                     {
                         (HttpMethod.Post, "/join"),
                         (HttpMethod.Patch, "/hero"),
                         (HttpMethod.Patch, "/team"),
                         (HttpMethod.Delete, "/membership")
                     })
            {
                using var request = Mutation(method,
                    "/api/lobbies/" + bad + suffix, "https://game.example");
                using var rejected = await guest.SendAsync(request);
                Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
                Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
                Assert.False(rejected.Headers.Contains("Set-Cookie"));
                Assert.False(rejected.Headers.Contains("Retry-After"));
            }
            Assert.Equal(0, cache.Reads);
            Assert.Equal(1, limiter.Calls);
        }

        // A canonically shaped but absent room still reaches the normal
        // guest auth check: Redis being unavailable must fail CLOSED (503),
        // not be mistaken for a missing room or bypass the authority.
        const string valid = "room-00000000000000000000000000000000";
        using (var roster = await guest.GetAsync(
                   "/api/lobbies/" + valid + "/roster"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, roster.StatusCode);
            Assert.Equal("no-store", roster.Headers.CacheControl?.ToString());
            Assert.False(roster.Headers.Contains("Set-Cookie"));
        }
        using (var request = Mutation(HttpMethod.Post,
                   "/api/lobbies/" + valid + "/join", "https://game.example"))
        using (var denied = await guest.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task ProductionHostileHubOriginsAreRejectedBeforeGuestRedisAuthority()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(1, limiter.Calls);
        Assert.Equal(0, cache.Reads);

        cache.Unavailable = true;
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/hubs/lobbies/negotiate?negotiateVersion=1"),
                     (HttpMethod.Get, "/hubs/lobbies")
                 })
        {
            foreach (var origin in new[]
                     {
                         "https://evil.example", "null", "https://game.example/path",
                         "https://game.example, https://evil.example"
                     })
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.TryAddWithoutValidation("Origin", origin);
                using var denied = await guest.SendAsync(request);
                AssertEarlyForbidden(denied);
                Assert.Equal(0, cache.Reads); // Hostile Origin never reaches Redis.
                Assert.Equal(1, limiter.Calls);
            }
        }

        // A legitimate HTTPS Origin passes this fence, but Redis failure
        // still fails closed. No cookie, sensitive error or roster leaks.
        using (var trusted = new HttpRequestMessage(HttpMethod.Post,
                   "/hubs/lobbies/negotiate?negotiateVersion=1"))
        {
            trusted.Headers.TryAddWithoutValidation("Origin", "https://game.example");
            using var unavailable = await guest.SendAsync(trusted);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
            Assert.False(unavailable.Headers.Contains("Set-Cookie"));
            Assert.DoesNotContain("Private Redis",
                await unavailable.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // A valid signed guest still negotiates once the authority recovers.
        cache.Unavailable = false;
        using var recoveredRequest = new HttpRequestMessage(HttpMethod.Post,
            "/hubs/lobbies/negotiate?negotiateVersion=1");
        recoveredRequest.Headers.TryAddWithoutValidation("Origin", "https://game.example");
        using var recovered = await guest.SendAsync(recoveredRequest);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task PublicHexPreviewWithSignedGuestCookieDoesNotRequireRedisAuthority()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // The browser may attach its signed guest cookie to the completely
        // public hex preview. Redis is unavailable, but this endpoint never
        // consumes the principal and must not wait for guest auth validation.
        cache.Unavailable = true;
        foreach (var (route, radius, expectedCells) in new[]
                 {
                     ("/api/preview/board", 2, 19),
                     ("/api/preview/board?radius=1", 1, 7),
                     ("/api/preview/board?radius=6", 6, 127)
                 })
        {
            using var response = await guest.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(radius, json.RootElement.GetProperty("radius").GetInt32());
            var cells = json.RootElement.GetProperty("cells");
            Assert.Equal(expectedCells, cells.GetArrayLength());
            Assert.All(cells.EnumerateArray(), cell =>
                Assert.Equal(0,
                    cell.GetProperty("q").GetInt32() +
                    cell.GetProperty("r").GetInt32() +
                    cell.GetProperty("s").GetInt32()));
            Assert.Equal(0, cache.Reads);
            Assert.Equal(1, limiter.Calls);
        }

        // Technical query validation is still enforced on public previews.
        foreach (var invalid in new[]
                 {
                     "/api/preview/board?radius=0",
                     "/api/preview/board?radius=7",
                     "/api/preview/board?radius=not-a-number"
                 })
        {
            using var response = await guest.GetAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.Equal(0, cache.Reads);
        }

        // No transitive bypass: protected identity and member-only roster
        // paths still consult Redis and fail closed with 503/no-store.
        foreach (var protectedPath in new[]
                 {
                     "/api/sessions/me",
                     "/api/lobbies/room-00000000000000000000000000000000/roster"
                 })
        {
            using var denied = await guest.GetAsync(protectedPath);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // A real protected session read works again only when Redis authority
        // is available; public preview never granted guest authorization.
        cache.Unavailable = false;
        using var me = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(3, cache.Reads);
    }

    [Fact]
    public async Task PublicProbesWithGuestCookieNeverAskUnavailableRedisForAuthentication()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter, stubReadiness: true);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        cache.Unavailable = true;

        // The signed cookie is attached, but neither public probe needs
        // to identify its visitor. Stub readiness isolates cookie auth.
        foreach (var route in new[] { "/api/health", "/api/ready" })
        {
            using var probe = await guest.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
            Assert.Equal("no-store", probe.Headers.CacheControl?.ToString());
            Assert.False(probe.Headers.Contains("Set-Cookie"));
            using var body = JsonDocument.Parse(
                await probe.Content.ReadAsStringAsync());
            if (route == "/api/health")
            {
                Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
                Assert.Equal("Shirokuro",
                    body.RootElement.GetProperty("application").GetString());
                Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            }
            else
            {
                Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
                Assert.Single(body.RootElement.EnumerateObject());
            }
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls); // Only initial issuance.

        // The protected API still revalidates this exact guest cookie
        // and fails closed if authoritative Redis is down.
        using var restricted = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, restricted.StatusCode);
        Assert.Equal("no-store", restricted.Headers.CacheControl?.ToString());
        Assert.False(restricted.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, cache.Reads);
    }
}
