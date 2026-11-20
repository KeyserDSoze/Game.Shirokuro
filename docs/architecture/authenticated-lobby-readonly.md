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

## Explicit roster refresh and unresolved 409 must remain stale (#208)

A roster is `current` only after the client has accepted a member-authorized,
fully validated HTTP response. Passive polling already marks the last
verified roster `stale` on 503/429, transport errors or malformed 200
replies. However the player's **explicit Refresh** button previously
reported an error without changing that freshness badge, leaving a visible
`current` roster despite failed verification. The same misleading state
occurred when an explicit hero/team/leave mutation received revision
conflict **409** and the one permitted read-only conflict refresh then
failed.

A failed manual `GET /roster` (temporary HTTP errors, network failure, or
a 200 that fails the strict room/revision/member/hero/team DTO validator)
now preserves the last known room, player and invitation but marks its
roster `stale`. The generic `run()` error path still handles confirmed
401 guest revocation and 404 member-only room loss as before; an error
must not fabricate either state transition. A subsequent explicit,
fully validated current/newer 200 returns the badge to `current`.
No passive poll can race a user operation, and failures never trigger
automatic retries of POST/PATCH/DELETE.

For 409 on an existing room, the roster is marked `stale` **before**
the server-authorized read-only refresh: a rejected optimistic revision
means the client's command was not accepted. If the same-room refresh
validates a current/newer roster with membership intact, `showRoom`
restores `current`; otherwise stale remains with the last verified
view, until another deliberate refresh or a valid passive observation.
A proven 401, 404 or verified member loss still follows the existing
room/guest clearing logic. This is exclusively the pre-match roster
transport, not match/fog-of-war state.

Playwright covers manual 503, 429, network abort and malformed DTO
replies, valid recovery, 409 plus failing/unverifiable follow-up, and a
separate valid recovery. It asserts the retained guest/room/revision,
no repeated mutations, and correct stale/current transitions. The
test freezes background timers to isolate explicit requests; separate
browser tests exercise passive poll/backoff. The server remains
authoritative and the remaining staging and anti-Sybil requirements
belong to parent #21.

## Uncertain existing-room mutations cannot certify current roster (#209)

An optimistic pre-match `PATCH hero`, `PATCH team`, or
`DELETE membership` may commit on the .NET server even when its response
is lost (browser transport failure, 15-second timeout), a 5xx is returned
by the ingress, or the returned HTTP 200 JSON fails strict roster
validation. In those cases the old browser revision is not evidence of
current membership/team/hero selection. It is unsafe to automatically
**retry** a command whose server-side outcome is unknown: this could
repeat the user's intent against a changed revision.

Only these three existing-room commands now use
`runRoomMutation()`. Its shared error handler retains the last
member-authorized room, guest, invitation and revision, but marks the
roster `stale` for failed network/timeout/5xx or invalid successful
payloads. A separately authorized, strictly validated **manual GET**
can restore `current` and reveal the actual latest revision; a
confirmed room-only GET 404 can clear the room while keeping the guest.
No failed mutation is automatically replayed.

Definite HTTP 400/403/429 responses and client-side input rejections do
**not** mark an otherwise verified roster stale by themselves. A 409
continues the single authorized read-only conflict recovery introduced
in #208; confirmed 401/404 preserve their existing guest/room revocation
semantics. The shared pure
`roomMutationOutcomeIsUncertain(error)` decision is covered by Vitest
for explicit 4xx, 5xx, timeouts, network failures, and malformed success
payloads. Playwright exercises a committed hero update with malformed
200, a committed team change answered 503, a committed leave whose
response is lost, rejected 400/403/429, verified read recovery and
authorized room loss. Tests assert only the original explicit mutations
are dispatched, not automatic retries, and no new session identities
are fabricated. No new rules, PvP match observation or backend API are
introduced. True failover/staging verification remains under #21.

## Explicit roster reads invalidated across offline transitions (#210)

After the passive reader was fenced on browser `offline` in #207, two
member-authorized **explicit** read paths could still consume a response
that started **before** loss of connectivity: the player's Refresh button
and the one read-only GET after a revision conflict 409. Both may have
returned 200 after an `offline` event already marked the last verified
roster `stale`, incorrectly clearing that warning with old evidence.
A delayed explicit 401/404 could also alter guest or room state without
a post-transition verification.

`LobbyPanel` now increments a local
`rosterConnectivityEpochRef` on every `offline` transition of the
currently displayed room. A manual `GET /roster` or 409 read-only
recovery captures that epoch **when the read begins**; before applying
either a validated 200 or a thrown HTTP status, it rechecks both the
existing guest/operation guards and the epoch. If offline intervened,
the old result is ignored: the known member-only room and guest
remain visible but `stale`, and only a subsequently started, fully
verified HTTP request may restore `current` or authorize 401/404.
This is a rejection of old *read evidence*, not a claim that an offline
signal proves revocation or that all HTTP responses are untrustworthy.

