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
    public static readonly TimeSpan CursorLifetime = TimeSpan.FromDays(1);

    // Atomic selection + shared round-robin offset. A permanently failing
    // group of >=128 due entries cannot monopolize every 5-minute sweep.
    // No user identifiers or encrypted payloads are saved in cursor state.
    // Both keys share the same Redis Cluster hash tag.
    private const string RotateDueWindowScript = """
        local count = redis.call('ZCOUNT', KEYS[1], '-inf', ARGV[1])
        if count == 0 then
            redis.call('DEL', KEYS[2])
            return {}
        end
        local wanted = math.min(count, tonumber(ARGV[2]))
        local offset = tonumber(redis.call('GET', KEYS[2]) or '0') or 0
        if offset < 0 then offset = 0 end
        offset = offset % count
        local batch = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1],
            'LIMIT', offset, wanted)
        if #batch < wanted then
            local wrapped = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1],
                'LIMIT', 0, wanted - #batch)
            for _,member in ipairs(wrapped) do
                batch[#batch + 1] = member
            end
        end
        redis.call('SET', KEYS[2], tostring((offset + #batch) % count),
            'EX', ARGV[3])
        return batch
        """;

    private readonly IDatabase _database;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly RedisKey _key;
    private readonly RedisKey _cursorKey;
    private readonly DateTimeOffset? _recoveryDueCeilingUtc;

    public RedisGuestExpiryIndex(
        IDatabase database, IDataProtectionProvider protection, TimeProvider clock,
        string? namespaceName = null, DateTimeOffset? recoveryCutoffUtc = null)
    {
        _database = database;
        _protector = protection.CreateProtector("Shirokuro.GuestExpiryIndex.v1");
        _clock = clock;
        // Guest issuance is fixed at 8 hours with no sliding renewal.
        // A cutoff is trusted only when supplied from deployment config
        // outside the restored Redis snapshot (never from a client).
        // Saturate the upper bound for a deliberately future-dated cutoff
        // rather than allowing a timestamp overflow to bypass maintenance.
        if (recoveryCutoffUtc.HasValue)
        {
            var maximumCutoff = DateTimeOffset.MaxValue - TimeSpan.FromHours(8);
            _recoveryDueCeilingUtc = recoveryCutoffUtc.Value >= maximumCutoff
                ? DateTimeOffset.MaxValue
                : recoveryCutoffUtc.Value.AddHours(8);
        }
        var name = namespaceName ?? "default";
        if (name.Length is < 1 or > 48 ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid guest expiry namespace.",
                nameof(namespaceName));
        var prefix = "shirokuro:guest-expiry:{" + name + "}:v1:";
        _key = prefix + "pending";
        _cursorKey = prefix + "scan-offset";
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

        await SaveAsync(trustedPlayerId, expiresUtc);
    }

    /// <summary>
    /// Queue post-revocation lobby cleanup immediately using the same
    /// encrypted, crash-resilient index as natural session expiration.
    /// The caller must have already revoked the server-side ticket. Unlike
    /// natural issuance the score is already due at the current clock time.
    /// </summary>
    public async Task<string> ScheduleRevokedAsync(
        string trustedPlayerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidGuestId(trustedPlayerId))
            throw new ArgumentException("Invalid trusted guest identity.",
                nameof(trustedPlayerId));
        return await SaveAsync(trustedPlayerId, _clock.GetUtcNow());
    }

    private async Task<string> SaveAsync(string trustedPlayerId, DateTimeOffset dueUtc)
    {
        // Do not persist plain guest IDs or session tokens, even as members.
        var plaintext = dueUtc.UtcTicks.ToString(CultureInfo.InvariantCulture) +
                        ":" + trustedPlayerId;
        var member = _protector.Protect(plaintext);
        await _database.SortedSetAddAsync(
            _key, member, CeilingUnixMilliseconds(dueUtc));
        return member;
    }

    public async Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow();
        // In recovery mode, treat the natural expiry jobs of all guests
        // registered before the out-of-band cutoff as immediately due.
        // This is safe only under a maintenance gate during cutover; newly
        // issued guests after the cutoff always have an expiry beyond this
        // fixed 8h window. Redis scores alone are not trusted: the sealed
        // due timestamp is validated below before any cleanup.
        var dueCeiling = _recoveryDueCeilingUtc is { } recovery &&
            recovery > now ? recovery : now;
        var members = (RedisResult[]?)await _database.ScriptEvaluateAsync(
            RotateDueWindowScript, [_key, _cursorKey],
            [CeilingUnixMilliseconds(dueCeiling), BatchSize,
             (long)CursorLifetime.TotalSeconds])
            ?? throw new InvalidDataException("Invalid guest expiry window.");
        if (members.Length > BatchSize)
            throw new InvalidDataException("Invalid guest expiry window size.");
        var result = new List<DueGuestExpiry>(members.Length);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string payload;
            try
            {
                payload = _protector.Unprotect(member.ToString());
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Never turn an unknown or unreadable protected member into
                // a valid player. Retain it durably for key-ring recovery,
                // while leaving later independent members processable.
                result.Add(DueGuestExpiry.Unreadable(member.ToString()));
                continue;
            }

            var separator = payload.IndexOf(':');
            if (separator < 1 ||
                !long.TryParse(payload.AsSpan(0, separator),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks ||
                !ValidGuestId(payload[(separator + 1)..]))
            {
                result.Add(DueGuestExpiry.Unreadable(member.ToString()));
                continue;
            }

            var expiry = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (expiry > now &&
                (_recoveryDueCeilingUtc is not { } ceiling || expiry > ceiling))
                continue; // Never trust the mutable Redis score over the protected time.
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

    // The Redis score has millisecond precision, while UTC times retain
    // ticks. Round both insert and lookup upward, then verify the exact
    // decrypted timestamp before removing any guest.
    private static long CeilingUnixMilliseconds(DateTimeOffset value)
    {
        var milliseconds = value.ToUnixTimeMilliseconds();
        return value.UtcTicks % TimeSpan.TicksPerMillisecond == 0
            ? milliseconds : milliseconds + 1;
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
