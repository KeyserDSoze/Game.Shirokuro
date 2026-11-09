# Guest invitation-only lobby transport — issue #20

## Scope and guarantees

This is the first genuinely shared **pre-match** browser workflow.
Two independent browsers can request **server-created, signed guest sessions**
(#19), create a room, copy its opaque invitation code, join with an allowed
team, and configure their own selected hero/team. The original hero roster
is read from `src/shared/heroes.json` on both client and server. The
3D hex world and all movement/treasure/gameplay remain local placeholders.

No match start, readiness, cards, ability, fog, team balance or hidden route
logic is defined by this feature.

## Identity and permissions

- A signed guest session from #19 is required for **all** commands.
  Server derives the only acting player ID via
  `AuthenticatedPlayerIdentity.FromPrincipal` and `GuestSessions.IsGuest`.
  HTTP headers, URLs and JSON `playerId` cannot override it.
- Each mutation requires `X-Shirokuro-Request: 1` and trusted `Origin`
  via `WebMutationGuard`. Write JSON request types reject unknown
  properties. Anonymous requests return 401; unauthorised or CSRF-invalid
  requests return 403; non-members and nonexistent rooms return uniform
  404 for member-only operations.
- Room codes `room-<32 lowercase hex>` contain 128 bits of server RNG
  and act as **bearer invitations**, not public lobby IDs; possessors may
  join by choosing an existing team. Do not publish/log/share broadly.
- Room creation and admission of its creator are **one locked
  transaction**. Failure cannot publish an orphan empty room.
- Membership updates are self-only. The same server-stored revision
  must be supplied for own team/hero change and leave. Conflict returns
  409; failed changes never alter other members or the revision.
- Join accepts optional `expectedRevision` for conditional clients.
  Without it, the coordinator reads current revision and joins under
  the same lock; the invite holder never needs to query the private roster.
- The `LobbyRosterReader` still returns **only** pre-match public names,
  team IDs and selected hero IDs, with revision, and no secret match
  locations/paths. Private HTTP responses use `Cache-Control: no-store`.
  SignalR `GetRoster` remains read-only, with per-invocation membership.

## HTTP requests (guest cookie required)

```text
POST   /api/lobbies                      { "teamIds": ["white","black"], "teamId":"white" }
POST   /api/lobbies/{code}/join          { "teamId":"black" }
GET    /api/lobbies/{code}/roster        (no body, member-only)
PATCH  /api/lobbies/{code}/hero          { "heroId":"kira", "expectedRevision":2 }
PATCH  /api/lobbies/{code}/team          { "teamId":"black", "expectedRevision":3 }
DELETE /api/lobbies/{code}/membership    { "expectedRevision":4 }
```

Create returns HTTP 201 + `LobbyRosterView` with the generated code,
other successful mutations return the new member-scoped
`LobbyRosterView` (leave returns 204). Guests cannot create arbitrary
hero IDs or team IDs. Invalid payload/unknown extra properties get 400,
stale state returns 409, capacity/rate limits return 429.

Technical abuse controls are **not official game rules**:
2–4 ASCII team IDs (1–24 chars), maximum 16 lobby participants,
256 live in-process rooms, eight room creations per IP per minute.
The final member leaving prunes the room immediately.

## Browser UX and tests

The React `LobbyPanel` remains opt-in: opening the preview does not
automatically create an identity or send state-changing requests.
A guest explicitly signs in, sets team IDs/team, creates a room, shares
the invitation code, and another guest enters with their chosen team.
An existing member may select a hero in the **local hero picker**
and separately confirm that hero in the server roster. A stale revision
shows a recoverable conflict, prompting manual roster refresh.

The Vite dev proxy forwards `/api` and `/hubs` to a local .NET 10 API
port 5186. Playwright config starts both servers; CI frontend installs the
.NET SDK. Multi-browser E2E verifies separate cookies, invite/join,
stale revision 409, both heroes visible after refresh, leave and
IT/EN mobile/dark UI. xUnit uses WebApplicationFactory/TestServer with
real signed cookies and checks atomicity, limits, concurrent revisions,
spoofed claims, CSRF, response redaction and no-store caching.
Vitest checks team parsing, opaque invite handling and exact client JSON.

## Not production-ready

All rooms are process-local and non-durable. Follow-up #22 now expires
rooms after 30 minutes without authorized activity, synchronously rejects
expired invites and sweeps abandoned rooms every minute. #23 adds
per-process guest-cookie revocation on logout, even for copied cookies
and SignalR connections already established. Limits and registries are
still **per process** and insufficient for distributed deployments;
Data Protection keys and room storage require persistence and sharing.
Remaining infrastructure work is tracked in #21. Match notifications, reconnect logic and
PvP observations remain #9 and cannot proceed without #3/#5.
