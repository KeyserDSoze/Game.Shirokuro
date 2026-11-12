using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Shirokuro.Api.Security;

/// <summary>
/// A persistent, encrypted, application-isolated Data Protection key ring.
/// The key repository and PFX are operator-managed: never create secret
/// directories or silently write unencrypted keys when configured.
/// </summary>
public static class PersistentDataProtection
{
    public const string ApplicationName = "Shirokuro";
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(90);

    public static void Configure(
        IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // The discriminator must be stable across independently deployed
        // processes and content roots, even in local Development.
        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .SetDefaultKeyLifetime(KeyLifetime);

        var section = configuration.GetSection("Security:DataProtection");
        var directory = section["KeysDirectory"];
        var certPath = section["CertificatePath"];
        var password = section["CertificatePassword"];

        var any = directory is not null || certPath is not null ||
                  password is not null ||
                  section.GetSection("PreviousCertificates").GetChildren().Any();
        if (!any && environment.IsDevelopment())
            return;

        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(certPath) ||
            string.IsNullOrEmpty(password) ||
            !Path.IsPathFullyQualified(directory) ||
            !Path.IsPathFullyQualified(certPath) ||
            !Directory.Exists(directory) ||
            !File.Exists(certPath))
        {
            throw new InvalidOperationException(
                "A persistent Data Protection key directory and PFX certificate are required.");
        }

        // Load the private key without adding it to the machine/user store.
        // Only the operator supplies the password through secret settings.
        var encryptor = LoadCertificate(certPath, password);
        builder.PersistKeysToFileSystem(new DirectoryInfo(directory))
               .ProtectKeysWithCertificate(encryptor);

        var oldCertificates = section.GetSection("PreviousCertificates").GetChildren()
            .ToArray();
        if (oldCertificates.Length > 8)
            throw new InvalidOperationException("Too many previous Data Protection certificates.");

        if (oldCertificates.Length != 0)
        {
            var old = oldCertificates.Select(cert =>
            {
                var oldPath = cert["Path"];
                var oldPassword = cert["Password"];
                if (string.IsNullOrWhiteSpace(oldPath) ||
                    string.IsNullOrEmpty(oldPassword) ||
                    !Path.IsPathFullyQualified(oldPath) ||
                    !File.Exists(oldPath))
                {
                    throw new InvalidOperationException(
                        "A previous Data Protection certificate requires a valid PFX and password.");
                }

                return LoadCertificate(oldPath, oldPassword);
            }).ToArray();
            builder.UnprotectKeysWithAnyCertificate(old);
        }
    }

    private static X509Certificate2 LoadCertificate(string path, string password)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                  or System.Security.Cryptography.CryptographicException)
        {
            // The underlying crypto/filesystem exception can carry a path,
            // certificate metadata or other operator-provided details.
            // Never retain it as InnerException: host startup loggers and
            // crash reports commonly serialize Exception.ToString().
            throw new InvalidOperationException(
                "Unable to load the configured Data Protection PFX certificate.");
        }

        if (!certificate.HasPrivateKey ||
            certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
            certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                "The Data Protection certificate must have a valid private key and date range.");
        }
        return certificate;
    }
}
