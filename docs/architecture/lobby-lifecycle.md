# Process-local room expiry and guest session revocation — #22 and #23

These safeguards secure **temporary pre-match** rooms; they do not add
any new PvP mechanics and are not sufficient for public production hosting.

## Room inactivity expiry (#22)

- `InMemoryLobbyCoordinator` accepts an injectable `TimeProvider` and
  a technical idle timeout. Default **30 minutes**. The timer is *not* an
  official game turn, match timer, team limit or treasure mechanic.
- New rooms start on revision 1 when their creator is atomically admitted.
  Every successful mutation (including a valid idempotent reconnect)
  updates the last-activity timestamp. A roster read extends the timeout
  **only after membership validation under the same lock**.
- `Get` does not touch activity. Unauthorized roster reads and invalid or
  conflicted mutations never refresh it. They cannot keep a room alive
  or enumerate its existence. The absent/expired room and nonmember
  continue to appear as 404 over HTTP and a uniform hub error.
- On every room lookup, an expired record is removed synchronously;
  on create, expired rooms are pruned *before* enforcing the 256-room
  technical cap. `LobbyExpiryWorker` also sweeps every minute using a
  `PeriodicTimer` bound to the same `TimeProvider`.
- StoredRoomCount is a server-only diagnostic count with no public route.
  Old invite codes cannot revive rooms once their TTL has elapsed.
  Parallel expiry and modifications use the coordinator lock.

## Cookie revocation (#23)

- Guest session issuance generates **two different 128-bit identifiers**:
  the guest player ID and a signed guest session token. The token is stored
  with player ID and expiration in `GuestSessionRegistry`, per process.
- `CookieAuthenticationEvents.OnValidatePrincipal` checks that a
  guest-like cookie has exactly one registered active session token tied to
  its signed player ID. Missing, forged, duplicate, legacy, expired or
  revoked guest registrations fail authentication **closed**.
- `DELETE /api/sessions/guest` revokes the matching registry entry
  **before** `SignOutAsync`, so a stolen/copy of the previous signed
  ticket receives 401 on later HTTP requests and SignalR negotiations.
  Invalid CSRF cannot revoke a guest.
- A SignalR connection can outlive logout: `LobbyRosterHub.GetRoster`
  now checks the registry on **each invocation**, denying even the old
  cached principal without revealing room existence.
- `GuestSessionExpiryWorker` sweeps expired registry entries every
  five minutes; registration also prunes expired entries opportunistically.
  StoredSessionCount is diagnostic-only, never a web endpoint.

## Per-guest creator quota (#24)

- Each authenticated guest identity may originate at most **four** active
  process-local invitation rooms, in addition to the existing **256-room**
  global ceiling. These are technical anti-abuse limits, not match rules.
- The creator ID comes only from the server-authenticated cookie. Room
  creation checks both limits and inserts the room atomically under the
  coordinator lock. Rejection is a generic HTTP **429**, with no invite ID
  or private roster data disclosed.
- Creator attribution survives team/hero changes and the creator leaving
  while other members remain; the room still consumes the creator's quota
  until it becomes empty or its 30-minute inactivity TTL expires. A fresh
  identity has an independent quota.
- This does **not** prevent an attacker from issuing several guest identities
  and does not provide distributed rate limits or durable per-account
  quotas; those operations remain in #21.

## Logout membership cleanup (#25)

- After a guest's signed ticket is successfully revoked, the server removes
  that trusted identity from **every active pre-match roster** under the
  coordinator lock. No client-supplied actor, lobby list or invite codes are
  used. This is a lobby-only lifecycle operation; no match rules are inferred.
- Each surviving room increments its roster revision **once** and retains its
  remaining participants, creator attribution and last-activity timestamp.
  Empty rooms are removed immediately; duplicate cleanup is harmless.
- Administrative cleanup does **not** renew the 30-minute inactivity TTL.
  Unauthorized/logout requests failing the cookie or CSRF checks cannot
  trigger removal. No room IDs or membership details are returned to logout.
