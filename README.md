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

Lo script legge esclusivamente i 20 file PNG consentiti, valida percorsi e dimensioni dello ZIP, li converte in WebP e produce `src/frontend/public/art/{heroes,tiles}/` insieme a `src/frontend/src/generated/art-manifest.json`. Il selettore mostra i ritratti dettagliati quando sono presenti nel manifest; altrimenti usa l'atlas delle miniature originali già incluso nella repository, senza richieste HTTP a file mancanti. I file generati possono essere verificati e committati separatamente; le abilità dei personaggi restano da definire.


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
 
## Immagini originali degli eroi nella preview (issue #11)

Il selettore degli eroi usa ora l'**atlas WebP derivato dai 14 PNG originali**,
con disposizione 7×2 nello stesso ordine di `src/shared/heroes.json`.
Il file `src/frontend/public/art/heroes-original-atlas.webp` contiene tutte
le miniature (448×148 px, circa 4,7 KB), senza importare i PNG da 312 MB.
Per rigenerarlo da `shirokurozip.zip`:

    python scripts/build_hero_atlas.py /percorso/allo/shirokurozip.zip

La mappa R3F contiene ancora una pedina 3D provvisoria. I ritratti dettagliati,
le carte e le texture originali della mappa saranno trattati in #4.
 
## Anteprima 3D dei terreni (issue #12)

Le **sei immagini originali** `tasselli/{black,white,city,sand,water,wild}.png`
sono ora incluse in un piccolo atlas `src/frontend/public/art/terrain-original-atlas.webp`
usato sia per le superfici degli esagoni 3D (Three.js) sia per i controlli
di selezione. Per rigenerare dal materiale originale, con Pillow installato:

    python scripts/build_terrain_atlas.py /percorso/allo/shirokurozip.zip

Il pannello **Terreni originali** permette di assegnare temporaneamente una
texture all'esagono selezionato e ripristinare la distribuzione dimostrativa.
Tutte le modifiche restano nel browser e **non** costituiscono mappa ufficiale,
regole di movimento, né dati inviati al backend. La qualità dei materiali e le
carte restano tracciate dall'issue #4. Gli atlas originali versionati vengono
validati anche dalla CI Python (dimensioni, formato WebP, peso e presenza di
tutte le celle); la UI e le selezioni sono testate con Playwright/Vitest.

## Esportazione/importazione della mappa locale (issue #13)

Il pannello **Salva e carica la mappa** genera un JSON copiabile con
`schemaVersion: 1`, `radius: 2` e le 19 celle `{q, r, terrain}`.
I documenti importati vengono controllati interamente prima di modificare
la scena: coordinate e terreni validi, nessuna cella duplicata/mancante,
nessuna proprietà aggiuntiva (ad esempio dati segreti di partita), e limiti
di dimensione. Il JSON può essere incollato da un altro utente per replicare
l'anteprima senza backend. Il formato è **solo per la progettazione grafica**,
non definisce una mappa ufficiale, le basi, il tesoro o un salvataggio partita.

È testato da Vitest e Playwright; in futuro una mappa usata per gameplay
richiederà nuove regole approvate e validazione server-side.
 
## Schede eroe originali (issue #14)

Sotto la mappa 3D si trova la galleria con i due lati delle schede originali
`screens2/` per l'eroe selezionato. L'asset compatto
`src/frontend/public/art/hero-ability-cards-original.avif`
contiene tutte le 28 immagini (14 fronti, 14 retro), senza scaricare
i grandi PNG sul client. Il catalogo è derivato dallo ZIP fornito dall'autore:

    python scripts/build_hero_card_atlas.py /percorso/allo/shirokurozip.zip

La versione integrata nel browser è un'anteprima di riferimento, **non
un regolamento**: il testo originale in miniatura può risultare illeggibile
e sarà necessario digitalizzarlo/validarlo con l'autore prima di creare
le abilità eseguibili (#3/#5). Le carte movimento, gli asset a maggiore
risoluzione e la provenienza/licenza per il rilascio restano in #4.

## Galleria carte movimento (issue #16)

Le 130 immagini originali di `carte movimento3/` sono ora mostrate
come miniature numerate per ciascuno dei **13 eroi** con un set nell'archivio.
La galleria segue il personaggio selezionato e mostra esplicitamente
l'assenza delle carte di **Gordunhao**: non significa che non possa
muoversi nel gioco, ma che il materiale originale non le contiene.

Il file versionato `src/frontend/public/art/movement-cards-original.avif`
è un atlas di riferimento 10 colonne × 13 righe con 130 immagini (560×1196
pixel, ~15KB), generato in ordine numerico e per eroe dalla lista canonica:

    python scripts/build_movement_card_atlas.py /percorso/allo/shirokurozip.zip

**Solo illustrazioni:** non si esegue una pesca, non si invia un movimento
al backend e nessuna iconografia viene trasformata in regola PvP senza
approvazione (#3/#5). Gli asset a piena risoluzione e le autorizzazioni
per il rilascio restano in #4.

## Verifica archivio originale (bug #17)

L'archivio del progetto include un file ausiliario di circa 23,9MB:
il validatore ne controlla il nome e i metadati, ma non lo importa.
Il limite per singola entry è stato portato a 32 MiB; rimangono i limiti
cumulativi, sul numero delle entry, sulle immagini e il blocco di
path traversal o duplicati.

## Trasporto roster autenticato (issue #18)

Il backend .NET 10 espone **solo letture pre-partita**:
`GET /api/lobbies/{lobbyId}/roster` e il metodo SignalR
`GetRoster(lobbyId)` su `/hubs/lobbies`. Entrambi richiedono una
sessione cookie ASP.NET Core valida con identità `NameIdentifier` unica,
e la presenza effettiva dell'utente nella lobby. Le richieste anonime
ricevono 401; quelle di membri di altre stanze non rivelano il roster
(404 indistinguibile da una stanza inesistente). Nessuna API accetta
`playerId` fornito dal client o espone posizioni/percorsi segreti.

**Non è ancora possibile effettuare login o creare/joinare lobby da
browser**: non esiste un identity provider autorizzato né un endpoint che
rilascia cookie, quindi il trasporto rifiuta normalmente gli accessi
fino alla configurazione futura. Non ci sono push/subscribe o partite
online. Sono inclusi test HTTP e SignalR con sessioni firmate in-process
e documentazione di sicurezza in
`docs/architecture/authenticated-lobby-readonly.md`.

