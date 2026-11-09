# Lobby foundation (issue #10)

This is a **server-side pre-match building block**, not a player-connectable
lobby. No HTTP endpoint, SignalR hub, matchmaking, user account or game rules
are implemented here.

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
  fail. This storage is intentionally process-local and non-durable.

## Authentication and PvP security requirements

All `trustedPlayerId` method arguments mean **a player identity already
verified by a server-side authentication boundary**. Never expose such
methods as unauthenticated HTTP endpoints that accept arbitrary player IDs.

A future authenticated API/SignalR implementation (issue #9) must:
1. Derive player identity exclusively from a validated session.
2. Authorize lobby creation, join, leave and all selection updates.
3. Keep revision checks and handle reconnect/retry scenarios.
4. Persist room state / coordinate multi-instance access as needed.
5. Never mix pre-match roster DTOs with `MatchStateSnapshot` secrets.
6. Use `MatchObservationProjector` when game sessions begin and
   apply the real visibility policy determined under issue #3.

## Unspecified features

No match start/readiness rules, character abilities, max players, balanced
teams, unique character selection, lobby roles, public permissions, combat
or capture-the-treasure rules are assumed.

Tested with xUnit: invariants, multiple teams, legitimate/invalid choices,
unknown participants, immutable snapshots, revision conflicts, simultaneous
writes, independent rooms and roster parity.