- Expired guest sessions without an explicit logout are still handled by the
  existing lobby TTL; multi-instance cleanup requires shared stores (#21).

## Natural guest-session expiration (#27)

- A guest's **absolute 8-hour session expiry** denies HTTP requests and
  new SignalR connections; on the server's five-minute session sweep the
  expired identity is also removed from every active pre-match lobby.
  This complements immediate logout cleanup (#25), with no browser action.
- The registry queues the trusted player ID when an expired ticket is
  pruned, **even when opportunistic registration or other pruning detects
  it first**. Only the worker drains these notifications. Duplicate expiry
  events are deduplicated, explicit session revocation is not treated as
  natural expiration, and identities with another still-active ticket are
  skipped.
- The worker reuses the lock-safe coordinator's administrative removal:
  empty rooms disappear, shared rosters advance revision, others retain
  access, creator attribution and last-activity timestamps are preserved.
  In normal operation this happens on the next five-minute sweep; existing
  rooms still independently enforce their own 30-minute inactivity TTL.
- This remains **per process**. A crash/restart discards both registries
  and rooms; cross-node revocation and atomic persistence remain #21.

## Testing

Per-creator quota tests cover the cap and independence, concurrent creation,
retention after creator departure, automatic expiry reclaim, TestServer HTTP 429 and browser Playwright handling of rejected creation
(with a mocked response to avoid exhausting the suite-wide per-IP limiter).

The server-side fake-clock xUnit cases exercise exact TTL boundaries,
authorized renewal, rejected-write/nonmember behavior, lock-safe races,
capacity reclaim, 404 over HTTP after expiration and worker tick execution.
Session tests cover a copied protected cookie used after logout, two
independent users, forged/legacy cookies, expiry, concurrency, an
already-connected hub and two-browser Playwright replay/refresh.
Logout cleanup adds coordinator/xUnit race, multi-room, idempotency, revision,
creator-quota and fake-clock tests; TestServer verifies revoked-cookie denial,
pruning of solo rooms and the surviving guest's authorized roster. Playwright
checks two guest browsers plus a copied ticket after logout.

Fake-clock #27 tests cover exact session expiry, independently live guest,
opportunistic pruning, deduplication, delayed replacement session, natural
expiry vs explicit logout, parallel sweeps, room revisions and TTL. TestServer
integration verifies HTTP 401 for the expired cookie, surviving roster
authorization, solo-room deletion and absence of invite revival.

## Redis atomic lobby snapshot foundation (#34)

The API project includes `RedisLobbySnapshotCodec` and
`RedisLobbySnapshotStore` as a **not-yet-wired infrastructure layer**.
A versioned snapshot retains the trusted invite, team/hero definition,
immutable roster, revision, original creator and last authorized activity.
Decoding revalidates all domain invariants. Redis keys include a SHA-256
hash of the opaque invite rather than revealing invite codes as key names.

`CreateAsync` uses Redis `SET NX` with the remaining inactivity TTL.
`CompareExchangeAsync` uses a single-key Lua transaction to compare the
**full previous snapshot**, not only the revision, before changing content
and TTL. A roster revision may be unchanged during a valid reconnect,
so two concurrent heartbeat/refresh operations must still conflict.
`DeleteIfCurrentAsync` similarly refuses to delete a newer snapshot.
Read-only `ReadAsync` never extends the TTL, and stale snapshots are
rejected even if Redis has not physically deleted the key yet. Redis
also removes truly abandoned keys via its own TTL without a worker.

The GitHub backend CI starts an isolated local Redis service and runs
xUnit integration with **two independent Redis clients**, including
concurrent CAS race, stale delete, same-revision heartbeat, logical
expiry and physical Redis TTL tests. The local CI instance is deliberately
not a production TLS deployment. Production Redis TLS/network/secret
requirements from #33 continue to apply.

**Important boundary:** production HTTP endpoints, quota enforcement,
the coordinator, SignalR membership reads and expiry workers still
use `InMemoryLobbyCoordinator`. This primitive is not a completed
multi-node lobby architecture. Integrating all mutations and read
authorization atomically, as well as global room/creator caps and cleanup,
is still required in #21. No request may expose a raw Redis snapshot.

## Remaining work in parent issue #21

The **HTTP lobby coordinator, roster, technical quotas and cleanup are
still in-memory, per-process**. An API restart discards those active rooms
and membership, even though the new #34 Redis primitive is available.
Production session **authentication and revocation** use shared TLS Redis
(#33), and the cookie key ring can be persisted and encrypted with X.509
(#32). Those security improvements are not substitutes for shared room
membership, atomic creator/global quotas or reliable reconnect.

The next slice must wire the API and SignalR into a fully authorized,
atomic distributed coordinator; handle cross-node expiry/logout cleanup,
global anti-abuse limits, crash recovery, and Redis operational failover.
Any snapshot reads used by endpoint logic must first enforce trusted
membership and protect the invite from enumeration. Public multi-node
deployment also requires TLS edge/Redis availability, durability and
observability checks. Do not claim production-ready multiplayer yet.

## Atomic creator/global Redis room quotas (#35)

`RedisLobbySnapshotStore.CreateBoundedAsync` provides the technical
global **256 active rooms** and **4 active rooms per server-authenticated
guest creator** admission primitive for future distributed coordinator
operations. Both checks and insertion execute in **one Redis Lua script**
with a shared global sorted index and a per-creator sorted index. Creator
IDs and bearer invitation codes are SHA-256 hashed in Redis key names;
index entries themselves contain only hashed room-key references.
No quota counters are accepted from the browser.

The index scores are taken from Redis's own `PEXPIRETIME`, avoiding
cross-node clock differences when reclaiming expired quotas. When a
snapshot changes (including a valid same-revision heartbeat), the Lua CAS
also moves both index deadlines; a versioned, stale or missing index
cannot authorize a write. Conditional deletes remove indexes **only**
when the matching current snapshot is actually removed. Creator
attribution survives the creator leaving while other guests remain.
Both indexes expire after inactivity, with opportunistic pruning on
creation, without requiring a background sweeper.

All keys share a Redis Cluster `{rooms}` hash tag (or a separate test
namespace tag), deliberately keeping capacity transactions within a
single slot. This favors consistency at the current 256-room technical
capacity limit, at the cost of concentrating writes in one slot. The
unindexed `CreateAsync` path remains only for low-level #34 tests and
**must not be used for live room creation after endpoint migration**.
The payload codec is v2 and explicitly distinguishes quota-tracked
rooms, so an accidental missing index fails closed rather than silently
bypassing quotas.

xUnit integration on real Redis in CI tests two independently connected
clients, 48 concurrent creates per identity, both capacity limits,
creator departure, stale delete, heartbeat, physical TTL and recovery
without a worker. Version-2 snapshots require explicit quota metadata,
and loss of a quota index causes tracked updates/deletes to fail closed.
This protects against accidental index corruption; operational Redis
integrity and network access control remain necessary. 
[CI #42747294057](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/42747294057)
passed 294 xUnit, 106 Vitest, 24 Playwright and 35 Python checks.
**Still not connected to live HTTP/SignalR:** the
distributed lobby coordinator must use only bounded creation and
authorized CAS for every operation, then migrate logout/expiry cleanup
and member-scoped reads together (#21).
