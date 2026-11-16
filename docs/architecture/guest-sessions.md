# Ephemeral guest identity — #19

Guest sign-in creates a **pseudonym**, not a verified personal identity.
The server generates a 128-bit cryptographically random identifier:
`guest-<32 lowercase hex chars>`. No request body or chosen identity
is accepted. A valid existing guest cookie is reused without rotation.

## Routes

- `POST /api/sessions/guest` — requires `X-Shirokuro-Request: 1`
  and a trusted `Origin`, issues a protected cookie, returns
  `{ playerId, kind: "guest" }`.
- `GET /api/sessions/me` — authenticated principal's own ID and kind,
  never cacheable.
- `DELETE /api/sessions/guest` — guest-only, same anti-CSRF checks,
  clears the cookie.

Cookies are HttpOnly, Secure and SameSite=Strict, expire after 8h without
sliding renewal, and use ASP.NET Core Data Protection signing/encryption.
The session kind claim is inside the signed ticket.

## Browser and abuse boundaries

The custom request header makes browser cross-origin writes require a CORS
preflight. As of #28, **non-Development** requests are denied unless their
canonical HTTPS `Origin` is explicitly configured in
`Security:AllowedOrigins`; `Host` and spoofed forwarded headers cannot grant
CSRF trust. No configured origin means all guest/lobby mutations return 403,
while health/read-only routes continue working. In Development, only a local
loopback API same-origin or the explicit `localhost:5173` /
`127.0.0.1:5173` Vite origins are accepted. During Development, only
the two Vite origins allow CORS credentials (never `*`). Guest issuance is limited to 12 POSTs per minute per trusted remote IP
with no queue. Since #37 this limit is **shared via Redis across all
non-Development API instances**; local Development retains the
process-local limiter. See [deployment-security.md](deployment-security.md).

