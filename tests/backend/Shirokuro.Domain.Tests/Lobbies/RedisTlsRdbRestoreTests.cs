using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Isolated single-node Redis TLS RDB disaster-recovery rehearsal. The job
/// provides a separate disposable Redis server on localhost:6381, an already
/// trusted test TLS certificate, and a private temporary backup directory.
/// Test clients never get Docker control. The key ring is kept intact here;
/// offline Data Protection restore is independently exercised by #47.
/// </summary>
public sealed class RedisTlsRdbRestoreTests
{
    private const string Source = "shirokuro-ci-redis-rdb-source";
    private const string Replacement = "shirokuro-ci-redis-rdb-replacement";
    private const string ExpiryIndexKey =
        "shirokuro:guest-expiry:{default}:v1:pending";

    private static string? Connection =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_RDB");

    private static WebApplicationFactory<Program> Server(
        DateTimeOffset? recoveryCutoffUtc = null)
    {
        var connection = Connection
            ?? throw new InvalidOperationException("Redis TLS RDB fixture required.");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            builder.UseSetting(GuestSessionRedisConfiguration.ConnectionStringKey, connection);
            // Cutoff is managed outside Redis, so restoring an old RDB
            // cannot roll it back. Every replacement node receives the same
            // post-recovery value from its independent configuration.
            var configuration = new Dictionary<string, string?>
            {
                [GuestSessionRedisConfiguration.ConnectionStringKey] = connection,
                ["AllowedHosts"] = "game.example",
                ["Security:AllowedOrigins:0"] = "https://game.example"
            };
            if (recoveryCutoffUtc is not null)
            {
                var value = recoveryCutoffUtc.Value.ToString("O", CultureInfo.InvariantCulture);
                configuration[GuestSessionRecoveryCutoff.ConfigurationKey] = value;
                builder.UseSetting(GuestSessionRecoveryCutoff.ConfigurationKey, value);
            }
            builder.ConfigureAppConfiguration((_, settings) =>
                settings.AddInMemoryCollection(configuration));
            // Actual Production DI: no fake Redis cache, multiplexer, lobby,
            // guest registry, expiry index, revocation fence or key ring.
        });
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

    private static HttpRequestMessage Mutation(HttpMethod method, string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<(string Id, string Cookie)> Login(HttpClient client)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("playerId").GetString()!,
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0]);
    }

    private static async Task<string> DockerAsync(params string[] args)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Isolated RDB fixture unavailable.");
        // Consume both streams; failures must not leak RDB/Redis data or paths.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill();
            throw new TimeoutException("Isolated RDB fixture command timed out.");
        }
        var result = await standardOutput;
        _ = await standardError;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Isolated RDB fixture command failed.");
        return result.Trim();
    }

    private static async Task StartReplacementAsync(string dataDirectory,
        string certificateDirectory)
    {
        await DockerAsync("run", "-d",
            "--name", Replacement,
            "-p", "127.0.0.1:6381:6380",
            "-v", certificateDirectory + ":/certs:ro",
            "-v", dataDirectory + ":/data",
            "redis:7.4-alpine", "redis-server",
            "--port", "0",
            "--tls-port", "6380",
            "--tls-cert-file", "/certs/server.crt",
            "--tls-key-file", "/certs/server.key",
            "--tls-ca-cert-file", "/certs/ca.crt",
            "--tls-auth-clients", "no",
            "--dir", "/data",
            "--dbfilename", "dump.rdb",
            "--save", "",
            "--appendonly", "no");

        for (var attempt = 0; attempt < 45; attempt++)
        {
            try
            {
                var reply = await DockerAsync("exec", Replacement, "redis-cli",
                    "--tls", "--cacert", "/certs/ca.crt", "-h", "localhost",
                    "-p", "6380", "ping");
                if (reply == "PONG")
                    return;
            }
            catch (InvalidOperationException)
            {
                // Container may not yet be listening; no secret is logged.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        throw new InvalidOperationException("Restored Redis TLS did not become ready.");
    }

    [Fact]
    public async Task ConsistentRdbRestoreRecoversSessionsEncryptedExpiryAndLobbyButEmptyRedisDeniesCookies()
    {
        if (Connection is null ||
            Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_RDB_CHAOS") != "1")
            return; // Only the isolated, explicitly opted-in CI fixture can be replaced.

        Assert.True(ConfigurationOptions.Parse(Connection).Ssl);
        var workspace = Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_RDB_DIR");
        var certificates = Environment.GetEnvironmentVariable("SHIROKURO_TLS_CERT_DIR");
        if (string.IsNullOrWhiteSpace(workspace) ||
            string.IsNullOrWhiteSpace(certificates) ||
            !Path.IsPathFullyQualified(workspace) ||
            !Path.IsPathFullyQualified(certificates) ||
            !Directory.Exists(workspace) ||
            !Directory.Exists(certificates))
            throw new InvalidOperationException("Invalid private Redis RDB CI fixture.");

        var backup = Path.Combine(workspace, "backup", "dump.rdb");
        var restored = Path.Combine(workspace, "restore");
        var empty = Path.Combine(workspace, "empty");
        Assert.True(Directory.Exists(restored));
        Assert.True(Directory.Exists(empty));
        Assert.False(File.Exists(backup));

        string alice, bob, revokedBeforeBackup;
        string cookieAlice, cookieBob, revokedCookie;
        string room;
        long revision;
        string[] expiryMembers;

        using (var originalA = Server())
        using (var originalB = Server())
        using (var visitorA = Client(originalA))
        using (var visitorB = Client(originalB))
        {
            using (var ready = await visitorA.GetAsync("/api/ready"))
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            (alice, cookieAlice) = await Login(visitorA);
            (bob, cookieBob) = await Login(visitorB);
            (revokedBeforeBackup, revokedCookie) = await Login(visitorA);
            using var playerA = Client(originalA, cookieAlice);
            using var playerB = Client(originalB, cookieBob);
            using var revokedClient = Client(originalB, revokedCookie);

            using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
                       new { teamIds = new[] { "white", "black" }, teamId = "white" }))
            using (var response = await playerA.SendAsync(create))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                room = json.RootElement.GetProperty("lobbyId").GetString()!;
                revision = json.RootElement.GetProperty("revision").GetInt64();
            }

            using (var join = Mutation(HttpMethod.Post, "/api/lobbies/" + room + "/join",
                       new { teamId = "black", expectedRevision = revision }))
            using (var response = await playerB.SendAsync(join))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using (var roster = await playerB.GetAsync("/api/lobbies/" + room + "/roster"))
            {
                Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
                using var doc = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
                revision = doc.RootElement.GetProperty("revision").GetInt64();
                Assert.Equal(2, doc.RootElement.GetProperty("participants").GetArrayLength());
            }

            // A token revoked BEFORE the consistent snapshot must stay
            // revoked even after restoration (this does not protect against
            // revocations performed AFTER the snapshot: see the runbook).
            using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
            using (var response = await revokedClient.SendAsync(logout))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            using (var denied = await revokedClient.GetAsync("/api/sessions/me"))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            var db = originalA.Services
                .GetRequiredService<IConnectionMultiplexer>().GetDatabase();
            expiryMembers = (await db.SortedSetRangeByRankAsync(ExpiryIndexKey))
                .Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.True(expiryMembers.Length >= 2);
            Assert.All(expiryMembers, x =>
            {
                Assert.DoesNotContain(alice, x, StringComparison.Ordinal);
                Assert.DoesNotContain(bob, x, StringComparison.Ordinal);
                Assert.DoesNotContain(revokedBeforeBackup, x, StringComparison.Ordinal);
            });

            // Redis SAVE blocks until a consistent RDB has been persisted.
            // The snapshot never leaves this private CI runner.
            var save = await DockerAsync("exec", Source, "redis-cli",
                "--tls", "--cacert", "/certs/ca.crt", "-h", "localhost",
                "-p", "6380", "SAVE");
            Assert.Equal("OK", save);
            await DockerAsync("cp", Source + ":/data/dump.rdb", backup);
            using var rdb = File.OpenRead(backup);
            Assert.True(rdb.Length > 50);
            var magic = new byte[5];
            await rdb.ReadExactlyAsync(magic);
            Assert.Equal("REDIS", Encoding.ASCII.GetString(magic));
        }

        // The source process and all its data are intentionally discarded.
        await DockerAsync("rm", "-f", Source);
        try
        {
            // New Redis with an empty /data: valid signed cookies alone MUST
            // NOT authenticate because the authoritative session keys are gone.
            await StartReplacementAsync(empty, certificates);
            using (var withoutData = Server())
            using (var staleCookie = Client(withoutData, cookieAlice))
            using (var anonymous = Client(withoutData))
            {
                using var ready = await anonymous.GetAsync("/api/ready");
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
                using var denied = await staleCookie.GetAsync("/api/sessions/me");
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                var db = withoutData.Services
                    .GetRequiredService<IConnectionMultiplexer>().GetDatabase();
                Assert.Equal(0, await db.SortedSetLengthAsync(ExpiryIndexKey));
            }

            await DockerAsync("rm", "-f", Replacement);
            File.Copy(backup, Path.Combine(restored, "dump.rdb"));
            await StartReplacementAsync(restored, certificates);

            // Two NEW real Production nodes share the restored authoritative
            // Redis database and original persisted Data Protection ring.
            using var recoveredA = Server();
            using var recoveredB = Server();
            using var guestA = Client(recoveredB, cookieAlice);
            using var guestB = Client(recoveredA, cookieBob);
            using var revoked = Client(recoveredB, revokedCookie);

            using (var ready = await recoveredA.CreateClient(
                       new WebApplicationFactoryClientOptions
                       { BaseAddress = new Uri("https://game.example") })
                       .GetAsync("/api/ready"))
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            foreach (var guest in new[] { guestA, guestB })
            {
                using var response = await guest.GetAsync("/api/sessions/me");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            using (var denied = await revoked.GetAsync("/api/sessions/me"))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            using (var roster = await guestB.GetAsync("/api/lobbies/" + room + "/roster"))
            {
                Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
                using var doc = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
                Assert.Equal(revision, doc.RootElement.GetProperty("revision").GetInt64());
                var actual = doc.RootElement.GetProperty("participants").EnumerateArray()
                    .Select(x => x.GetProperty("playerId").GetString()).ToArray();
                Assert.Equal(2, actual.Length);
                Assert.Contains(alice, actual);
                Assert.Contains(bob, actual);
            }

            var restoredDatabase = recoveredB.Services
                .GetRequiredService<IConnectionMultiplexer>().GetDatabase();
            var actualExpiry = (await restoredDatabase
                    .SortedSetRangeByRankAsync(ExpiryIndexKey))
                .Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(expiryMembers, actualExpiry);

            using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
            using (var response = await guestA.SendAsync(logout))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            using (var denied = await guestA.GetAsync("/api/sessions/me"))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            // The restored revocation fence is again enforced by Redis Lua.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                recoveredA.Services.GetRequiredService<IAsyncGuestLobby>()
                    .JoinAsync(room, alice, "white", null));

            using (var survivor = await guestB.GetAsync("/api/lobbies/" + room + "/roster"))
            {
                Assert.Equal(HttpStatusCode.OK, survivor.StatusCode);
                using var doc = JsonDocument.Parse(await survivor.Content.ReadAsStringAsync());
                var participant = Assert.Single(doc.RootElement.GetProperty("participants")
                    .EnumerateArray());
                Assert.Equal(bob, participant.GetProperty("playerId").GetString());
            }

            // SECURITY REGRESSION: Alice was revoked AFTER the RDB snapshot.
            // Restoring that older snapshot resurrects the Redis session key
            // (and the old membership). The signed cookie would authenticate
            // again unless an OUT-OF-BAND issuance cutoff is deployed.
            await DockerAsync("rm", "-f", Replacement);
            var rollback = Path.Combine(workspace, "rollback");
            Directory.CreateDirectory(rollback);
            File.Copy(backup, Path.Combine(rollback, "dump.rdb"));
            await StartReplacementAsync(rollback, certificates);

            using (var unprotected = Server())
            using (var replay = Client(unprotected, cookieAlice))
            {
                using var accidentallyValid = await replay.GetAsync("/api/sessions/me");
                Assert.Equal(HttpStatusCode.OK, accidentallyValid.StatusCode);
            }

            // The operator places the environment behind a maintenance
            // gate and rotates this trusted config value AFTER the snapshot.
            // Redis cannot undo it by loading old keys or session tokens.
            var cutoff = DateTimeOffset.UtcNow;
            // The framework serializes cookie ticket issued-at values with
            // second-level granularity. Wait beyond that boundary before
            // expecting fresh issuance to pass the strict '>' comparison.
            // A session minted in the cutoff second must fail closed.
            await Task.Delay(TimeSpan.FromSeconds(2));
            using var guardedA = Server(cutoff);
            using var guardedB = Server(cutoff);
            using var staleOnA = Client(guardedA, cookieAlice);
            using var staleOnB = Client(guardedB, cookieAlice);
            using var oldBob = Client(guardedA, cookieBob);
            foreach (var stale in new[] { staleOnA, staleOnB, oldBob })
            {
                using var denied = await stale.GetAsync("/api/sessions/me");
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }

            // The recovery-mode expiry worker must also remove old
            // pre-match memberships BEFORE their original 8h expiration,
            // while preserving the encrypted-fence/CAS/ack ordering.
            // Worker startup may have raced and already completed it.
            var maintenance = Assert.Single(guardedA.Services
                .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<GuestSessionExpiryWorker>());
            await maintenance.SweepOnceAsync();
            var restoredStorage = guardedA.Services
                .GetRequiredService<RedisLobbySnapshotStore>();
            Assert.Null(await restoredStorage.ReadAsync(room));
            var restoredExpiry = guardedA.Services
                .GetRequiredService<IConnectionMultiplexer>().GetDatabase();
            Assert.Equal(0, await restoredExpiry.SortedSetLengthAsync(ExpiryIndexKey));

            // New guest sessions after the cutover remain valid across
            // independently started Production nodes on the same TLS Redis,
            // including an explicit additional recovery sweep. New lobby
            // and expiry entries must not be accidentally purged.
            using var visitor = Client(guardedA);
            var (_, freshCookie) = await Login(visitor);
            using var freshAcrossNodes = Client(guardedB, freshCookie);
            using var freshMe = await freshAcrossNodes.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.OK, freshMe.StatusCode);
            using (var freshCreate = Mutation(HttpMethod.Post, "/api/lobbies",
                       new { teamIds = new[] { "white", "black" }, teamId = "white" }))
            using (var createdFresh = await freshAcrossNodes.SendAsync(freshCreate))
            {
                Assert.Equal(HttpStatusCode.Created, createdFresh.StatusCode);
                using var doc = JsonDocument.Parse(await createdFresh.Content.ReadAsStringAsync());
                var freshRoom = doc.RootElement.GetProperty("lobbyId").GetString()!;
                await maintenance.SweepOnceAsync();
                Assert.NotNull(await restoredStorage.ReadAsync(freshRoom));
                Assert.Equal(1, await restoredExpiry.SortedSetLengthAsync(ExpiryIndexKey));
                using var freshRoster = await freshAcrossNodes.GetAsync(
                    "/api/lobbies/" + freshRoom + "/roster");
                Assert.Equal(HttpStatusCode.OK, freshRoster.StatusCode);
            }
        }
        finally
        {
            // Cleanup in both xUnit and the CI always() workflow step.
            try { await DockerAsync("rm", "-f", Replacement); }
            catch (InvalidOperationException) { }
        }
    }
}
