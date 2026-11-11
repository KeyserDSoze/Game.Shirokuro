using System.Globalization;
using System.Text.RegularExpressions;

namespace Shirokuro.Api.Security;

/// <summary>
/// A deployment-managed guest-ticket issuance cutoff independent of Redis.
/// During an RDB rollback, an older deleted session token may reappear in
/// Redis. A trusted cutoff from config OUTSIDE the restored snapshot rejects
/// the old Data Protection-signed ticket before consulting that token.
/// </summary>
public static class GuestSessionRecoveryCutoff
{
    public const string ConfigurationKey =
        "Security:GuestSessions:AcceptIssuedAfterUtc";

    // Explicit UTC ISO 8601 only; no local timezone, loose date formats,
    // extra whitespace, missing offset, or sub-second precision >7 digits.
    private static readonly Regex UtcTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static DateTimeOffset? Read(
        IConfiguration configuration, IHostEnvironment environment)
    {
        // Development retains process-local guest sessions and is not used
        // for Redis disaster recovery. All other environments fail startup
        // if an operator has supplied a malformed cutoff.
        if (environment.IsDevelopment())
            return null;

        var raw = configuration[ConfigurationKey];
        if (raw is null)
            return null; // Unconfigured rollout preserves prior behaviour.

        if (!UtcTimestamp.IsMatch(raw) ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var utc) ||
            utc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException(
                "Invalid configured guest session recovery cutoff.");

        return utc;
    }

    public static bool Accepts(
        DateTimeOffset? ticketIssuedUtc, DateTimeOffset? minimumIssuedAfterUtc) =>
        minimumIssuedAfterUtc is null ||
        (ticketIssuedUtc is { } issued && issued > minimumIssuedAfterUtc.Value);
}
