# PvP hidden-state observation boundary (foundation #8)

**Status:** implemented as a pure server-side projection, but no authenticated match API,
real combat, fog-of-war, movement validation or realtime gameplay exists yet.

## Layers

- **Domain** `MatchStateSnapshot` / `AuthoritativeHeroState`: the authoritative
  player roster, team, hero, position and private planned destination. The
  snapshot makes a read-only copy of the roster and validates player IDs.
- **Application** `MatchObservationProjector`: emits a client-facing
  `MatchObservation` with `Self` and `Others`.
- **API**: **no endpoint** yet. Never serialize a Domain snapshot as a response.
  A future authenticated endpoint/hub must derive the observer from its
  authenticated session and must compute reveal grants inside the trusted server.

## Default deny and explicit grants

An observer sees their own position and planned destination. Without any
`AuthorizedHeroReveal` grants they see **no one else**, including teammates.
Each grant allows another identity/team/hero to be included, and can optionally
reveal that character's **current position**. The client-facing
`OtherHeroObservation` does not have a destination/route field at all.
No grant can reveal a rival's planned destination.

This deliberately does **not** decide whether teammates should share their
positions, which heroes are in line of sight, or whether dead players can
spectate: those are **game design decisions** still open in issues #3 and #5.
The grants represent the output of that future policy; do not expose them
as query parameters, JSON request fields, WebSocket client events or user
configuration.

## Threat model and rules for implementation

1. Authenticate the requesting player server-side; never take `observerId`
   as trusted from a client URL or body.
2. Validate match membership and player authority before projection.
3. Generate grants inside a trusted, independently tested visibility policy.
4. Serialize **only** the projected observation DTO. Never reuse the
   Domain snapshot or send a whole-room state to the browser for CSS hiding.
5. Authorize event streams and reconnect snapshots per receiving connection.
6. Test with multiple players/teams and verify hidden fields are absent
   in the raw JSON payload (not merely invisible on-screen).
7. Audit game-affecting reveal and movement decisions without leaking
   secrets through public logs.

## Tests

`tests/backend/Shirokuro.Domain.Tests/Observations/MatchObservationProjectorTests.cs`
covers default-deny, self-only destination, explicit reveal, omitted private
fields in serialized JSON, cross-observer isolation, invalid/duplicate IDs and
snapshot copying.

Until authenticated transport and gameplay rules are implemented, this is a
**security building block**, not a claim of a functioning PvP mode.

