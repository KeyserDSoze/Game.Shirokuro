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

## Remaining work in parent issue #21

Both registries are **in-memory and per-process**. Restart discards rooms
and session registration (previous guest cookies fail safely); multi-node
deployments cannot work without shared storage and consistent revocation.
Still missing: persistent/distributed atomic room state, Data Protection
key storage/rotation, global rate limiting, durable accounts, cross-identity abuse prevention, recovery,
cross-instance reconnect and operational metrics/privacy policy.
Do not deploy publicly as a distributed or production-ready multiplayer
service until these are designed, built and tested.

