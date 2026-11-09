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

The frontend renders a **local preview board** and an accessible 14-hero selector (local-only; no abilities or authoritative match state). Separately, the **guest lobby panel** now uses the .NET API for server-issued guest sessions, invitation rooms and pre-match roster choices (#19/#20). Public API health/board preview endpoints remain available. No live PvP match, movement rules, fog-of-war or hidden-route networking exists yet.

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

**Aggiornamento #19/#20:** gli ospiti ricevono ora sessioni firmate, possono
creare/entrare in una lobby tramite invito, leggere il roster, cambiare la
propria squadra e scegliere il proprio eroe. Non è ancora una partita:
non esistono avvio partita, matchmaking, push realtime, persistenza o regole
ufficiali. Vedi `docs/architecture/lobby-foundation.md`.
 
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

Con #19/#20 un browser può ottenere un'identità ospite temporanea, creare
una lobby privata ed entrare con invito; restano da implementare account
permanenti, push realtime e partite online. Il roster HTTP è ancora una
lettura per soli membri e il metodo SignalR `GetRoster` non espone
informazioni di gioco. Vedi
`docs/architecture/authenticated-lobby-readonly.md`.

## Sessione ospite temporanea (#19)

Il backend dispone ora di un accesso ospite **senza password**:
`POST /api/sessions/guest` assegna un ID imprevedibile generato sul server;
`GET /api/sessions/me` legge la propria sessione e
`DELETE /api/sessions/guest` cancella il cookie nel browser. Il cookie è
firmato/protetto, HttpOnly, Secure e SameSite=Strict, con durata di otto
ore e senza rinnovo automatico. È un ID temporaneo, **non** un account
verificato o permanente.

Ogni POST/DELETE richiede gli header `Origin` (API stessa oppure i due
frontend Vite esplicitamente autorizzati in Development) e
`X-Shirokuro-Request: 1`. Il limite base è di 12 richieste di emissione
al minuto per IP. Nessun playerId è accettato dalla richiesta. Il logout
cancella il cookie del browser ma non revoca un ticket già copiato: prima
di distribuire il gioco servono revoca, HTTPS, chiavi Data Protection
persistenti/condivise e difese anti-abuso distribuite.

Il guest non entra automaticamente in una lobby: deve usare esplicitamente
un invito o crearne una (#20), poi la #18 verifica la membership. Vedi
`docs/architecture/guest-sessions.md`. Il multiplayer delle partite
resta ancora da sviluppare.

## Lobby online pre-partita con invito (#20)

Due browser distinti possono ora scegliere **Entra come ospite**,
creare una stanza e unirsi condividendo il codice `room-...` mostrato
nella UI. Il codice è generato dal server con 128 bit casuali e
funge da **invito opaco**; non viene pubblicato in un elenco di stanze.
Il partecipante entrante deve conoscere anche il nome della squadra.
Le squadre configurabili `white,black` sono un esempio, **non la regola
ufficiale** del futuro PvP.

Il pannello permette di confermare l'eroe locale nella lobby, cambiare la
propria squadra, aggiornare il roster o uscire. Gli altri membri diventano
visibili dopo un **Aggiorna roster** manuale: non esistono ancora notifiche
push, turni o avvio partita. Se una scelta usa una revisione precedente,
il server rifiuta l'operazione con HTTP 409 e l'interfaccia chiede di
aggiornare la stanza. Il resto della scena 3D resta una preview locale.

API .NET (cookie guest e `X-Shirokuro-Request: 1` + `Origin` per writes):
- `POST /api/lobbies`: configura 2–4 team tecnici, crea stanza e iscrive
  il creatore **atomically**.
- `POST /api/lobbies/{code}/join`: ingresso tramite invito;
  `expectedRevision` è facoltativa, la revisione corrente viene
  controllata sotto lock.
- `PATCH /api/lobbies/{code}/hero` e `PATCH /api/lobbies/{code}/team`:
  modifiche del solo utente identificato dal cookie e controllo revisione.
- `DELETE /api/lobbies/{code}/membership`: abbandona e libera la
  stanza se è vuota.
- `GET /api/lobbies/{code}/roster`: sola lettura per membri,
  `Cache-Control: no-store`.
Le risposte non includono mai posizioni o destinazioni di movimento.
Limiti tecnici attuali: **256 lobby in memoria**, **16 membri per lobby**,
**8 creazioni stanza/minuto per IP**, senza suggerire dimensioni ufficiali
delle squadre. Esegui `dotnet run --project
src/backend/applications/Shirokuro.Api` insieme a `npm run dev`:
la configurazione Vite inoltra `/api` e `/hubs` a
`http://127.0.0.1:5186` (avvia l'API con `--urls http://127.0.0.1:5186`).

I test Playwright avviano **due browser guest distinti** e l'API .NET 10
con un server dedicato. La CI testa xUnit e i tre casi Playwright della
lobby. La sessione è **pseudonima e non persistente** e le stanze vivono
solo nella memoria del processo; scadenza automatica, revoca sessioni,
distribuzione e recupero sono tracciati in #21. Dettagli:
`docs/architecture/guest-invitation-lobby.md`.
