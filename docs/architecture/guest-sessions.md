# Ephemeral guest identity — #19

Guest sign-in creates a **pseudonym**, not a verified personal identity.
The server generates a 128-bit cryptographically random identifier:
`guest-<32 lowercase hex chars>`. No request body or chosen identity
is accepted. A valid existing guest cookie is reused without rotation.

## Routes

- `POST /api/sessions/guest` — requires `X-Shirokuro-Request: 1`
  and a trusted `Origin`, issues a protected cookie, returns
  `{ playerId, kind: "guest" }`.
- `GET /api/sessions/me` — authenticated principal's own ID and kind,
  never cacheable.
- `DELETE /api/sessions/guest` — guest-only, same anti-CSRF checks,
  clears the cookie.

Cookies are HttpOnly, Secure and SameSite=Strict, expire after 8h without
sliding renewal, and use ASP.NET Core Data Protection signing/encryption.
The session kind claim is inside the signed ticket.

## Browser and abuse boundaries

The custom request header makes browser cross-origin writes require a CORS
preflight. As of #28, **non-Development** requests are denied unless their
canonical HTTPS `Origin` is explicitly configured in
`Security:AllowedOrigins`; `Host` and spoofed forwarded headers cannot grant
CSRF trust. No configured origin means all guest/lobby mutations return 403,
while health/read-only routes continue working. In Development, only a local
loopback API same-origin or the explicit `localhost:5173` /
`127.0.0.1:5173` Vite origins are accepted. During Development, only
the two Vite origins allow CORS credentials (never `*`). Guest issuance
is limited to 12 POSTs per minute per remote IP with no queue, **per
server instance**. See [deployment-security.md](deployment-security.md).

Follow-up #23 now records a **separate 128-bit random session ID** in the
signed cookie and in a server-side registry alongside the guest's player ID
and expiry. `OnValidatePrincipal` checks this registry on every HTTP request;
legacy, revoked, expired or unregistered guest cookies are rejected. Guest
logout revokes the registry entry **before** clearing the browser cookie,
so a copied ticket cannot authenticate again. SignalR `GetRoster` also
checks the active registry on every hub invocation (a WebSocket may have
connected before logout). Registry entries are pruned every five minutes. With #27, each natural
expiry also queues a trusted player ID for the session worker, which
removes that player from active pre-match rosters without changing room TTL
or authorization of other members. IDs pruned opportunistically at guest
registration are retained for the next sweep; explicit logout (#25) still
removes membership immediately, not through the expiry queue.

The optional reverse-proxy configuration (#29) accepts `X-Forwarded-For`
and `X-Forwarded-Proto` **only** from explicitly trusted proxy IPs, with
single-hop forwarding. This preserves per-client issuance throttles behind
a real reverse proxy without allowing untrusted clients to spoof their
IP. `X-Forwarded-Host` is never accepted. See
[deployment-security.md](deployment-security.md).

These guarantees are **per process**. A restart loses the registry and
invalidates previous guests (safe but not seamless). The store and
Data Protection keys are not yet shared across instances; cross-node
revocation, durable identity, recovery, anti-abuse and verified accounts
remain in #21. Use HTTPS and persistent/shared Data Protection before
production; proxies must reliably set scheme and host.

Session issuance by itself does not grant admission. #20 now exposes
invitation-based room creation/join, and the #18 HTTP roster and SignalR
reads still require server-side membership and expose no secret PvP
positions/routes. Per-process logout revocation is now delivered by #23;
durable identity and distributed revocation require #21. Actual match
networking and visibility still depend on #3/#5/#9.
