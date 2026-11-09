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

Use HTTPS and configure persistent/shared Data Protection keys before
production; proxies must reliably set scheme/host. Real distributed
anti-abuse protection, account recovery and revocation are not yet built.
`SignOutAsync` clears the browser cookie, but a stolen copied ticket
is **not revoked** until expiry.

Session issuance by itself does not grant admission. #20 now exposes
invitation-based room creation/join, and the #18 HTTP roster and SignalR
reads still require server-side membership and expose no secret PvP
positions/routes. Durable identity and revocation require #21; actual
match networking and visibility still depend on #3/#5/#9.
