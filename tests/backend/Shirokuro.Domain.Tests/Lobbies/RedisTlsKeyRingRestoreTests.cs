using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// A simulated offline disaster-recovery drill: both API processes terminate,
/// their original Data Protection volume disappears, and the encrypted XML
/// ring plus original private-key PFX are restored to a new absolute path.
/// Redis is deliberately left intact and uses real authenticated TLS.
/// </summary>
public sealed class RedisTlsKeyRingRestoreTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_TLS");

    private sealed class TemporaryRing : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "shirokuro-keyring-restore-" + Guid.NewGuid().ToString("N"));
        public string Source { get; }
        public string Restored { get; }
        public string MissingKeys { get; }
        public string WrongCertificate { get; }
        public string Password { get; } = Guid.NewGuid().ToString("N");

        public TemporaryRing()
        {
            Source = Path.Combine(_root, "original");
            Restored = Path.Combine(_root, "restored");
            MissingKeys = Path.Combine(_root, "no-keys");
            WrongCertificate = Path.Combine(_root, "wrong-certificate");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Restored);
            Directory.CreateDirectory(MissingKeys);
            Directory.CreateDirectory(WrongCertificate);
            foreach (var folder in new[] { _root, Source, Restored, MissingKeys, WrongCertificate })
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(folder,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            WritePfx(Source);
        }

        public string Certificate(string folder) => Path.Combine(folder, "protect.pfx");

        public void WritePfx(string folder)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Shirokuro Backup Restore CI",
                rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
            File.WriteAllBytes(Certificate(folder),
                certificate.Export(X509ContentType.Pkcs12, Password));
            Restrict(Certificate(folder));
        }

        private static void Restrict(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public (string FileName, byte[] EncryptedXml)[] CaptureEncryptedKeys()
        {
            var entries = Directory.GetFiles(Source, "key-*.xml")
                .Select(file => (FileName: Path.GetFileName(file),
                                 EncryptedXml: File.ReadAllBytes(file)))
                .ToArray();
            Assert.NotEmpty(entries);
            Assert.All(entries, entry =>
            {
                var xml = System.Text.Encoding.UTF8.GetString(entry.EncryptedXml);
                Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
                Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
                Assert.DoesNotContain("guest-", xml, StringComparison.Ordinal);
            });
            return entries;
        }

        public void Restore((string FileName, byte[] EncryptedXml)[] keys,
            byte[] pfx)
        {
            // The original installation disappears before this restore.
            Directory.Delete(Source, recursive: true);
            foreach (var item in keys)
            {
                var file = Path.Combine(Restored, item.FileName);
                File.WriteAllBytes(file, item.EncryptedXml);
                Restrict(file);
            }
            File.WriteAllBytes(Certificate(Restored), pfx);
            Restrict(Certificate(Restored));
        }

        public void RestoreIncomplete(byte[] pfx,
            (string FileName, byte[] EncryptedXml)[] keys)
        {
            File.WriteAllBytes(Certificate(MissingKeys), pfx);
            Restrict(Certificate(MissingKeys));
            WritePfx(WrongCertificate);
            foreach (var item in keys)
            {
                var file = Path.Combine(WrongCertificate, item.FileName);
                File.WriteAllBytes(file, item.EncryptedXml);
                Restrict(file);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static WebApplicationFactory<Program> Server(TemporaryRing ring,
        string folder)
    {
        var redis = Connection ??
            throw new InvalidOperationException("The Redis TLS test fixture is required.");
        var certificate = ring.Certificate(folder);
        var values = new Dictionary<string, string?>
        {
            [GuestSessionRedisConfiguration.ConnectionStringKey] = redis,
            ["Security:DataProtection:KeysDirectory"] = folder,
            ["Security:DataProtection:CertificatePath"] = certificate,
            ["Security:DataProtection:CertificatePassword"] = ring.Password,
            ["AllowedHosts"] = "game.example",
            ["Security:AllowedOrigins:0"] = "https://game.example"
        };

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var (key, value) in values)
                builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(values));
            // Intentionally no ConfigureTestServices: redis cache/multiplexer,
            // lobby, expiry worker, auth and DP PFX loader are all Production.
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

    private static HttpRequestMessage Mutation(HttpMethod method,
        string path, object? body = null)
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

    private static async Task<(string Room, long Revision)> CreateRoom(HttpClient client)
    {
        using var request = Mutation(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("lobbyId").GetString()!,
                json.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task OfflineRestoreToNewAbsolutePathPreservesTlsCookiesRoomAndEncryptedExpiryPurpose()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        Assert.True(ConfigurationOptions.Parse(Connection).Ssl);
        using var ring = new TemporaryRing();

        string alice;
        string bob;
        string cookieA;
        string cookieB;
        string room;
        long revision;
        string protectedExpiry;

        // Two genuine Production nodes with their own TLS Redis clients.
        // Startup/issuance produces an encrypted, persisted key-*.xml file.
        using (var nodeA = Server(ring, ring.Source))
        using (var nodeB = Server(ring, ring.Source))
        using (var visitorA = Client(nodeA))
        using (var visitorB = Client(nodeB))
        {
            (alice, cookieA) = await Login(visitorA);
            (bob, cookieB) = await Login(visitorB);
            using var playerA = Client(nodeA, cookieA);
            using var playerB = Client(nodeB, cookieB);

            (room, revision) = await CreateRoom(playerA);
            using (var join = Mutation(HttpMethod.Post, "/api/lobbies/" + room + "/join",
                       new { teamId = "black", expectedRevision = revision }))
            using (var joined = await playerB.SendAsync(join))
                Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

            using (var roster = await playerB.GetAsync("/api/lobbies/" + room + "/roster"))
            {
                Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
                using var doc = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
                revision = doc.RootElement.GetProperty("revision").GetInt64();
                Assert.Equal(2, doc.RootElement.GetProperty("participants").GetArrayLength());
            }

            // This is precisely the purpose used by encrypted Redis expiry
            // members, not the cookie purpose; it must survive the same restore.
            var protector = nodeA.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Shirokuro.GuestExpiryIndex.v1");
            protectedExpiry = protector.Protect(
                DateTimeOffset.UtcNow.AddHours(1).UtcTicks + ":" + alice);
            Assert.DoesNotContain(alice, protectedExpiry, StringComparison.Ordinal);
        }

        var keys = ring.CaptureEncryptedKeys();
        var certificateBackup = File.ReadAllBytes(ring.Certificate(ring.Source));
        ring.Restore(keys, certificateBackup);
        ring.RestoreIncomplete(certificateBackup, keys);
        Assert.False(Directory.Exists(ring.Source));

        // A PFX without the original ring MUST NOT authorize old cookies even
        // though their Redis guest-session records still exist.
        using (var incomplete = Server(ring, ring.MissingKeys))
        using (var stale = Client(incomplete, cookieA))
        {
            using var denial = await stale.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.Unauthorized, denial.StatusCode);
        }

        // New ring XML with the WRONG PFX is insufficient to decrypt old
        // purpose-isolated expiry records (or the original cookies).
        using (var wrong = Server(ring, ring.WrongCertificate))
        {
            var protector = wrong.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Shirokuro.GuestExpiryIndex.v1");
            Assert.ThrowsAny<CryptographicException>(() =>
                protector.Unprotect(protectedExpiry));
        }

        // Entire source installation is gone. Fresh Production hosts are
        // constructed from the restored ring and PFX in another directory.
        using var recoveredA = Server(ring, ring.Restored);
        using var recoveredB = Server(ring, ring.Restored);
        using var guestA = Client(recoveredB, cookieA);
        using var guestB = Client(recoveredA, cookieB);
        using var anonymous = Client(recoveredB);

        foreach (var guest in new[] { guestA, guestB })
        {
            using var me = await guest.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }

        var afterProtector = recoveredA.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Shirokuro.GuestExpiryIndex.v1");
        Assert.EndsWith(":" + alice, afterProtector.Unprotect(protectedExpiry),
            StringComparison.Ordinal);

        using (var roster = await guestB.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            using var doc = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
            Assert.Equal(revision, doc.RootElement.GetProperty("revision").GetInt64());
            var players = doc.RootElement.GetProperty("participants").EnumerateArray()
                .Select(x => x.GetProperty("playerId").GetString()).ToArray();
            Assert.Equal(2, players.Length);
            Assert.Contains(alice, players);
            Assert.Contains(bob, players);
        }

        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var response = await guestA.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var denied = await guestA.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var surviving = await guestB.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, surviving.StatusCode);
            using var json = JsonDocument.Parse(await surviving.Content.ReadAsStringAsync());
            var participant = Assert.Single(json.RootElement.GetProperty("participants")
                .EnumerateArray());
            Assert.Equal(bob, participant.GetProperty("playerId").GetString());
        }
    }
}
