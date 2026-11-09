# Shirokuro (白黒)

Shirokuro is a web-first, PvP-focused game concept set on a hexagonal map with heroes and hidden movement. The gameplay rules are intentionally **not yet implemented**: this repository contains a technical preview, not a playable match.

## Stack and structure

- Frontend: React 19, TypeScript, Vite, Tailwind CSS and shadcn/ui for application panels, Three.js + React Three Fiber for a 3D hex board and placeholder hero.
- Backend: ASP.NET Core .NET 10 with Domain, Application, Infrastructure and API boundaries.
- Tests: xUnit (.NET), Vitest (pure TypeScript geometry), Playwright (browser UI). GitHub Actions runs the gates.
- IT/EN text and system/light/dark themes are part of the initial shell.

Folder layout mirrors the high-level architecture of Vuxo, without copying its unrelated gameplay:

    src/backend/domains/Shirokuro.Domain/
    src/backend/business/Shirokuro.Application/
    src/backend/infrastructure/Shirokuro.Infrastructure/
    src/backend/applications/Shirokuro.Api/
    src/frontend/
    tests/backend/Shirokuro.Domain.Tests/
    docs/

## Local development

Prerequisites: .NET 10 SDK, Node.js 22+ and npm.

Backend:

    dotnet restore Shirokuro.slnx
    dotnet build Shirokuro.slnx -c Release
    dotnet test Shirokuro.slnx -c Release
    dotnet run --project src/backend/applications/Shirokuro.Api

Frontend (separate terminal):

    cd src/frontend
    npm ci
    npm run dev

The frontend currently renders a **local preview board** without calling the API. The API provides GET /api/health and GET /api/preview/board?radius=2 as scaffolding. No match, player, fog-of-war or movement state exists yet.

Frontend checks from src/frontend:

    npm run test:unit
    npm run build
    npx playwright install chromium
    npm run test:e2e

See docs/architecture/overview.md and AGENTS.md. All feature work starts from a GitHub Issue. Foundation: #1.