Follow-up #23 now records a **separate 128-bit random session ID** in the
signed cookie and in a server-side registry alongside the guest's player ID
and expiry. `OnValidatePrincipal` checks this registry on every HTTP request;
legacy, revoked, expired or unregistered guest cookies are rejected. Guest
logout revokes the registry entry **before** clearing the browser cookie,
so a copied ticket cannot authenticate again. SignalR `GetRoster` also
checks the active registry on every hub invocation (a WebSocket may have
connected before logout). Registry entries are pruned every five minutes. With #27, each natural
expiry also queues a trusted player ID for the session worker, which
removes that player from active pre-match rosters without changing room TTL
or authorization of other members. IDs pruned opportunistically at guest
registration are retained for the next sweep; explicit logout (#25) still
removes membership immediately, not through the expiry queue.

In #30, non-Development requests also require a canonical, explicit
`AllowedHosts` entry for every API, health and SignalR request. An unlisted
or malformed Host is rejected (HTTP 400) before any authentication or
lobby mutation. Configure `AllowedHosts` separately from the Origin
allowlist; neither trusts `X-Forwarded-Host`.

The optional reverse-proxy configuration (#29) accepts `X-Forwarded-For`
and `X-Forwarded-Proto` **only** from explicitly trusted proxy IPs, with
single-hop forwarding. This preserves per-client issuance throttles behind
a real reverse proxy without allowing untrusted clients to spoof their
IP. `X-Forwarded-Host` is never accepted. See
[deployment-security.md](deployment-security.md).

**Shared guest authorization and revocation (#33):** Outside Development,
the session ID is checked on **every HTTP request and SignalR roster method**
against a TLS-enabled Redis distributed cache. Session issuance persists
the server-generated 128-bit random token there before issuing a cookie;
Redis keys contain only the SHA-256 of that token, and values contain the
guest identity hash and absolute expiry. Redis TTL is never sliding.
Guest logout removes the shared token before clearing its cookie, so
another API instance rejects it at the next authentication check.
A Redis outage never grants access based on local memory.

The key ring/certificate must be shared across nodes (#32), with identical
TLS Redis configuration (#33). Since #36, lobby states, rosters and
membership cleanup are **shared through Redis** in non-Development:
logout B revokes the credential across nodes and removes that guest from
rooms created on A, without renewing their idle TTL. The 5-minute
expiry worker uses the same coordinator, but notifications about naturally
expired sessions are queued on the issuing process and may be lost if it
crashes. Crash-proof reconciliation and full multi-IP anti-abuse hardening
are still pending #21. Per-IP issuance and creation limits are now
distributed on non-Development APIs (#37); Development remains
completely in-memory. With #31, non-Development HTTP is denied for all API and SignalR
routes (403 without redirect). Trusted proxies can report HTTPS only
through the #29 allowlisted peer mechanism; direct clients cannot grant
themselves HTTPS with `X-Forwarded-Proto`. A bounded 30-day HSTS header
is emitted on HTTPS responses outside Development. Use actual TLS at
the edge and persistent/shared Data Protection before production;
proxies must reliably set scheme and host.

Session issuance by itself does not grant admission. #20 exposes
invitation-based room creation/join; the #18 HTTP roster and SignalR
reads require server-side membership and expose no secret PvP
positions/routes. Distributed session revocation (#33) and pre-match
roster cleanup (#36) are now in place outside Development. Durable
identity and operational hardening remain #21; actual match networking
and visibility still depend on #3/#5/#9.

## Strict guest-only cookie principals (#139)

Shirokuro currently registers **one** cookie authentication scheme, and it
issues only cryptographically generated guest identities. There is no verified
account login provider yet. A Data Protection-signed legacy ticket with an
arbitrary/non-guest `NameIdentifier`, missing/incorrect guest kind, ambiguous
identity or noncanonical `guest-<32 lowercase hex>` player ID is therefore
**not** a verified account. The cookie middleware now rejects this entire
class **before consulting Redis**. On private APIs and the SignalR
negotiate endpoint it yields 401/no-store, never privileged content.

A signed unsupported ticket hitting `POST /api/sessions/guest` does **not**
get silently exchanged for a fresh guest: a server-side per-request marker
returns 401/no-store with no new cookie/session registry write. The existing
pre-auth IP admission limit may still charge such a request; the marker is
not a quota bypass. Anonymous users can continue to issue a guest session,
and a legitimately revoked guest can explicitly obtain a fresh one following
the existing recovery flow. Canonical guests with live registry records
remain valid; during Redis outages they still return 503/no-store, not 401.
Public `/api/health`, `/api/ready` and board preview remain identity-
independent and skip optional Redis validation for availability, without
ever granting protected access. Framework synthetic 405 remains public
method rejection, never an authorization rule.

A future verified account system must introduce a **separate, explicitly
authorized identity provider/scheme and policy** with independent tests;
a stale cookie signed under the guest scheme cannot implicitly become an
account. Tests cover malicious/legacy ticket shapes in Development and
Production TestServer, missing Redis authority, no replacement issuance and
normal valid guest behavior. This change does not establish durable accounts,
anti-Sybil defenses or PvP gameplay rules; those remain tracked under #21
and the original game-design issues.


## Verified shared guest ticket write — #174

Outside Development, a completed `IDistributedCache.SetAsync` is not enough
to publish an authentication ticket. The guest registry now reads **the same
server-generated, SHA-256-token-keyed record** back and uses
`CryptographicOperations.FixedTimeEquals` to verify that the precise
serialized expiry/player digest was persisted. The write and confirmation
share **one two-second deadline**, rather than two independent timeouts.
The random session ID, derived Redis key and sealed player digest are never
logged or returned in error responses. A null, stale/mismatched, unavailable
or timed-out read results in **503/no-store without Set-Cookie** and without
adding a local registered-session entry. An unconfirmed late SET, if any,
remains a cryptographically unguessable orphan with its original absolute
expiry, not a usable client cookie. There is no automatic retry.

The Production/Staging boundary does not change the Development-only local
registry, the eight-hour absolute session limit, CSRF/Origin/Host checks,
Redis authority validation on each HTTP request/Hub invocation, remote
revocation, or encrypted guest expiry maintenance. No new account provider
or match rule is introduced.

xUnit in #174 covers a cache silently discarding SET, corrupting the
confirmation GET, or never completing that GET, followed by successful
recovery with a valid signed cookie. The original mutation/roster tests
continue to assert no *unauthorized* Redis reads: they now distinguish the
mandatory registration confirmation from subsequent auth reads. The
full runner verifies actual Redis TLS/ACL/HA/Sentinel and Linux lifecycle,
but it is not a staging ingress across physical pods or LB failover; #21
remains open for those operational prerequisites.


## Confirmed shared guest ticket deletion — #175

In non-Development, a completed `IDistributedCache.RemoveAsync` is not
proof of revocation. A silently ignored delete could leave a copied signed
cookie valid on another node while the logout returns HTTP 204. The guest
registry now uses a single bounded two-second deadline for reading the
existing ticket, deleting its SHA-256-token-keyed record, and **reading the
same key back**. Only a missing record confirms revocation; the key is never
reused. If delete is ignored, the confirmation returns unexpected bytes, or
Redis cannot complete the confirmation, the HTTP mutation reports a generic
503/no-store with no new Set-Cookie and no false promise of logout. A ticket
that was actually deleted may already return 401 while the client received
503 because its confirmation failed; clients must not infer authority from
this transient error. A subsequent retry can confirm deletion if the key
still exists. No raw token, key, player identity or Redis exception is sent
in responses.

This preserves Development's in-memory behavior, verified-session cookie
authority, post-revocation lobby fence and cleanup, and all gameplay rules.
Tests exercise two Production TestServers sharing a cache which ignores
remove, corrupts the verification read or blocks it, including cross-node
cookie replay and recovery after an unconfirmed delete. Parent #21 remains
open for staging/multi-pod and operational failover validation.

Validation: [main CI #43899998215](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43899998215) passed all three jobs on `3707e3f`: 796 standard xUnit, 21 Redis TLS/ACL/HA/Sentinel, Linux SIGUSR1, 347 Vitest, 43 Playwright, 35 Python and builds. The two preStop/issuance-drain rollback tests now explicitly count the additional post-delete authority read rather than weakening deletion invariants.


## Conferma del fence per logout e scadenza — #177

Dopo la revoca del token Redis condiviso, un guest potrebbe avere una
mutazione lobby già autenticata in esecuzione. La barriera Redis
dell'attore protegge i Lua CAS di tale richiesta; ora la sua
installazione è considerata conclusa solo se un GET conferma lo stesso
marker server-owned `"1"` sulla chiave SHA-256. Una scrittura
`SET NX` riportata come completata ma non memorizzata, o un errore
della lettura di conferma, non costituisce prova di blocco. Quando il
cookie è già stato revocato, il logout non può ripristinarlo, ma
restituisce 503/no-store anziché un falso 204; il lavoro di cleanup
rimane ritentabile dal worker. Un fence già presente viene
riconfermato senza estenderne l'8h TTL. SET/GET usano un solo
deadline di 2s, senza ritentativi di mutazione incerta.

Nessun segreto o ID viene restituito in errore HTTP. Le regressioni
xUnit Production e Redis controllato accompagnano i normali test di
revoca cross-node; la parent #21 resta aperta per failover operativo,
account verificati e integrazione atomica cross-store.


## Conferma cancellazione ticket restaurato prima dell'ack — #178

Un indice Redis di scadenza restaurato dopo un rollback RDB può
contenere work item relativi a guest pre-cutover. Solo il work item
autenticato e cifrato con Data Protection contiene il digest SHA-256
del token originale: il browser non può scegliere la chiave. Il worker
verifica prima il fence e ripulisce le membership delle lobby tramite
CAS, poi può rimuovere il ticket mirato e fare ack del lavoro cifrato.

Da #178 `RemoveRestoredSessionAsync` deve verificare una lettura
`GetAsync` **nulla della stessa chiave** dopo `RemoveAsync`, entro
un'unica deadline di **2 secondi**. Se l'adapter Redis ignora DELETE,
la lettura è non nulla/corrotta, fallisce o rimane bloccata, il work
item protetto non viene confermato, resta per retry e gli altri
guest già letti nel batch possono completare la propria pulizia.
Un secondo tentativo su chiave già assente ha successo senza creare
alcuna sessione o prolungare una scadenza.

Il logout ordinario mantiene la propria conferma di revoca (#175);
questa è solo la cancellazione di recovery di un digest autenticato.
Nessun cookie, player ID, digest, chiave Redis o eccezione privata è
esposto in risposte HTTP, log o metriche. L'osservazione di assenza
sul client Redis non garantisce un quorum tra repliche o un corretto
restore multi-pod. Mantenere il cutoff esterno di disaster recovery
e il lavoro operativo aperto nella parent #21.

[CI applicativa #43915651694](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43915651694)
SUCCESS 3/3 su `7de2770`: 816 xUnit, 21 Redis TLS/ACL/HA/Sentinel,
SIGUSR1 Linux, 347 Vitest, 43 Playwright, 35 Python e build.


## Conferma di persistenza della coda cifrata delle scadenze — #179

Prima della consegna del cookie guest in non-Development, l'API
registra il ticket in Redis e accoda il work item di scadenza cifrato
con Data Protection. In precedenza il secondo passaggio si fidava
del solo risultato di `SortedSetAddAsync`, senza verificare che il
membro fosse realmente persistito. Un adattatore Redis che confermava
un ADD ignorato poteva produrre un cookie valido ma privo del lavoro
persistente necessario al cleanup cross-node dopo un crash.

Ora `RedisGuestExpiryIndex.SaveAsync` scrive il membro opaco e
rilegge **lo score Redis dello stesso membro** con
`SortedSetScoreAsync`; l'esatto score UTC arrotondato al millisecondo
deve corrispondere a quello scritto. ADD e SCORE usano un unico
deadline di **2 secondi**, con cancellazione controllata tra le
chiamate, sia durante `ScheduleAsync` (issuance) sia durante
`ScheduleRevokedAsync` (logout). ADD riuscito senza record, score
mancante o corrotto, errore e timeout non confermano la persistenza.
In caso di issuance fallita, il codice HTTP già esistente tenta
la revoca del ticket non pubblicato e risponde 503/no-store
**senza Set-Cookie**. Per il logout una coda non confermata
non è riportata come durabile e si usa il percorso di cleanup/fallback
esistente; non si promette la consegna di un lavoro che manca.

Lo score e il membro cifrato sono tecnici; player ID, token, digest,
inviti, path e contenuti Redis non entrano in metriche, log o risposte.
I nuovi xUnit verificano ADD ignorato, ZSCORE nullo/corrotto,
eccezione, lettura bloccata, cancellation e ripresa per entrambe
le operazioni, nonché il boundary HTTP Production e il recupero
dell'emissione. Il read-back prova solo lo stato osservato sul
connettore Redis, **non** un quorum fra repliche o il deployment
staging reale: #21 resta aperta.

[CI codice #43919216201](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43919216201)
SUCCESS 3/3 su `57b9ef6`: 831 xUnit, 21 Redis TLS/ACL/HA/Sentinel,
SIGUSR1, 347 Vitest, 43 Playwright, 35 Python e build.


## Conferma ack del lavoro di scadenza guest — #180

Dopo la pulizia delle membership e di eventuali ticket restaurati,
la rimozione del lavoro cifrato dalla coda Redis viene verificata:
un `ZREM` completato non è da solo prova di persistenza della
rimozione. Lo stesso `key/member` viene letto con `ZSCORE`
sotto **un solo deadline di 2 secondi**. L'ack è valido soltanto se
il membro non esiste più; una rimozione ignorata, una lettura non
nulla/corrotta, un errore o un blocco lasciano il lavoro disponibile
per la prossima sweep. Il retry non cancella altri membri, e
l'assenza già confermata dopo un primo tentativo o da altro nodo
è idempotente.

Il membro è sempre il ciphertext Data Protection fornito dalla coda,
mai un ID o una chiave selezionata dal browser; nessun segreto viene
aggiunto a risposte HTTP, log o dimensioni delle metriche.
Sette nuovi xUnit validano i fault dell'ack e cancellation, e
non sostituiscono i test Redis reali e le verifiche del rollout
operativo multi-pod ancora sotto #21.

[CI codice #43927171180](https://github.com/KeyserDSo/Game.Shirokuro/actions/runs/43927171180)
SUCCESS 3/3: 838 xUnit, 21 Redis, SIGUSR1, 347 Vitest,
43 Playwright, 35 Python, build.
