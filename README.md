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

The frontend currently renders a **local preview board** and offers a 14-hero selector (local-only; no hero abilities or authoritative match state) without calling the API. The API provides GET /api/health and GET /api/preview/board?radius=2 as scaffolding. No match, player, fog-of-war or movement state exists yet.

Frontend checks from src/frontend:

    npm run test:unit
    npm run build
    npx playwright install chromium
    npm run test:e2e

See docs/architecture/overview.md and AGENTS.md. All feature work starts from a GitHub Issue. Foundation: #1.



## Importare gli asset originali (issue #4)

Il file ZIP originale è volutamente escluso dal repository: le immagini grezze non vanno caricate direttamente nel frontend. Per generare i **14 ritratti degli eroi** e le **6 texture dei terreni** ottimizzati:

    python -m pip install -r scripts/requirements-art.txt
    python scripts/import_art.py /percorso/allo/shirokurozip.zip

Lo script legge esclusivamente i 20 file PNG consentiti, valida percorsi e dimensioni dello ZIP, li converte in WebP e produce `src/frontend/public/art/{heroes,tiles}/` insieme a `src/frontend/src/generated/art-manifest.json`. Il selettore visualizza i veri ritratti quando sono presenti nel manifest; altrimenti mostra un fallback privo di richieste HTTP mancanti. I file generati possono essere verificati e committati separatamente; le abilità dei personaggi restano da definire.


## Sicurezza del movimento PvP (issue #8)

`MatchStateSnapshot` conserva lo stato completo degli eroi **solo sul server**; `MatchObservationProjector` produce una vista differenziata per osservatore. Senza grants verificati sul backend, non vengono esposti altri personaggi. Anche quando la posizione di un altro eroe è autorizzata, la sua **destinazione prevista non compare mai nel DTO**. La presenza di questa proiezione non implica che matchmaking, autenticazione, visibilità di squadra o movimento siano già stati implementati. Si veda [docs/architecture/match-observation-security.md](docs/architecture/match-observation-security.md).
 
## Lobby pre-partita (issue #10)

I nuovi componenti `LobbyDefinition`, `LobbyState` e
`InMemoryLobbyCoordinator` supportano squadre configurabili, registrazione,
uscita e scelta di un singolo eroe per giocatore. Lo stato è immutabile e
versionato: due modifiche contemporanee sulla stessa revisione non possono
sovrascriversi. Il roster originale di 14 eroi viene importato da
`src/shared/heroes.json` come risorsa embedded dal progetto .NET Application.

**Non è ancora una lobby online:** mancano identità/autenticazione, API,
SignalR, persistenza e regole ufficiali di avvio partita. Nessun endpoint
consente di impersonare un giocatore. Dettagli:
`docs/architecture/lobby-foundation.md`.

