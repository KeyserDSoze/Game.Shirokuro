# Initial architecture

Inspired by the folder hierarchy of Vuxo, not by its individual game mechanics.

## Runtime boundaries

- Domain (.NET 10): axial hex coordinate model and future deterministic, authority-owned rules.
- Application (.NET 10): preview map generation and future match orchestration.
- Infrastructure (.NET 10): adapter placeholder for future persistence and transport integrations.
- API (.NET 10): public health/preview, server-issued **ephemeral guest sessions** (#19), member-scoped authenticated roster HTTP/SignalR reads (#18), and invite-only lobby creation/admission and self-only hero/team/leave HTTP writes (#20). Durable identity, realtime game events and match observations are not yet exposed.
- Frontend (React/TS): pages, themes, localization, shadcn/ui controls.
- World renderer (R3F/Three.js): client-visible 3D hex scenery and hero placeholders.
- Pure frontend geometry: axial coordinate calculations; these are **not authority rules**.

## Security constraint for hidden movement

First step in #8: `MatchStateSnapshot` (Domain) holds authoritative hero positions and private planned destinations; `MatchObservationProjector` (Application) requires an authenticated observer ID from a **future server-side authentication boundary** and trusted per-player visibility grants. Without an explicit grant, other heroes are omitted. Granting visibility of a hero never transmits that hero's planned destination. This is currently an isolated, tested projection model, **not a public API or implemented authorization**.

Before gameplay implementation, specify visibility policy and per-player observations. Client may submit intents such as a route selection, but should receive *only* authorized observations, never a globally synchronized full state to hide later with CSS or rendering. Avoid logging opponent hidden routes in user-facing responses. Server validates turn ownership, permitted moves and information disclosure.

## Not yet implemented

Modes, hero traits, movement rules, fog of war, PvP/PvE, persistence, durable account sign-in, lobby session revocation/expiry (#21), live multiplayer events, real match networking and matchmaking. These are governed by later issues once game design is provided.

## Why React Three Fiber?

Phaser is a strong 2D renderer, but this project calls for true 3D hexagonal cells and stylized 3D hero figures. React Three Fiber composes Three.js declaratively with React without coupling Three state to the application UI. Heavy effects, performance budgets and asset loading are deferred until art and game rules are known.
