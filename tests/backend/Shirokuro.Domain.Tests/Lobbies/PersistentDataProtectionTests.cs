using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class PersistentDataProtectionTests
{
    private sealed class KeyRingFiles : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(),
            "shirokuro-dp-" + Guid.NewGuid().ToString("N"));
        public string CertificatePath => Path.Combine(Folder, "primary.pfx");
        public string Password { get; } = "only-in-test-" + Guid.NewGuid().ToString("N");

        public KeyRingFiles()
        {
            Directory.CreateDirectory(Folder);
            WriteCert(CertificatePath, Password);
        }

        public static void WriteCert(string path, string password)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Shirokuro KeyRing Test",
                rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(7));
            File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, password));
        }

        public Dictionary<string, string?> Settings() => new()
        {
            ["Security:DataProtection:KeysDirectory"] = Folder,
            ["Security:DataProtection:CertificatePath"] = CertificatePath,
            ["Security:DataProtection:CertificatePassword"] = Password
        };

        public void Dispose()
        {
            try { Directory.Delete(Folder, recursive: true); }
            catch (IOException) { /* The Windows runner may still hold a key. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static IConfiguration Config(Dictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static ServiceProvider Provider(
        IConfiguration config, string environment = "Production")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        PersistentDataProtection.Configure(services, config, new TestEnvironment(environment));
        return services.BuildServiceProvider();
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Shirokuro";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider
        { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public void DevelopmentWithoutKeysConfigurationStillWorksLocally()
    {
        using var provider = Provider(Config(new()), "Development");
        var protector = provider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("test-guest-cookie");
        Assert.Equal("development", protector.Unprotect(protector.Protect("development")));
    }

    [Fact]
    public void NonDevelopmentWithoutConfigFailsClosed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Provider(Config(new())));
        Assert.Contains("persistent Data Protection", ex.Message);
    }

    [Theory]
    [InlineData("KeysDirectory")]
    [InlineData("CertificatePath")]
    [InlineData("CertificatePassword")]
    public void IncompleteSettingsNeverCausePlaintextPersistence(string missing)
    {
        using var ring = new KeyRingFiles();
        var settings = ring.Settings();
        settings.Remove("Security:DataProtection:" + missing);
        Assert.Throws<InvalidOperationException>(() => Provider(Config(settings)));
    }

    [Fact]
    public void RelativePathAndInvalidPfxCannotStart()
    {
        using var ring = new KeyRingFiles();
        var settings = ring.Settings();
        settings["Security:DataProtection:KeysDirectory"] = "./keys";
        Assert.Throws<InvalidOperationException>(() => Provider(Config(settings)));

        settings["Security:DataProtection:KeysDirectory"] = ring.Folder;
        settings["Security:DataProtection:CertificatePassword"] = "incorrect";
        Assert.Throws<InvalidOperationException>(() => Provider(Config(settings)));

        settings["Security:DataProtection:CertificatePassword"] = ring.Password;
        settings["Security:DataProtection:CertificatePath"] =
            Path.Combine(ring.Folder, "missing.pfx");
        Assert.Throws<InvalidOperationException>(() => Provider(Config(settings)));
    }

    [Fact]
    public void SharedEncryptedRingWorksAcrossIndependentServiceProvidersAndRestarts()
    {
        using var ring = new KeyRingFiles();
        var settings = Config(ring.Settings());
        string encrypted;
        using (var first = Provider(settings))
        {
            var firstProtector = first.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Shirokuro.cookie");
            encrypted = firstProtector.Protect("private-ticket-payload");
            var options = first.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
            Assert.Equal(TimeSpan.FromDays(90), options.NewKeyLifetime);
        }

        var keyXml = Directory.GetFiles(ring.Folder, "key-*.xml");
        Assert.NotEmpty(keyXml);
        Assert.All(keyXml, file =>
        {
            var xml = File.ReadAllText(file);
            Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("private-ticket-payload", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
        });

        using var second = Provider(settings);
        var secondProtector = second.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Shirokuro.cookie");
        Assert.Equal("private-ticket-payload", secondProtector.Unprotect(encrypted));

        // Stable app discriminator across independently built roots.
        var app = second.GetRequiredService<IOptions<DataProtectionOptions>>().Value;
        Assert.Equal("Shirokuro", app.ApplicationDiscriminator);

        var unrelated = second.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Shirokuro.other-purpose");
        Assert.Throws<CryptographicException>(() => unrelated.Unprotect(encrypted));
        Assert.Throws<CryptographicException>(() =>
            secondProtector.Unprotect(encrypted[..^1] + (encrypted[^1] == 'A' ? 'B' : 'A')));
    }

    [Fact]
    public void RotatedCertificateRetainsOldKeyDecryptabilityOnlyWithOldCertificate()
    {
        using var ring = new KeyRingFiles();
        var originalSettings = Config(ring.Settings());
        string token;
        using (var first = Provider(originalSettings))
            token = first.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("rotating-cookie").Protect("signed");

        var nextPath = Path.Combine(ring.Folder, "next.pfx");
        KeyRingFiles.WriteCert(nextPath, ring.Password);
        var replacement = ring.Settings();
        replacement["Security:DataProtection:CertificatePath"] = nextPath;
        using (var missingLegacy = Provider(Config(replacement)))
        {
            var wrong = missingLegacy.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("rotating-cookie");
            Assert.Throws<CryptographicException>(() => wrong.Unprotect(token));
        }

        replacement["Security:DataProtection:PreviousCertificates:0:Path"] =
            ring.CertificatePath;
        replacement["Security:DataProtection:PreviousCertificates:0:Password"] =
            ring.Password;
        using var rotated = Provider(Config(replacement));
        var protector = rotated.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("rotating-cookie");
        Assert.Equal("signed", protector.Unprotect(token));
    }

    private static WebApplicationFactory<Program> Server(KeyRingFiles ring,
        IDistributedCache? shared = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestGuestSessionRedisConfiguration.Apply(builder, shared);
            foreach (var (key, value) in ring.Settings())
                builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var entries = ring.Settings();
                entries["AllowedHosts"] = "game.example";
                entries["Security:AllowedOrigins:0"] = "https://game.example";
                configuration.AddInMemoryCollection(entries);
            });
        });

    private static HttpClient HttpClientFor(WebApplicationFactory<Program> server,
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

    [Fact]
    public async Task CookieCryptoIsPortableButGuestAuthorizationStillRequiresSharedStore()
    {
        using var ring = new KeyRingFiles();
        using var firstServer = Server(ring);
        using var first = HttpClientFor(firstServer);
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post,
            "/api/sessions/guest");
        loginRequest.Headers.Add("Origin", "https://game.example");
        loginRequest.Headers.Add("X-Shirokuro-Request", "1");
        using var issued = await first.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var cookie = Assert.Single(issued.Headers.GetValues("Set-Cookie"))
            .Split(';')[0];
        using var payload = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        var id = payload.RootElement.GetProperty("playerId").GetString()!;

        using var secondServer = Server(ring);
        var format = secondServer.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;
        var ticket = format.Unprotect(cookie.Split('=', 2)[1]);
        Assert.NotNull(ticket);
        Assert.Equal(id, ticket!.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);

        // Shared signing/encryption keys do not mean shared session revocation:
        // server B cannot authorize server A's still-valid guest registry entry.
        using var other = HttpClientFor(secondServer, cookie);
        using var blocked = await other.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);

        using var originalMember = HttpClientFor(firstServer, cookie);
        using var accepted = await originalMember.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }
}
