# Authenticated lobby roster transport — issue #18

This was the first **fail-closed** pre-match HTTP and SignalR transport
boundary. Follow-up #20 now implements separate guest room creation/join and
self-only mutation endpoints, while this HTTP roster and SignalR hub remain
read-only, member-scoped, and **not** a gameplay/match observation feed.

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
anti-cheat network tests remain in #9 and depend on #3/#5. The roster
reader now renews inactivity only for verified lobby members (#22);
SignalR rechecks session revocation on every call, even on old sockets
(#23). #19 has issued
pseudonymous guest tickets and #20 provides room admission and updates,
but neither is production-grade account identity or match networking.

## Regression coverage

xUnit includes direct coordinator/reader tests, validated principal
identity tests, and WebApplicationFactory/TestServer HTTP/SignalR negotiate
integration tests for authenticated/anonymous clients, spoofing, room
nonmembers, missing or duplicate claims and wire-format redaction.


## Browser roster wake hints versus outage backoff (#199)

The authenticated pre-match browser uses member-only, read-only
`GET /api/lobbies/{room}/roster/peek` at a normal 15-second cadence
(conditional `If-None-Match` can return a verified 304). During Redis,
edge or network failure it already uses jittered 30/60/120-second
exponential backoff; **no passive GET can renew room inactivity**.

Online/focus/visibility hints used to reschedule the poll to the
15-second floor on *every* accepted hint, even when the Redis service
had already failed for several 503 responses. Since hints have their
own 30-second cooldown, a page could repeatedly bypass the 120-second
outage backoff and increase load when Redis was most fragile.

The #199 client now allows **at most one wake-triggered expedited
read per consecutive outage episode**, never closer than 15 seconds
from its previous attempt. If that extra GET also fails, later
focus/visibility/online signals do **not** shorten the scheduled
30/60/120s retries. A hint delivered **while** a read is in flight
consumes the same one-off allowance when that attempt fails: it cannot
create concurrent reads or a second early poll. A verified, member-
authorized HTTP 304 or valid current/newer HTTP 200 replenishes
the allowance; old-revision 200, HTTP 429/503, rejected DTOs and
timeouts do not. New guest/room effects start a fresh budget.

The browser keeps displaying the **last verified** member roster
as stale through transient outages. Only a server-confirmed 401/404
or fully validated *newer* roster missing the member can clear the
guest/room; hints are never authorization or a reason to mutate a
lobby. The manual Refresh button remains an explicit player GET,
distinct from unattended polling. No POST/PATCH/DELETE is replayed.

The policy is a small pure TypeScript function
`rosterCanAdvanceOnWake(failures, used)` covered by Vitest
(including invalid clocks/counters). Playwright checks that a wake
still helps recovery once, but repeated hints under consecutive 503
cannot overtake the 60-second scheduled retry; it retains the
authenticated roster and issues no additional mutations. Existing
.NET xUnit Redis TLS/HA and all frontend tests remain mandatory
CI gates. This load-shedding is **per browser tab**, not a substitute
for server-authoritative rate limiting or multi-pod staging under #21.


## Fail-closed polling on lagging Redis replica HTTP 200 (#200)

The public-client roster peek is an authenticated **member-only** read;
it is not a game observation or permission to mutate. A replica behind
the revision already confirmed in the browser may reply HTTP 200 with a
strictly shaped, valid but **older** roster. That response must not
replace the newer cached room, clear the `stale` indicator or prove that
the authoritative service has recovered. In the earlier implementation
the read was classified visually as `older` but still reset the
exponential failure count to zero. This unintentionally scheduled 15s
normal reads instead of continuing 30/60/120s backoff, and interacted
with the one-time wake allowance from #199.

The #200 fix treats an older-revision 200 as an **unverified freshness
observation**: keep the current roster and its ETag/revision, increment
the bounded failure counter (max 3) and retain the stale status. Once
an early wake is spent, later focus/online/visibility hints cannot
circumvent this backoff, even if a locally inconsistent counter is zero.
An authenticated 304 or valid current/newer 200 is the only successful
roster read that both resets the failure backoff and replenishes the
early wake budget. A real, verified newer roster without the actor is
still the only 200-based membership-loss evidence.

Vitest tests the wake policy's fail-closed behavior for a consumed
allowance even at count zero, and the older-revision reconciliation
and status invariants. Playwright's browser fake-clock regression uses
503, 503 (one expedited wake), **valid older 200**, repeated
focus/online hints, then a verified newer 200 and a 304 in a later
outage episode. It asserts no read earlier than the 120s backoff
after stale replica evidence, unchanged old-room ETag, preserved
identity/invitation, eventual recovery and **no mutation replay**.
This is local resilience logic; it does not validate real staging
Redis replication lag or Kubernetes ingress (remaining in #21).


## Conditional roster 304 requires an exact bound validator (#201)

The server-only, member-authorized `/api/lobbies/{lobbyId}/roster/peek`
emits a **strong** `ETag: "rev-<revision>"` and
`Cache-Control: no-store` on both 200 and 304, and checks cookie,
membership, revocation and drain state **before** issuing a 304.
Previously the browser interpreted **any** 304 as a successful
confirmation of cached roster freshness, even if the outgoing request
had no valid `If-None-Match`, a proxy dropped ETag, the returned
revision did not match the locally verified room, or the 304 was
cacheable. Such a response is not sufficient evidence to clear a
stale/outage badge or reset the 30/60/120-second retry budget.

The frontend now accepts 304 as a no-change result only when the
request sent a canonical nonnegative safe-integer revision validator,
the response has an **exactly matching strong ETag**, and its
Cache-Control includes the `no-store` token. A missing/weak/mismatched
ETag, no-store substring without the token, cacheable response, or
unsolicited 304 throws a generic, non-authenticating protocol error.
The existing passive poll catches this as a transient failure:
the last verified room and guest stay visible as **stale**, and
bounded retry/backoff and the one-time wake budget remain in force.
This does not convert the error into 401/404, grant membership, leak
hidden PvP state, parse a 304 body or retry a mutation.

Vitest checks the exact 304 contract and bad variants with no JSON
read, while Playwright simulates `503 → wrong ETag 304 → exact ETag
304` across an accelerated clock and focus storm, confirming stale
retention, bounded backoff, session/room continuity and no writes.
All legacy fake-304 test fixtures now mirror the actual .NET headers;
the production server is unchanged. An ETag is not a replacement for
TLS or authenticated membership, and the real multi-pod ingress/WSS
validation is still tracked by parent #21.

## Reject contradictory cache policy on member-authorized roster 304 (#206)

After the strict validator checks in #201, the browser still accepted
`Cache-Control: no-store, public, max-age=60` because the first match
for the `no-store` token was treated as sufficient. An ingress that
adds cache-enabling directives to that same field must not be promoted
to evidence of authorized roster freshness or recovery after Redis
failover. The production .NET endpoint continues to issue only
`Cache-Control: no-store`; no backend API or PvP observation changes.

The client now requires **both** an exact strong ETag matching the
sent `If-None-Match` *and* an unambiguous caching policy with exactly
one `no-store` directive. Only optional `private`, `no-cache`, and
`must-revalidate` tokens are accepted, ignoring harmless case and
surrounding spaces. Duplicate/empty tokens or any other directive
(`public`, `max-age`, `s-maxage`, `immutable`, `stale-*`,
unknown extensions) reject the 304 as a transient protocol failure.

The already displayed room remains `stale`, identity and membership
are not fabricated or revoked, the 30/60/120s backoff and single
outage-wake budget stay in force, and **no mutation is replayed**.
A later verified 304 or valid current/newer 200 can restore freshness.
Vitest checks negative and positive 304 variants; Playwright exercises
`503 → matching-ETag but cacheable 304 → verified 304`, including
a burst of focus hints and the 60-second retry floor. This client
defense does not establish that the ingress is correctly configured,
or that a response was truly member-authorized without server checks;
real staging verification remains open in #21.

## In-flight roster reads invalidated on browser offline (#207)

The pre-match browser shows the last member-authorized roster during an
outage but must label it **stale** until a *new* authorized read verifies
freshness. Previously the `offline` event set the stale badge without
cancelling an already in-flight passive `GET /roster/peek`. A late HTTP
200/304, begun before the connectivity transition, could overwrite the
badge with `current` even while offline. The server-issued response
might be well formed but is not evidence of recovery **after** that
transition.

`LobbyPanel` now aborts the in-flight **passive** roster controller on
the `offline` event and clears any trailing wake hint collected while
that read was active. `guestLobbyApi.peek()` races the abort signal
against fetch/body parsing, so even a transport ignoring its abort
signal cannot commit the late response to React state. Explicit
player actions, including mutations, are not aborted by the
connectivity listener. Polling remains suspended while offline; the
next online/focus hint can schedule a new read subject to the existing
15-second floor and bounded 30/60/120-second outage backoff. Only a
**fresh** authorized 200/304 removes the stale badge.

Playwright exercises both late 200 and late strong-ETag 304 with an
abort-ignoring intercepted fetch, a real browser `offline` event,
the aborted signal, a fresh 304 after `online`, and zero automatic
POST/PATCH/DELETE replays. This is read-only pre-match state: the
browser cannot revoke a guest, infer match secrets, or certify actual
staging network conditions. Those requirements remain tracked by #21.
