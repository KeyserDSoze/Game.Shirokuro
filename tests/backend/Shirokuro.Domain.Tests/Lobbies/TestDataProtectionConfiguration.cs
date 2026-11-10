using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Test-only certificate and key volume, generated at runtime (no private
/// keys, certificate files or passwords in the repository).
/// </summary>
internal static class TestDataProtectionConfiguration
{
    private static readonly Lazy<(string Folder, string Pfx, string Password)> State =
        new(Create);

    private static (string Folder, string Pfx, string Password) Create()
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "shirokuro-tests-dataprotection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var certPath = Path.Combine(folder, "protect.pfx");
        const string password = "local-test-only-pfx-password";
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Shirokuro Test Key Protection",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(10));
        File.WriteAllBytes(certPath, cert.Export(X509ContentType.Pkcs12, password));
        return (folder, certPath, password);
    }

    public static void Apply(IWebHostBuilder builder)
    {
        var (folder, pfx, password) = State.Value;
        builder.UseSetting("Security:DataProtection:KeysDirectory", folder);
        builder.UseSetting("Security:DataProtection:CertificatePath", pfx);
        builder.UseSetting("Security:DataProtection:CertificatePassword", password);
        // Include a late provider so TestServer sees the same values during
        // host construction regardless of which config phase it exercises.
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:DataProtection:KeysDirectory"] = folder,
                ["Security:DataProtection:CertificatePath"] = pfx,
                ["Security:DataProtection:CertificatePassword"] = password
            }));
    }

    public static (string Folder, string Pfx, string Password) Values => State.Value;
}
