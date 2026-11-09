# Authenticated lobby roster transport — issue #18

This is the first **fail-closed** pre-match HTTP and SignalR transport
boundary. It does **not** implement gameplay, room creation, lobby joining,
account sign-in or the match observation feed.

## Routes

- Public: `GET /api/health` and `GET /api/preview/board` unchanged.
- Protected: `GET /api/lobbies/{lobbyId}/roster` returns a deliberately
  limited `LobbyRosterView`, **only for an authenticated member**.
- Protected: `/hubs/lobbies` SignalR hub, with exactly one read-only method:
  `GetRoster(lobbyId)`. Each method invocation rechecks membership. There
  are **no subscriptions/groups/broadcasts** that could outlive membership.

Both endpoints accept the lobby ID, **never playerId**. The trusted
identity must come from exactly one `ClaimTypes.NameIdentifier` of an
authenticated ASP.NET Core principal; malformed/absent/duplicate claims
are rejected. Query strings and custom HTTP headers cannot assert identity.

HTTP semantics: 401 unauthenticated, 403 authenticated but identity missing
or ambiguous; 404 for both nonexistent lobby and unauthorized membership
(avoid lobby enumeration). Hub errors do not distinguish these cases.
No other lobby state is sent to the client.

## Session boundary

ASP.NET Core cookie authentication signs/encrypts tickets using Data
Protection. The cookie is HttpOnly, Secure and SameSite=Strict, expires after
8 hours and has no sliding renewal. API/hub challenges return 401/403
instead of HTML login redirects.

**Follow-up #19 provides server-issued ephemeral guest sessions**, not verified
accounts: visitors can obtain a signed, random pseudonymous cookie through
`POST /api/sessions/guest` with anti-CSRF checks, read their own identity,
and sign out. This does not grant access to any existing lobby until the
server explicitly adds a member. Integration tests still use signed test
tickets to exercise ambiguous identity and membership edge cases. Any future sign-in
provider needs its own threat model (CSRF, account lifecycle, revocation,
production key persistence, deployment topology and HTTPS). Cookies alone
do not constitute a complete auth product.

## Observation hygiene

The only output type is `LobbyRosterView` with lobby ID, revision,
configured team IDs, and pre-match participant (player ID, team ID and
chosen hero). It is assembled as a fresh DTO by `LobbyRosterReader` and
**never** serializes `LobbyState` or `MatchStateSnapshot` directly.
It does not include coordinates, planned destinations, fog-of-war or hidden
cards. This roster is **not** the match observation projection.

The future PvP realtime transport, visibility policy, reconnect logic and
anti-cheat network tests remain in #9 and cannot be implemented until
game-design decisions in #3/#5. No production room administration or
account identity issuance is implied here.

## Regression coverage

xUnit includes direct coordinator/reader tests, validated principal
identity tests, and WebApplicationFactory/TestServer HTTP/SignalR negotiate
integration tests for authenticated/anonymous clients, spoofing, room
nonmembers, missing or duplicate claims and wire-format redaction.


