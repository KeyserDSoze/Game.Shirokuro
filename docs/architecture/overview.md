# Initial architecture

Inspired by the folder hierarchy of Vuxo, not by its individual game mechanics.

## Runtime boundaries

- Domain (.NET 10): axial hex coordinate model and future deterministic, authority-owned rules.
- Application (.NET 10): preview map generation and future match orchestration.
- Infrastructure (.NET 10): adapter placeholder for future persistence and transport integrations.
- API (.NET 10): HTTP transport, preview and health; future authenticated player-specific views and realtime SignalR.
- Frontend (React/TS): pages, themes, localization, shadcn/ui controls.
- World renderer (R3F/Three.js): client-visible 3D hex scenery and hero placeholders.
- Pure frontend geometry: axial coordinate calculations; these are **not authority rules**.

## Security constraint for hidden movement

Before gameplay implementation, specify visibility policy and per-player observations. Client may submit intents such as a route selection, but should receive *only* authorized observations, never a globally synchronized full state to hide later with CSS or rendering. Avoid logging opponent hidden routes in user-facing responses. Server validates turn ownership, permitted moves and information disclosure.

## Not yet implemented

Modes, hero traits, movement rules, fog of war, PvP/PvE, persistence, auth, networking, 3D art pipeline and matchmaking. These are governed by later issues once game design is provided.

## Why React Three Fiber?

Phaser is a strong 2D renderer, but this project calls for true 3D hexagonal cells and stylized 3D hero figures. React Three Fiber composes Three.js declaratively with React without coupling Three state to the application UI. Heavy effects, performance budgets and asset loading are deferred until art and game rules are known.

