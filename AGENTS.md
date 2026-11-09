# Shirokuro engineering guidance

Follow GitHub Issues as the source of truth: find/create a feature or bug issue before coding, link the PR, update docs/engineering/development-ledger.md, and only close after relevant tests + CI pass. Target VERSION is 0.0.1 until a version change is explicitly opened.

## Architecture
Follow Vuxo's folder boundaries:
- src/backend/domains: pure rule model; no HTTP, DB, or rendering dependencies.
- src/backend/business: application use cases and interfaces.
- src/backend/infrastructure: implementations for external concerns, as needed.
- src/backend/applications: ASP.NET Core API and transport composition.
- src/frontend/src/game: Three.js/R3F renderer and pure hex geometry; src/components/ui: shadcn/ui application UI.
- tests/backend: xUnit. src/frontend/src/**/*.test.ts: Vitest. src/frontend/tests: Playwright.

## Rules
- The server is authoritative for PvP state. Client input is an intent, never an authoritative move.
- Hidden routes, opponent positions and fog-of-war information must never be transmitted to unauthorized clients. Enforce filtering at server boundaries, not merely by hiding UI.
- Do not invent gameplay mechanics before the game design material is supplied; the board and hero in this foundation are explicitly placeholders.
- Use shadcn/ui for non-game controls and R3F for 3D world visuals, not ad-hoc WebGL inside UI components.
- Keep coordinate math pure and testable. Separate geometry from game rules.
- All user-facing UI copy should be translatable (at least Italian and English); support light, dark and system color schemes.
- Prefer original procedural placeholder art; don't commit external copyrighted art.
- No secrets, production domains or real user data in source.
- Each feature must include meaningful low-level tests and appropriate Playwright / xUnit coverage.
- Run frontend unit tests, build and Playwright; run dotnet build and dotnet test. If unavailable locally, report that honestly and rely on CI.

## Development ledger
Keep docs/engineering/development-ledger.md synchronized with issue and PR status. Write test results and links in the issue before closure. Architecture and rules documents are living records.

