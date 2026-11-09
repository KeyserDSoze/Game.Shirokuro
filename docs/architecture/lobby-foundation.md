# Lobby foundation (issue #10)

This is a **server-side pre-match building block**, not a player-connectable
lobby. Issue #18 has subsequently added a **member-scoped, read-only** HTTP
roster endpoint and SignalR `GetRoster` hub behind signed-session checks.
There is still no cookie-issuing identity provider, room admission, matchmaking,
lobby mutation endpoint or live game rules. See
`docs/architecture/authenticated-lobby-readonly.md`.

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

The read-only authenticated roster boundary in #18 is the first transport step.
The later **writable** lobby and match implementation (#9 and dependent issues) must:
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
