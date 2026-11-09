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
preflight; only the API's own origin or the two explicit local Vite dev origins
are allowed by the Origin check. During Development, only the two local Vite
origins allow CORS credentials (never `*`). Guest issuance is limited to
12 POSTs per minute per remote IP with no queue, **per server instance**.

Follow-up #23 now records a **separate 128-bit random session ID** in the
signed cookie and in a server-side registry alongside the guest's player ID
and expiry. `OnValidatePrincipal` checks this registry on every HTTP request;
legacy, revoked, expired or unregistered guest cookies are rejected. Guest
logout revokes the registry entry **before** clearing the browser cookie,
so a copied ticket cannot authenticate again. SignalR `GetRoster` also
checks the active registry on every hub invocation (a WebSocket may have
connected before logout). Registry entries are pruned every five minutes.

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
