using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace Shirokuro.Api.Security;

/// <summary>
/// Restart-safe guest expiry work queue stored as a Redis sorted set.
/// The member is authenticated/encrypted with the same shared Data Protection
/// key ring as cookies, and the score is the absolute UTC expiry. A due entry
/// is removed only AFTER its lobby cleanup succeeds. Multiple nodes may read
/// the same due item safely: lobby removal is CAS-based and idempotent.
/// </summary>
public sealed class RedisGuestExpiryIndex : IGuestExpiryIndex
{
    public const int BatchSize = 128;
    private readonly IDatabase _database;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly RedisKey _key;

    public RedisGuestExpiryIndex(
        IDatabase database, IDataProtectionProvider protection, TimeProvider clock,
        string? namespaceName = null)
    {
        _database = database;
        _protector = protection.CreateProtector("Shirokuro.GuestExpiryIndex.v1");
        _clock = clock;
        var name = namespaceName ?? "default";
        if (name.Length is < 1 or > 48 ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid guest expiry namespace.",
                nameof(namespaceName));
        _key = "shirokuro:guest-expiry:{" + name + "}:v1:pending";
    }

    public async Task ScheduleAsync(
        string trustedPlayerId, DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidGuestId(trustedPlayerId))
            throw new ArgumentException("Invalid trusted guest identity.",
                nameof(trustedPlayerId));
        if (expiresUtc <= _clock.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(expiresUtc));

        // Do not persist plain guest IDs or session tokens, even as members.
        var plaintext = expiresUtc.UtcTicks.ToString(CultureInfo.InvariantCulture) +
                        ":" + trustedPlayerId;
        var member = _protector.Protect(plaintext);
        var deadline = expiresUtc.ToUnixTimeMilliseconds();
        if (expiresUtc.UtcTicks % TimeSpan.TicksPerMillisecond != 0)
            deadline++; // never clean up even one millisecond early
        await _database.SortedSetAddAsync(_key, member, deadline);
    }

    public async Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow();
        var members = await _database.SortedSetRangeByScoreAsync(
            _key, stop: now.ToUnixTimeMilliseconds(), take: BatchSize);
        var result = new List<DueGuestExpiry>(members.Length);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string payload;
            try
            {
                payload = _protector.Unprotect(member.ToString());
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // Do not delete a damaged or unreadable entry silently: a key
                // ring misconfiguration must be repaired to restore cleanup.
                throw new InvalidDataException(
                    "Unreadable encrypted guest expiry work item.", ex);
            }

            var separator = payload.IndexOf(':');
            if (separator < 1 ||
                !long.TryParse(payload.AsSpan(0, separator),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks ||
                !ValidGuestId(payload[(separator + 1)..]))
                throw new InvalidDataException("Invalid guest expiry work item.");

            var expiry = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (expiry > now)
                continue; // defend against timestamp/score mismatch and clock skew
            result.Add(new DueGuestExpiry(member.ToString(), payload[(separator + 1)..]));
        }
        return result;
    }

    public async Task AcknowledgeAsync(
        string protectedMember, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedMember);
        await _database.SortedSetRemoveAsync(_key, protectedMember);
    }

    private static bool ValidGuestId(string? value)
    {
        if (value is not { Length: 38 } ||
            !value.StartsWith("guest-", StringComparison.Ordinal))
            return false;
        foreach (var c in value.AsSpan(6))
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}
