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

## Read-only, member-scoped roster auto-refresh (#100)

The pre-match React lobby shows a **15-second** read-only automatic
roster refresh while the authenticated guest is in a room and the tab is
visible. Every update uses the existing authenticated, member-only
`GET /api/lobbies/{code}/roster`; the manual Refresh button remains.
No automatic guest sign-in, room join, HTTP mutation, SignalR broadcast
or cross-tab exchange of room codes/player IDs is introduced. The feature
is deliberately a pre-match UX improvement, **not** the match observation
security boundary of #9 or a substitute for server-side authorization.

The browser permits at most one polling GET in flight, suspends polling
in hidden tabs and while another guest operation is busy, and cancels
its timer on room/identity changes or unmount. Results are fenced by the
active guest generation, operation counter and room ID. Only a newer
server revision with current member identity can update the roster;
stale, same-revision, wrong-room or malformed JSON bodies are ignored.
A server-confirmed 401 revokes local guest/roster, while 404 or verified
loss of membership drops the private room; transient 403/429/503/offline
preserve existing data without immediate retries or clearing user action
feedback. These are local UI decisions; only the server authorizes access.
A follow-up (#103) adds bounded backoff under outages; polling is no longer
a fixed-frequency interval during repeated transient failures.

Vitest covers revision/membership/room validation and safe error
classification. Existing two-real-browser Playwright now checks that the second guest's
new hero/roster revision appears in the first browser **without clicking
Refresh or creating extra guest sessions**. A separate deterministic
browser HTTP simulation covers 503 preservation, 404 room removal,
401 guest revocation, and **zero HTTP mutation requests** during each
polling interval. Other real-browser tests already verify server-side
revocation across copied guest cookies and private lobby endpoints. Hosting, alerting and anti-Sybil controls still belong
to #21; game rules, hidden movement and match-scoped observations
remain #3/#5/#9.

## Passive roster polling must not extend idle room lifetime (#101)

The automatic 15-second roster GET introduced in #100 must **not**
count as authorized activity. Otherwise a browser merely left open
would keep every pre-match room alive forever, defeating the server's
30-minute inactivity expiry and exhausting creator/global room quotas.
The frontend now sends these timer reads to the authenticated,
member-only `GET /api/lobbies/{roomId}/roster/peek` endpoint. The
existing explicit `GET /roster` manual Refresh and the SignalR roster
method intentionally retain their original authorized heartbeat semantics;
room create/join/hero/team/leave are unchanged. Both paths enforce exact
canonical room codes, current guest-cookie authority, server-derived actor,
uniform 404 for nonmembers/absent rooms, no-store response and 503 for
unavailable Redis. No cross-tab room state, writes or match observations.

Development verifies membership **under the same coordinator lock** while
reading the immutable snapshot, without modifying last activity. In
Production/Staging Redis uses an atomic **read-only Lua GET combined with
the server-owned hashed guest revocation fence**, before decoding the
authoritative snapshot and checking membership. No CAS, PEXPIRE, index
score update, TTL extension, or fallback to per-node memory occurs.
Authenticated polling can still see another player's newer revisions,
but neither a broken tab nor unattended kiosk sustains room capacity.
Tests use a fake clock to verify 30-minute expiry despite repeated polls,
manual GET renewal, anonymous/outsider redaction and Redis two-client
TTL/bytes and revocation-fence guarantees.

## Conditional passive polling without sharing private roster bodies (#102)

The pre-match `GET /api/lobbies/{roomId}/roster/peek` now supports an
optional, **exact, quoted** `If-None-Match: "rev-N"` validator based only
on the last server-verified roster revision. The server first verifies the
current signed guest ticket, canonical lobby ID, persisted room snapshot
and membership **for every request**, then emits the non-secret
`ETag: "rev-N"`. If the revision matches exactly it returns
**304 Not Modified with no JSON body**; otherwise it returns the
authorized roster as 200 with the new ETag. Malformed/multi-tag/wildcard
validators never skip authorization or return false success.

**Every response remains `Cache-Control: no-store`**, including 304.
The browser never persists or shares ETags, invitations or rosters across
tabs. A 304 is an in-memory no-op; it does not reset the 30-minute
inactivity deadline (see #101), and cannot mask 401 guest revocation,
404 absent/unauthorized lobby or 503 Redis unavailability. Manual
refresh `GET /roster`, SignalR and user mutations retain their existing
semantics and do not return 304 in response to conditional tags.
This reduces unchanged roster response bytes, **not** server-side auth
checks or Redis workload; matching a version is never access control.

xUnit HTTP/real two-node Redis tests cover 304/200 on revision changes,
no content/no-store, outsider/anonymous/error responses despite a matching
validator and 30-minute idle expiry. Vitest and browser E2E cover request
headers, null unchanged response, 401/404/503 behavior and absence of
background writes. Match visibility/privacy rules (#9) remain separate.

## Bounded backoff during Redis outages and rate limiting (#103)

The authorized, no-heartbeat, conditional roster poll normally performs
a single `GET /roster/peek` **15 seconds after the previous response**
(200 or bodyless 304). On a transient error (403, 429, 502/503/504 or
offline), consecutive failures increase the next delay to a nominal
**30, then 60, then 120 seconds maximum**, with up to 20% positive
jitter where the 120-second ceiling permits. Success restores the normal
15-second cadence. All timeouts are local browser UX/load control, not
gameplay timing; no extra headers, tokens, cookies or identifiers are sent.

A new single self-scheduling timer replaces fixed `setInterval`, so
responses taking longer than one interval cannot create overlapping
requests. The timer is cancelled on room/guest switch and unmount and
never issues requests in a hidden tab or during an explicit guest action.
An old request completing after a mutation began cannot impose stale
backoff or update private state. 401 invalidates the guest immediately,
404 or verified membership loss removes the private room; neither is
treated as a retryable outage. No auto-login, auto-join, browser storage
or HTTP writes were introduced; explicit manual Refresh still works and
remains an authorized heartbeat, while passive polling cannot extend
the room's 30-minute idle TTL (#101).

Pure Vitest covers the exponential cap, jitter, invalid inputs and
reset on success; browser Playwright checks unchanged 304, 503, delayed
next GET, then 404/401 with no background mutation. This reduces
pressure during Redis failover but does not replace observability,
monitoring and HA deployment requirements in #21.