The operation is deliberately **not aborted**: explicit actions retain
their 15-second timeout and server-side authority, and a partially
committed PATCH/DELETE is never cancelled or replayed by the client.
The existing passive poll AbortController, 15-second floor, wake budget
and 30/60/120-second outage backoff are unchanged. This epoch is only
an in-process browser validity guard; it carries no session, cookie,
room invitation or player ID and does not attempt to synchronize
clients across tabs.

Playwright uses an abort-ignoring first roster fetch and six scenarios
(Refresh and 409 conflict recovery, each with late 200/401/404). The
old result cannot refresh or revoke a room after `offline`; a new
member-authorized 200 after reconnect restores the newest revision.
Only the explicitly requested PATCH, if any, is sent. Real deployment
and cookie revocation remain server-owned and tracked in #21.

## Successful pre-offline mutations do not restore roster freshness (#211)

The existing connectivity epoch (#210) fences explicit roster **GETs**
that were started before an `offline` transition. A separate race
remained for a confirmed **PATCH hero/team** on an already verified
pre-match room: the player's request may have been sent while online
and resolve with a valid, member-authorized 200 **after** the browser
became offline. `showRoom()` accepted the new server revision and
unconditionally set the roster status to `current`, clearing the
offline warning even though the server had not been queried on the
new connectivity epoch.

The `runRoomMutation()` path now captures the same local connectivity
epoch at the beginning of each explicit room mutation. After a
successful action, and only for the same signed-in operation and room,
it checks whether `offline` occurred during the operation. In that
case the fully verified successful roster **is retained** (the
command really succeeded and must not be replayed), but the freshness
status is set back to `stale`. This is not a failed mutation or a
revocation: the player sees the updated selection/revision, the guest
and invitation remain valid, and the success message is preserved.
An on-time 200 without an intervening offline transition remains
`current`. A successful DELETE 204 has already cleared the room,
so the stale policy cannot resurrect a departed room.

Neither the mutation nor the server request is aborted, retried, or
converted into a new GET. Existing uncertain-commit handling (#209)
and 409 recovery (#208/#210) are unaffected. Only a subsequent
**new, authorized and validated roster read** can prove freshness
after reconnect. Two Playwright regressions cover late successful
hero and team PATCH replies over an abort-ignoring transport, offline
stale persistence, correct roster revision, no extra POST/PATCH, and
recovery via deliberate member-only GET. This does not infer new PvP
rules or modify the backend.

## Offline guest session hints and connectivity-era identity fencing (#212)

Cross-tab `BroadcastChannel('shirokuro:guest-session-changed')`,
window focus and visibility changes are **hints**, not authenticated
identity or permission to issue unbounded Redis-backed
`GET /api/sessions/me` requests. The browser already coalesced bursts
and enforced a 2-second monotonic minimum start interval (#122), but
these checks continued **while offline** and a result initiated before
disconnection could return afterwards with 401, `kind: other`, or a
different guest ID. That could revoke or replace an existing verified
guest/room from evidence belonging to the previous connectivity era.

The session recheck effect now tracks a local offline flag and
monotonically incremented connectivity epoch. Offline cancels a queued
cooldown timer, retains a single pending verification, and suppresses
all new `/sessions/me` rechecks even under an untrusted focus/
cross-tab hint storm. The current in-flight request is not assumed
cancelled: when it finishes, **both successful identities and 401
errors** are ignored if offline occurred since its start. The
existing generation guard still prevents a different guest/operation
from acquiring stale room state.

An `online` event releases exactly one coalesced recheck, subject
to the original 2-second start-to-start minimum and existing single
in-flight transport. This fresh, authenticated `/sessions/me` is
allowed to recognize a legitimately revoked cookie (401), a newly
issued guest, or a non-guest session. An unavailable service/503
still neither logs out nor silently renews guests. No ticket, roster,
invite, cookie or player ID travels over `BroadcastChannel` and
nothing is automatically posted or patched.

Playwright verifies three possible late pre-offline replies (401,
different guest 200, other-kind 200), a burst of 50 cross-tab and
focus hints while offline, zero extra server checks during outage,
one check after online, preserved room/guest while stale, and a
subsequent truly fresh authoritative 401. The roster poll abort,
outage backoff and explicit player mutations remain separate.
Server-side Redis authority remains the source of truth; no change
to gameplay or staging acceptance under parent #21.
