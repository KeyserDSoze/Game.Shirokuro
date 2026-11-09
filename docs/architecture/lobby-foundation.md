# Lobby foundation (issue #10)

This began as a server-side pre-match building block in #10. The subsequent
#18–#20 milestones provide **server-issued guest cookie sessions**, a
member-scoped HTTP roster and read-only SignalR `GetRoster`, plus creation,
invitation, admission and self-only team/hero/leave HTTP commands.
These rooms are browser-connectable now, but **not active PvP matches**.
There is still no durable identity, matchmaking, live roster push,
persistence or approved game rules. See
`docs/architecture/guest-invitation-lobby.md`.

## Domain

- `LobbyDefinition`: configured lobby ID, allowed team IDs and selectable hero IDs.
  A team-based lobby has at least two configured team IDs, but no maximum
  number of teams, cap on participants, team balancing or hero uniqueness rule.
  The latter rules are **not confirmed** by the author.
- `LobbyState`: immutable snapshot with monotonic revision, roster and
  operations `Join`, `Leave`, `ChangeTeam`, `ChooseHero`. A participant
  may have **one selected hero** or none. Repeating an identical join/change
  with the same current revision is idempotent.
- `LobbyRevisionConflictException`: rejects stale expected revisions to
  protect against overwriting another player's choice.
- `LobbyParticipant`: public **pre-match** lobby selection data, separate
  from gameplay's hidden positions and routes.

## Application

- `OriginalHeroRoster` loads the canonical 14 IDs embedded from
  `src/shared/heroes.json` at build time, so the frontend and backend
  share a single hero allow-list.
- `InMemoryLobbyCoordinator` stores lobby snapshots **inside the server**
  and applies actions atomically under a lock; writes on a stale revision
  fail. Follow-up #22 adds 30-minute idle TTL and a background minute
  sweep; failed or nonmember requests do not renew it. This storage is
  intentionally process-local and non-durable.

## Authentication and PvP security requirements

All `trustedPlayerId` method arguments mean **a player identity already
verified by a server-side authentication boundary**. Never expose such
methods as unauthenticated HTTP endpoints that accept arbitrary player IDs.

Guest HTTP write endpoints added in #20 now derive identity exclusively
from signed sessions, authorize membership for self-only changes and use
optimistic revision checks. The remaining **real match** implementation
(#9 and dependent issues) must:
1. Provide durable identity and *distributed* revocation, beyond the
   process-local guest revocation now in #23 (#21).
2. Persist/coordinate room state and connection recovery across instances (#21).
3. Add authorization for realtime room events at every send.
4. Never mix pre-match roster DTOs with `MatchStateSnapshot` secrets.
5. Use `MatchObservationProjector` when game sessions begin, after
   the real visibility policy is confirmed under issue #3.

## Unspecified features

No match start/readiness rules, character abilities, max players, balanced
teams, unique character selection, lobby roles, public permissions, combat
or capture-the-treasure rules are assumed.

Tested with xUnit: invariants, multiple teams, legitimate/invalid choices,
unknown participants, immutable snapshots, revision conflicts, simultaneous
writes, independent rooms and roster parity.
