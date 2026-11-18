# Deployment security: Origin, Host, proxy and cryptographic keys — #28–#32

This is an **intermediate hardening step**, not a production-ready
multi-node deployment. Shirokuro currently holds its room/session registries
in memory on each process; persistent accounts, distributed session revocation,
guest session registry and shared room state remain #21. A persistent
encrypted Data Protection key ring can now be operator-configured (#32),
but does not make those in-memory stores distributed.

## CSRF Origin allowlist

All cookie-backed HTTP writes (guest session issue/logout and lobby create,
join, team, hero, leave) require both:
1. Exactly one `Origin` that is a canonical `scheme://host[:port]` with
   no wildcard, userinfo, trailing slash, path, query, fragment or list.
2. Exactly `X-Shirokuro-Request: 1`.

For any environment except `Development`, the Origin must be **HTTPS** and
match an explicitly configured value in the .NET configuration array
`Security:AllowedOrigins`, by scheme, hostname and port. Example environment
configuration for **your own deployment** (replace the demonstration domain):

```sh
Security__AllowedOrigins__0=https://game.example.com
AllowedHosts=game.example.com
```

With no configured trusted origin, mutating endpoints **fail closed (403)**;
public read-only routes such as `/api/health` keep working. Incorrect
entries (HTTP, wildcard, path, query, etc.) are ignored. Do not use `*`,
allow arbitrary Origin reflection, copy `Origin` from `Host`, or trust
`X-Forwarded-Host` / `X-Forwarded-Proto` from incoming user requests.

The Origin allowlist provides CSRF authorization; **issue #30** adds an
independent, fail-closed **Host allowlist** in non-Development environments.
A missing, empty, wildcard (`*`), malformed or duplicated `AllowedHosts`
configuration causes startup failure. The semicolon-separated entries are
exact canonical hostnames, optionally including a specific port, e.g.
`AllowedHosts=game.example.com;api.example.com:8443`. Subdomain
wildcards and unconfigured ports are not authorized.

Every request (including health, read-only APIs, SignalR negotiate, and
guest/lobby writes) must have **one** syntactically valid Host that exactly
matches an allowlisted host and port; otherwise the API responds `400`,
before auth, rate limiting and endpoint logic. The built-in ASP.NET Core
Host filter receives hostname-only entries as defense-in-depth; Shirokuro's
additional ingress policy enforces the optional port exactly (including
non-default ports) and rejects comma-joined/duplicate Host values. `X-Forwarded-Host` is never
used to bypass this check. Host policy is separate from Origin: configure
both `AllowedHosts` and `Security:AllowedOrigins`, consistently with the
actual proxy routing. In Development the Host allowlist restriction is
disabled to preserve Vite/localhost; cookie writes still require the
loopback/Vite Origin checks of #28.

## HTTPS-only API ingress (issue #31)

Outside `Development`, **every** HTTP request, including health, public
preview, session reads/writes, lobby actions and SignalR negotiation,
must arrive with an HTTPS request scheme. Plain HTTP is rejected with
`403 Forbidden` and `Cache-Control: no-store`, **without a redirect** that
could interfere with authenticated API clients. HTTPS responses use HSTS
(`max-age=2592000`, 30 days), with neither `includeSubDomains` nor
`preload` implicitly enabled. Local `Development` HTTP and Vite are
unchanged, and HSTS is not emitted in Development.

For TLS-terminating reverse proxies, forward the external `https` scheme
in `X-Forwarded-Proto` **only from the explicitly configured proxy peer**
(`Security:KnownProxies`, #29). Incoming HTTP with an arbitrary
`X-Forwarded-Proto: https` is not trusted when the TCP peer is unknown:
the API still returns 403. Hosts remain independently allowlisted (#30)
and `X-Forwarded-Host` stays ignored. The proxy must strip incoming
client-supplied forwarding headers and set its own before relaying.

**TLS termination, certificates, port exposure and the ingress firewall
remain deployment-operator responsibilities.** This guard checks the
trusted effective request scheme; it does not open a TLS listener,
provision a certificate or independently verify edge TLS security.

## Trusted reverse proxy (issue #29)

Forwarded-header processing is **disabled unless explicitly configured**.
For a known **single proxy hop**, set exact transport peer IP addresses in:

```sh
Security__KnownProxies__0=192.0.2.10
# Security__KnownProxies__1=2001:db8::10
```

The above are documentation-only addresses: use the **actual IP of your
reverse proxy**, not client IPs. Configure `Security:KnownProxies` as an array
of unique, canonical IPv4/IPv6 addresses. CIDR/networks, wildcards, empty
entries, duplicated addresses and unspecified/broadcast addresses are
not allowed and cause a **startup failure**, not a trust expansion.

With this setting, ASP.NET Core's standard `ForwardedHeadersMiddleware`
runs **before** authentication, CORS and the per-IP throttles. It accepts
**only** `X-Forwarded-For` and `X-Forwarded-Proto` from a peer matching
the configured proxy IP. `ForwardLimit=1` (single rightmost hop), and
symmetric counts of forwarded values are mandatory. It **never**
processes `X-Forwarded-Host`, so no client or proxy can rewrite the
application Host through that header. An unknown peer or missing config
cannot change the IP partition used by the throttles. A trusted proxy
must **remove and replace** user-supplied `X-Forwarded-*` headers at the
ingress; otherwise clients may spoof their address even behind that proxy.
Restrict direct network access to the service where possible.

The Host guard (#30) validates the **backend-facing Host** without reading
`X-Forwarded-Host`: configure the trusted edge proxy to replace Host with
an explicitly allowlisted value. The proxy must also terminate HTTPS
properly, strip client-controlled forwarding headers and restrict direct
access. These operational guarantees cannot be proven just by ASP.NET tests
and remain deployment work under #21.
A separate frontend origin also requires an explicit CORS policy review:
this feature does **not** enable cross-origin production credentialed CORS.

In `Development`, only loopback same-origin traffic and the two fixed Vite
origins (`http://localhost:5173` and `http://127.0.0.1:5173`)
are accepted. This prevents a forged `Host: evil.example` matching
`Origin: http://evil.example` from granting CSRF trust even in dev.

## Persistent encrypted Data Protection key ring (issue #32)

Production, Staging and other non-Development environments require an
explicit persistent key directory and a PFX **with a private key**.
The API fails to start if these values are absent, relative, missing or
unreadable. Local Development continues to support its existing ephemeral
keys when no Data Protection storage is configured.

```sh
# Illustrative paths only; mount these as dedicated, access-controlled volumes
Security__DataProtection__KeysDirectory=/srv/shirokuro/data-protection
Security__DataProtection__CertificatePath=/run/secrets/shirokuro-keys.pfx
Security__DataProtection__CertificatePassword=<provided via secret manager>
```

The key directory **must already exist**; Shirokuro does not create it or
silently switch to an unencrypted repository. The PFX file and password
must come from deployment secrets, **not the Git repository or the public
web root**. Give only the service identity read/write access to the key
directory, and only authorized service identities access to PFX material.
Backup the key ring and PFX securely and restrict who may overwrite or
delete them: compromise of either affects cookie confidentiality or
integrity. A shared key ring can be mounted read/write across processes,
but requires storage that supports consistent concurrent writes.

The API uses `PersistKeysToFileSystem` and `ProtectKeysWithCertificate`
to encrypt the XML key ring at rest, a stable Data Protection application
discriminator `Shirokuro`, and automatic Data Protection key lifetime
of 90 days. Older key files must be **retained** so existing cookies remain
cryptographically readable after automatic key rotation; do not delete
old key files as part of a deployment.

To rotate the **PFX wrapping certificate**, replace
`Security__DataProtection__CertificatePath` and configure old
certificates with their individual secrets during the transition:

```sh
Security__DataProtection__PreviousCertificates__0__Path=/run/secrets/shirokuro-previous.pfx
Security__DataProtection__PreviousCertificates__0__Password=<previous secret>
```

Keep the previous decryptors until any keys they wrap have been retired
according to your operational retention policy. Do not log PFX passwords,
ticket payloads or key XML. Configuration is verified with xUnit
cross-provider crypto, encrypted XML, certificate migration and cookie
TestServer tests. Since #33, Production guest tickets use a **shared Redis
session registry**: the key ring alone is not authorization, but a token
present in the shared registry is valid across API nodes. Since #36,
pre-match lobby state is also shared through Redis. Availability and
public production hardening still remain #21.

## Distributed guest session authorization (issue #33)

All non-Development deployments must configure a shared Redis guest-session
store with **TLS enabled**:

```sh
Security__GuestSessions__RedisConnectionString="redis.internal.example:6380,ssl=true,password=<secret from deployment>"
```

Use the real internal Redis hostname and secret via an appropriate secret
manager, not the repository. A missing, invalid or plaintext
`Security:GuestSessions:RedisConnectionString` prevents startup; no
fallback to the node's local memory is allowed. Redis must be reachable
only through trusted networks with TLS and authentication. Every API
instance must use the **same** Redis service and the **same** Data Protection
key ring/certificates (#32). Key names contain SHA-256 hashes of the
128-bit server-generated session tokens, not tokens or player IDs in
plaintext; cache values bind a guest identity hash and absolute expiration.
`OnValidatePrincipal` and SignalR `GetRoster` check Redis on each use,
and logout deletes the shared entry before clearing the browser cookie.

Since #36, non-Development lobby API mutations, member-scoped HTTP and
SignalR roster reads, and logout/expiry membership cleanup use the
**Redis coordinator**, not the process-local one. The same TLS Redis
connection configuration is used for sessions and rooms, with fail-closed
behavior on outages. Since #37, the guest-session/room-creation **per-IP
rate limits are also shared via Redis**, while durable encrypted expiry work is now shared between nodes (#38). Production needs Redis TLS
network access control, durable storage/backup and tested failover.
The xUnit integration uses **two Production TestServers, independent
Redis connections and a shared fake guest session cache**; it is not
a complete TLS Redis cluster failover or soak test.

## Security regression coverage

`WebMutationGuardTests` uses real TestServer endpoints in Production,
Staging and Development. It checks unconfigured fail-closed behavior;
configured HTTPS issuance, lobby mutation and logout; preservation of
cookies/roster after rejected writes; malformed and spoofed origins;
host and forwarded-header spoof attempts; and the local Vite flow.
`HttpsIngressTests` verifies plaintext rejection across route types,
valid direct and trusted-proxy HTTPS, cookie/lobby protection after HTTP
writes, HSTS, ignored `X-Forwarded-Proto` from unknown peers and
unchanged local Development HTTP behavior. The Playwright local-browser
workflows remain unchanged.

## Redis distributed API mutation throttling (#37)

Only the two endpoints that create server resources consume permits:
`POST /api/sessions/guest` is limited to **12/min/IP** and
`POST /api/lobbies` to **8/min/IP**. In non-Development these are
**Redis-backed, cluster-wide** fixed 60-second windows with atomic
Lua check-and-increment, no token queue and no sliding refresh.
Rate budget is based on ASP.NET `Connection.RemoteIpAddress` AFTER
allowlisted proxy forwarding (#29), not arbitrary client headers.
Policy scope and SHA-256 of the canonical IP are all that appear
in Redis key names; no cookie, invitation ID, guest identity or raw
IP address is put in keys or logs by this component. Redis errors
return 503 fail-closed without issuing a new session or lobby,
and over-budget requests return 429 with Retry-After. Development
continues to use the .NET in-memory rate limiter.

Use **exact trusted proxy addresses** and strip untrusted forwarding
headers at the ingress. All API nodes must share the same TLS Redis
service and credentials. A per-IP limit can affect users sharing NAT
and cannot prevent abuse spread over multiple IP addresses.
TestServer/CI Redis is local non-TLS and does not prove TLS availability,
persistence after failover or operation under network partitions.

This does not solve edge TLS/network validation, rotating-IP/Sybil attacks,
Redis availability/failover or durable player accounts:
all remain open in #21. Crash-safe guest expiry cleanup is handled by #38.

## Crash-safe guest expiry maintenance (issue #38)

In non-Development, a successful guest-session issuance first registers its
server-generated session token in Redis (#33), then schedules an **encrypted**
guest expiry item in the Redis sorted set
`shirokuro:guest-expiry:{default}:v1:pending`, and only then signs/returns
the HttpOnly guest cookie. A failed schedule revokes the just-created token;
there is no intentional local-memory fallback in Production/Staging.
Each item's sorted-set score is the fixed absolute 8-hour expiry time;
its member is protected with the existing shared ASP.NET Core Data Protection
key ring (purpose-isolated), never a plaintext guest ID or a session token.
The index key itself contains no user data.

Every API node runs the same maintenance worker: on startup and every five
minutes it loads up to 128 due entries, invokes the **server-side Redis lobby
coordinator** to remove stale memberships via revision-aware CAS, then removes
the expiry entry only after success. After a node crash another node can
continue the queue; an outage leaves work for later, and overlapping workers
are idempotent. Administrative cleanup must not renew the inactivity TTL of
surviving rooms. Local Development continues using the existing in-memory
expiry registry without a Redis requirement.

This is **eventual** cleanup, not an instantaneous presence/disconnect
signal. A revoked guest is denied immediately by the shared ticket registry;
logout's existing request path still removes memberships immediately on
success. Expiry queue recovery does not provide account upgrade, verified
identity, durable accounts, online-presence semantics, arbitrary Redis failover
or protection against attackers controlling many IP addresses (#21).
Operators must retain and share old Data Protection keys/certificates until
all protected expiry items have been processed; an unreadable entry is not
silently discarded. Real TLS Redis failover still needs deployment testing.

## Liveness and readiness for node routing (issue #39)

`GET /api/health` is **process liveness**: it answers 200 while the API
process is serving HTTP, even if Redis or the shared Data Protection key ring
cannot be used. It must **not** be used to decide whether a production node
can serve multiplayer traffic.

`GET /api/ready` is **node readiness** and remains unauthenticated for an
internal load balancer. Outside Development, every fresh check requires:

- a short-lived `SET` on a fixed technical key through the **lobby/mutation/expiry** Redis multiplexer, rejecting read-only replicas;
- a short-lived write **and readback** through the **separate guest session
  `IDistributedCache`** client (neither key contains guest data);
- a small round-trip through the configured purpose-isolated ASP.NET Core
  **Data Protection** key ring to confirm the node can protect and unprotect.

All checks must pass. Responses are intentionally identical across failures:
`200 {"status":"ready"}` or `503 {"status":"unavailable"}`, always
`Cache-Control: no-store`. Exceptions, secret paths, Redis credentials,
player identities and invitation details are never returned to clients or
written to readiness logs. Each process shares a **single in-flight probe task** across concurrent
readiness requests, and caches both success and failure for **3 seconds**,
with a **2-second** probe timeout. Individual HTTP client cancellation stops
only that client's wait, not the shared bounded Redis/Data Protection probe.
No per-request semaphore queue or positive cache is kept beyond this window. This is a bounded operational signal, not a full health audit.
For fast convergence after a failure/recovery, a load balancer must poll
periodically instead of caching HTTP responses. Development returns ready
without Redis, preserving local Vite/Playwright.

Existing fail-closed HTTPS and exact-Host ingress rules still apply before
either endpoint in non-Development. Operators must probe with the **expected
HTTPS scheme and allowlisted Host** through a trusted network/ingress, not
disable ingress protections for the health checks. Limit exposure or
rate-limit health endpoints at the edge: public anonymous probes reveal only
one-bit readiness but should not be advertised to arbitrary external clients.
A green local CI with plain Redis does **not** prove real Redis TLS, Sentinel/
cluster failover, backup/restore, load-balancer configuration or durable
accounts; these remain under #21.

## Aggregate multiplayer security metrics (issue #40)

The API exposes `System.Diagnostics.Metrics` instruments from meter
**`Shirokuro.Api.Multiplayer`** (version `0.0.1`). A deployment can
subscribe with a .NET/OpenTelemetry collector **explicitly configured and
restricted by the operator**. The application does **not** register a public
`/metrics` route, telemetry exporter **by default**, external collector, or any secrets
in configuration. A strictly opt-in, outbound-only HTTPS OTLP metrics exporter
was added in #60 (below). This code does not claim an operational monitoring backend
has been deployed.

| Counter (long) | Bounded attributes | Meaning |
| --- | --- | --- |
| `shirokuro.guest_mutation.decisions` | `policy=session\|lobby`, `outcome=allowed\|limited\|unavailable` | One technical rate-limiter decision for each protected POST, *not* success/failure of the later HTTP handler. Local Development and Redis deployment use the same trusted endpoint metadata. |
| `shirokuro.readiness.probes` | `outcome=ready\|unavailable` | One **actual** readiness verification. Cached Redis readiness hits are excluded; Development has no Redis to check. |
| `shirokuro.guest_expiry.sweeps` | `mode=local\|redis`, `outcome=success\|failure` | Completed or failed guest expiry maintenance passes. Expected host-shutdown cancellation is not a failure. |
| `shirokuro.guest_expiry.memberships_removed` | `mode=local\|redis` | Number of stale **lobby memberships** actually removed, **including successful items from a partially failed sweep**; not the number of accounts, sessions or rooms. |
| `shirokuro.guest_expiry.unreadable_items` | *(nessuna)* | Number of observations of unauthenticatable or structurally invalid encrypted expiry members; not distinct entries, no identity and no acknowledgement. |

All tag values are derived from **hard-coded server enum cases / boolean
statuses** and validated; there are no endpoint URLs, arbitrary status
messages, IP addresses, host names, Redis keys, player IDs, room/invitation
references, cookies, tokens, errors, or exception text. In particular, never
add these to metric dimensions: unbounded/high-cardinality labels can become
a privacy and memory-denial problem. No log of an individual abuse attempt is
required by this feature. A limiter's `allowed` value means a **permit was
granted**, not that the final endpoint produced HTTP 2xx.

An operator can configure **aggregate** alerts for sustained
`outcome=unavailable` (service dependency), `outcome=limited` (capacity/
abuse) and failed cleanup sweeps; threshold/SLA selection is deployment
policy, not a hard-coded gameplay rule. Counts are per process until an
external collector aggregates nodes. Instrumentation is not an abuse
prevention mechanism or a substitute for Redis TLS, HA/failover testing,
controlled monitoring access or durable identity (#21).

## Readiness latency distribution and safe failure-phase telemetry (#59)

Beyond the existing counter `shirokuro.readiness.probes`, each **executed
Production Redis/Data Protection readiness probe** now records exactly one
`Histogram<double>` from `Shirokuro.Api.Multiplayer`:

| Instrument | Unit | Closed labels | Interpretation |
| --- | --- | --- | --- |
| `shirokuro.readiness.duration` | seconds (`s`) | `outcome=ready\|unavailable`, `stage=ok\|redis-write\|session-read\|key-ring\|timeout` | End-to-end *monotonic* wall time of the **single shared** write/read/key-ring verification. |

`stage` describes only the **phase of the probe**, not proof of which
backend is down. In particular, `redis-write` can result from **either**
the lobby or the independent guest-cache Redis connection: those writes
run concurrently; `session-read` is the cache round-trip readback;
`key-ring` is Data Protection verification; and `timeout` overrides
the phase when the 2-second budget expires. Healthy probes use `ok`.
The 3-second result cache, concurrent HTTP callers and cancellation
of only one waiter **do not multiply observations**. The duration is
measured using `Stopwatch` monotonic ticks, independent of UTC clock
adjustments. Development readiness does not perform this Redis probe,
so it does **not** contribute timing samples.

Operators may collect this meter through a separately configured,
access-restricted OpenTelemetry exporter/collector, then construct alerts
for sustained `unavailable` probe ratios and high histogram p95/p99
(for example, report the fraction of unavailable probes and latency
percentiles, split by the bounded `stage` values). Choose thresholds,
time windows and paging policies from real deployment SLOs instead of
guessing a game-specific guarantee. A histogram measurement never
contains exceptions, connection strings, URLs, player IDs, IPs or
cookies; do not add them to metric labels. **No** public `/metrics`,
Grafana/Prometheus endpoint or exporter is enabled by this change.
Redis/Sentinel server health, replication lag, partitions, RPO/RTO
and an actually deployed alert receiver still require infrastructure
monitoring under #21.

Readiness timing and failure-stage privacy checks passed in [CI main #43093799657](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43093799657) SUCCESS: 378 standard xUnit, 5 real Production Redis TLS, 2 Redis TLS ACL, 1 ACL blue/green rotation, 1 RDB, 1 replica manual promotion, 1 Sentinel TLS ACL automatic failover, 106 Vitest, 24 Playwright, 35 Python, and .NET 10/React builds.
The tests verify that healthy/failed/recovered probes emit finite histogram
attributes, cache/write and Data Protection failures are classified by
**phase** only, and a stalled 2-second Redis probe emits
`stage=timeout` while the public HTTP response remains minimal/uncached.
The `Histogram<double>` type and `s` unit are also explicitly tested.

## Immediate durable cleanup after guest logout (#41)

Guest logout previously revoked the Redis session token first, then removed
the player's pre-match lobby memberships. When room cleanup failed after
revocation, the player's old browser cookie could not retry logout, but an
inactive roster entry could persist until the **original 8-hour expiry**.
The existing crash-safe encrypted expiry index (#38) is now also used for
**immediate revoked-session cleanup**.

Outside Development the server performs these operations in order:

1. The trusted guest cookie is authenticated and the Redis session token is
   **revoked** across nodes. A failed revocation must **never enqueue an
   immediate removal**, since the player could still be authorized.
2. Only after revocation, the server schedules a new **already-due encrypted
   item** in `shirokuro:guest-expiry:{default}:v1:pending`. The member
   contains Data Protection-protected UTC timestamp and server-owned guest ID,
   never an IP, cookie, raw identity or invitation in Redis keys/logs.
3. The logout handler attempts the usual synchronous Redis lobby cleanup.
   If it succeeds, best-effort acknowledgement removes the extra immediate
   item; acknowledgement failure is harmless and the worker will replay the
   idempotent cleanup. If it fails, the HTTP response is a minimal **503**
   with the browser cookie **cleared**, and the due item remains for the
   startup/periodic worker of any healthy node to recover within its bounded
   maintenance cadence.
4. If queuing the immediate item fails, the server **still attempts direct
   cleanup**. A successful direct cleanup allows the logout to finish with
   204; if both operations fail, the response is 503 and the already
   scheduled original 8-hour expiration is a weaker backstop. Do not
   claim immediate guaranteed recovery in this double-failure case.

The logout cookie is invalid after revocation even if cleanup later returns
503; a replay cannot authorize, rejoin or perform further mutations. Cleanup
does not extend survivor room TTLs or change other players. Local Development
continues to use the in-memory lobby and expiration registry without Redis.
A due member may be processed by overlapping workers and the normal
8-hour item is retained: administrative CAS removal and ack are idempotent.

This is eventual stale-roster cleanup, **not** transactional atomicity
between the shared session cache and Redis room/index keys; logout and
another already-authenticated in-flight mutation can still race. Hard
atomic cross-key fencing, real Redis TLS/failover, signed-in accounts and
multi-IP/Sybil defenses remain #21.

## Atomic guest revocation fences for lobby mutations (#42)

The API verifies the guest session cookie against the shared Redis session
registry before entering a lobby handler. An **already-authenticated in-flight
request** can have read a valid room snapshot before a second node revokes the
cookie. Rechecking the cookie only at the HTTP handler is not enough to stop
the old request from writing the snapshot after logout.

In non-Development, the server now uses a bounded *mutation fence* in the
same Redis Cluster hash slot as the authoritative lobby snapshot:
`shirokuro:lobby:{rooms}:v2:revoked:<SHA256(server-owned-guest-ID)>`.
The key stores only `1`, expires no later than 8 hours after installation,
and uses `SET NX`, so duplicate fence attempts do **not** slide its TTL.
Neither player IDs, session tokens, IPs, cookie contents nor invite references
are present in the fence key/value or in application logs.

A logout that **successfully revokes** its guest session then installs the
fence **before** scheduling due cleanup and removing lobby memberships.
The expiry worker installs the same fence for the naturally expired session
before acknowledging its encrypted Redis work item; failure retains that item
for a later retry. A fence write failure during logout cannot restore the
already-invalid cookie; even if direct room cleanup succeeds, the request
responds **503** with the browser cookie cleared and a queued due cleanup
item is left for worker retry if available.

The **same Lua script** that performs each actor-owned room create, CAS,
delete or read-heartbeat checks the fence key atomically:
- create (including global/per-creator quota index writes);
- join, choose hero, change team and leave;
- member-only roster read's activity refresh (HTTP and SignalR).
An actor with an installed fence gets the generic hidden-lobby denial, not
an error mentioning a session or Redis. Other guests are not fenced.
Administrative CAS cleanup omits the actor fence so a worker can still remove
a revoked guest without changing survivor room TTLs. A concurrent write that
completed **before** the fence is handled by cleanup. Writes attempted after
the fence are rejected even if they hold an old complete Redis snapshot.

**Limit of the guarantee:** the Redis guest ticket registry is maintained
through a different `IDistributedCache` key space and is not atomically
committed in the same Lua script as the lobby fence. A revocation can succeed
but the fence write can fail; a two-dependency outage may defer cleanup to
the natural 8h expiration. This implementation is a **lobby write barrier
after successful fence installation**, not full cross-store atomic
revocation, and does not certify TLS Redis cluster/Sentinel failover,
observability exporter or durable accounts. Those stay open in #21.

## Actual Production Redis TLS transport contract in CI (#43)

The standard backend job retains its independently tested Redis 7.4
**plaintext fixture** for low-level Lua/CAS, quotas and expiry tests.
A second, entirely ephemeral Redis 7.4 service on port **6380** now exercises
the unmodified **Production** API dependency graph with **TLS-only** enabled
(`--port 0 --tls-port 6380`). The test service is bound only to the CI
runner's loopback interface and is not exposed outside the runner.

Each CI run creates a fresh root CA and a server certificate with SAN
`DNS:localhost,IP:127.0.0.1` using OpenSSL, explicitly verifies the
certificate chain and hostname, and adds **only that temporary CA** to the
runner's system trust store. No private key or certificate is committed,
cached, sent to an artifact, or used outside CI. The container is configured
not to require **client certificates**; that is intentional to test the
server-authenticated TLS transport, not mTLS. No test callback overrides
certificate validation and the backend keeps `ssl=true` with
`sslHost=localhost`. Incorrect hostname certificates and attempts to speak
unencrypted Redis to the TLS port must fail.

The dedicated `RedisTlsProductionTests` suite starts **two independent
Production TestServer API hosts**, with shared encrypted Data Protection
keyring and **real** `IDistributedCache`, `IConnectionMultiplexer`,
rate limiter, lobby snapshot/quotas, encrypted expiry index and revocation
fence. It verifies both nodes' readiness, guest issuance on A and
cross-node authentication on B, room creation/join/roster, logout on B,
denial of replay on A/B, atomic lobby fencing after logout and preservation
of an unaffected player's membership.

This verifies TLS **transport and server authentication in CI**, not a
real deployment. It does not validate cloud/cluster/Sentinel connection
topologies, Redis ACL/password rotation, replication, TLS certificate
rotation, HA/failover, firewall/ingress TLS termination, backup/restore,
or that the deployment is using the same trust and security configuration.
Those are still production acceptance criteria in #21. Never configure
`abortConnect=false`, permissive certificate callbacks or local-memory
fallback merely to make a broken deployment appear available.

## Per-item guest expiry recovery without batch head-of-line blocking (#44)

The Redis guest expiry worker reads a bounded list of up to **128 due,
encrypted work items**. A previous maintenance pass aborted on the first
transient failure (fence, room cleanup or acknowledgement), leaving all
independent items behind it unprocessed until the next 5-minute sweep.
This is a pre-match stale-roster risk under partial failures even when Redis
remains available for unrelated guests.

Each due entry now has its **own** fence → administrative room CAS cleanup →
encrypted member acknowledgement sequence. A failure on one item leaves it
**unacknowledged** for retry by any API node, while subsequent entries in
the same batch can still be cleaned and acknowledged. Once the batch ends,
the worker reports a **single sanitized failure** if any entry failed, so
maintenance is visibly degraded. No player identity, room reference, Redis
key, exception message or invitation is included in the failure. A cancelled
host stops the pass immediately and leaves remaining work persisted.

The aggregate `shirokuro.guest_expiry.sweeps` metric reports one
`outcome=failure` on a partial failure; aggregate
`shirokuro.guest_expiry.memberships_removed` counts **real successful
membership removals even in a partially failed batch** so a retry cannot
inflate the count. No new unbounded metric dimensions are introduced.

The idempotent Redis CAS cleanup still avoids extending survivor room
inactivity TTLs; a retry after a failed acknowledgement can return zero
additional removals while safely acknowledging the pending member.
Development's in-memory sweep is unchanged. Errors in decrypting or reading
the entire due batch still fail closed rather than silently dropping
unverifiable encrypted work. **Limitation:** more than 128 permanently
failing due items (or an unreadable encrypted item in `ReadDueAsync`)
can still delay later entries; operators must repair the failing dependency
or key ring. This is not a complete poison-message/dead-letter solution,
Redis HA/failover certification or durable identity (#21).

## Fair rotating Redis expiry windows and unreadable protected members (#45)

The expiry index previously always read the first **128** due members of
its Redis sorted set and aborted the whole read on an invalid encrypted
payload. Thus 128 permanently unprocessable members, or one member whose
Data Protection key is absent, could indefinitely prevent later, healthy
guest cleanup even after #44's per-item error isolation.

The Redis index now atomically selects up to **128** due members with a
shared **round-robin offset** stored at
`shirokuro:guest-expiry:{default}:v1:scan-offset`, beside the existing
`...:pending` sorted set. One Lua command uses `ZCOUNT` and a bounded
`ZRANGEBYSCORE` window, wrapping to the beginning when needed. It advances
the cursor once for each scanned window, across independently started
nodes. Cursor values contain **only an integer offset**, no guest identity,
session token, room, invitation or encrypted payload. The cursor uses the
same Redis hash tag as the sorted set (Redis Cluster-safe), expires after
24 hours of inactivity and is cleared if no work is due. This does not
change the expiry score, CAS cleanup, 8-hour session validity, remaining
room TTL or batch size.

On a member with failed Data Protection authentication or a decrypted
payload failing strict UTC timestamp/guest-ID validation, the reader
returns an **explicit unreadable marker with no player identity**. The
worker never passes such a marker to the revocation fence, lobby CAS or
acknowledgement. It continues with valid entries and reports a **single
sanitized failed sweep**, so the unreadable work remains durable for
operator restoration of the original keys. Real Redis read/Lua faults
still fail closed, and host cancellation stops processing immediately.

`shirokuro.guest_expiry.unreadable_items` is a monotonic .NET meter
counter **without attributes**, measuring observations of unreadable
members rather than distinct unique records (the same item may be seen
again on later sweeps). `sweeps{outcome=failure}` and aggregate
`memberships_removed` metrics remain available. No public telemetry
endpoint is exposed.

**Limitations:** a rotating numerical offset in a sorted set that changes
through concurrent acknowledgements provides bounded progress over a
stable due population, but does not guarantee strict fairness or an SLA
under continuous writes/removals. Unreadable work is **not discarded**;
lost keys or corrupted records still need operator remediation. A severe
multi-node Redis outage, wrong certificates, missing keyring or sustained
excess work can still delay cleanup. Redis HA/failover deployment, durable
accounts and production alerting stay open in #21.

## Production Redis TLS outage and same-process recovery simulation (#46)

The isolated GitHub Actions Redis TLS fixture from #43 now optionally runs a
**temporary service outage** test. It starts **two independent running
Production TestServers** with the real TLS Redis-backed session cache,
mutation limiter, lobby coordinator, Data Protection key ring, and expiry
worker. The test creates live guest tickets and a shared room before
suspending only the ephemeral `shirokuro-ci-redis-tls` container. The
container is **paused, not removed**, so it retains all keys, session state
and room revisions; the test unpauses it in a `finally` block even if an
assertion fails.

During this induced Redis outage:
- `GET /api/health` remains process liveness (200).
- After the documented 3-second readiness cache expires, both nodes'
  `GET /api/ready` respond with minimal `503 {"status":"unavailable"}`
  and `Cache-Control: no-store`, bounded by the normal 2-second probe.
- Previously signed cookies **cannot authorize a request** while Redis
  cannot validate their server-side token (401); an attempted new guest
  issuance fails closed (503) without falling back to process-local state
  or leaking session identities.
- The TLS trust/hostname validation and strict HTTPS ingress remain
  enforced throughout; no certificate callback or insecure connection is
  introduced.

When the same TLS Redis process resumes, **both API nodes remain running**.
The test polls readiness with a bounded deadline, validates the **same
original cookies** across nodes and confirms that the unchanged shared room
still has its original revision and participants. A subsequent logout must
revoke the original guest token and remove only that player's membership.
This test is explicitly gated by `SHIROKURO_TEST_REDIS_CHAOS=1` in the
dedicated CI TLS job; no production or local environment controls Docker,
and no browser request supplies a Docker command or container name.

**Important scope limit:** pausing and resuming *one* Redis instance verifies
fail-closed behavior during a **temporary outage and reconnect**. It is
not a Redis Sentinel/Cluster failover, leader promotion, persistent
disaster recovery, network partition, load-balancer test, or production SLA
measurement. Replica promotion, TLS high availability, backup/restore,
real certificate rotation and operational monitoring/alerts remain #21.

The frontend CI still executes every Playwright test. GitHub-hosted runners
prefer their installed Google Chrome via Playwright's `channel=chrome` when
available; on custom runners without Chrome, a bounded Chromium dependency
installer retries transient mirror failures. This follows Ubuntu apt mirror
DNS outages observed on CI #141/#142, without skipping E2E tests or reducing
security coverage.

## Encrypted Data Protection key-ring backup and restore drill (#47)

Guest cookies and the protected guest-expiry work items are encrypted and
authenticated by **ASP.NET Core Data Protection** with purpose isolation.
Successful Redis replication **does not** make historical cookies usable if
the Data Protection key ring or the PFX needed to unwrap it is lost. Conversely,
having the correct cookie encryption keys **does not** authorize an old ticket
without its server-side Redis session token. Both authorities must survive
for continuity.

The CI recovery drill tests this contract with a TLS-only Redis server and
**real, independent Production API hosts**:

1. Two nodes sharing an encrypted, persisted key ring create separate guest
   sessions and a shared two-member pre-match lobby; a separate ciphertext
   is protected using the actual `Shirokuro.GuestExpiryIndex.v1` purpose.
2. After both initial API hosts terminate, the drill captures in memory
   the encrypted `key-*.xml` bytes and their wrapping **private-key PFX**.
   The original key-ring directory is deleted.
3. The backup bytes are restored into a **different absolute directory**
   with the same PFX and app discriminator `Shirokuro`; two *fresh* Production
   hosts authenticate the same pre-backup cookies through real Redis TLS,
   preserve the room revision/participants, unprotect the expiry-purpose
   ciphertext, then successfully revoke/logout and remove only the
   logged-out player.
4. Negative controls confirm that restoring the PFX **without the old
   `key-*.xml` files** does not validate old cookies, and that restoring
   old XML keys with an unrelated PFX cannot decrypt the older ciphertext.

The two Production TLS test classes share a **serialized xUnit collection**:
the independent Redis pause/resume test from #46 must not overlap the
keyring backup/restore test, since both use the same ephemeral TLS Redis
fixture. A failing external dependency cannot be silently ignored.

All keys, PFX passwords and guest identities in this drill are freshly
generated at runtime on the runner; the source directory is private to the
test process, no key material is committed or uploaded, and temporary
directories are removed after the test.

**Operator backup/restore requirements (deployment-specific):** take
consistent, access-controlled backups of *all* historical Data Protection
`key-*.xml` files plus **every** PFX private key needed to unwrap them,
including configured `PreviousCertificates`. Keep the certificates and
their passwords in an independent encrypted secrets store, not next to
unencrypted backups, and restrict write permissions to trusted operators and
the service identity. Preserve the `Shirokuro` application discriminator
and the same Data Protection purpose strings. Restore into a protected,
writable shared key-ring volume **before** starting replacement API nodes;
check certificates are valid and old decryptors still configured; test a
sample known-purpose ciphertext in a controlled staging environment. Retain
old keys until every cookie and durable expiry record they protect has
expired or been migrated/processed. Never delete unknown encrypted expiry
members to make the node appear healthy; an unreadable member remains due
for safe operator recovery (#45).

**Important limits:** this exercise preserves Redis as an already-available
authoritative database. It is *not* a Redis RDB/AOF backup-and-restore,
replication, Sentinel/Cluster promotion, rolling certificate rotation
across live nodes, real deployment RPO/RTO validation or production HA
certification. Simultaneous loss of Redis session keys and the key ring
still invalidates active guest sessions and can strand unprocessed cleanup
records until operator repair. These tasks and operational monitoring stay
in #21.

## Redis RDB snapshot loss-and-restore drill (#48)

The Redis TLS test for #43 and the temporary outage test for #46 left
Redis's **stored data** intact. The #47 offline drill separately tested
backup/restore of the encrypted Data Protection XML key ring and its
private-key PFX. The #48 drill covers the other half of multiplayer
recovery: a **consistent Redis RDB snapshot**, including the authoritative
guest-session token cache, lobby snapshots/revision/capacity indexes and
Data Protection-encrypted pending guest-expiry members.

The CI creates a **separate**, private TLS-only Redis 7.4 instance on
`127.0.0.1:6381` so snapshot/destroy/restore cannot disturb the normal
TLS fixture on 6380. It reuses only the ephemeral CI CA and SAN-valid
server certificate; there is no plaintext Redis listener and no bypass
of certificate validation.

The test performs the following controlled sequence against genuine
Production API dependency injection, without replacing Redis services:

1. Two API hosts share the same persisted Data Protection key ring and
   private TLS Redis instance. They issue guest cookies, join a
   two-person lobby and record its exact revision and members. A third
   session is explicitly revoked **before** the snapshot.
2. The test validates that the expiry sorted set contains protected,
   opaque members with no plaintext guest IDs. It runs a synchronous
   Redis `SAVE` and captures `dump.rdb` in a runner-local, temporary,
   private directory. The file must have the RDB `REDIS` signature.
   After the test the fixture and backup file are destroyed, and
   nothing is uploaded as a CI artifact.
3. The original Redis process/data volume is removed. A **new, empty**
   TLS Redis server starts at the same endpoint. Node readiness returns
   `200` (the empty datastore is reachable), but an original signed
   guest cookie is rejected as `401`: Redis's server-side session
   record, not just the signed cookie, is required for authentication.
4. After that disposable instance is removed, the backed-up RDB is
   copied to a **different** writable data volume. A new Redis process
   loads that snapshot. Two newly started Production API hosts accept
   the original *active-at-backup* guest cookies, preserve the room
   revision/participants and the exact encrypted pending expiry members,
   while the cookie revoked before backup remains invalid.
5. An ordinary logout against the restored Redis revokes the active
   ticket, installs the lobby mutation fence, removes only that player's
   membership and leaves the other player authorized.

**Production restoration contract:** Redis RDB/AOF files and backup
copies may contain player identifiers, lobby membership data, hashed
session record keys and other sensitive game state. **Redis persistence
is not an encryption mechanism.** Protect all snapshots in an encrypted,
access-controlled backup store, restrict permissions, encrypt transfer,
avoid writing snapshots to public artifacts/logs, and verify restoration
in a quarantined environment. Record the time/age of the backup, verify
its integrity before restoring, and separately preserve the entire
Data Protection key ring with all required private PFX keys and
`PreviousCertificates` as described in #47. A Redis restore without the
matching cryptographic ring can leave encrypted expiry entries
unreadable, even if room records are intact. Restore Redis before
admitting traffic, validate `/api/ready` and test a known sample
in a restricted staging environment.

**CRITICAL SECURITY LIMITATION: snapshot rollback can resurrect
previously revoked session records and lobby memberships.** This
test proves revocations completed *before* the RDB snapshot survive.
It does **not** prove revocations completed *after* the snapshot remain
effective when older Redis state is restored. During a real incident,
treat older Redis snapshots as potentially replayable, put the service
behind a maintenance gate, and **invalidate all pre-restore guest
sessions** before reopening guest traffic unless an independently
preserved authoritative revocation ledger is available and replayed.
Preserve audit/security evidence and plan explicit logout/re-authentication
rather than silently treating restored cookies as safe. This project
does not yet ship an atomic cross-store global session epoch or a
transactional revocation log, so the forced-invalidation procedure and
its automated tests remain an open requirement in #21.

**Not covered:** Redis Sentinel/Cluster replica promotion, TLS HA under
partitions, simultaneous Data Protection and Redis restore with live
nodes, AOF recovery, cloud snapshot encryption, RPO/RTO or throughput
under a real incident. The isolated CI drill is not production HA
certification; follow-up stays tracked by #21.

The drill is automated in [CI #43040819970](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43040819970), where all six Redis TLS test cases (five existing, one new RDB restore) and the complete backend/frontend/artwork suites passed on `main`.

## Reject restored guest tickets after an older Redis snapshot rollback (#49)

Redis session-token keys may be resurrected by an RDB snapshot taken
**before** an operator or guest revoked them. Even a valid signed cookie
is therefore not automatically safe after an RDB restore. Redis itself
cannot safely store the only invalidation epoch: restoring the same
snapshot would roll back that epoch too.

In non-Development, the API now accepts an optional deployment-managed
setting **`Security:GuestSessions:AcceptIssuedAfterUtc`**. It must be an
**explicit UTC ISO 8601 timestamp**, for example
`2026-11-11T15:00:00Z` or `2026-11-11T15:00:00.0000000+00:00`.
Missing means the legacy session policy applies. A malformed, local-time,
non-UTC or whitespace value **fails application startup** with a generic
configuration error. Development ignores this Production recovery control.

When configured, cookie authentication validates the **Data Protection
signed** ticket's `AuthenticationProperties.IssuedUtc` **before** consulting
the restored Redis guest-session registry. An absent issuance timestamp,
or a time **less than or equal to** the cutoff, invalidates guest
authentication (including legacy guest-shaped tickets) on **every HTTP
request and SignalR authentication**. There is no per-node positive cache,
no query parameter or endpoint for adjusting the cutoff, and no PII or
cutoff value is returned in the HTTP denial. A guest ticket issued **after**
the cutoff still requires the corresponding valid Redis server-side token.

### Controlled stale-snapshot recovery procedure

1. Stop external guest admission and all API nodes behind a maintenance
   gate. Identify the recovered RDB's timestamp and remember that **any
   guest session revoked after that snapshot can be resurrected**.
2. Restore Redis and the matching encrypted Data Protection ring/PFX,
   keeping the service inaccessible to players.
3. Set `Security:GuestSessions:AcceptIssuedAfterUtc` to the trusted
   **post-incident UTC cutover instant**, *not a value derived from the
   restored Redis*, in your deployment configuration/secrets store.
   This value must be **strictly later than** all old guest-ticket issue
   times. Persist it in the recovery runbook and deploy **the identical
   value** to every active and standby API node before opening ingress.
4. Restart/roll **all** API nodes while ingress is closed, verify readiness
   and demonstrate in a restricted staging or guarded smoke test that
   old signed tickets receive 401 while newly issued cookies work across
   nodes. If clocks are not synchronized, correct clock skew before
   admission; a future cutoff can deny new sessions (safe failure).
5. Reopen guest traffic only after confirming all nodes use the same
   cutoff. Changing or **rolling back** this external configuration
   while previously restored session-token keys are alive can re-enable
   old tickets. Never remove the cutoff during their lifetime without a
   verified migration strategy.

The CI RDB drill (#48) explicitly demonstrates that a cookie revoked
**after** the snapshot authenticates again on an unguarded host after
rollback, then rejects that same cookie on two Production API hosts
configured with the **same external cutoff**. A newly issued guest cookie
remains valid across those hosts. These tests run against real TLS Redis
and unchanged production authentication/cache/lobby DI.

**Scope boundary:** the cutoff **does not delete or clean up** stale room
memberships, restored Redis session keys or encrypted expiry queue entries.
Those may remain until a separate authorized cleanup or natural expiry.
It is an **operator-configured authentication barrier**, not an automatic
revocation ledger or cross-store transaction. It does not work if
operators restore an older **configuration** too, or leave unguarded
nodes behind the load balancer. Automatic global recovery fencing and
full post-restore queue/roster reconciliation, secure deployment
coordination, Sentinel/Cluster HA and real RPO/RTO are still open in #21.

## Pre-cutover guest lobby reconciliation from encrypted Redis expiry queue (#50)

The external cookie `AcceptIssuedAfterUtc` cutoff from #49 rejects historical
guest authentication after restoring an older RDB, but *authentication
alone* left the corresponding pre-match roster memberships in Redis until
their original eight-hour absolute session expiration. Those orphaned
participants could consume room capacity and appear in a survivor's
roster even though their cookie no longer works.

With a configured, trusted UTC cutoff, the **Production** Redis guest-expiry
index now expands its *bounded* due window to the later of the current time
and **cutoff + eight hours**, matching Shirokuro's fixed eight-hour
non-sliding guest session lifetime. This advances recovery of naturally
scheduled, Data Protection-encrypted expiry records created on or before
the cutover. The existing Redis Lua shared cursor still returns **at most
128 members per sweep**. Before any cleanup, the index **authenticates
the protected due timestamp and guest identity** and discards from this
pass any payload still scheduled beyond both now and the trusted
recovery ceiling, even if its Redis sorted-set score is manipulated.
No client input, redis-stored cutoff, untrusted roster claim or new
public admin route is introduced.

The existing durable worker then executes **fence → administrative
lobby CAS removal → acknowledgement** for those trusted pre-cutover
items, preserving survivor activity timestamps and the original
room TTL. Independent due items continue despite per-item failures,
while unverifiable encrypted entries stay in the queue for operator
key-ring recovery. A fresh guest created *after* cutover receives an
expiry after `cutoff + 8h` and is not selected for premature cleanup.
Production hosts sharing the same cutoff and Data Protection key ring
can resume pending reconciliation across crashes and restarts.
When the recovery cutoff is **absent**, normal expiry scheduling and
Development's local worker remain unchanged.

**Required deployment precondition:** rotate the external cutoff while
ingress is **closed** and all old issuing nodes are stopped, then start
replacement nodes with identical cutoff, matching Data Protection ring
and Redis state. The guarantee that `expiry <= cutoff + 8h` means
pre-cutover issuance depends on the fixed 8h issuance contract and
having no concurrent issuance across the cutover. Do not enable recovery
while old nodes can continue issuing guest tickets: a ticket registered
before the cutoff but signed just after it might otherwise have an
older natural expiry yet be accepted by another node. Changing the
guest lifetime requires a coordinated migration of this rule and the
recovery tests. System clocks should be synchronized.

**Limitations:** the queue must contain the authenticated natural
expiry record created before cookie issuance; a missing entry, lost
Data Protection key, malformed member or interrupted Redis write
requires operator remediation. This work does not bulk delete the
separately stored old Redis session tokens (they remain until their
original absolute TTL and cannot authenticate due to the cookie cutoff),
remove unrelated unindexed legacy room snapshots or automatically
coordinate deployment maintenance. The operator remains responsible
for verifying all API nodes are guarded before opening ingress.
Automatic full reconciliation, a persistent global epoch independent
of snapshots and real TLS Redis Sentinel/Cluster HA are still #21.

## Targeted deletion of restored pre-cutover Redis session records (#51)

The recovery cutoff (#49) rejects signed cookies issued before the
trusted UTC cutover, and the encrypted expiry queue (#50) fences and
removes their stale pre-match lobby memberships. Until now the
corresponding `IDistributedCache` records could remain in restored Redis
until their natural eight-hour TTL.

For **newly issued** guest sessions, Shirokuro now adds a **one-way
SHA-256 digest of the random server-issued session token** to the
*Data Protection-encrypted* natural expiry work payload. This payload
is authenticated with the same `Shirokuro.GuestExpiryIndex.v1` purpose
used previously. The Redis sorted-set member and its key reveal
neither the raw session token nor its digest nor guest identity.
Legacy encrypted `expiration-ticks:guest-id` work items remain valid;
new members use
`expiration-ticks:guest-id:lowercase-sha256-token-digest`.
A malformed protected digest makes the whole item unreadable, so the
worker never invokes lobby cleanup or cache deletion from it.

On a recovery item whose **authenticated natural expiration** is at
or before the configured `cutover + eight hours` and that contains
a validated digest, the durable worker performs:

1. Guest revocation fence in the lobby Redis hash slot.
2. Idempotent administrative Redis CAS lobby cleanup, preserving
   survivor inactivity timestamps.
3. **Exactly targeted** removal of the associated server-side
   `IDistributedCache` entry, `guest-session:<digest>`,
   through the same Production Redis cache authority, *never* with
   `SCAN`/`KEYS`, a browser-provided ID or any raw token.
4. Acknowledge the opaque encrypted expiry member **only after** all
   preceding operations succeed. Any cache or network failure leaves
   the item durable for retry. The token deletion is idempotent.

A healthy guest issued **after** the cutoff has a natural expiry
after the recovery ceiling and must not be deleted, even if a Redis
sorted-set score is altered. Normal production expiry without a
recovery cutoff keeps its prior semantics: no extra cache removal.
Raw token or digest data is not added to HTTP responses, metric
attributes or logs.

This improvement is **forward-compatible, not a retroactive keyspace
sweep**. Older encrypted work items without a digest still allow
the earlier lobby fencing and cleanup (#50), but cannot prove which
session-cache key belongs to that guest. Such legacy records remain
in Redis until their original absolute TTL, while their old cookies
are denied by the out-of-band cutoff. Unreadable or missing encrypted
work still needs key-ring restoration or an operator-maintained
procedure. The cutover remains a maintenance-gated, consistent
operator decision on every node; this does not provide a universal
epoch, cross-store atomicity, production Sentinel/Cluster HA or
automatic incident recovery (#21).

## TLS Redis replica promotion laboratory (#52)

CI now creates an **isolated two-process Redis 7.4 TLS-only primary/replica
topology** on loopback ports 6382/6383 (not the production or other test
6379/6380/6381 fixtures). Both accept only TLS on port 6380 inside their
containers. The replica connects to the primary using `tls-replication
yes` and validates the CI CA-signed server certificate. The ephemeral
certificate has SANs for localhost and the master Docker DNS name; the
existing incorrect-hostname test remains effective. No certificate trust
bypass, redis password disclosure, plaintext listener or checked-in
private keys are introduced.

An xUnit test uses two independent, **real Production API TestServers**
against the initial primary. They issue two guest cookies, create a
two-player pre-match room, and verify its revision. Before stopping
anything, the test polls the replica to establish that **both signed
sessions' authoritative Redis cache records and the hashed room key**
have been replicated over the live TLS link. This precondition is
necessary because Redis replication is asynchronous: a write accepted
by the primary is not automatically durable on the replica.

The test stops the old master *before* promotion. Both old API nodes,
still configured to the now-unreachable 6382 primary, must report
`/api/ready = 503` after its bounded cache and reject existing guest
cookies (401) without fallback. The isolated replica is then promoted
with `REPLICAOF NO ONE`; `INFO replication` must report `role:master`.
After an **explicit operator-style change of Redis connection string
to localhost:6383**, two *new* Production API hosts must accept the
same original signed cookies via the shared Data Protection key ring
and the promoted Redis state, recover the exact room revision and
membership, and support secure logout/revocation-fence CAS and new
guest issuance. The old primary is never restarted: the test does not
intentionally create split-brain.

CI always destroys the temporary master, replica and dedicated Docker
network. The test controls Docker only when the dedicated
`SHIROKURO_TEST_REDIS_HA_CHAOS=1` flag is present, uses fixed
container names, and has bounded commands/polling. No exposed route
can promote Redis.

**Precisely what this does NOT prove:** there is no Sentinel quorum,
automatic service discovery/reconfiguration of existing client
multiplexers, Cluster routing, leader election, fencing of an old
primary in a network partition, `WAIT`-based synchronous write
durability, RPO=0, RTO/SLA, cloud replication, backup policy, load
balancer switch or production HA certification. Redis replication
does not by itself guarantee a recovered snapshot contains every
accepted write. For operational recovery, use monitored cluster-aware
routing, explicit old-primary fencing, authoritative replication lag
and persistent backups, plus the #49-#51 session-cutover controls
when data loss or rollback is possible. These deployment controls
and true automatic failover remain #21.

Verified by [Main CI #43055949194](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43055949194): both Production backend and frontend builds, 363 standard xUnit, five Redis TLS cases, one RDB rollback case, **one real Redis TLS replica promotion case**, 106 Vitest, 24 Playwright and 35 Python tests succeeded. The CI fixture remains a non-production manual promotion demonstration.

## Redis Production readiness must require a writable primary (#53)

The public `/api/health` endpoint is still **process liveness** and does
not depend on Redis. The bounded, coalesced `/api/ready` endpoint is
**traffic admission** and must distinguish a writable primary from a
reachable, TLS-authenticated but **read-only replica**. A Redis `PING`
and a guest cache `GET` alone cannot establish that new guest sessions,
revocations, lobby CAS and fencing can succeed.

Outside Development, each fresh three-second readiness probe now performs:
- An actual `SET` with a ten-second TTL against the independent lobby
  `IConnectionMultiplexer` connection.
- A `SetAsync` with a ten-second absolute TTL **followed by a GetAsync**
  that verifies the value against the independently configured Microsoft
  `IDistributedCache` guest-session connection.
- The existing Data Protection encrypted key-ring round-trip.

These use **fixed, reserved technical keys** (`shirokuro:readiness:write-probe-v2`
and `shirokuro:readiness-write-probe-v2` after the configured session
cache prefix), a constant non-sensitive value and automatic TTL. No
user identity, cookie, token, IP address or secret ever appears in the
probe or its response. There is no keyspace scan, and the three-second
cached readiness and two-second probe budget avoid a write per HTTP
request. A missing key, failed read, failed write, lost connection or
bad Data Protection ring yields only a `503 {"status":"unavailable"}`
without infrastructure detail.

**TLS replica integration:** the existing #52 real Redis master/replica
CI drill now boots an extra Production API against the *replica while
replication is healthy*. Its `/api/health` remains 200 but its
`/api/ready` must be 503; mere TLS connection and cached reads cannot
produce a false ready. After the original primary has been stopped and
the replica is manually promoted, **two new Production API hosts
explicitly configured for the promoted primary** must report 200
readiness and still verify cookies, lobby state, issuance and revocation.
An already-running Redis multiplexer connected while the node was a
replica **may retain stale role/topology metadata**; neither this
readiness change nor the #52 test claims automatic client role
rediscovery. Until clients are reconfigured/restarted, a conservative
503 is preferable to admitting writes that would fail. A unit test
independently checks that
a guest cache that permits `GET` but rejects `SET` fails readiness
and recovers after writes return.

**Limitations:** a successful technical write verifies the particular
configured endpoints at the instant of the probe, not every Redis
Cluster hash slot, quorum, persistence, replica lag, zero data loss,
split-brain avoidance or Sentinel/Cluster discovery. During a role
transition cached readiness can remain up to three seconds stale:
edge admission must also use normal authorization/mutation
fail-closed paths. Operational HA and automatic failover remain #21.

The writable-readiness drill passed in [Main CI #43060005358](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43060005358), including the real
read-only replica denial before promotion and successful readiness on
two replacement Production hosts after promotion. All 363 standard
xUnit, 5 TLS, 1 RDB, 1 promotion, 106 Vitest, 24 Playwright and 35
Python tests passed, with backend/frontend builds.

## Managed Redis Sentinel TLS topology discovery (#54)

Shirokuro's two independent Production Redis connectors now share
`GuestSessionRedisConfiguration.ReadTlsOptions` for the lobby
`IConnectionMultiplexer` and Microsoft `IDistributedCache` guest sessions.
Both still require **TLS**, valid endpoints and ordinary certificate
validation; the configured connection string or password is never
included in exception messages. The unchanged direct TLS Redis format
continues to work. A Sentinel configuration adds the
**StackExchange.Redis `serviceName`** option:

`sentinel-a:26379,sentinel-b:26379,sentinel-c:26379,serviceName=shirokuro-main,ssl=true,...`

When `serviceName` is present, StackExchange.Redis connects to Sentinel,
discovers the writable Redis primary for that service, and manages the
primary connection across Sentinel `+switch-master` events. Both
the room state multiplexer and the separate Microsoft session cache
use this managed-primary mode without a local database fallback.
Use the appropriate SAN/certificate trust, Sentinel ACL credentials,
and Redis user credentials for the deployment; **do not** disable TLS
validation or embed secrets into source control. An unavailable
Sentinel quorum or unwriteable target fails the #53 readiness probe
and guest operations fail closed until reconnect is successful.

The CI Sentinel drill uses **TLS-only** Redis master and replica
on dedicated loopback 6384/6385 and **three distinct TLS-only Sentinel
processes** on loopback 26380/26381/26382 with quorum 2. To prevent
address translation from confusing service discovery on Linux
GitHub-hosted runners, this fixture uses isolated Docker containers
with host network access bound explicitly to 127.0.0.1 and ephemeral
certificate files generated by the existing TLS test setup.
All five containers and temporary writable Sentinel configs are
deleted in `if: always()`. No cleartext Redis or Sentinel listener
is opened, and no fixture secrets are uploaded or committed.

The test starts **two real Production API hosts**, issues signed guest
sessions and joins a two-member lobby. It verifies that the replica
has actually received both authoritative session tokens and the
room record (Redis replication is asynchronous), **then stops the old
primary**. It never sends a manual `REPLICAOF NO ONE`. Three
Sentinel processes with quorum 2 must elect/promote the replica,
confirmed through `SENTINEL get-master-addr-by-name` and
`INFO replication role:master`. The **same two already-running API
processes**, using their existing managed Sentinel connections, must
recover readiness, recognize the original signed guest cookies,
preserve roster/revision, execute cross-node logout and Lua guest
fence, and issue a new cookie that authenticates on the second node.
All loops and Docker commands are time-bounded; temporary errors
during the election may produce 503/401 but never cause a local
fallback.

**Production boundary:** three Sentinel processes on a single CI
runner are not independent failure domains. The test assumes the
observed session/room keys reached the replica before the outage;
it does not establish RPO=0, exactly-once writes, Redis Cluster
support, protection from an adversarial partition/split brain, mTLS,
Sentinel ACL/key rotation, backups, dedicated host load-balancing,
or guaranteed RTO. Deploy Sentinel processes on separate failure
domains with alerting, fencing and backup/restore plans. Retain the
out-of-band recovery cutoff (#49) for any possible data rollback;
operational validation and true production HA remain #21.

The automated 3-Sentinel/2-Redis TLS drill was verified in [CI main #43063718376](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43063718376):
371 standard xUnit, 5 Production TLS tests, 1 RDB restore, 1 manual
TLS replica promotion, 1 Sentinel automatic promotion and live managed
API reconnect, 106 Vitest, 24 Playwright, 35 Python and both builds
passed. No test bypassed certificate validation or started a plaintext
Sentinel listener.

## Authenticated TLS Redis ACL preflight (#56)

The Production Redis connections for guest sessions and lobby state use a
**single validated TLS connection string**. They support the StackExchange.Redis
`user` and `password` options for a named Redis ACL account. Put these
credentials in an operator-managed secret store, not the repository, log
messages, URL, HTTP headers or frontend:

```sh
Security__GuestSessions__RedisConnectionString="redis.internal.example:6380,ssl=true,user=shirokuro,password=<secret from secret manager>"
```

For CI, #56 creates an **additional isolated Redis TLS-only** process on
127.0.0.1:6386 with the same ephemeral verified CA, a disabled Redis
`default` account, a random per-run masked password and an ACL identity
restricted to Redis keys matching `shirokuro:*`. The CI policy enables
only the connection, scripting, read/write, transactional and pub/sub
command categories needed for the application plus connection diagnostics;
the ACL additionally denies `@dangerous`/`@admin` commands while explicitly granting only the diagnostic commands used by this test (including Lua's `TIME`). Destructive `FLUSHDB` and administrative `CONFIG` are tested as denied. It remains a test configuration, **not a reviewed production privilege matrix**.
Two *real* Production API hosts must use the ACL account across their two
independent Redis clients to issue and validate signed sessions, report
readiness, create/join a shared room and perform logout with Lua revocation
fencing. The test also rejects anonymous and wrong-password connections
and a write outside the authorized key namespace. The ACL file and the
container are deleted with `always()`; the password is generated at
runtime and masked rather than printed or stored as an artifact.

For a real deployment, grant Redis users only the commands and key patterns
they need, rotate credentials and validate a separate operational Sentinel
user when `serviceName` is configured; avoid reusing administrative Redis
identities. Run negative AUTH/ACL tests after a credentials change. Failure
to authorize must never fall back to Development's memory store. The CI
single-node ACL test does **not** certify dedicated production ACL policy,
authenticated Sentinel quorum, dynamic credentials rotation, TLS client
certificate authentication, firewall isolation, security monitoring,
split-brain resistance or RPO/RTO; these remain under #21.

The scoped Redis ACL (including denial of keyless administrative commands) and real Production two-node session/lobby integration passed in [CI main #43069457198](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43069457198) SUCCESS: 375 xUnit standard, 2 real Redis TLS ACL xUnit, 5 Production TLS xUnit, 1 RDB, 1 replica promotion, 1 automatic Sentinel failover, 106 Vitest, 24 Playwright, 35 Python and .NET/React builds. This does not replace a deployment-specific access-control review.

## Authenticated TLS Sentinel quorum and distinct ACL identities (issue #57)

For a Production Sentinel deployment, the validated shared connection string supports **different** credentials for the Redis primary/replica data plane (`user`, `password`) and the Sentinel discovery plane (`sentinelUser`, `sentinelPassword`). Both independent client connectors (guest-session `IDistributedCache` and lobby multiplexer) use the same validated configuration, with TLS certificate verification and no memory fallback. Supply every secret from an external secrets manager, never source control:

```sh
Security__GuestSessions__RedisConnectionString="sentinel-a:26379,sentinel-b:26379,sentinel-c:26379,serviceName=shirokuro-main,ssl=true,sslHost=redis.internal.example,user=game-app,password=<redis-app-secret>,sentinelUser=sentinel-discovery,sentinelPassword=<sentinel-client-secret>"
```

An operator must provision **separate** ACL identities for: the game API's keyspace-scoped Redis operations; replica authentication to the old/new master (`masteruser`/`masterauth`); Sentinel-to-Redis monitoring/reconfiguration (`sentinel auth-user`/`sentinel auth-pass`); Sentinel-to-Sentinel peer elections (`sentinel sentinel-user`/`sentinel sentinel-pass`); and the game's minimally permitted Sentinel discovery client. These principals have **different credentials**, with `default off` on every Redis and Sentinel instance. The peer Sentinel principal is more privileged than the discovery client and must not be made available to the application. Key patterns do not restrict keyless commands: deny destructive/admin commands for the game API and test those denials.

The CI fixture is five TLS-only containers on the same runner, bound to loopback; the CA is ephemeral and verified. ACL files, 5 independently generated and masked passwords, and writable Sentinel configs live in temporary runner directories and are destroyed by `always()`. Two original API Production TestServers keep their cookies and lobby revision across a **real automatic** quorum failover with no forced `REPLICAOF NO ONE`; tests also check invalid/missing authentication and Redis ACL denial. This is a controlled regression test, **not** evidence of separate failure domains, cert/credential rotation, network firewall policy, RPO=0, split-brain protection, mTLS, complete least privilege or a production monitoring system. All these remain in #21.

The two Production connectors now pin StackExchange.Redis **3.4.31**, which supports independent Sentinel and Redis ACL credentials (introduced in upstream 3.1.13). The CI security probe permits admin commands only in a deliberately isolated test client to prove that **Redis ACL itself** rejects destructive operations; real API connections keep the library admin switch disabled. The Sentinel 7.4 ACL deliberately omits unsupported `ECHO` even though ordinary Redis connections require it. [CI main #43073747222](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43073747222) SUCCESS: 376 xUnit standard, 5 real Redis TLS Production, 2 ACL TLS xUnit, 1 RDB recovery, 1 manual promotion, 1 automatic authenticated Sentinel quorum failover, 106 Vitest, 24 Playwright, 35 Python and .NET 10/React builds. No deployment-level HA guarantee is implied.

## Redis TLS ACL blue/green credential rotation (issue #58)

Redis ACL user disable is **not a live connection revocation mechanism**:
`ACL SETUSER shirokuro-blue off` denies NEW `AUTH`, but clients that
were already authenticated as `shirokuro-blue` retain their permissions
until their connections are closed. An operator must intentionally drain
old API hosts **and** execute `CLIENT KILL USER shirokuro-blue` (or
`ACL DELUSER shirokuro-blue`, with its stronger destructive implications)
on every relevant Redis node. Removing just the password or setting
`off` is not equivalent to a completed rotation.

Recommended ordered cutover for direct Redis TLS (operator-run, maintenance
procedures and controls required):

1. Confirm a recoverable Redis backup and external deployment settings,
   stable Data Protection key ring/discriminator, identical Redis key
   namespace, TLS trust chain, and current readiness. Keep old user
   credentials available only in a secrets manager during the overlap.
2. Use a privileged operator identity in a private control plane to
   create a NEW scoped app user/password using `ACL SETUSER`; preserve
   the *same* `shirokuro:*` key access and required connection/Lua
   commands, but deny admin/destructive operations. Never expose this
   operator identity in application configuration.
3. Launch **new** API instances with a new TLS Redis connection string
   using the new username/password, sharing the existing Redis data and
   Data Protection ring. Keep both old and new users active during the
   overlap. Wait for green `/api/ready`, then verify an existing cookie
   and roster/revision across green nodes, including ability to write.
   The running app does **not** hot-reload its Redis multiplexer secrets.
4. Shift ingress traffic to green and drain/shut down the old blue API
   instances. Do not delete or rename session/lobby keys as a shortcut:
   session authority and logout fencing must survive the credential cutover.
5. Disable old logins with `ACL SETUSER shirokuro-blue off`. After
   confirming all intended healthy green hosts are connected and old
   hosts should not serve traffic, run `CLIENT KILL USER shirokuro-blue`
   on each Redis primary/replica that can retain old connections.
   Verify old credentials cannot AUTH and old connections are gone.
   Old surviving API processes must return unready/deny user auth,
   not silently authenticate from local cache or fall back to in-memory.
6. Reverify green readiness, cross-node guest cookies, lobby roster and
   logout/fencing before declaring the rotation complete. Alert on failed
   credential cutovers. Persist the intended ACL configuration through
   the deployment's secure configuration mechanism; a transient runtime
   `ACL SETUSER` without ACL persistence will not survive all restarts.

For example, use `redis-cli --tls --cacert <trusted-ca> --user
<operator-user>` with secrets supplied securely to `REDISCLI_AUTH`
instead of putting passwords directly on the command line. Do not copy
the CI password or operator ACL to production. A separate staged plan is
required for Sentinel-to-Redis monitor, Sentinel peer and Sentinel
discovery credential rotation; this #58 lab covers **direct TLS Redis**
only. The fixture uses an isolated loopback-only Redis 7.4 process and
three independent, masked runtime passwords, with the `default` account
disabled, all files and containers cleaned by `always()`. It exercises
two blue and two green **real Production API TestServers** with independent
lobby and session-cache Redis connections, ACL-off versus retained old
connections, explicit eviction, rejection of old cookies/readiness,
and successful green logout/fence/new issuance. It does not certify
hot rotation, RPO=0, split brain, distributed keyring changes, managed
Redis service ACL semantics or real failure-domain HA; these remain #21.

The blue/green direct Redis TLS credential-cutover integration passed in [Main CI #43079718090](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43079718090) SUCCESS: 377 standard xUnit, 5 Production Redis TLS, 2 Redis TLS ACL, **1 new Redis TLS ACL credential rotation integration**, 1 RDB recovery, 1 manual replica promotion, 1 automatic TLS Sentinel ACL failover, 106 Vitest, 24 Playwright, 35 Python, .NET/React builds. The test distinguishes ACL-off (new AUTH denied while existing connections still work) from the operator's explicit `CLIENT KILL USER`, after which blue Production hosts fail readiness/authentication and green remains fully functional. Real rolling deployment still requires operator-controlled ingress draining, rotation procedures and Redis ACL persistence; this does not test live Sentinel credential refresh.

## Optional private OTLP metrics export (#60)

Since #59 the API reports aggregate readiness latency, rate-limit outcomes
and expiry cleanup with **closed, low-cardinality labels**. Outside
Development, operators may explicitly send **only**
`Shirokuro.Api.Multiplayer` to a private collector. Export is disabled by
default, and it does not enable logs, traces, automatic URL instrumentation or
any incoming metrics endpoint.

Configure the deployment using its external secrets/configuration manager:

```sh
Security__Monitoring__OtlpMetricsEnabled=true
Security__Monitoring__OtlpMetricsEndpoint=https://otlp.internal.example:4318/v1/metrics
```

The **complete** signal-specific path `/v1/metrics` is mandatory. Only
absolute `https://` URLs without embedded usernames/passwords, query
strings, fragments, extra paths or whitespace pass startup validation.
Missing, malformed or HTTP endpoints fail application startup with a fixed
non-sensitive message when enabled; false/absent flags register no exporter.
Development never registers this exporter. The SDK uses its standard
certificate and hostname verification; no allow-untrusted CA option,
redirecting `HttpClient` override or plaintext fallback is introduced.
Operator-provisioned internal CA certificates must be trusted through the
deployment operating system trust store. Package versions are pinned to
OpenTelemetry.Extensions.Hosting and
OpenTelemetry.Exporter.OpenTelemetryProtocol **1.20.1**.

The exporter uses the **OTLP HTTP/protobuf** protocol on HTTPS, with a
bounded 5-second transport timeout and background metric collection. It
registers only the application's explicit meter, not HTTP route or
identity instrumentation. It sends no application bearer token/header by
default. Deploy a separately authenticated/isolated collector behind
private-network policy and trusted TLS termination; **do not** expose the
collector to the browser or public Internet. This change does not configure
mTLS client certificates, proxy auth or API-key headers. Those protections
need a dedicated reviewed deployment design, not embedding secrets in
endpoint URIs or query strings.

The telemetry transport is **not** used to decide Redis/session/lobby
readiness or guest authorization. During collector outage, metric export
may drop or retry batches, but must never be interpreted as a successful
failover/monitoring guarantee; redis-authoritative gameplay continues to
use its existing fail-closed behavior. An operator should validate separately
that the collector receives the finite labels in
`shirokuro.readiness.probes`, `shirokuro.readiness.duration`,
`shirokuro.guest_mutation.decisions` and the expiry aggregates, then build
per-service alerts for sustained unavailable ratios, p95/p99 latency,
throttling and failed expiry sweeps. No production collector, receiver,
dashboard, alert or pager is deployed by the repository.

The xUnit test suite checks disabled-by-default/Development registration,
strict HTTPS URL validation and error-message redaction, and actual
OTLP protobuf payload generation to a **fake in-memory HTTPS transport**
without network access or relaxed certificate checks in the Production
implementation. Its transport fixture is **not** proof of real TLS trust,
network segmentation, collector availability, telemetry persistence or
alert delivery; these and Redis/Sentinel replication metrics remain #21.

The #60 OTLP metrics integration and #61 per-ref CI concurrency passed in [CI implementation #43119051733](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43119051733) SUCCESS on `5231016`: **397 xUnit standard**, 5 Production Redis TLS, 2 Redis TLS ACL, 1 ACL blue/green rotation, 1 RDB restore, 1 replica manual promotion, 1 automatic Sentinel TLS ACL failover, **106 Vitest, 24 Playwright, 35 Python tests**, .NET 10 and React builds. The CI revealed that .NET OTLP metric export can use `HttpClient.Send` (synchronous) during ForceFlush; the CI-only fake collector now supports both `Send` and `SendAsync` without changing Production transport. Additional integration tests verify collector HTTP 503 cannot prevent **guest sign-in, lobby creation, active cookie lookup, logout and revocation**, and that OTLP requests do not carry application guest cookies or authorization headers. The collector remains privately configured and disabled by default. No real exporter credentials, production receiver or alert system were provisioned by the test.

## Redis client lifecycle events for operational triage (#62)

The optional OTLP-backed meter `Shirokuro.Api.Multiplayer` now also publishes
`Counter<long> shirokuro.redis.connection.events`. It counts StackExchange.Redis
connection notifications for the two **independent** Production multiplexers,
without emitting exception text or connection identifiers.

| Bounded label | Values | Interpretation |
| --- | --- | --- |
| `client` | `lobby`, `session` | The server-side lobby Redis connector or the separate guest-session cache connector. Not a room, player or network address. |
| `event` | `failed`, `restored`, `configuration-changed` | Physical connection failure, connection restoration, or client reconfiguration notification from StackExchange.Redis. |

The lobby multiplexer has event subscriptions immediately after being
constructed; the ASP.NET Core Redis cache uses
`RedisCacheOptions.ConnectionMultiplexerFactory` to observe its own
lazy, independent multiplexer while preserving the same validated TLS
and optional Sentinel settings. Later test/deployment
`RedisCacheOptions.ConfigurationOptions` overrides are honored at actual
connection time; this wiring does not share the lobby mux or configure
an in-memory fallback.

These **are client notifications**, not Redis server exporter signals,
per-host availability gauges, a trusted master election count or
durability acknowledgements. An outage or Sentinel primary switch can
generate multiple events per connector or may not generate one restored
event for the old endpoint; do not build exact-once logic or RTO/RPO
guarantees from these counters. Combine their rate changes with
`shirokuro.readiness.probes`, `shirokuro.readiness.duration`,
Redis server role/lag metrics from an independently secured exporter
and independently validated alerts. The public `/api/ready` and
`/api/health` routes remain as before, with **no** `/metrics` ingress
exposure. Collector and alert deployment, network partition tests,
durable account authentication and robust failure domains remain #21.

The Redis client lifecycle integration passed in [CI main #43121286726](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43121286726) SUCCESS: **397 xUnit standard**, **5 Production Redis TLS**, **2 Redis TLS ACL**, **1 blue/green ACL rotation**, **1 RDB restore**, **1 manual replica promotion**, **1 real TLS Sentinel ACL automatic failover** with both Redis clients' telemetry observed, **106 Vitest, 24 Playwright, 35 Python**, .NET 10 + React builds. The
existing two Production API processes remain alive throughout the
authenticated Sentinel failover while both lobby and guest-cache
Redis clients emit at least one finite-label connection event; cookie,
roster, revision, logout fence and new issuance continue across the
auto-promotion. This is **client event observability**, not a
telemetry alert receiver or certification of split-brain safety or RPO=0.

## Private Redis INFO replication telemetry, separate observer ACL (#63)

Application read/write connectors **must not** enable StackExchange.Redis
`AllowAdmin`: the .NET client rejects its `INFO` command in ordinary
mode, even though Redis ACL permits the read-only `INFO replication`.
Generic `ExecuteAsync` does **not** bypass this client guard. The
production-safe solution is an **independent Redis monitoring connection**
with a separately provisioned, intentionally read-only Redis ACL user,
not a relaxation of the game/lobby/guest-session identities.

The read-only poll is completely **OFF by default**, and always OFF in
Development. To opt into it outside Development, configure using secure
external deployment settings (never check in real credentials):

```sh
Security__Monitoring__RedisReplicationEnabled=true
Security__Monitoring__RedisReplicationConnectionString='redis.internal.example:6380,ssl=true,sslHost=redis.internal.example,user=shirokuro-observer,password=<secret-manager-value>'
```

For a Sentinel deployment use the same `serviceName` as the game API,
supply the observer's **distinct** Redis username/password, and include
**explicit** `sentinelUser` / `sentinelPassword` for discovery; the
library's implicit fallback to Redis application credentials is rejected.
The observer endpoint must be TLS with a trusted CA and valid hostname.
Startup rejects missing, invalid, duplicate game credentials, the
`default` user, non-TLS endpoints, supplied `allowAdmin=true` and
mismatched Sentinel service names, using a **constant, sanitized error**.
The app does not create any listener or open `/metrics` from this flag.

Provision the Redis server ACL *separately* from the game ACL: disable the
default account, allow the observer only `INFO` plus the minimum
connection-handshake commands required by StackExchange.Redis
(`AUTH`, `HELLO`, `PING`, `ECHO`, `COMMAND`, limited
`CLIENT SETNAME/SETINFO`), no access to `shirokuro:*` game keys and
no `GET`, `SET`, `CONFIG`, `FLUSHDB` or other destructive/admin
permissions. Use a distinct vaulted password and restrict network access
to the API/collector trusted zones. The only `AllowAdmin=true` setting
is on this internally owned monitor multiplexer, because the Redis
client uses it to permit `INFO`; **Redis server ACL remains the
authoritative security boundary**. No monitoring multiplexer is registered
for lobby/guest application DI, and it is disposed with its sampler.

Every 30 seconds the observer executes **only** `INFO replication`
on a connected Redis server from its managed topology, with a two-second
client wait budget. Network, Redis ACL or parsing errors produce bounded
failure observations; they never block or alter cookie validation, lobby
mutations, liveness or the existing fail-closed readiness. The first poll
runs after host startup, not synchronously on the request path.

The pre-existing opt-in OTLP meter `Shirokuro.Api.Multiplayer` adds:

| Instrument | Kind/unit | Bounded attributes | Interpretation |
| --- | --- | --- | --- |
| `shirokuro.redis.replication.observations` | Counter / observation | `outcome=ok\|timeout\|invalid\|unavailable`, `role=master\|replica\|unknown`, `link=up\|down\|na` | Each attempted INFO read, including failure. A missing/failed sample **is not** a healthy zero-replica result. |
| `shirokuro.redis.replication.connected_replicas` | Histogram / replica | No dynamic attributes | `connected_slaves` observed on a successfully sampled primary. Counts are samples, **not a current-state gauge**; no sample is recorded on a failed poll or a replica. |
| `shirokuro.redis.replication.replica_ack_lag_seconds` | Histogram / seconds | No dynamic attributes | Maximum reported `lag` across `slaveN` rows whose state is `online`. No lag sample when no online replica exists. |

Redis `INFO` includes server addresses, ports, replication IDs and even
other potentially identifying strings. The bounded parser consumes only
`role`, `connected_slaves`, `master_link_status` and the numeric
`lag` of `slaveN` entries that are online. It rejects oversized
payloads, malformed counts and invalid role/link without echoing raw
Redis content. The primary's `slaveN` rows must use **canonical, unique decimal
indices** (no aliases such as `slave00`) and match the ordered `slave0`
through `slaveN-1` slots reported by `connected_slaves:N`. Gaps are
invalid INFO on a primary. Redis 7.4 also reports `connected_slaves:0`
on a **replica**: that zero value (or an absent field) is valid there and
must NOT publish a replica-count histogram on a replica. A positive
`connected_slaves` or any `slaveN` row on a replica is invalid.
This behavior was confirmed by the TLS promotion test's privacy-safe
diagnostic after CI caught a false-positive parser rejection (#108).
Only known `online`, `wait_bgsave` and `send_bulk` states are accepted;
only `online` contributes to the maximum ACK lag. Replica-role INFO
must not claim a positive dependent-replica count or `slaveN` rows. On inconsistency
the observer emits a fixed `outcome=invalid` sample and no replica count or
ACK lag: an invalid sample is **not** a healthy zero-lag observation (#108).
**Reported ACK lag is time since replica acknowledgment**;
it is NOT replica byte-offset lag, not an end-to-end data-consistency
proof, and cannot establish RPO=0 or failover RTO/SLA. Use independent,
least-privilege Redis server exporter metrics, Sentinel quorum alerting,
replication-offset/ID comparisons and backups for operational HA under
[#21](https://github.com/KeyserDSoze/Game.Shirokuro/issues/21). An
OTLP collector/dashboard/alert receiver is **not deployed** by this code.

Verified in [CI code #43127985070](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43127985070) SUCCESS on main `27530fa`: 434 standard xUnit, 6 real Production Redis TLS, 4 scoped Redis TLS ACL, 1 ACL blue/green rotation, 1 RDB recovery, 1 manual replica promotion, 1 Sentinel automatic authenticated TLS failover, 106 Vitest, 24 Playwright, 35 Python, and .NET 10/React builds. xUnit exercises strict TLS identity separation,
real Redis 7.4 INFO on a dedicated ACL observer, denial of game-key
reads/writes and FLUSHDB/CONFIG even when the client-side INFO guard is
enabled, a real Production host with opt-in worker, standalone and
master/replica/promotion parsing, strict finite metric labels and fake
HTTPS OTLP protobuf payload with no replica IP in telemetry.

## Gameplay Redis TLS and client admin-mode guard (#64)

Both Production multiplayer Redis connectors (lobby and the independent
guest-session `IDistributedCache`) always require TLS and
`AllowAdmin=false`. A configured `allowAdmin=true` is rejected at
startup, even when a Redis server ACL also denies admin commands.
The rule applies equally to direct Redis TLS and Sentinel discovery.
Administrative client operations such as `INFO` are reserved for the
**separate, opt-in, server-ACL-scoped observer** of #63, never the app.

Microsoft's Redis cache can receive an operator/test DI override of
`RedisCacheOptions.ConfigurationOptions` after initial registration.
The lazy `ConnectionMultiplexerFactory` therefore rechecks **effective
options** before opening the socket: disabled SSL, empty endpoints or
`AllowAdmin=true` are refused with a fixed sanitized error. A properly
configured TLS override still works and is not silently replaced with
the startup connection. Keep server Redis ACL restrictions (including
`-@admin`) independently: disabling StackExchange.Redis client admin
mode **does not** confer server-side least privilege. Existing real
Redis TLS ACL/Sentinel/rotation/recovery CI exercises the valid path;
new xUnit regressions reject unsafe startup and late overrides without
requiring a network connection. Production edge policies and HA/SLA
remain under #21.

## Bounded distributed guest mutation admission (#65)

The shared Redis Lua rate limiter for guest session issuance (**12 per minute
per trusted client IP**) and room creation (**8 per minute per trusted
client IP**) now bounds the **client wait** to 2 seconds, combined with the
actual HTTP request's cancellation token. On timeout/Redis outage, a
still-connected guest receives a sanitized `503` with `Cache-Control:
no-store` and **no guest cookie or new lobby**. If the HTTP client has
disconnected, its cancellation stops the server wait instead of inventing
a successful decision. No local rate-limit allowance, optimistic reply,
or automatic retry exists.

Redis Lua still serializes the counter update across nodes. Cancelling the
.NET await **does not necessarily interrupt a Lua script already sent to
Redis**: a request that got 503 may still consume one quota permit. Clients
must not assume an unconsumed permit and operators should not infer exact
successful issuance counts from admission counters. Retrying automatically
would risk double-counting and is deliberately avoided. This bounded
admission check is separate from downstream guest/session/lobby Redis
availability, from the read-only observer (#63), and from the real-world
network HA/abuse controls still open in #21.

## IPv6 /64 anti-abuse admission partitions (#95)

The shared non-Development Redis guest session-issuance (12/min) and room
creation (8/min) limiters now charge **native IPv6 addresses by their /64
network prefix**, rather than by the full 128-bit address. Rotating the
interface identifier inside one /64 therefore cannot obtain new permits.
Different /64 prefixes retain separate budgets; the two endpoint policies
remain separate. The Redis key contains only SHA-256 of the binary network
prefix, never raw IPv6. Native IPv4, IPv4-mapped IPv6, and unknown-address
partitioning remain byte-for-byte unchanged; the existing key namespace is
kept for backwards compatibility with IPv4 quotas.

This change affects only the Redis admission limiter in Production/Staging.
The trusted proxy list and forwarded-header sanitization still govern the
remote IP; client-provided XFF from an untrusted peer cannot choose a quota
partition. During deployment, **roll all API nodes to the same version
together**: older nodes hash full IPv6 addresses and newer nodes hash /64,
temporarily yielding separate budgets until the fleet converges. Monitor
aggregate 429/503 admission metrics and adapt edge controls separately.

A /64 is **not** a verified person, home, device or abuse-proof identity.
It can group unrelated users (e.g., shared networks) and cannot stop an
attacker who owns many IPv4 addresses, multiple IPv6 /64 prefixes or a
distributed network. Keep WAF/edge controls, proxy configuration, detection
and account-grade identity policy under the open umbrella issue #21.

## Byte-bounded administrative lobby index enumeration (#98)

The Production/Staging administrative `capacity:all` Redis index is
persisted data, not permission to read arbitrary room snapshots. Earlier
safeguards (#93/#94) limited room-key **grammar and total count** (at most
256 indexed rooms plus one overflow sentinel). A compromised/stale sorted
set could still contain a single enormous member, causing an excessive
network reply during expiry cleanup or explicit guest logout.

The index scanner now uses a single **read-only Lua ZRANGE** (ranks 0..256)
that substitutes an empty invalid sentinel for any member longer than the
**exact namespace-specific room-key byte length** *before returning it*
to the API. It never deletes or modifies the original key, score or TTL.
The API still refuses the entire result if it exceeds 256 members or
contains any invalid/null/oversized key, and it now validates **all keys
before any room GET**. No partially-read index may result in guest-expiry
ACK, roster mutation, or false cleanup success.

A damaged index is an availability incident: the API/worker fails closed
with a sanitized error, leaving encrypted expiry work for retry. Operators
must inspect Redis integrity, determine which original sorted-set members
are legitimate, and reconcile/remove corrupt entries under an authorized
runbook. Never log raw keys, hashes, invitations or room snapshots.
Bounded responses mitigate network and API allocation pressure; they do
not remove oversized values from Redis memory or replace edge HA and
monitoring/alerting requirements in parent issue #21.

## Size-bounded encrypted guest expiry work (#97)

The Redis guest-expiry queue rotates through at most 128 due items per
maintenance pass. Each item now additionally has a **4096-byte bound** on
its encrypted Data Protection Base64Url member. Server issuance rejects
oversized output before the Redis write, rather than claiming to have
scheduled a work item that the worker could not later safely read.

Redis's selection Lua script replaces an oversized persisted/legacy ZSET
member with an **empty unreadable sentinel before returning the batch**.
This bounds the transmitted response size even when the stored member is
huge. The script does not delete, truncate or change the original member
or score; round-robin fairness and processing of other healthy items in
the same batch remain intact. C# never attempts to decrypt or acknowledge
the marker. The background worker reports degraded maintenance and records
unreadable metrics, but can still fence, clean and acknowledge healthy
entries independently.

A permanently damaged member stays durably queued and requires authorized
operational investigation: verify/restore Data Protection key rings and
inspect Redis integrity, then reconcile or delete the original affected
entry only under an approved incident runbook. Never log, echo or export
raw ciphertext, guest identities, session tokens or invitation data.
This limit constrains network and worker allocations, not Redis's storage
capacity or the need for edge controls and observability tracked under #21.

## Corrupt Redis guest admission buckets fail closed (#96)

An existing Redis quota bucket is persisted input, not trusted merely because
its key has the expected SHA-256 name. The Production/Staging limiter now
checks inside its atomic Lua script that the stored counter is a **canonical
positive decimal integer within the policy limit** and its TTL is **strictly
positive and at most 60 seconds**. Negative, zero, fractional, noncanonical,
nonnumeric, over-limit, never-expiring and anomalously extended counters
cannot grant new guest sessions or room creations. Missing keys still create
fresh 60-second windows; healthy counters still yield normal increments or
429 without sliding the expiry. This does not change the IPv4/IPv6 /64 key
derivation, per-policy quota or trusted proxy configuration.

A corrupted bucket returns an **opaque 503/no-store** and no new cookie or
lobby. It is **not repaired, deleted or renewed automatically**: treating
damage as a reset could award fresh quota to an abusive source. Operators
should investigate Redis integrity/health and reconcile or delete only the
affected hashed bucket using access-controlled incident procedures, or let a
finite TTL expire. Do not log or disclose raw counter values, remote IPs,
session IDs or hashed Redis keys. One damaged shared IPv6 /64 or IPv4 NAT
bucket may temporarily deny legitimate guests; alerting and manual repair
belong to operational policy. External HA, multi-network Sybil defenses and
edge anti-bot protections remain open in #21.

## Sanitized Data Protection PFX startup failures (#66)

Production and Staging Data Protection keys still require an operator-managed
persistent key directory and a private-key-bearing PFX. A failed PFX load
(wrong password, missing access, corrupt file) now raises a **fixed**
`InvalidOperationException` with **no inner exception**. This applies to
both the active encryption certificate and every configured historical
decryption certificate. Startup or logging frameworks that serialize
`Exception.ToString()` therefore do not inherit exception messages from
cryptographic or filesystem loaders that might contain paths or operator
configuration. Do not log the original PFX error as a workaround.

This does **not** relax the existing path, certificate-validity, private
key, maximum historical certificate count, encrypted key ring or
multi-instance portability requirements. A failure is still a startup
failure; it never silently falls back to plaintext or ephemeral keys.
Keep the directory and PFX permissions, secure distribution, certificate
rotation, secure alerting and real disaster-recovery validation under
operator control (#21). New xUnit regressions exercise wrong passwords
and corrupted PFX on **both** active and previous-certificate paths,
with fixed error message, no `InnerException` and no secret path/password
in `ToString()`. Valid key-ring rotation and portability tests are
unchanged.

## Early CSRF admission guard for guest and lobby budgets (#67)

The two known, server-marked `GuestMutationRateLimit` POST routes
(`/api/sessions/guest` and `/api/lobbies`) check the canonical Origin
allowlist and `X-Shirokuro-Request: 1` **after authentication/authorization
but before Redis Lua admission or the local Development limiter**. Invalid
intents are rejected with HTTP `403`, `Cache-Control: no-store`, no
`Retry-After` and no cookie, **without decrementing any rate-limit budget**.
The original endpoint-level CSRF checks remain as independent defense in
depth. Middleware selects the routes by their trusted metadata, never by
comparing a client-provided path/case/query.

Previously even a rejected cross-origin POST could consume one of the
12 guest issuance/minute or 8 room creation/minute per-IP slots, exhausting
the shared NAT client's allowance or turning a 403 into a 429/503 if Redis
was slow. After this fix, **authorized** intents still go through the
unchanged Redis-based atomically shared quota (and its 2s fail-closed
admission timeout from #65). The Development budget also no longer counts
invalid-origin guest attempts; 18 rejected submissions cannot exhaust its
12/minute local threshold. xUnit TestServer regressions cover both
Production routes, unavailable limiter, room membership, and Development.

This is not a general anti-bot barrier: an automated client can forge
its own Origin and request intent header, and cookie authentication may
still need a shared session lookup *before* this middleware. Edge-layer
traffic controls, identity abuse and deployment-grade observability remain
open in #21.

## Bounded encrypted guest expiry/revocation queue operations (#68)

Outside Development, guest session issuance and revocation depend on an
**encrypted, Redis-shared due-work index** so membership cleanup survives
issuer crashes and works across API instances. Previously the index checked
its CancellationToken only at method entry; a queued Redis `ZADD`,
`ScriptEvaluate` rotating read or `ZREM` acknowledgement could hold a
request/maintenance sweep after host shutdown or client disconnect.

Every index Redis command now has a **2-second bounded client wait** linked
to its caller's cancellation token: natural issuance `ScheduleAsync`,
revocation `ScheduleRevokedAsync`, batch `ReadDueAsync`, and
`AcknowledgeAsync`. Already-cancelled calls do not send a command. A
stalled write never reports a confirmed durable work item, so normal guest
issuance continues to **fail closed** (no cookie) and its existing rollback
path applies. A stalled read cannot authorize/fabricate a cleanup item.
An uncertain acknowledgement is not considered successful; the worker
reports a retryable sweep and can re-run idempotent fencing/lobby cleanup
later. No automatic immediate write retries are introduced.

As with the bounded admission limiter (#65), interrupting the .NET await
**does not cancel a Redis command already sent to the server**. A timed-out
`ZADD` may eventually persist an orphan protected work item; a timed-out
`ZREM` may still remove an item. Neither outcome authorizes a guest or
exposes its identity. The existing encrypted payload, durable replay,
rotation and independent cleanup tests remain required. This is not an
operational HA/queue-lag SLA or proof that every Redis outage is 2 seconds
long; real monitoring/alerting, distributed identity and deployment remain
in parent #21.

## Bounded Redis guest session authority operations (#69)

In Production/Staging, the **independent** Redis `IDistributedCache` is
still the sole authority for browser guest cookies, subsequent HTTP requests
and SignalR roster calls. All shared session operations now bound the
**client-side wait per Redis command to 2 seconds**, linked to the original
request/worker cancellation token: issuing a token (`SetAsync`), validating
a session (`GetAsync`), revoking it (`GetAsync` then `RemoveAsync`), and
deleting a trusted restored token digest during disaster recovery
(`RemoveAsync`). A cancelled request does not start a new cache command.

An **unconfirmed SET cannot issue a guest cookie** or mark a local session
active; the existing endpoint returns sanitized `503` without `Set-Cookie`.
If Redis cannot confirm the guest ticket on read, protected HTTP and
SignalR authorization **fail closed** (no cached positive permissions).
If either shared-cache revocation step fails/times out, guest logout now
returns sanitized `503` instead of leaking a raw `500` or incorrectly
claiming a confirmed logout with HTTP `204`. It does not clear the cookie
until a revocation has actually completed, and it cannot re-grant access to
an already deleted ticket. Existing cross-node session revocation, fenced
lobby cleanup and encrypted expiry-index replay remain separate guarantees.

**Crucial limitation:** abandoning the .NET await does not cancel a Redis
command already delivered. A timed-out SET may later persist an orphan
entry with its fixed TTL, but the application did not issue the secret
cookie. A timed-out removal may complete asynchronously; retrying logout
may then encounter an already revoked/unauthorized cookie. No optimistic
fallback, immediate command retry or claim of rollback is introduced.
This reduces stalled request costs; it is not a Redis deployment SLA and
does not replace independent Redis server ACL/TLS/HA/alerts (#21).

## Bounded Redis lobby I/O and cancellation (#70)

The Redis-authoritative lobby snapshot store now has a **2-second .NET wait
limit per Redis command**, linked to the HTTP `RequestAborted`, SignalR
`ConnectionAborted` or worker stop token. The boundary covers direct room
reads/creates, shared-capacity Lua create, compare-and-exchange, deletion,
guest-revocation tombstones, indexed-room scan and individual indexed reads.
The runtime propagates the caller token across join, hero/team changes,
leave, member roster reads, quota-controlled creates and session revocation/
expiry cleanup, including version-conflict re-reads. Pre-cancelled requests
do not dispatch commands. Stalled lobby reads/mutations fail closed: HTTP
returns sanitized `503` with `no-store`, and SignalR reports only
`Lobby unavailable.`. Invalid/hidden rooms and unauthorized members
continue to share `404`; the new guard does not reveal rosters or IDs.

**Operational limitation:** cancelling the application's wait cannot
retract a Redis Lua script or SET command already accepted by the server.
A timeout must never be counted as a successful CAS, room creation or
revocation fence, and the client should not replay an uncertain mutation
automatically. The existing server-side atomic quotas, fencing, member
checks and idempotent cleanup remain authoritative. The 2-second limit
applies **to each Redis command**, not to the total duration of a multi-
step operation or cleanup across multiple rooms. Independently monitored
Redis TLS/ACL/HA and anti-abuse protections remain under umbrella #21.

## Browser recovery after guest session revocation (#71)

The React pre-match lobby keeps a private local room/invite/roster snapshot
only while it has a valid server guest identity. Authenticated operations
still rely on Redis to authorize every request: a response of **401** means
the browser must clear its cached guest identity, private room, invite,
roster and revision, and offer an **explicit** guest sign-in action.
A guest tab also makes a **read-only** `GET /api/sessions/me` on
window focus, visibility restoration or a same-origin `BroadcastChannel`
notification after another tab explicitly signs in/out. Channel messages
contain only the fixed string `recheck`, **not** user identity, invite,
roster or any cookie; no browser storage, persistent credential or auto-login
is introduced. This keeps a background tab recoverable even when its
browser does not emit a reliable focus event. If the server returns 401,
a non-guest identity or a
different guest ID, it discards the old guest's private room before any
further action. No new cookie is silently minted; the user must click sign
in. Transient failures and other HTTP statuses (403/429/503) do not falsely
delete a known-valid guest state. IT/EN statuses are accessible via the
existing live-region message.

The authoritative server invalidates cookies immediately on logout: a
background or unfocused browser may still visually hold an old snapshot
until its next focus/request, but it cannot access or mutate the revoked
room on the backend. Never treat UI clearing as the authorization boundary
or assume Web Storage can revoke a signed cookie. Existing server-side
Redis validation, membership checks and SignalR revalidation remain the
security boundary. Operational abuse prevention remains parent #21.

## Discarding late browser responses across guest identity transitions (#72)

A successful Redis-authorized HTTP roster response is not tied to the
browser's **current** guest if an old request was already in-flight when
another tab revoked or replaced the cookie. The UI now tracks an in-memory
session generation and operation sequence. On logout/expiry/revocation,
foreign guest identity, or an authoritative 401, it increments both and
clears session/room/invite/roster and the pending-action busy state.
Every asynchronous mutation, roster refresh, login/logout and initial or
focus session check verifies its generation *before* applying a result,
error or status message. An old 401 cannot clear a replacement guest;
an old 200 roster/room update cannot be shown under the new guest. An
outdated operation's `finally` cannot unlock a newer in-progress action.
Neither guest tokens nor private rosters are stored in Web Storage.

This is an **in-memory UI defense** against delayed HTTP callbacks, not
a way to cancel a server command that has already executed. API and
SignalR still authorize per request; do not infer any new match rules.
A resumed browser must explicitly sign in again after revocation.
Playwright exercises a real 200 roster generated before logout whose
delivery is delayed until a newly issued guest identity is visible.

## Redis guest authority outage versus definite revocation (#73)

The guest cookie validation path now differentiates a server-confirmed
**inactive** guest ticket (missing, expired, mismatched or cutoff) from a
shared Redis `IDistributedCache` **unavailable** result (exception or
2-second GET timeout). Both reject authentication; neither restores a guest
from local memory or trusts client-supplied status. For endpoints with
`RequireAuthorization`, the cookie challenge returns `401` only for
a certainly invalid guest, and sanitized **`503` + `Cache-Control:
no-store`** for Redis authority failures. This includes HTTP member roster
and SignalR negotiation. It never emits `Set-Cookie` or a Redis error
message. A missing cookie remains `401` even if Redis is unhealthy.
The shared session's existing cookie is not replaced or cleared; once
Redis recovers the same valid cookie can authenticate again.

An unavailable authority still grants **no** protected data or SignalR
connection; this is fail-closed security with honest availability status.
The frontend's prior #71 safeguard intentionally retains a guest/room on
`503` and only clears them on a true `401`. A protected endpoint
returns `503` for an unavailable Redis validation, NOT proof that the
guest remains valid: only a subsequent healthy Redis read can grant access.
The independent `/api/health` liveness route remains public, while
readiness evaluates the actual Redis dependencies separately. This does
not change Redis cluster HA requirements or ensure availability (#21).

## Development lobby cancellation parity (#74)

The Development/Vite in-memory `LocalGuestLobbyRuntime` now observes the
same pre-dispatch `CancellationToken` contract as the Redis Production
runtime for **all seven** methods: Create, Join, ChooseHero, ChangeTeam,
Leave, ForMember and RemovePlayerFromAllRooms. A cancelled HTTP request,
disconnected SignalR invocation or stopped guest expiry worker cannot
create a room, alter a member/roster revision or remove members *after*
its caller has already cancelled. A new request with an uncancelled token
continues to work normally, including explicit guest sign-in and roster
reads. The authoritative domain coordinator and its quotas are unchanged.

Because Development operations are synchronous under the coordinator's
lock, cancellation is checked immediately **before** each operation. It
cannot retract a synchronous mutation already begun, just as #70 cannot
retract a Redis Lua command already dispatched. No new game rules, account
types, per-IP quota or network fallback have been introduced. xUnit
covers seven already-cancelled calls, invariance of room quota/revision/
participants, cancelled worker cleanup and normal valid-token behavior.
Production Redis deadlines and guest session authorization are unchanged.

## Cookie mutation guard before guest authentication (#75)

All cookie-backed guest/lobby mutations now carry server-owned
`WebMutationGuardRequired` endpoint metadata: guest issuance POST, guest
logout DELETE and the full pre-match lobby POST/PATCH/DELETE route group.
The Origin + `X-Shirokuro-Request: 1` intent guard runs **before**
`UseAuthentication` and `UseAuthorization`, instead of just before the
shared Redis rate limiter. An invalid, hostile, absent or malformed Origin
or intent is rejected with **403 / Cache-Control: no-store**, without
`Set-Cookie`, `Retry-After`, a guest Redis ticket lookup, or an
admission-budget permit. Valid requests still authenticate against the
independent Redis session authority and must pass all existing domain
membership, CAS/fencing, and distributed quota controls. Endpoint handler
guards are kept as defense in depth; the middleware never bases security
on a client-supplied path or header claim about the endpoint type.

Only **mutation endpoints** carry this metadata: health, readiness,
`GET /api/sessions/me`, member roster reads and SignalR endpoints do not
add an Origin requirement. Standard Development Vite origins and
Production explicit HTTPS allowlists are unchanged. This change reduces
avoidable Redis work on rejected cross-origin traffic; it is **not**
proof of trust for arbitrary non-browser clients that can forge Origin and
intent, nor a substitute for SameSite cookies, backend authentication,
rate limits or operational WAF/anti-Sybil defenses (#21).

## Limite dei byte degli snapshot lobby (#105)

Gli snapshot Redis delle lobby v2 hanno un limite applicativo di **32768 byte**. Le tre letture server (GET diretto, GET passivo con fence, scansione dell'indice amministrativo) applicano lo stesso limite già nello script Lua read-only, **prima che Redis invii il risultato**. Un record sopra soglia è sostituito da un marker vuoto impossibile da interpretare come stanza valida: i client autorizzati ricevono il normale risultato opaco di indisponibilità, mentre una chiave realmente assente resta distinta. Il marker non ripara, non elimina e non estende il TTL di nessun record o indice; le attività di cleanup devono essere riprovate dopo indagine/riparazione esplicita dell'operatore. Una lettura passiva sotto revocation fence verifica prima la barriera senza leggere il payload. Non dedurre che uno snapshot alterato sia una stanza vuota.

## Manutenzione Redis: scansione a finestre della quota globale (#106)

La rimozione amministrativa di un guest revocato/scaduto enumera fino a **256** lobby, ma valida **tutte** le chiavi opache dell'indice Redis prima di leggere alcun record. Le letture di snapshot usano finestre di **massimo 8 comandi Redis Lua read-only simultanei**; ogni comando mantiene il limite di **32768 byte** prima della risposta. Un record invalido in una qualunque finestra rende indisponibile l'intero scan: **nessuna membership viene modificata, cancellata o confermata** finché la scansione completa non è riuscita. Indici e TTL restano immutati da questa lettura; chiavi scadute dopo lo scan sono ignorate e l'operatore deve riparare esplicitamente i dati corrotti prima di ritentare il cleanup.

## Timestamp di attività Redis corrotti (#107)

Oltre alla forma JSON e al limite di 32768 byte, l'API verifica che un record lobby ripristinato da Redis abbia `LastActivityUtc` **non nel futuro** rispetto all'orologio del nodo che lo legge. Date future (incluso l'estremo `DateTimeOffset.MaxValue`) non rendono valida una stanza e non possono provocare overflow nel calcolo della scadenza: le letture dirette, passive con fence e amministrative rispondono con indisponibilità opaca, senza avviare cleanup, refresh TTL o modifiche agli indici. Un record veramente scaduto continua a risultare assente. Ripristinare solo tramite procedura operativa autorizzata e controllare la sincronizzazione UTC/NTP fra i nodi: uno skew positivo dei clock può causare indisponibilità fail-closed e non è un motivo per aggirare questa barriera.

## Quota delle sessioni guest e verifica del cookie (#127, #128)

Ordine delle operazioni in **Production/Staging** per la sola `POST /api/sessions/guest`:

1. Validazione Host/HTTPS, Origin/intento CSRF e richiesta **senza body**: rifiutare prima di interrogare Redis
2. Quota condivisa `GuestMutationKind.SessionIssuance` (12/min per IP trusted / prefisso IPv6): 429 con Retry-After, oppure 503 se la quota Redis è indisponibile, **prima** di verificare qualunque ticket guest firmato allegato
3. Cookie authentication: il server convalida l'identità firmata usando l'autorità Redis senza cache positiva locale; i limiti quota non concedono mai l'accesso
4. Handler guest: il cookie guest valido risponde con la **stessa** identità e non viene rinnovato. Quando `OnValidatePrincipal` segnala `GuestSessionAuthorityStatus.Unavailable` e rifiuta il principal, l'handler **deve controllare** `GuestSessionRegistry.UnavailableRequestMarker` (solo server-side, `HttpContext.Items`) e dare **503/no-store senza Set-Cookie**. Non trasformare un'autenticazione non verificabile in un browser anonimo che ottiene un nuovo ticket. Un ticket **certamente Inactive**, invece, può permettere una nuova emissione esplicita se la quota e le scritture Redis sono disponibili

Per la distinta `POST /api/lobbies` resta valido l'ordine **autenticazione/autorizzazione guest prima della quota room-creation** (8/min): un client non autorizzato non deve consumare budget di creazione stanza. La funzione di ammissione condivisa usa soltanto metadata degli endpoint assegnati dal server per conservare 429/503 sanificati, Retry-After corretto e metriche una sola volta. In Development la quota locale conserva il suo ordine. Le route GET, SignalR e logout non usano questa quota di ammissione.

I test Production di #127/#128 simulano separatamente redis quota deny/outage, cookie registry GET outage **con SET ancora disponibile**, conferma di sessione Inactive e successivo ripristino; verificano che un guasto di lettura non registri né restituisca un nuovo guest. Questi controlli non certificano protezione Sybil/multi-IP, TLS/HA edge, account permanenti né i failure domain reali richiesti dalla parent #21.

## Guest logout senza body (#129)

`DELETE /api/sessions/guest` non ha un DTO né bisogno di contenuti in ingresso. Come `POST /api/sessions/guest`, ora porta metadata server-owned `GuestSessionEmptyBodyRequired`: dopo il filtro Host/HTTPS e Origin/intento ma **prima** della cookie authentication, qualsiasi body dichiarato (Content-Length positivo o Transfer-Encoding) o segnalato da `IHttpRequestBodyDetectionFeature.CanHaveBody` (HTTP/2/3, anche senza Content-Length) restituisce 400 e Cache-Control no-store, senza lettura Redis o revoca della sessione, senza consumare quote né leggere il corpo. L'handler mantiene lo stesso controllo come difesa in profondità prima di `RevokeAsync`. Un DELETE legittimo senza body continua a verificare autorità Redis e a revocare, installare fence e ripulire membership con le garanzie fail-closed già definite; la protezione non prova che i non-browser siano affidabili e non sostituisce le difese anti-bot/edge della parent #21.

## Contatori di rifiuto all'ingress prima di Redis (#130)

Il meter `Shirokuro.Api.Multiplayer` (esportato via OTLP HTTPS **solo** con opt-in operatore) ora include il counter aggregato `shirokuro.ingress.rejections`. Questo strumento registra solo rifiuti effettivi dei guard precoci, **prima dell'autenticazione cookie Redis e delle quote**: una sola osservazione per risposta. L'unica label `reason` è una stringa fissa scelta dal server da un enum chiuso:

- `room-id`: 404 per codice lobby impossibile sul guard canonico
- `mutation-origin`: 403 per Origin o intento di mutazione cookie-backed non valido
- `hub-origin`: 403 per Origin browser ostile sul trasporto SignalR
- `lobby-body-size`: 413 per lunghezza dichiarata oltre 4096 byte in una mutazione pre-match
- `session-body`: 400 per body non previsto su POST/DELETE della sessione guest

Non esportare mai come label IP/X-Forwarded-For, cookie, ID guest, room ID/invito, URL/path raw, header Origin, JSON o exception details. Non ci sono bucket per attore e questi contatori non sostituiscono una protezione edge/WAF. La metrica è distinta da `shirokuro.guest_mutation.decisions`, che misura le decisioni del **rate limiter** già raggiunto. Non interpreta un HTTP 401/429/503 come un rifiuto ingress e non tenta di attribuire l'origine ai singoli giocatori. L'assenza di un collector OTLP in produzione significa che questi dati non vengono trasmessi: gli allarmi esterni, le soglie, la retention e la verifica dell'edge restano responsabilità operative nella #21.

## Rifiuti Host e trasporto non cifrato (#131)

I primi due guard della pipeline — **Host + porta** e **HTTPS obbligatorio** fuori da Development — vengono eseguiti prima di Origin/intento, cookie authentication e Redis. Le risposte di blocco non sono redirect: un Host non esplicitamente autorizzato restituisce `400` e `Cache-Control: no-store`; una richiesta HTTP diretta o con forwarded scheme non validato da un proxy trusted restituisce `403/no-store`, senza cookie. Ogni risposta effettivamente rifiutata produce **un solo** evento `shirokuro.ingress.rejections` con `reason=host` oppure `reason=transport`. Host errato su HTTP conta **host** e non transport, senza quote/letture Redis. Host/porta, raw X-Forwarded-Proto, URL, IP e sessioni **non entrano mai nelle label**: si aggiungono solo due valori statici alla whitelist chiusa di #130. Development continua a supportare HTTP locale.

Il contatore misura solo richieste giunte fino ai guard dell'API: un proxy edge che rifiuta Host/HTTP prima di inoltrare non produce nessun campione applicativo. Per TLS edge, WAF, soglie di allarme, retention e mitigazione di abuso serve la piattaforma operativa di #21.

### Ordine del middleware Host applicativo e filtro di framework (#131, prima CI)

ASP.NET Core aggiunge un HostFiltering middleware automatico **prima** dei middleware dichiarati con `app.Use`. Nella prima [CI #43714264864](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43714264864), tre test hanno dimostrato che i 400 Host erano ancora generati dall'host filter del framework e non dall'handler dell'app; perciò non ricevevano no-store né la misura `reason=host`. La correzione registra `EarlyHostIngressStartupFilter` **in testa** alla collection DI `IStartupFilter`: applica lo **stesso** `IngressHostPolicy` stretto e rifiuta con 400/no-store + un evento aggregato **prima** del filtro automatico. Il middleware HostFiltering **resta abilitato** con la allowlist di nomi esatta e lo stesso controllo Host+porta interno resta defense in depth (senza un secondo conteggio). Nessun wildcard Host, bypass della verifica o fiducia in X-Forwarded-Host. Se viene modificato l'ordine dei middleware in future versioni ASP.NET, i test Host avvisano se un 400 torna privo di no-store.

## Validazioni cookie guest: esiti aggregati senza identità (#132)

`shirokuro.session.cookie_authority` misura **ciascuna vera convalida di cookie guest firmato**, non gli account distinti, gli accessi unici o le emissioni di sessione. Una singola misura `outcome`, scelta dal server da quattro soli valori fissi, è emessa da `OnValidatePrincipal` **dopo** ingress Host/HTTPS/Origin/body/quote della richiesta:

- `active`: `GuestSessionRegistry.CheckAuthorityAsync` ha confermato il ticket nel Redis condiviso
- `inactive`: il registro ha confermato assenza/revoca/scadenza del ticket (autorizzazione 401)
- `unavailable`: Redis non è verificabile entro il deadline o è indisponibile (fail-closed 503; non significa ticket revocato)
- `recovery-cutoff`: il cutoff UTC out-of-band configurato dall'operatore ha respinto il ticket con `IssuedUtc` anteriore/non valido, **prima** di leggere Redis

Le richieste senza cookie, i guard ingress precoci e i probe pubblici `/api/health`, `/api/ready` e `/api/preview/board` non generano misure di questa serie; i check ripetuti di un metodo Hub hanno una semantica diversa e non sono conteggiati come validazioni cookie. Non inserire mai cookie, identificativi guest/room, token, IP, path, header, valori di cutoff o messaggi di eccezione nelle metriche: soltanto `outcome` a cardinalità quattro. L'export continua a essere OTLP HTTPS opt-in, senza endpoint `/metrics`. Un picco di `unavailable` indica **richieste** di verifica non riuscite e richiede correlazione operativa con readiness e connettività Redis; un `inactive` non prova né quantifica furti di cookie. Contatori e snapshot non costituiscono un allarme né mitigazione automatica senza collector esterno.

## Content-Type pre-auth delle mutazioni lobby (#133)

Solo le cinque mutazioni lobby con DTO JSON (POST create, POST join, PATCH hero, PATCH team, DELETE membership) rifiutano `Content-Type` esplicitamente incompatibile con JSON con **HTTP 415 / Cache-Control: no-store**, senza cookie nuovo, Retry-After, parsing/buffering del body o accesso a Redis. La verifica standard `Request.HasJsonContentType()` riconosce `application/json`, `application/*+json` e charset; un header assente resta gestito dal binder con la semantica preesistente. L'ordine di precedenza è Host/HTTPS, ID lobby canonico (404), Origin/intento (403), limite dichiarato >4096 byte (413), media type (415), cookie auth Redis, quota room. GET roster/sessioni, session issuance/logout, SignalR e preview non sono interessati.

**Attenzione al routing ASP.NET Core:** `AcceptsMatcherPolicy` riconosce un media type non supportato **durante il matching** e seleziona l'endpoint sintetico `415 HTTP Unsupported Media Type`. Quell'endpoint non contiene più i metadata `GuestLobbyMutationBodyLimitRequired`, né quelli di autenticazione/Origin. Il guard pre-auth per il normale endpoint DTO (basato esclusivamente su metadata server) non potrebbe quindi controllare questi input. Un piccolo adattamento diagnostico riconosce **soltanto** il fallback sintetico del framework sulla lista chiusa dei cinque metodi/percorsi lobby noti, senza usare URL o header come label metriche o come fonte di identità, e restituisce il rifiuto 404/403/413/415 appropriato prima della fase di cookie auth. Non altera il routing degli endpoint esistenti e non concede mai autorizzazione; testano esplicitamente questo comportamento i test Production con cookie guest firmato e cache unavailable. Se vengono aggiunte/variate route JSON lobby, aggiornare nello stesso commit questa lista di fallback e i test, o il 415 del framework potrebbe non conservare `no-store`. La metrica `shirokuro.ingress.rejections` registra un solo `reason=lobby-media-type` statico per 415 applicativo, senza valori client. Questo controllo riduce I/O inutile, ma non sostituisce edge/WAF, limiti IP o la protezione anti-Sybil operativa richiesta dalla #21.

## Route API sconosciute e cookie guest Redis (#135)

Dopo il routing e i guard di Host/HTTPS (e CORS in Development), ma prima di autenticazione cookie e quota, il middleware rifiuta con **404/no-store** una richiesta il cui `GetEndpoint()` è `null` e il cui primo segmento di path è esattamente `/api` o `/hubs`. Non prova a validare il cookie guest in Redis: un URL mai registrato non ha risorse autorizzabili. Nessun `Set-Cookie`, `Retry-After`, nome stanza o messaggio diagnostico viene prodotto dalla risposta di fallback; non vengono create etichette metriche con URL input. I confini segmentati escludono `/apix`, `/hubs-extra` e le route future esterne a questi namespace.

La selezione del routing resta autoritativa: i fallback framework **405** per metodo errato e **415** per media type non JSON su vere route non hanno un endpoint nullo e conservano il loro percorso, inclusa la precedenza 404/403/413/415 della #133. API protette, GET roster, health/readiness, preview e SignalR effettivo non cambiano. Test xUnit Production verificano 404 senza lookup Redis nonostante cookie firmato durante outage e un 503 per la vera `/api/sessions/me`; un Playwright riusa l'identità browser per dimostrare 404/no-store e successiva sessione valida. La modifica è soltanto una riduzione dell'I/O abusabile, non sostituisce rate limit, anti-Sybil/WAF, monitoraggio o deployment TLS/HA. **Attendere CI verde sull'HEAD prima di considerare chiusa #135.**

## Risposte 405 sintetiche senza letture guest Redis (#136)

Le route esistenti con verbo HTTP errato producono tramite `HttpMethodMatcherPolicy` un endpoint sintetico `405 HTTP Method Not Supported`, il quale non esegue alcun handler Shirokuro e non porta metadata auth/mutation della route originale. Per non consumare Redis durante uno scarto già determinato dal routing, `ApiMethodMismatchGuard` identifica esclusivamente questo caso con **display name fisso, zero metadata e path segmentato `/api` o `/hubs`**. Il callback `OnValidatePrincipal` non interroga il registry guest per quel fallback, mentre il middleware pre-auth marca la risposta `Cache-Control: no-store`. **Il router continua a eseguire il proprio endpoint e a comporre `405` + header `Allow`**: non si anticipano né si imitano i metodi consentiti, non si eseguono delegate senza autorizzazione. Le rotte normali e i fallback 415 conservano le rispettive verifiche cookie, Origin, ID canonico, limiti di body e quota. I test Production con guest firmato e fake Redis indisponibile verificano il numero zero di GET sul metodo errato, `Allow: POST`, e 503 sulla richiesta realmente protetta; Playwright verifica l'identità invariata nel browser. Un cambiamento del nome/metadata synthetic 405 nel framework interrompe solo l'ottimizzazione (fail closed verso le verifiche preesistenti), e deve essere rilevato dai test. #21 resta aperta per deployment resiliente, osservabilità e anti-Sybil; **la #136 si chiude solo con CI verde verificata sull'HEAD**.

## Telemetria aggregata ingress e cookie legacy (#140)

L'unico `Meter` .NET `Shirokuro.Api.Multiplayer` ora registra tre ulteriori
esiti **senza cambiare lo schema né aprire nuovi endpoint**:

- `shirokuro.ingress.rejections` con `reason=unknown-route` quando
  il router non trova endpoint nei **segmenti esatti** `/api` o `/hubs`
  e l'ingress ritorna 404/no-store prima di cookie auth e Redis (#135).
- Lo stesso contatore con `reason=method-not-allowed` per il **solo
  fallback 405 del framework**, identificato da display name/metadata
  server e confinato agli stessi namespace (#136). Il framework continua
  a produrre codice e `Allow`; il contatore non cambia il routing.
- `shirokuro.session.cookie_authority` con `outcome=unsupported-ticket`
  quando un cookie Data Protection **firmato** contiene un principal non
  canonico o un presunto account senza provider (#139): il callback lo
  rifiuta subito senza letture Redis. Cookie guest canonici continuano
  ad emettere `active/inactive/unavailable/recovery-cutoff` soltanto
  quando arrivano alla rispettiva fase di verifica.

Le label sono stringhe statiche provenienti da enum chiusi; non includono
mai indirizzi IP, path, query, identity claim, cookie, invite, chiavi,
errori, hostname o dati utente. Gli esiti 404 e 405 sono mutuamente esclusivi
(`GetEndpoint() == null` vs endpoint framework sintetico) e restano diversi
da 415 e dai guard Origin/body. L'autenticazione dei probe pubblici è
intenzionalmente indipendente da Redis e non marca cookie rigettati:
quegli endpoint non accedono a dati privati. Ogni misura è il conteggio
di **richieste/scarti**, non di attaccanti, utenti unici o tentativi
fraudolenti verificati: i contatori non sostituiscono anti-Sybil, WAF,
rate limit e alert gestiti dall'operatore.

Test xUnit catturano le metriche attraverso un `MeterListener` process-wide
insieme a veri HTTP TestServer con cookie firmati/fake Redis bloccato,
e verificano il contratto di label a cardinalità finita; l'export TLS OTLP
resta opt-in, con un test che controlla anche il payload e l'assenza di
header credenziali. Come per altri contatori, il listener può vedere
test concorrenti: la correlazione fra richiesta e assenza di I/O Redis
è comprovata dal fake cache/limiter, non dalla pretesa che un counter
sia una sessione o un identificatore.

## Shutdown del nodo e readiness fail-closed (#141)

In **Development** e **Production/Staging**, il server usa
`HostLifetimeReadinessDrainSignal` (`IHostApplicationLifetime.ApplicationStopping`)
come unica fonte **server-owned** del segnale di spegnimento. Non esiste
un header, query parameter o endpoint client per iniziare/arrestare il drain.

`GET /api/ready`, da quando è iniziato `ApplicationStopping`, ritorna
sempre **503** con il contratto JSON minimale `{"status":"unavailable"}`
e `Cache-Control: no-store`, senza cookie, dettagli dei backend o `Retry-After`.
La verifica del segnale avviene **prima della risoluzione DI** di
`IDeploymentReadiness`: il solo accesso alla readiness durante lo
shutdown non crea i connector Redis né esegue probe Redis/keyring.
Il segnale è ricontrollato **dopo** `await IsReadyAsync`, così anche un
probe partito prima dello shutdown, o il suo positivo cache di 3 secondi,
non può dichiarare il nodo ready quando la richiesta termina già in drain.
Un probe condiviso in volo non viene cancellato da questo segnale; termina
per chi era già in attesa, mentre l'esito HTTP viene correttamente negato.

`GET /api/health` rimane un puro controllo di liveness (`200/no-store`
finché il server può rispondere) e non diventa una readiness implicita.
Il contatore `shirokuro.readiness.probes` continua a contare soltanto i
probe tecnici realmente eseguiti, non richieste 503 per shutdown. Il drain
non cambia il comportamento di autenticazione, lobby, SignalR o dati PvP.

**Operazione:** il bilanciatore deve usare `/api/ready`, non `/api/health`,
per scegliere nuovi nodi. Un `SIGTERM`/host shutdown cancella
`ApplicationStopping` e rende non-ready le eventuali richieste ancora
servite. Questo non sostituisce una finestra `preStop`/propagazione dei
poll del bilanciatore, `terminationGracePeriod`, drenaggio WebSocket e
controlli HA sul cluster: tali attività rimangono in #21. Non si sostiene
che il 503 possa essere osservato se Kestrel ha già smesso di accettare
nuove connessioni. I test xUnit TestServer simulano lo shutdown con un
segnale monotono iniettato, in entrambe le modalità; verificano sia il
salto totale della risoluzione del readiness service sia il race con un
probe positivo in volo, e l'indipendenza di liveness. Un test unitario
verifica il collegamento al vero token di `IHostApplicationLifetime`.

## Drenaggio delle connessioni SignalR su ApplicationStopping (#142)

Oltre al fail-closed di `/api/ready` (#141), il processo applica il
segnale **server-owned** `IHostApplicationLifetime.ApplicationStopping`
al trasporto lobby SignalR `/hubs/lobbies`:

- Nel middleware **pre-auth**, il marker server `CookieSocketOriginGuardRequired`
  identifica esclusivamente le route hub reali selezionate da ASP.NET Core.
  Dopo i normali 404 per URL inesistenti, ma **prima** di cookie auth/Redis,
  negoziazione e upgrade del WebSocket ritornano `503/no-store` durante
  lo shutdown, senza `Set-Cookie` o `Retry-After`. Non sono intercettate
  route `/hubs` estranee, fallback 405, API pubbliche o mutazioni lobby.
  Gli altri controlli Host/HTTPS esistenti restano attivi.
- `LobbyConnectionDrainRegistry` è un singleton per **singolo nodo**:
  mantiene solo `ConnectionId -> HubCallerContext` per i socket attivi,
  mai ID giocatore, cookie, room, inviti, indirizzi IP o altri dati di gioco.
  `OnConnectedAsync` registra l'istanza solo finché il nodo non è in
  arresto, con un controllo dopo l'inserimento per coprire la race;
  `OnDisconnectedAsync` la rimuove. Il callback `ApplicationStopping`
  invoca `HubCallerContext.Abort()` su ogni connessione tracciata;
  eventuali errori di un trasporto già chiuso non bloccano gli altri.
  Una registrazione tardiva si abortisce autonomamente.
- `GetRoster` continua a ri-autorizzare il guest per ogni invocazione
  e a filtrare i membri per room come prima. Durante lo shutdown
  ritorna l'errore generico `Lobby unavailable.` **prima** delle letture
  guest/room e **dopo** l'eventuale await di un roster Redis avviato
  prima dello stop. Nessun roster letto in volo viene restituito da
  un nodo osservato come draining al termine dell'attesa.
  Non vengono aggiunti nuovi metodi SignalR invocabili dai client.

**Contratto e limiti:** la readiness resta `503/no-store` durante lo
stop, `/api/health` resta liveness mentre Kestrel risponde, e gli
endpoint API non-hub conservano i propri controlli di autenticazione.
Non esiste un endpoint per comandare il drain da un browser e non
viene conferito alcun privilegio PvP. Il server non promette che un
bilanciatore vedrà il `503` dopo che Kestrel ha già smesso di accettare
connessioni: distribuire preStop/grace period, polling e protezione dei
WebSocket con l'orchestratore è un'attività **esterna** ancora sotto #21.
Il client può riconnettersi attraverso il bilanciatore verso un altro
nodo, ma la resilienza del reconnect reale non è garantita da questo
solo change.

Regressioni: TestServer Production con cookie guest firmato e Redis
fake indisponibile verifica `503/no-store` su hub negotiate e GET
upgrade senza lookup guest né quota, `404` sulle rotte non-hub,
`/api/ready` draining e `/api/health` indipendente. xUnit sui
lifecycle Hub e sul registry verificano chiusura dei socket attivi,
rimozione dei disconnected, registrazioni tardive abortite, shutdown
idempotente, e race della lettura roster in volo.

## Client-side recovery of the pre-match roster after failover (#143)

The web client currently uses **read-only authenticated HTTP polling** on
`GET /api/lobbies/{room}/roster/peek`, not a persistent SignalR browser
connection. Its existing 15-second normal cadence and bounded 30/60/120s
backoff still protect the API and shared Redis during failures.

When an existing tab returns **online**, gains **focus**, or becomes
**visible**, its single polling effect can move the next read forward
without waiting for the remainder of an outage backoff. The wake:
- Applies only to the **currently verified** guest and pre-match room.
  It never signs in, issues a mutation, retries an uncertain command or
  trusts an unverified room ID or response.
- Requires the tab to be visible and `navigator.onLine` true, and does not
  start any passive GET while an explicit user mutation is active.
- Enforces at least **15 seconds since the previous passive read** (also
  since first room display), and accepts at most **one wake hint in 30
  seconds**; a single timer keeps the earliest scheduled deadline, so
  bursts of focus/online/visibility events cannot create GET storms.
- Never overlaps a read already in flight. One pending wake hint can be
  coalesced into a later check, still respecting the 15s floor; abandoned
  reads remain abortable on session change, room change or unmount.
- Does **not** reset an outage counter on a hint. Only a verified 200
  (strict member/room/revision DTO) or an authorized 304 resets backoff.
  Confirmed 401/404 still revoke the guest/room respectively; 5xx and
  network failures preserve the last verified view.

Vitest covers the pure wake-delay/cooldown decisions including invalid
and backwards monotonic clocks. Playwright creates a deterministic guest
and pre-match room without spending production quota, observes a real
browser 503, emits a burst of wake events, and proves that only one
subsequent conditional GET adopts a verified newer revision without
mutation replay. This is resilience for the **current HTTP implementation**,
not a claim that browser SignalR reconnection or multi-node routing is
implemented. Deployments still need a load-balancer grace period,
cross-node Redis authority and an explicitly tested reconnect policy
under #21.

## Stato di freschezza del roster lato browser (#144)

La lobby pre-partita espone una **vista dell'ultimo roster autenticato**.
Quando il polling `GET /api/lobbies/{room}/roster/peek` fallisce per
`503`, indisponibilità di rete, un DTO 200 malformato oppure una revisione
inferiore a quella già verificata, il client **mantiene** lo snapshot
precedente ma visualizza un avviso IT/EN con `role=status`,
`aria-live=polite`: non può confermare che le informazioni siano ancora
attuali. L'avviso non mostra ID, inviti, dettagli Redis, IP, orari arbitrari
o dati di gioco nascosti. Un evento browser `offline` può solo marcare
la vista come potenzialmente non aggiornata: non revoca alcun guest.

La freschezza torna `current` **soltanto** dopo un `304` autorizzato
oppure un `200` completo e verificato per stanza, partecipazione e
revisione uguale/successiva; una mutazione o lettura esplicita con
risultato autenticato adottato tramite `showRoom` conferma la stanza.
Il semplice ritorno online/focus/visibilità, o una risposta `200`
ben formata ma più vecchia della revisione già vista, **non** bastano.
Una richiesta sospesa/cancellata non inventa una perdita di membership
né una prova di recupero. Su `401` o `404` confermati continuano le
normali revoche di sessione/stanza e l'avviso viene rimosso insieme
alla vecchia vista; risposte appartenenti a operazioni/identità obsolete
non possono cambiare lo stato di un'altra room.

Questa indicazione riguarda la sincronizzazione del **roster pre-match
già autorizzato**, non fornisce osservazioni di match, metodi di
movimento, reconnect SignalR/WebSocket o una garanzia che il browser
sappia se Redis sia guasto. I test Vitest coprono le transizioni pure
fra last-verified/stale; Playwright verifica 503 -> avviso -> nuovo 200
verificato -> avviso rimosso, stato offline, 200 malformato e rimozione
della vista con 401/404 senza inviare mutazioni durante il recupero.
Il deploy multi-nodo, l'orchestrazione e il traffico realtime restano
in #21.

## Drain delle letture HTTP roster su ApplicationStopping (#145)

Dopo readiness fail-closed (#141) e la chiusura dei socket SignalR
(#142), anche le **due letture HTTP membro-autorizzate** del roster
pre-partita rispettano `ApplicationStopping`:

- Solo `GET /api/lobbies/{lobbyId}/roster` (heartbeat esplicito) e
  `GET /api/lobbies/{lobbyId}/roster/peek` (poll non rinnovante)
  possiedono il marker **server-owned** `LobbyRosterReadDrainRequired`.
  Il middleware pre-auth verifica quel marker e il segnale
  `IReadinessDrainSignal` **prima dell'autenticazione cookie Redis**:
  quando il nodo sta fermandosi risponde `503/no-store` senza roster,
  `ETag`, `Retry-After` o un nuovo cookie. I comandi di lobby,
  `DELETE /api/sessions/guest` e le altre API continuano a seguire
  la loro normale policy; non è un bypass generale dell'autenticazione.
- I controlli di routing rimangono prima del drain: una route inesistente
  o un ID stanza tecnicamente impossibile restituisce ancora `404`,
  e un metodo non consentito il **405 sintetico** del framework.
  Il client non può creare marker con header, query, path parziale o
  falsi cookie.
- Per richieste iniziate *prima* dello stop, il responder
  `ServeGuestRosterAsync` ricontrolla il segnale **prima** della lettura
  member-only e subito **dopo** `await ForMemberAsync/PeekForMemberAsync`.
  Un roster validato/ETag/304 che completa durante il drain viene
  convertito in `503/no-store` **senza consegnare i byte del roster**.
  Questo controllo non annulla né disfa una lettura già effettuata,
  non revoca sessioni e non cambia TTL/revisioni.
- Quando il processo è disponibile, entrambe le GET conservano i
  normali controlli di sessione e membership, l'heartbeat solo per
  `/roster` e l'ottimizzazione 304 autorizzata solo su `/roster/peek`.
  Un browser che riceve 503 mantiene il proprio ultimo snapshot come
  *potenzialmente obsoleto*, grazie alle garanzie #143/#144.

Regressioni xUnit verificano in **Production TestServer** un cookie
guest firmato con fake Redis indisponibile e conteggi delle letture
conservati: 503 anticipato sulle due route, 404/405 indipendenti,
liveness/readiness invariati, 503 normale delle altre API protette.
Test separati con un provider lobby sospeso coprono anche il
risultato member-only in-flight e un ETag che altrimenti avrebbe dato
304. Nessuna di queste protezioni sostituisce il preStop del
load balancer, un grace period misurato o il traffico multi-nodo reale,
ancora aperti in #21.

## Stop delle nuove ammissioni guest/lobby durante shutdown (#146)

A complemento di readiness (#141), drain SignalR (#142) e roster HTTP
(#145), `ApplicationStopping` ora impedisce l'apertura di **nuove**
sessioni guest (`POST /api/sessions/guest`) e **nuove** lobby
(`POST /api/lobbies`). L'ingress usa il metadata **server-owned**
`GuestMutationRateLimit` (solo due tipi di ammissione) e il segnale
monotono `IReadinessDrainSignal`. In entrambi gli ambienti, se il nodo
è draining la richiesta corretta riceve `503` e `Cache-Control:
no-store`, senza body privato, `Set-Cookie` o `Retry-After`, prima
di validare il guest su Redis o di consumare un permesso di quota.

L'ordine di sicurezza rimane: selezione routing e 404/405 del framework,
controlli Origin/intent e body/media type, quindi drain delle nuove
ammissioni, quindi autenticazione e limitatori. Un browser non può
attivare il drain aggiungendo header, URL o parametri. Le GET di health,
readiness e roster seguono le policy #141/#145; i websocket la #142.
**Logout, revoca e mutazioni sulle stanze già esistenti non sono bloccati
da questo nuovo controllo**, per consentire la pulizia in corso.

Per un deployment reale occorrono ancora preStop del bilanciatore, un
grace period e un protocollo per le richieste di ammissione che erano
già entrate nel server prima dello shutdown: questa barriera agisce
sull'arrivo della richiesta, non annulla transazioni già in-flight.
xUnit Development/Production coprono gli endpoint, cookie guest
firmato, quota Redis, error precedence e logout. #21 resta aperta.

## Drain degli ingressi in stanza pre-partita (#147)

La policy di admission in shutdown descritta in #146 ora include anche
`POST /api/lobbies/{roomId}/join`. Questo è un **nuovo ingresso** di
partecipante, non una modifica di un membro esistente: un nodo che ha
ricevuto `ApplicationStopping` non può più confermare nuove membership
di stanza. Il solo endpoint join ha il metadata server-owned
`LobbyJoinDrainRequired`. Con il segnale host `IReadinessDrainSignal`,
il guard pre-auth risponde `503`, `Cache-Control: no-store` senza
`Set-Cookie`, `Retry-After`, roster o dettagli Redis, prima di qualunque
verifica cookie/registry o lookup lobby. Non esiste un parametro URL/header
per attivare o disattivare questo comportamento.

Le validation di routing e canonical room ID precedono il drain (404/405),
come Origin/intent, body-size e Content-Type (403/413/415). La quota per
creazione stanza e la quota per session issuance rimangono quelle di #146.
Una PATCH hero/team, il DELETE membership e il logout guest **non sono
nuove ammissioni** e conservano autenticazione, privacy e possibilità di
cleanup nel drain. Restano indipendenti i controlli GET roster e SignalR
(#145/#142). xUnit con guest guest reale su TestServer Production/Development
esercita queste distinzioni.

La barriera protegge le richieste arrivate **dopo** lo stop: non offre
atomicità col CAS Redis di un join già avviato. Un deploy in produzione
necessita di preStop e grace period orchestrati, retry client senza
duplicare intenti e gestione dei socket multi-nodo (#21).

## Seconda barriera ingressi dopo autenticazione/limiter (#148)

La barriera pre-auth di #146/#147 non basta per richieste già passate
quando il nodo era `ready`: la validazione di cookie Redis o l'acquisizione
di una quota IP può completarsi **dopo** l'inizio dello shutdown. Un secondo
middleware, posizionato **dopo** autenticazione/autorizzazione e limitatori
ma **prima** degli handler, ricontrolla l'indicatore server-owned
`IReadinessDrainSignal`, usando gli stessi marker verificati da
`IsNewGuestAdmission`: POST `/api/sessions/guest`, POST `/api/lobbies`,
POST `/api/lobbies/{roomId}/join`.

Il fence restituisce 503 con `Cache-Control: no-store`, senza cookie
nuovo, ETag, `Retry-After` o rappresentazione privata. La **prima**
barriera conserva la priorità di 404/405, Origin/intent, body-size,
Content-Type, canonical room ID, e nega le richieste iniziate già in
shutdown prima che autenticazione e Redis possano spendere risorse.
La seconda barriera intercetta richieste sospese nelle operazioni auth
o limiter: queste ultime **possono già avere consumato una quota**; la
risposta 503 non implica che Redis abbia rimborsato un permesso.

Sono volutamente esclusi logout, leave, hero/team e cleanup, oltre a
health, readiness, roster e Hub che hanno policy dedicate. Nuovi test
xUnit Production TestServer guidano la race con gate asincroni, senza
sleep: permesso sessione, permesso creazione stanza, validazione cookie
per join. Non si promette un rollback dei comandi che hanno **già
iniziato** una scrittura Redis o un CAS di lobby prima di
`ApplicationStopping`: per questo servono load balancer preStop,
grace period, gestione delle risposte incerte e HA (#21).

## Sessioni guest già in fase di emissione quando inizia il drain (#149)

Le barriere HTTP pre/post-auth di #146/#148 escludono i nuovi ingressi
**prima dell'handler**, ma una chiamata già dentro POST
`/api/sessions/guest` può attendere la conferma della SET nel registro
guest Redis o la persistenza dell'indice di scadenza. Il middleware non
può più fermarla. L'handler ora verifica lo stesso indicatore monotono
`IReadinessDrainSignal` (server-owned) dopo ciascun await rilevante:
`RegisterAsync`, `IGuestExpiryIndex.ScheduleAsync`, `SignInAsync`.

Se il nodo è draining, l'handler non consegna un nuovo cookie, rimuove
anche un eventuale header `Set-Cookie` già preparato e risponde
503/`Cache-Control: no-store`. Tenta inoltre `RevokeAsync` del preciso
token appena registrato; la stessa routine è usata se scadenza o
firma del cookie falliscono. La revoca può non essere confermata a
causa di Redis unavailable / timeout: in questo caso rimane solo un
record server-side non divulgato con scadenza assoluta (8h), **non** un
cookie valido che il client conosca. Una richiesta può aver consumato
una quota limitatore o scritto un lavoro durabile prima del 503;
non significa che quel lavoro sia stato annullato.

La verifica finale dopo `SignInAsync` riduce l'ultima finestra di race,
ma non è un protocollo atomico con rete HTTP, Redis o
`ApplicationStopping`: dopo il controllo finale possono accadere altri
eventi e una risposta già trasmessa non si può revocare dall'handler.
Il drain orchestrato (ready=503, stop nuove connessioni lato
bilanciatore, preStop, grace period), l'osservazione delle risposte
incerte e l'HA rimangono responsabilità aperte in #21. Nessun
comando Lua/CAS di lobby già consegnato al Redis è annullato.

## Richieste create/join col JSON body lento durante shutdown (#150)

Le barriere pre/post-auth di #146/#148 sono middleware e terminano
**prima** che gli endpoint Minimal API deserializzino i DTO JSON. Un
browser con cookie già verificato e quota room-creation assegnata può
mantenere la request sospesa durante il binding del body. Se
`ApplicationStopping` parte mentre manca ancora l'ultimo byte, il
callback non deve iniziare una nuova mutazione dopo il drain.

I callback di POST `/api/lobbies` e POST
`/api/lobbies/{roomId}/join` ricontrollano ora il segnale monotono
server-owned `IReadinessDrainSignal` **dopo** il model binding e
`TryGuest`, immediatamente **prima** di `IAsyncGuestLobby.CreateAsync`
o `JoinAsync`. Nel caso draining: 503/`Cache-Control: no-store`,
nessuna Location, nuova stanza o roster privato, nessuna membership
aggiunta da un comando che non è mai partito.

Non cambia la precedenza dei guard Origin/intent, dimensione/Content-Type,
canonical room ID, auth Redis e quota (che può già essere stata spesa
prima di leggere il JSON). Non blocca hero/team/leave/logout e non
restituisce un finto 503 per un CAS Redis già committato. Come per #149,
il controllo non è atomico con `ApplicationStopping`: una chiamata
`CreateAsync/JoinAsync` già entrata nel Redis CAS può completare, e
la risposta deve riflettere il risultato certo invece di invitare un
retry incerto. Il coordinamento del drain con bilanciatore, preStop,
grace period, in-flight connections e HA resta in #21. Test xUnit
Production TestServer con stream JSON partizionato provano questa race
senza usare `Task.Delay`.

## PreStop Linux opzionale prima di SIGTERM — #151

Per i rollout orchestrati, il solo `ApplicationStopping` (SIGTERM)
arriva **troppo tardi** per offrire al bilanciatore una finestra di
propagazione readiness **prima** della terminazione. La #151 abilita,
**solo su Linux e solo con opt-in esplicito**, un secondo evento trusted
locale per `IReadinessDrainSignal`: `SIGUSR1` (numero Linux **10**).
Non è un HTTP/SignalR endpoint amministrativo; nessun header, Origin,
cookie o query può iniziare un drain. Per compatibilità il default è
**disabilitato** e il comportamento di `ApplicationStopping` è invariato.

Configurazione .NET in Production/Staging:

```sh
Security__PreStopDrain__EnableSigusr1=true
```

Valori ammessi **solo** `true` e `false` minuscoli (omissione = `false`).
Una stringa non valida, l'opt-in in Development o l'opt-in fuori da Linux
sono errori di avvio, non permissive fallback. Il segnale viene
registrato **prima** che Kestrel accetti traffico, e smontato al
dispose del processo.

Al ricevimento di SIGUSR1, il processo intercetta/cancella il normale
comportamento OS del segnale, si marca in drain **monotonico** e resta
in esecuzione: `GET /api/ready` torna immediatamente **503/no-store**
senza risolvere Redis/DP; `GET /api/health` continua 200; le nuove
sessioni guest, creazioni/ingressi in lobby, negoziazioni SignalR e
roster GET seguono le barriere di drain già esistenti. Logout e azioni
autorizzate dei membri esistenti mantengono la policy corrente per
consentire cleanup. Ripetere SIGUSR1 è idempotente. Il successivo
SIGTERM esegue il **normale** host shutdown di ASP.NET Core.

### Esempio di sequenza operatore per un pod Linux

**Non** è un manifesto completo pronto per la produzione. Solo se il
processo `dotnet Shirokuro.Api.dll` è davvero **PID 1**, l'immagine
contiene `/bin/sh` e `kill`, e il servizio è dietro un bilanciatore
che rispetta la readiness, un esempio di hook è:

```yaml
spec:
  terminationGracePeriodSeconds: 60
  containers:
    - name: shirokuro-api
      env:
        - name: Security__PreStopDrain__EnableSigusr1
          value: "true"
      lifecycle:
        preStop:
          exec:
            command:
              - /bin/sh
              - -c
              - 'kill -USR1 1 && sleep 10'
```

Prima del rollout l'operatore deve **verificare** PID, disponibilità
della shell e il controllo di readiness configurato correttamente con
l'allowlist Host/HTTPS/proxy già attiva. Se `dotnet` non è PID 1, non
inviare il segnale arbitrariamente a PID 1: selezionare il PID reale in
modo sicuro secondo il runtime dell'immagine. Il `sleep` è un esempio
di periodo minimo per propagazione al proxy/LB, **non** una garanzia di
svuotamento dei client. Testare tempi reali in staging; ricalibrare
grace period per coprire **sia** preStop **sia** SIGTERM/shutdown, inclusi
Redis persistente e connessioni in corso. Il grace countdown di
Kubernetes comincia **prima** di preStop e SIGTERM è inviato dopo la
conclusione dell'hook; un hook fallito può terminare il container.
Non utilizzare un probe HTTP interno ingenuamente su `http://`:
l'API Production richiede HTTPS effettivo e Host allowlisted anche
sul readiness probe.

### Limiti importanti

SIGUSR1 non trasferisce connessioni WebSocket esistenti, non annulla
comandi Redis Lua/CAS già iniziati e non conferma che il bilanciatore
abbia rimosso il pod. La policy di routing, l'osservabilità, un vero
tempo di grace misurato, l'HA multi-node e il protocollo di gestione
di risposte incerte restano responsabilità di deployment in #21. Gli
xUnit #151 **simulano la transizione** del callback POSIX con l'identico
metodo in-processo, e installano in Linux la registrazione reale senza
inviare SIGUSR1 all'intero processo di test. Una prova operativa di
preStop+LB resta necessaria in staging.

## PreStop: chiusura dei socket SignalR già connessi (#152)

La fase Linux `SIGUSR1` di #151 passa immediatamente il nodo a
`IsDraining`. Fino a #152 questo rendeva indisponibili nuove
negoziazioni e invocazioni `GetRoster`, ma i trasporti SignalR
**già connessi** restavano aperti fino al successivo `SIGTERM`.
Il `HostLifetimeReadinessDrainSignal` ora pubblica un
`IPreStopDrainNotification.PreStopStarted` **one-shot**, un
`CancellationToken` locale interno al processo, attivato dopo aver
marcato il drain: **non** è un endpoint amministrativo HTTP e nessun
client può impostarlo.

`LobbyConnectionDrainRegistry` registra un callback su quel token,
**oltre** all'originale `ApplicationStopping`. Quando SIGUSR1
avvia preStop, abortisce best-effort i soli contesti Hub **attivi**
tracciati, non quelli già disconnessi; blocca e abortisce le nuove
registrazioni e non abortisce due volte per il successivo SIGTERM.
Se il preStop precede la creazione lazy del registro, la registrazione
su un token già cancellato chiama subito il callback. Le letture
`GetRoster` attive hanno comunque il proprio controllo dopo l'await
Redis: nemmeno il risultato arrivato a cavallo del drain può
pubblicare roster privati. Non viene scritto alcun `playerId`,
cookie, token o identificativo lobby nei callback.

Il preStop **non** invia ai client un messaggio che promette di
riprenderli altrove e non trasferisce automaticamente le connessioni
su un altro nodo. I browser devono gestire una normale disconnessione
e, se opportuno, ristabilire una connessione autenticata al bilanciatore
dopo la propagazione del routing/readiness. Il token non implica un
commit/rollback delle mutazioni Redis già avviate, né garantisce che
ogni connessione TCP esterna del bilanciatore sia stata chiusa. La
durata del preStop e la terminazione forzata restano da validare in
uno staging reale sotto #21. xUnit #152 verifica il callback
direttamente su contesti SignalR fittizi con tracking e richiesta
roster in-flight: non simula un bilanciatore effettivo, TCP o
SIGUSR1 inviato dall'orchestratore.

## Metriche locali di preStop e tentativi di chiusura SignalR (#153)

Per validare il drain durante i rollout e analizzare un eventuale preStop
inefficace, il meter aggregato esistente `Shirokuro.Api.Multiplayer` espone
due nuovi counters **solo server-side** (nessun nuovo endpoint):

- `shirokuro.node.drain.transitions`: un evento per processo al primo
  passaggio monotono allo stato draining; attributo server-defined
  `cause=prestop|host-shutdown`. Un SIGUSR1 ripetuto o il successivo
  SIGTERM non incrementano nuovamente. Uno shutdown senza preStop
  emette solo `host-shutdown`; i normali GET di readiness **non**
  incrementano questo contatore.
- `shirokuro.signalr.drain_abort_attempts`: un incremento per
  `HubCallerContext.Abort()` **tentato dal registro connessioni**,
  con attributo finito `reason=prestop|host-shutdown|late-connection`.
  Il registro misura solo trasporti attivi tracciati e nuove
  registrazioni respinte a drain avviato: non è una metrica di tutti i
  socket TCP, né di chiusure riuscite, né di utenti distinti. Gli
  eventuali `Abort()` invocati direttamente da SignalR fuori dal
  registro non fanno parte del contatore.

Le etichette sono codificate tramite enum chiusi: non includono
connection ID, giocatori, inviti, lobby ID, IP, header, cookie,
eccezioni o URL. Il meter confluisce nell'exporter OTLP metrics-only
HTTPS già disponibile e **disabilitato per default**; non espone
stato sensibile agli endpoint pubblici. I test #153 verificano le
etichette con `MeterListener`, la semantica first-transition in
`ApplicationStopping`/SIGUSR1, i contatori dei socket
attivi/disconnessi/tardivi e l'export fittizio OTLP senza credenziali.

Durante una prova staging del rollout, correlare tali serie a
`/api/ready` (503 dal preStop), alla capacità del bilanciatore di
rimuovere il nodo e alla riconnessione browser **sul nuovo nodo**.
Non dedurre che il bilanciatore abbia svuotato un nodo dalla sola
presenza di un contatore abort; un termination forzato può anche
perdere le metriche non ancora esportate. La prova reale di SIGUSR1
e del time budget/grace period rimane nel parent #21. Gli xUnit non
certificano l'osservazione dell'evento OTLP da un collector reale.

## Prova CI di segnale Linux reale, senza toccare i processi CI (#154)

La #154 aggiunge una prova di livello **processo/OS** al job backend
Linux della CI, complementare agli xUnit TestServer #151–#153.
Una piccola applicazione `.NET 10` separata in
`tests/backend/Shirokuro.PreStopSignalSmoke` importa **le classi reali**
`HostLifetimeReadinessDrainSignal` e `LobbyConnectionDrainRegistry`
dell'API; non avvia la Production API completa e non richiede Redis,
Data Protection, account o ingress esterno.

Il job compila il fixture, avvia un unico **PID figlio** tramite
`dotnet <fixture.dll>`, aspetta un marker scritto soltanto dopo
la registrazione dell'handler POSIX e del contesto SignalR test, invia
**SIGUSR1 reale** soltanto a quel PID e aspetta un secondo marker che
richiede: `IsDraining=true`, `ApplicationStopping=false`,
`AbortCount=1`. Verifica che il processo sia ancora vivo,
invia SIGUSR1 di nuovo e poi il normale SIGTERM: il fixture attende
lo stop dell'host e verifica `AbortCount=1` senza un secondo abort.
Il test non invia mai segnali al runner, a xUnit, ai Redis CI o
all'API reale. Usa timeout brevi, `trap` di cleanup e cancella
il log tecnico temporaneo: nessun token, cookie, player ID o stanza.

È una prova reale del callback Linux `PosixSignalRegistration`,
della cooperazione fra lifetime .NET/registro SignalR e dello
shutdown ordinario dopo preStop. **Non** prova il comportamento di
Kubernetes `preStop`, di TLS/proxy/LB, di WebSocket TCP effettivi
o della riconnessione browser, e non misura RTO/RPO né garantisce
rollback di un CAS Redis già avviato. Quelle prove devono essere
eseguite in uno staging multi-nodo secondo #21. Il test di processo
è aggiuntivo: restano obbligatori gli xUnit .NET10, la matrice Redis
TLS/ACL/HA, Vitest, Playwright e artwork.

## Correttezza degli abort SignalR concorrenti (#155)

Il preStop #152 e i contatori #153 non bastavano per garantire che
`Abort()` fosse chiamato **una volta per trasporto** quando una
connessione passa dal controllo iniziale di `TryTrack` e viene
aggiunta al registro mentre, in parallelo, parte un drain:
il callback preStop/`ApplicationStopping` poteva abortire il contesto
già inserito e il controllo post-inserimento poteva ripetere l'abort.
`LobbyRosterHub.OnConnectedAsync` poteva aggiungere un terzo abort
dopo il rifiuto da `TryTrack`. Oltre al rumore nei log di SignalR, il
counter `shirokuro.signalr.drain_abort_attempts` sovracontava la
stessa connessione.

Ogni entrata in `LobbyConnectionDrainRegistry` ha ora uno stato
**locale atomico** con CAS `Active → AbortAttempted` oppure
`Active → Disconnected`. Un solo percorso vince e può chiamare
`HubCallerContext.Abort()` e incrementare il contatore. Se
`Untrack` ha già osservato il disconnect, un vecchio snapshot del
registro non deve abortire quel contesto; una chiamata abort già
in corso prima di `Untrack` non può essere annullata.
`OnConnectedAsync` mantiene `Context.Abort()` per un drain
rilevato **prima** di coinvolgere il registro, ma non lo ripete dopo
un `TryTrack` fallito: il registro ha già eseguito il suo tentativo
best-effort.

Tre test xUnit con gate `TaskCompletionSource` (senza sleep o
segnali OS al runner) forzano il controllo **dopo l'inserimento**
a completare *dopo* (1) preStop, (2) shutdown host attraversando
`OnConnectedAsync`, (3) una disconnessione già osservata seguita
da preStop. Verificano rispettivamente un solo abort, un solo abort
anche con la doppia callback Hub/registro, e nessun abort di un
contesto già disconnesso. Restano in vigore le barriere auth,
privacy dei roster, liveness/readiness e il smoke Linux reale
SIGUSR1→SIGTERM della #154.

Questo non prova né garantisce che `Abort()` faccia terminare
istantaneamente una connessione TCP esterna: il bilanciatore e le
sessioni browser devono ancora essere testati in staging (#21).
Il meccanismo non trasferisce connessioni, non modifica le regole
PvP né annulla operazioni Redis CAS già iniziate.

## PreStop: isolamento degli errori nei subscriber e dispose concorrente (#156)

La notifica locale `IPreStopDrainNotification.PreStopStarted` invoca
i callback registrati **sincronicamente** durante `SIGUSR1`.
`CancellationTokenSource.Cancel()` chiama gli observer, ma può
lanciare `AggregateException` se anche uno solo fallisce:
senza protezione quell'eccezione poteva propagarsi dal callback di
`PosixSignalRegistration` e compromettere il grace period preStop.

`HostLifetimeReadinessDrainSignal` mantiene ora lo stato di drain
monotono **prima** della notifica e isola le sole
`AggregateException` causate dagli observer di `Cancel()`;
la cancellazione .NET tenta tutti i callback nonostante l'errore.
Il registry SignalR continua ad abortire i socket tracciati anche
quando un altro observer è guasto; un SIGUSR1 ripetuto non ripete
né la notifica né la metrica first-transition. Non vengono registrati
messaggi/eccezioni dei subscriber, che potrebbero contenere dettagli
sensibili. Altre eccezioni inattese fuori dalla notifica non vengono
soppresse genericamente.

Durante la dismissione dell'host la registrazione POSIX viene
smontata prima della fonte di cancellazione. Se un SIGUSR1 già
distribuito attraversa `Dispose()`, la relativa
`ObjectDisposedException` è gestita esclusivamente **quando
il teardown è effettivamente iniziato**. Il
`CancellationToken` immutabile viene catturato prima
dell'installazione dell'handler ed è leggibile anche dopo il
dispose della source. `Dispose()` è idempotente.
La protezione non promette l'invio di nuovi eventi dopo la fine
dell'host né sostituisce `ApplicationStopping`:
`SIGTERM` resta il fallback autorevole.

Quattro test xUnit deterministici (failure di observer,
registro SignalR con socket attivo, dispose già avvenuta
e dispose che si sovrappone a un observer in esecuzione)
esercitano il comportamento senza mandare veri segnali al
runner; il test di processo Linux reale di #154 resta in CI.

Questa è una garanzia locale sulla robustezza del dispatcher.
Non dimostra che il bilanciatore abbia rimosso il pod,
che ogni WebSocket TCP sia realmente chiuso, che un client
si sia riconnesso altrove o che un CAS Redis già avviato
possa essere annullato. Queste prove rimangono operative in #21.

## Test del vero transport WebSocket SignalR nel TestServer (#157)

I test delle #152/#155 simulavano `HubCallerContext.Abort()` e il
processo Linux #154 usava un contesto Hub fittizio con `SIGUSR1`
**reale**. La #157 verifica un passo successivo: nel job backend
xUnit, ASP.NET Core `TestServer.CreateWebSocketClient()` attraversa
la **vera pipeline SignalR pre-match**, con autenticazione mediante
cookie guest firmato dal server e registro guest attivo, handshake
JSON SignalR con framing `0x1e`, invocazione di `GetRoster`
autorizzata e risposta privata conforme alla membership.

Durante la connessione aperta, l'invocazione del preStop trusted
`HostLifetimeReadinessDrainSignal.BeginPreStopDrain()` deve causare
la chiusura del **WebSocket** osservata dal client entro un tempo
limitato. Il nodo rimane vivo (`ApplicationStopping=false`,
`/api/health=200`) ma rifiuta nuova readiness (`/api/ready=503`
con `Cache-Control: no-store`) e nuove negoziazioni SignalR
(`503/no-store`, senza cookie né redirect). Test separati negano
`POST /hubs/lobbies/negotiate` con **401/no-store** in assenza di
cookie o con ticket invalido. Il normale test Linux SIGUSR1/SIGTERM
di #154 continua a essere eseguito indipendentemente dalla suite.

Si tratta di un **WebSocket effettivo in-process** gestito da
TestServer, non di un falso `HubCallerContext`, ma **non**
attraversa una vera connessione TCP, TLS edge, reverse proxy,
Kubernetes o load balancer multi-nodo. Non dimostra riconnessione
automatizzata su una seconda istanza, che il LB abbia propagato
readiness, né rollback di CAS Redis già avviati. La prova di
rollout, grace period e failover in staging reale rimane aperta
nella #21. Le identità di test sono fittizie e nessuna stanza o
cookie è esposto come etichetta di telemetria.

## Test della privacy WebSocket pre-match e revoca dopo handshake — #158

Le prove TestServer della #157 vengono estese in #158 con vere
invocazioni SignalR JSON **su socket autenticato**, oltre al solo handshake
o alla chiusura in preStop. Un guest membro della stanza A riceve lo
**stesso errore generico** richiedendo la stanza B (con un membro diverso)
o una stanza inesistente: nessuna completion deve includere un roster,
un ID stanza/membro privato o informazioni riservate. Dopo due rifiuti,
il socket deve ancora restituire il roster della sola stanza A.

Un secondo caso registra un guest Development, legge il proprio roster,
**revoca il suo ticket nel registry** mentre il WebSocket resta aperto
e verifica che il successivo `GetRoster` sia respinto senza dati.
Anche `GET /api/sessions/me` col medesimo cookie deve risultare 401/no-store
senza nuovo `Set-Cookie`. La verifica di appartenenza e di sessione è
lato server **a ogni invocazione**: il solo handshake non autorizza
letture future.

I test usano vere cookie firmate e framing WebSocket/SignalR su TestServer,
ma non dimostrano Redis distribuito in failover, reale rete TCP/TLS,
load balancer/proxy, reconnect del browser o matrice futura delle
osservazioni PvP (bloccata dalla #3/#9). Gli aspetti operativi sono
ancora tracciati nella parent #21.

## Prove logout e leave HTTP con WebSocket già connessi — #159

A valle delle verifiche #157/#158, la #159 esegue l'intero flusso di
**perdita dei permessi** senza fermarsi a una revoca diretta di fixture:
due guest con cookie autentici e due WebSocket SignalR JSON TestServer
accedono inizialmente alla stessa stanza. Il primo invia un autentico
`DELETE /api/lobbies/{room}/membership` con Origin/intent CSRF e revision
corretta; il server risponde 204/no-store. Sul WebSocket **già aperto**
`GetRoster` deve fallire con completion opaca e nessun roster del secondo
partecipante. Il guest uscito rimane correttamente autenticato su HTTP,
mentre il secondo socket vede il solo membro rimasto a revision incrementata.

Nel caso distinto il primo guest effettua il vero
`DELETE /api/sessions/guest`, anch'esso protetto dal guard CSRF.
Il backend revoca il ticket e rimuove le sue membership; la connessione
SignalR già negoziata non può più leggere il roster e lo stesso ticket
su `GET /api/sessions/me` restituisce 401/no-store. Il secondo
guest non subisce revoca e riceve una vista aggiornata filtrata a server.
I test usano timeout bounded e non tracciano in metriche cookie, room ID
o altri identificativi.

Questa verifica rafforza la sicurezza del **trasporto pre-match**:
non riguarda ancora l'osservazione delle mosse nascoste del futuro match
(#3/#9) e non simula una rete TCP/TLS reale, bilanciatore, WebSocket tra
nodi o Redis failover di produzione. La validazione di rollout con load
balancer e i casi cross-node di #21 restano necessari.

## Fence di revoca dopo roster asincrono SignalR — #160

Per un `GetRoster(lobbyId)` guest ricevuto su WebSocket già
autenticato il server non presume che l'autorizzazione della connessione
sia eterna. Oltre alla verifica guest iniziale, `LobbyRosterHub`
controlla `GuestSessionRegistry.IsActiveAsync` **dopo** l'`await`
della lettura member-scoped e **prima** di ritornare il DTO al trasporto
SignalR. Se il ticket è stato revocato mentre il backend Redis rispondeva,
o se non è più verificabile per indisponibilità dell'autorità, ritorna
solo `HubException("Lobby unavailable.")`: non il roster già letto.
La fence di preStop viene ricontrollata dopo la seconda richiesta, così
il drain resta fail-closed anche se inizia durante quest'ultima.

**Impatto operativo:** ogni `GetRoster` guest riuscito causa due
validazioni di sessione contro l'autorità configurata (in
Production/Staging: **due letture Redis**), oltre all'accesso al
roster. Monitorare la latenza del path e gli errori Redis; non
introdurre cache positiva per aggirare la revoca server-side.
Questo riduce una finestra ben definita, **non** è una transazione
atomica tra revoca, lettura e network send: una revoca che accade
dopo il controllo finale può concorrere con la serializzazione/invio.
Per la garanzia forte di annullamento immediato anche dopo la
verifica finale servirebbe un protocollo di fencing distribuito e/o
interruzione delle connessioni già attive tra nodi (resta #21).
Nessuna di queste verifiche riguarda ancora match/PvP osservazioni
nascoste (#3/#9).

La regressione xUnit simula due registry su `IDistributedCache`
condivisa e blocca deterministicamente una lettura mentre il secondo
registro revoca; la prova WebSocket integra `DELETE /api/sessions/guest`
HTTP con due connessioni SignalR e una lettura ritardata. Entrambe
isolano un preciso ordine di operazioni. TestServer in-process non
sostituisce TCP/TLS reale, ingress LB, Redis reale tra processi o rollout
Kubernetes di produzione.

## Autorizzazione guest HTTP dopo lettura roster in-flight — #161

Le route `GET /api/lobbies/{id}/roster` e
`GET /api/lobbies/{id}/roster/peek` conservano il loro controllo
member-scoped dello snapshot e la fence `IReadinessDrainSignal`.
Il principal HTTP è verificato dall'autenticazione cookie **prima**
di invocare il backend della lobby; una lettura Redis può restare
in volo mentre `DELETE /api/sessions/guest` invalida il ticket su
un altro nodo. #161 richiede quindi una seconda chiamata
`GuestSessionRegistry.CheckAuthorityAsync` immediatamente
**dopo** il risultato `IAsyncGuestLobby` e prima di restituire
il DTO, creare un ETag o considerare `If-None-Match`.
La fence di shutdown è ricontrollata dopo il secondo await.

Una sessione confermata inattiva/revocata ritorna HTTP **401**;
se l'autorità non è raggiungibile ritorna HTTP **503**, senza
trattare la mancanza di Redis come logout definitivo. Entrambi i
risultati hanno `Cache-Control: no-store`, nessun ETag e nessun
roster privato. Il 304 richiede ancora autenticazione **corrente**,
membership autorizzata e validatore canonico. Nessuna nuova route
pubblica o privilegio di read è introdotto.

**Impatto operativo:** per ogni lettura guest HTTP riuscita servono
due validazioni dell'autorità sessioni (l'autenticazione iniziale
e il controllo post-roster), che in Production/Staging significano
**due richieste Redis** anziché una, oltre alla lettura della lobby.
Monitorare la latenza e non introdurre cache positive della sessione:
invaliderebbero la revoca. Questo risolve la race delimitata fra
autenticazione iniziale e ritorno del backend, **non** realizza
un'operazione atomica fra revoca e invio HTTP; una revoca subito
dopo la verifica finale può ancora concorrere con la scrittura
del body. Lo staging multi-nodo con ingress/LB/TCP/TLS rimane #21.

I test xUnit differenziano revoca certa (401) e indisponibilità
della cache condivisa (503), con `TaskCompletionSource` bounded;
ulteriori integrazioni eseguono un vero logout HTTP parallelo a
una lettura trattenuta e verificano che l'altro guest mantenga
il roster ristretto al proprio membership. TestServer non simula
un vero bilanciatore o traffico cross-pod.

## Telemetria anonimizzata della seconda verifica guest roster — #162

Le verifiche di autorità guest delle issue #160 (SignalR) e #161 (HTTP)
eseguono un controllo successivo al risultato member-scoped del backend.
Il meter già esistente `Shirokuro.Api.Multiplayer` include ora
`shirokuro.roster.post_read_authority`, un Counter<long> incrementato
**una sola volta per ogni verifica post-read effettivamente conclusa**.
Ha esattamente due attributi di enum server-side, con domini chiusi:
`surface = http-refresh | http-peek | signalr` e
`outcome = active | inactive | unavailable` (9 combinazioni).

Non ci sono attributi da request: mai cookie, token, player ID, room ID,
invito, IP, percorso, Host, URL, messaggio di errore o dettagli Redis.
Gli ingress negati prima di leggere il roster, le probe anonime, gli
errori di membership e la cancellazione prima della seconda verifica
non contano come revalidazioni post-read. Il contatore è per chiamate,
**non** per guest unici e **non** dimostra che una risposta sia stata
serializzata o consegnata al client.

Per distinguere i due casi di fallimento dell'ultima verifica,
`LobbyRosterHub.GetRoster` usa `GuestSessionRegistry.CheckAuthorityAsync`
e alimenta l'outcome; continua a rispondere con identico
`HubException("Lobby unavailable.")` sia per revoca che per Redis
non disponibile, senza rivelare lo stato della sessione al peer.
Il trasporto HTTP conserva 401 per ticket certamente revocato e 503
per un'autorità indisponibile. Nessuna verifica Redis in più viene
introdotta rispetto alle due letture sessione per roster guest
preesistenti (#160/#161).

L'operatore può esportare solo questo meter con il meccanismo OTLP
HTTPS **opt-in** già documentato (mai un endpoint metrics HTTP
pubblico). Monitorare la frequenza relativa di `inactive` e
`unavailable` per i tre trasporti; senza un deployment/staging reale
non fissare soglie di alert o SLO non misurati. L'ultimo controllo
e l'invio dei byte non sono una transazione distribuita: questa
metrica non certifica l'atomicità della revoca (resta #21).

## Mutazioni roster HTTP: fence guest dopo il commit — #163

Le quattro mutazioni pre-match che rispondono con un
`LobbyRosterView` (`POST /api/lobbies`, `POST /api/lobbies/{id}/join`,
`PATCH /api/lobbies/{id}/hero`, `PATCH /api/lobbies/{id}/team`)
autorizzano il principal via cookie e applicano guard origin/intent,
quota e stato lobby. In una richiesta concorrente, tuttavia, un
`DELETE /api/sessions/guest` può revocare il ticket mentre il comando
lobby sta per terminare, così il DTO prodotto prima della revoca non è
più pubblicabile.

`AuthorizedMutationRosterAsync` rivalida l'autorità guest **dopo**
il completamento riuscito del comando `IAsyncGuestLobby`, ma **prima**
di creare `Results.Ok` o `Results.Created` (incluso il `Location`
che conterrebbe l'ID room). Confermata revoca => HTTP 401; autorità
Redis non verificabile => HTTP 503. Entrambi con no-store, senza
roster, ETag, Set-Cookie o Location. Un errore del comando stesso
(400/404/409/429/503) mantiene la semantica precedente senza
effettuare un lookup sessione supplementare. `DELETE membership`
non restituisce un roster: nessuna nuova verifica post-command.

La verifica HTTP del cookie all'inizio e il ricontrollo post-commit
richiedono ora **due letture di sessione guest per mutazione che produce
un roster**, contro una in precedenza, oltre al proprio CAS/lettura
lobby. Non utilizzare cache positiva locale per saltare la seconda.
La revalidation non riapplica, ritenta o annulla una mutazione riuscita.
Se essa ha committato *prima* del logout, l'operazione può avere avuto
effetto: è il worker/cleanup server-side di logout che rimuove le
membership, mentre la fence Redis #42 impedisce l'attore revocato
in CAS iniziati troppo tardi. Una revoca che arrivi *dopo* il check
finale può ancora concorrere con la serializzazione dei byte:
**non** è una transazione distribuita o una garanzia di revoca
istantanea sul trasporto TCP. Il rollout multi-pod, LB, TLS reale
rimangono nella parent #21.

TestServer esercita ciascuno dei quattro endpoint con una lobby
realmente mutata e un risultato sospeso tramite `TaskCompletionSource`
fino al completamento di un **vero logout HTTP**. Verifica 401/no-store,
assenza di dati e Location, sessione del secondo guest intatta e
cleanup delle membership. Un'altra suite rende indisponibile la cache
sessioni solo prima della seconda validazione, confermando 503/no
roster; test di successi e comandi invalidi misurano i lookup Redis.
Non equivale a una prova di stallo rete tra due pod in produzione.

## Fence preStop sul risultato delle mutazioni lobby HTTP — #164

`POST /api/lobbies`, `POST /api/lobbies/{id}/join` e le due
`PATCH /api/lobbies/{id}/hero|team` rispondono con un roster
member-only, e create aggiunge un header `Location` con l'invito.
Un comando partito prima di `SIGUSR1` può committare su Redis mentre
il nodo viene ritirato dal load balancer e avere un risultato ancora
in attesa di pubblicazione. La sola barriera di ammissione iniziale
non copre questa race.

La funzione `AuthorizedMutationRosterAsync` controlla
`IReadinessDrainSignal.IsDraining` **subito dopo** l'await del comando
e **prima** di consultare per la seconda volta il registry guest.
La fase di drain restituisce HTTP `503` e `Cache-Control: no-store`,
senza body, `Location`, ETag o `Set-Cookie`: non si attende
ulteriormente Redis sessioni quando il drain è già rilevabile.
Un secondo controllo **dopo** `CheckAuthorityAsync` chiude la
finestra in cui il preStop parte durante quel secondo await. Una
sessione guest Active non supera il drain; senza drain conserviamo
i risultati #163 (401 per revoca, 503 per Redis non disponibile).

Il comando originale può avere già modificato la lobby in Redis:
non viene annullato, ritentato né compensato da una cancellazione
silenziosa. I guard server-side di nuova ammissione durante drain
restano separati dalla facoltà degli ospiti già membri di iniziare
azioni durante la transizione. Questa protezione **nega solo la
pubblicazione dello snapshot completato**, non è un fence atomico
fra Redis CAS e rete. La vera chiusura di socket TCP/TLS, la rimozione
LB/ingress e la convergenza multi-nodo richiedono ancora staging
dedicato (#21). Non promettere revoca di byte già trasmessi.

Regressione: TestServer con true cookie guest, `IAsyncGuestLobby`
che committa in `InMemoryLobbyCoordinator` e trattiene solo la
risposta, `IReadinessDrainSignal` manuale e cache sessioni con
contatore delle letture. Per ognuna delle quattro route si testa
drain dopo commit (un solo lookup auth) e drain iniziato esattamente
durante il secondo lookup (due controlli e 503 anche con Active).
La suite Linux esistente verifica SIGUSR1 → drain/socket abort →
SIGTERM su processo reale; i test in-process non sostituiscono
una prova di rete/convergenza di più pod.

## WebSocket autentici cross-node nel job Redis TLS reale — #165

La verifica `RedisTlsProductionTests.CrossNodeProductionTlsRealSignalRWebSocketsHonorJoinAndRemoteLogout`
colma la differenza tra i test con `HubCallerContext` simulato
nei percorsi multi-istanza e i test WebSocket SignalR JSON in
Development sul singolo host. Nel job CI già previsto per Redis
TLS vengono avviati **due host ASP.NET Core Production** indipendenti
con DI e configurazioni **non sostituite**. Entrambi condividono
l'autorità sessioni guest, il persistito Data Protection key ring,
il Redis TLS Redis-lobby/Lua/fencing e la vista room server-side.

Tre client WebSocket stabiliscono un handshake JSON completo
(`wss://game.example/hubs/lobbies` nel TestServer, con Origin
`https://game.example` e vero cookie guest firmato):
Alice su host A, lo stesso cookie Alice su host B e Bob su host B.
Un non-membro non deve leggere o distinguere un invito privato
da una stanza mancante: errore SignalR opaco identico, nessun DTO.
Il join HTTP su B abilita la nuova membership e il successivo
`GetRoster` dei tre socket conserva la revisione e i soli membri.
Dopo logout HTTP su B l'autorità Redis e le membership diventano
indisponibili ai due socket già aperti di Alice, anche sul nodo A.
Bob conserva sessione e roster senza Alice. Il ticket revocato
non può più avviare una nuova negoziazione SignalR, né su A né su B.

La suite usa reali Redis TLS, firme di cookie e middleware Production,
non mock positivi di auth. L'interazione SignalR usa però la
implementazione WebSocket in-memory di ASP.NET Core TestServer:
**non** esercita TCP/TLS effettivo tra il client browser e Kestrel,
certificati del load balancer, ingress, processi/pod isolati,
routing sticky/non-sticky o rediscovery K8s. Questi restano nella
parent #21, che non può essere chiusa in base a tale test.
I/O con timeout esplicito; nessun ID, cookie, corpo privato o
connection string finisce nei nomi dei test o in telemetria.

## Verifica CI con due processi OS Kestrel e HTTPS/WSS effettivi — #166

La issue #165 ha verificato SignalR JSON con due host Production
`TestServer`, ma il WebSocket era un trasporto in-memory nel **medesimo**
processo del test. La #166 aggiunge
`RedisTlsProductionTestsKestrel` al job CI Redis TLS già esistente:
due processi Linux indipendenti `dotnet Shirokuro.Api.dll` usando
configurazione **Production non sostituita**, collegati allo stesso
Redis TLS (sessioni, lobby Lua/CAS, revocation fencing ed expiry
condivisi) e allo stesso volume di chiavi Data Protection e certificato
PFX. Il secondo processo parte **dopo** che il primo ha emesso un guest
e inizializzato il keyring persistente.

Kestrel serve `https://localhost:<porta-effimera>` su loopback in
ciascun processo, con il certificato server temporaneo firmato dalla
CA generata per Redis TLS nel job CI (SAN `localhost`). Il PFX è
assemblato nel test in una directory temporanea protetta e cancellato
dopo il run. I client `HttpClient` e `ClientWebSocket` usano le normali
verifiche di trust e hostname del runner: **nessun**
`DangerousAcceptAnyServerCertificateValidator` o callback TLS
di approvazione, niente X-Forwarded-Proto/Host simulati. L'allowlist
Production di `AllowedHosts` e `Security:AllowedOrigins` segue
esattamente l'autorità e la porta di ciascun listener; l'host non
ascolta su interfacce pubbliche.

La prova esercita `/api/ready` su entrambi i processi, cookie emesso
da A e verificato da B, create di A, impossibilità del non-membro Bob
di leggere via WSS su B, join HTTP su B, roster member-only sui
tre socket WSS (Alice su A e su B, Bob su B), logout HTTP di Alice
su B e rifiuto dei vecchi socket di Alice su entrambi i processi.
Bob mantiene sessione e proprio roster dopo il cleanup; negotiate
con cookie revocato è 401/no-store su A e B. Le operazioni sono
delimitate da timeout e il teardown termina e raccoglie i figli
OS anche in caso di asserzione fallita; gli output delle child API
vengono drenati senza essere stampati, così nessuna sessione, stanza,
URL, password o stack trace con configurazioni finisce nei log CI.

**Limite operativo**: questo è traffico TCP/TLS reale verso Kestrel
ma sempre su loopback di un singolo runner. Non verifica routing di un
load balancer, header forward di un proxy di ingress, sticky sessions,
ingress WSS/HTTPS esterno, certificati/rinnovi di dominio reali,
network partition o pod Kubernetes. La parent #21 rimane aperta
per uno staging end-to-end con edge e processi/pod su nodi reali.

## Reverse proxy reale TLS→TLS, WebSocket e bilanciamento CI — #167

Dopo due host TestServer con Redis TLS (#165) e due processi
Kestrel HTTPS/WSS su socket reali (#166), il test di integrazione
`RedisTlsProductionTestsKestrel.RealNginxTlsEdgeBalancesProductionKestrelHttpAndPrivateWss`
aggiunge un vero **Nginx Docker** davanti a entrambi i processi.
Esegue il solo scenario opt-in `SHIROKURO_TEST_REDIS_TLS` nel job CI
già dotato di Docker e CA/certificato di durata effimera.

Nginx ascolta soltanto su `127.0.0.1` e una porta effimera
(`--network host` Linux; nessuna publish/expose di porta pubblica).
Termina TLS con il certificato `localhost` e inoltra a un
`upstream` round-robin di due Kestrel **via TLS con validazione
esplicita del certificato**, `proxy_ssl_verify on`,
`proxy_ssl_name localhost` e root CA del runner in
`proxy_ssl_trusted_certificate`. I client usano il normale trust
OS senza callback bypass. Un `X-Shirokuro-CI-Upstream` presente
**solo nella configurazione temporanea di test** prova che entrambi
gli upstream rispondono a richieste HTTPS senza cookie affinity.

L'edge inoltra il `Host` originale al server, che lo valida
contro una allowlist esatta; non inoltra né si fida di
`X-Forwarded-Host` e sostituisce interamente `X-Forwarded-For`
e `X-Forwarded-Proto`. Il backend permette soltanto l'IP
esatto del trusted proxy (`127.0.0.1` nel test loopback).
L'Origin resta configurato **esplicitamente** sull'API per la
porta esterna, non è dedotto dagli header Host o forwarded.
Un `SslStream` certificato-verificato con SNI `localhost`
ma `Host: attacker.example` verifica 400/no-store senza
confondere TLS e Host. Le mutazioni da Origin ostile sono 403
senza cookie e i tentativi di XFF/XFP/XFH spoof non sostituiscono
gli header imposti dall'edge.

Gli upgrade WebSocket HTTP/1.1 con `Upgrade/Connection` passano
attraverso Nginx: Bob non membro riceve una completion SignalR
opaca, entra nella room via HTTP e quindi può leggere il roster;
due socket Alice e uno Bob vedono soltanto i due membri. Logout
Alice via edge revoca i due socket già aperti e HTTP cookie su
**entrambi** gli upstream, mentre Bob conserva la propria vista.
Il test non stampa body roster, ID o credenziali nei log Docker.

**Attenzione al negotiate SignalR:** il test apre WSS direttamente
tramite il percorso WebSocket-only, senza avvalersi di
`/hubs/lobbies/negotiate` per ottenere un connection ID locale.
Il bilanciamento senza affinità delle richieste **negotiate +
WebSocket upgrade** non è certificato né promesso: senza un
backplane/connection manager condiviso o sticky routing si può
atterrare su due processi diversi. Abilitare una strategia di
affinità esplicita sull'ingress o usare client WebSocket-only
che supportino skip-negotiation, e convalidare il comportamento
con la vera configurazione di staging prima del rollout.

Questo è un test CI con Docker e Kestrel sullo **stesso host**,
non un deployment Kubernetes multi-pod, un LB/cloud ingress,
terminazione TLS di dominio esterno, failover proxy o fault
injection di rete. Non chiudere la parent #21 sulla sola base
di questa prova.

## Affinità sperimentale Nginx per SignalR negotiate→WSS — #168

Il test #167 ha dimostrato il passaggio di WebSocket **diretti**
attraverso Nginx senza usare `/hubs/lobbies/negotiate`. La risposta
di negotiate contiene un `connectionToken` per una connessione
conosciuta **solo dal processo** SignalR selezionato. Senza affinità,
una successiva richiesta WSS `?id=<connectionToken>` può finire
su un altro upstream: Redis condiviso per le lobby **non** è un
SignalR connection manager distribuito.

`RedisTlsProductionTestsKestrel.RealNginxSignedGuestCookieAffinityPreservesNegotiateAndWssUpgrade`
esegue un secondo container Nginx temporaneo con stessa configurazione
TLS verificata di #167, ma con una scelta di backend deterministica.
La direttiva `map` estrae **solo** il valore del cookie autenticato
`Shirokuro.Session` da `$http_cookie` e il modulo upstream usa
`hash $shirokuro_route_key consistent;`. Il fallback per richieste
senza cookie è l'IP sorgente verso l'edge (non affinità per utenti
anonimi); l'header Cookie intero non viene usato come chiave. È
necessario preservare lo stesso ticket tra negotiate e WebSocket
upgrade; cookie estranei aggiunti non modificano la route.
Non vengono registrati valori di cookie, player, room o token
negotiation. L'affinità **non** concede accesso e non sostituisce
validazione della firma cookie, controllo sessione guest su Redis,
revocation fencing e verifica membership in Hub.

Il test usa due processi OS Kestrel Production distinti, Nginx
Docker su loopback, TLS autenticato client→Nginx e Nginx→Kestrel,
Data Protection keyring e Redis TLS condivisi. Con lo stesso cookie
esegue due POST negotiate verso lo stesso upstream, poi reali
upgrade WSS con token opachi e handshake SignalR JSON su tre
socket (Alice normale, Alice con cookie aggiuntivo, Bob). Verifica
errore opaco di Bob outsider e stanza inesistente, join e roster
member-only, logout via edge che revoca Alice su entrambi i
socket già aperti, HTTP 401 da entrambi i Kestrel, negotiate 401
col vecchio ticket; Bob conserva sessione e roster privato dopo
cleanup. I token negotiate restano in memoria del test, mai in
messaggi d'asserzione o log; un errore WebSocket viene sanitizzato.
Readiness Nginx viene verificata con retry/timeout prima di aprire
WSS, per non confondere il bind asincrono del container con errori
di autenticazione o connessione.

**Limitazioni e rollout**: usare l'intero ticket guest come
affinity key è solo un esempio CI controllato; ticket refresh,
reissue o rotazione delle chiavi possono modificarlo e cambiare
backend selezionato mentre una negotiate è in corso. Ingress
reale deve definire esplicitamente sticky cookie/affinità, durata
e capacità di fallback in rollout/drain, oppure supportare solo
WebSocket skip-negotiate a condizioni note. Questo test NON
dimostra affinity durante refresh, reconnect automatico, backend
down, scaling, sessioni account persistenti, due pod su host
diversi, WAF o certificati esterni. Nessuna configurazione Nginx
di test va copiata come deployment completo. Parent #21 aperta.

## Verifica SIGUSR1 del child smoke e ordine callback — #169

La callback Linux di `HostLifetimeReadinessDrainSignal.BeginPreStopDrain`
pubblica subito `IsDraining`, poi invoca
`CancellationTokenSource.Cancel` per notificare gli iscritti,
fra cui `LobbyConnectionDrainRegistry.BeginDraining` che esegue
`HubCallerContext.Abort()` sui socket. La cancellazione del token
è osservabile **prima** che tutte le callback siano terminate;
non è corretto dedurre da `Task.Delay(…, PreStopStarted)` che il
registro di connessioni abbia già tentato tutti gli abort.

Lo smoke Linux isolato `Shirokuro.PreStopSignalSmoke` prima verifica
immediatamente che il preStop sia attivo e che `ApplicationStopping`
**non** sia stato cancellato. Poi esegue una attesa asincrona
condizionale e **bounded (massimo 500 × 10 ms)** soltanto fino a
`registry.IsDraining` e `socket.AbortCount > 0`; il controllo
finale esige esattamente **uno** `Abort()`, registry drenato e
processo ancora vivo. Se il callback non termina, abortisce due volte
o arriva SIGTERM troppo presto, il test fallisce. Il secondo SIGUSR1
e il SIGTERM conclusivo mantengono la verifica dell'abort singolo.
L'applicazione **non** ha ricevuto nuovi endpoint di drain o
meccanismi di bypass: è esclusivamente una correzione delle assunzioni
di sincronizzazione del test in un vero processo OS.

La race è stata osservata nel workflow [#43876479160](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43876479160)
sul commit docs-only della #168, dopo una CI 3/3 verde con la
medesima logica runtime. La risoluzione resta subordinata a CI verde
sull'HEAD finale e non sostituisce i test reali di rolling restart
e ingress multi-pod della parent #21.


## Revoca sessione tra SignalR negotiate e WebSocket upgrade — #170

Un risultato positivo di `POST /hubs/lobbies/negotiate?negotiateVersion=1`
crea un **opaque connection token locale al nodo**, non un titolo di
accesso persistente. Anche il successivo WebSocket `GET /hubs/lobbies?id=...`
deve superare nuovamente l'autenticazione del cookie guest e la verifica
server-side del ticket: il token negoziato non è una credenziale.

La regressione xUnit TestServer della #170 crea Alice e Bob con sessioni
firmate e un roster pre-match. Alice ottiene negotiate 200 e poi invoca
il vero `DELETE /api/sessions/guest` **prima dell'upgrade**: vecchio
cookie + connection token già assegnato non devono completare il
WebSocket. `GET /api/sessions/me` risponde 401/no-store senza Set-Cookie.
La connessione negoziata di Bob resta valida e legge soltanto il proprio
roster (Alice rimossa). Ci sono timeout bounded; nessun token/cookie viene
stampato. Questa prova non abilita endpoint pubblici aggiuntivi e non
modifica alcuna regola PvP.

La suite è in-process TestServer/Development: dimostra il confine auth
HTTP→WebSocket, **non** un redirect/upgrade WSS affidabile su due nodi,
né rotazione cookie, failure/reconnect, aggiornamento hash LB o ingress
sticky di staging. La #168 copre separatamente il percorso TLS Nginx;
la #21 resta aperta per deployment multi-pod reale.


## Redis readiness per-probe nonce — #172

Il probe `GET /api/ready` Production prova i **due** connector Redis
indipendenti e il key ring. Per il cache delle sessioni non basta un
`GetAsync` che legge il valore lasciato da un probe precedente: un adapter
con `SetAsync` che completa ma non persiste la scrittura può ancora
restituire il valore storico. Un controllo con il vecchio byte statico
`[1]` e chiave fissa v2 avrebbe riportato erroneamente un 200.

Ogni nuova istanza di `RedisDeploymentReadiness` crea due chiavi tecniche
`...:readiness:write-probe-v3:<random>` indipendenti con 128 bit
casuali, una per ogni connector. A ogni *nuova* verifica la cache di
sessione riceve un nonce fresco di 128 bit; la lettura deve restituire
**esattamente quel nonce**. Un valore vecchio o la risposta di un altro
nodo non certificano la nuova scrittura. Tutte le chiavi tecniche hanno
TTL 10s e non contengono identità/cookie/lobby/secret; non vengono esposte
in HTTP, log o attributi metriche. Non usare chiavi probe per autenticazione,
revoca, quota o matchmaking.

Il comportamento HTTP resta invariato: `/api/ready` restituisce 200/503
minimal con `Cache-Control: no-store`, massimo 2 secondi per un probe
reale, coalescing delle richieste simultanee e memoizzazione per 3 secondi.
`/api/health` rimane liveness, non dipende da Redis; la verifica del key
ring non è saltata. xUnit (#172) prova un SetAsync silenziosamente
ignorato dopo il primo probe: il successivo è 503 e poi torna 200 dopo
recovery; un altro xUnit prova chiavi distinte tra due istanze.

La suite usa Redis del runner CI e TestServer, non dimostra la capacità
operativa di due pod/ingress reali, reconnect durante failover né disaster
recovery affidabile del deployment. Questa evidenza non chiude la parent #21.


## Readiness Redis lobby con read-back del nonce — #176

L'endpoint di readiness Production/Staging verifica due connettori Redis
indipendenti: storage delle lobby e cache delle sessioni guest, oltre al
key ring Data Protection. La #172 aveva già introdotto un nonce con
read-back per la cache guest, ma il connector lobby si fidava del solo
`StringSetAsync` positivo di un valore statico `"1"` senza leggere la
chiave. Un adapter o failover incoerente che conferma un SET non persistito
poteva quindi far apparire `/api/ready` sano anche quando le lobby non
erano scrivibili in modo affidabile.

Con #176 ogni probe reale genera **due nonce indipendenti e freschi a
128 bit**: uno binario per `IDistributedCache`, uno lowercase hex per
`IDatabase` lobby. Dopo i SET, `StringGetAsync` sullo **stesso
connector lobby** deve restituire esattamente il nonce scritto; solo dopo
il controllo locale viene letta la cache delle sessioni e verificato il
key ring. Il valore è tecnico e non diventa mai un ticket guest, una
membership, una quota, un invito, un log o una dimensione metrica. Una
mancanza, una vecchia lettura, un valore non corrispondente, un errore o
un timeout falliscono con HTTP 503 minimal e no-store. La metrica interna
espone soltanto la fase a cardinalità chiusa `lobby-read`, oppure
`timeout` quando il deadline scade, mai i valori di Redis.

Restano intatti il TTL tecnico di 10 secondi, la chiave casuale isolata
per servizio, il limite condiviso di 2 secondi per probe, la coalescenza
dei waiter, la cache del risultato di 3 secondi, la liveness indipendente
`/api/health`, Host/HTTPS e le autorizzazioni dei roster. I test xUnit
Production TestServer con `IConnectionMultiplexer` simulato riproducono
SET ignorato dopo un primo probe sano, GET nullo/corrotto/errore/bloccato,
liveness durante l'errore e recovery successivo 200. La suite standard
Redis TLS/ACL/HA/Sentinel rimane necessaria. Questa non è una verifica
dello staging multi-pod o del LB esterno: #21 resta aperta.

[CI applicativa #43904871330](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43904871330)
ha superato tutti e tre i job su `f6f44eb` (801 xUnit standard, 21
Redis TLS/ACL/HA/Sentinel, smoke Linux SIGUSR1, 347 Vitest, 43
Playwright, 35 Python, build .NET/frontend).


## Conferma della barriera Redis di revoca guest — #177

Il record Redis di revoca impedisce ai vecchi handler HTTP/SignalR,
autenticati prima del logout, di completare una mutazione lobby nel
successivo Lua CAS. Prima della #177, l'installazione chiamava
`IDatabase.StringSetAsync(..., when: When.NotExists)` e considerava
conclusa la barriera dopo il solo comando: un connettore che confermava
la scrittura senza persisterla avrebbe lasciato la vecchia richiesta
libera di modificare il roster pur facendo proseguire il logout.

Ora `RedisLobbySnapshotStore.FenceGuestAsync` verifica l'esatto
marker server-owned `"1"` tramite `StringGetAsync` sulla stessa
chiave hash SHA-256 e sullo **stesso connector Redis**. Vale sia dopo
SET NX=true sia per NX=false: una fence preesistente è idempotente solo
se il marker è ancora presente. Il secondo tentativo non rinnova il TTL
di 8 ore, non genera nuove identità e non rimuove il blocco. SET e GET
condividono un unico timeout di 2 secondi e la cancellazione prima
della lettura non invia una nuova operazione. Marker assente/corrotto,
scrittura ignorata, errore o timeout non sono una conferma: il caller
non deve restituire un falso logout completo. In Production il flusso
esistente ha già revocato il cookie guest, tenta la pulizia e risponde
503/no-store se non può confermare la fence. Il worker mantiene il
work item dovuto per un nuovo tentativo; le letture e le mutazioni
continuano a richiedere autorità Redis e CAS.

Il marker, la chiave e il player ID non diventano log, dimensioni di
metriche o testo di risposta HTTP. I test xUnit usano un
`IDatabase` `DispatchProxy` controllato per riprodurre scritture
ignorate, risposte NX=false incongruenti, GET assente/alterato,
eccezione, blocco, recovery, cancellazione e nessuno sliding TTL. Un
Production TestServer verifica 503/no-store per una fence silenziosamente
ignorata e un vecchio cookie già invalidato. Restano obbligatori i test
Redis TLS/ACL/HA/Sentinel e quelli Lua reali: il test non dimostra una
transazione atomica cross-store tra session cache e lobby Redis, né
staging con più pod e load balancer reale (#21).

[CI implementativa #43908925553](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43908925553)
SUCCESS 3/3 su `94e31b4`: 809 xUnit standard, 21 Redis,
SIGUSR1, 347 Vitest, 43 Playwright, 35 Python, build.


## Recovery guest: conferma di cancellazione prima dell'ack — #178

Dopo un RDB rollback, Redis può contenere nuovamente un ticket
revocato. Il cutoff esterno
`Security:GuestSessions:AcceptIssuedAfterUtc` rimane essenziale per
rifiutare i vecchi cookie; la pulizia dell'indice cifrato è una misura
complementare e non sostituisce quel controllo operativo.

Il worker `GuestSessionExpiryWorker` valida l'entry autenticata
Data Protection, installa il fence contro le azioni in-flight,
ripulisce le lobby e, solo per il work item pre-cutover con digest,
chiama `GuestSessionRegistry.RemoveRestoredSessionAsync`. La rimozione
fa `REMOVE → GET` della **medesima chiave SHA-256**, con un solo
timeout di 2s: soltanto un valore assente permette l'ack del lavoro
persistente. DELETE silenziosamente ignorato, GET non nullo,
eccezione, timeout o cancellazione non sono una conferma e il lavoro
resta ritentabile. Il worker continua con altri item indipendenti
nello stesso batch; chiavi già assenti sono idempotenti.

I test xUnit con cache deterministica simulano lost DELETE, GET
corrotto, eccezione e lettura bloccata, ripresa, retry e ack
selettivo del peer sano. Non sono introdotti nuovi endpoint,
identità, dati privati in risposte/log/metriche o regole PvP.
La CI mantiene Redis reale TLS/ACL/HA/Sentinel, ma la verifica non
certifica linearizzabilità cross-replica, restore operator o
deploy ingress multi-pod: parent #21 aperta.


## Conferma ZADD/ZScore della coda expiry guest cifrata — #179

La coda `RedisGuestExpiryIndex` conserva membri opachi Data Protection
in un sorted set Redis, con score che esprime la scadenza assoluta
UTC in millisecondi. La #179 verifica che ogni nuova emissione
guest e ogni work item post-logout sia stato **davvero osservato
in Redis**: `SortedSetAddAsync` è seguito da
`SortedSetScoreAsync` sulla **stessa coppia key/member**. Il valore
letto deve essere esattamente lo score intero previsto; la conferma
booleana dell'ADD da sola non basta. Il membro è cifrato e
randomizzato; non è una stringa scelta dal browser.

I comandi condividono un solo timeout di 2s; una cancellazione
dopo ADD non invia SCORE tardivo. Un risultato nullo,
score non corrispondente, guasto o read-back stalled
fallisce l'operazione, senza retry automatico di una scrittura
dal risultato ambiguo. L'emissione Production ritorna
503/no-store senza cookie su scheduling non confermato,
eseguendo il rollback guest preesistente. Per il logout
non viene dichiarata durabile una entry non verificata;
resta il fallback di cleanup già tracciato da #21/#41.

Test xUnit con fake `IDatabase` simulano scritture confermate ma
ignorate, SCORE errato, assente, errore e timeout, recovery,
cancellazione nel mezzo delle operazioni e boundary HTTP Production
senza leak di identificativi. La CI mantiene Redis reale con
TLS/ACL/HA/Sentinel, Linux preStop, Vitest e Playwright.
Il test non dimostra linearizzabilità cross-replica, Redis Cluster
o un ingress multi-pod operativo, ancora sotto #21.


## Conferma dell'ack dell'indice di scadenza Redis — #180

La rimozione dei work item di scadenza guest cifrati viene effettuata
solo dopo il completamento della pulizia lobby (fence e CAS) e,
per i ticket pre-cutover, della rimozione mirata confermata (#178).
Se il Redis adapter dichiarava concluso `SortedSetRemoveAsync` senza
eliminare il membro, il worker poteva erroneamente contabilizzare
l'ack; lo stesso item sarebbe riapparso in ogni scansione successiva
del bounded batch e avrebbe prodotto lavoro inutile.

Da #180, `RedisGuestExpiryIndex.AcknowledgeAsync` esegue
`SortedSetRemoveAsync → SortedSetScoreAsync` sulla **stessa chiave
namespace e lo stesso membro opaco autenticato**, con un unico
deadline di **2 secondi**. Soltanto `ZSCORE = null` (elemento
assente) conferma l'ack. Un ritorno `ZREM = false` non implica
errore se un altro nodo ha già rimosso lo stesso membro; una risposta
`ZREM = true` non prova successo se la lettura trova ancora il
membro. Score non nullo, errore o timeout fanno fallire l'ack senza
ritentativi interni di mutazioni incerte. Il worker conserva
l'elemento per sweep idempotenti futuri, elaborando comunque
gli altri item nello stesso batch e senza registrare
identificativi o ciphertext nelle metriche/log.

Test xUnit deterministici con `IDatabase DispatchProxy` coprono
rimozione ignorata, score residuo, errore e lettura bloccata,
recovery/idempotenza e cancellazione prima del readback; la suite
Redis TLS/ACL/HA/Sentinel viene ripetuta integralmente. Nessun
cambiamento a cookie guest, gameplay o visibilità PvP. Questa
conferma è osservata dal connettore Redis, non dimostra quorum
cross-replica, ingress multipod o failover reale (#21).

[CI implementativa #43927171180](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43927171180)
SUCCESS 3/3 sul codice `64a7d18`: 838 xUnit standard,
21 Redis TLS/ACL/HA/Sentinel, smoke Linux SIGUSR1,
347 Vitest, 43 Playwright, 35 Python e build.


## Conferma atomica di stanza e indici di quota nella creazione PvP — #181

La creazione di una stanza tramite `RedisLobbySnapshotStore.CreateBoundedAsync`
esegue un Lua `BoundedCreateScript` che applica le quote globali/per creator,
installa il record della stanza e scrive entrambi gli indici nello **stesso
hash slot Redis**. Un risultato `1` del primo Lua, tuttavia, non prova da
solo la persistenza delle tre scritture in presenza di adapter difettoso,
failover o conferma di rete ambigua. Prima della #181 un `201 Created`
poteva promettere una room inesistente o senza indici di capacità.

Da #181, **solo dopo un risultato create = 1** viene eseguito un secondo
Lua di sola lettura, sulla **stessa connessione e namespace**, che:
confronta byte-per-byte lo snapshot della stanza con il payload che il
server aveva appena inviato; verifica con `ZSCORE` che la stessa room key
sia membro degli indici **globale e del creator**; richiede che entrambi gli
score corrispondano a `PEXPIRETIME` della room calcolato da Redis stesso.
Non restituisce mai raw roster, token, id dell'attore o invito alla .NET
application; il risultato è un semplice flag numerico.

Il Lua di creazione e quello di conferma condividono **un deadline
complessivo di 2 secondi**, con verifica cancellazione prima del secondo
dispatch. Stato incompleto, indice mancante/stale, record cambiato,
read-back fallito o timeout fanno fallire la creazione con
`LobbyCreationUnavailableException`, *senza ripetere una mutation incerta*.
L'endpoint Production rende **503/no-store**, senza Location/roster
riservato e senza dettagli Redis, non `409 lobby_state_conflict`.
Restano **409** i veri conflitti di revisione, **429** le quote,
**404** gli accessi non autorizzati, ed è intatto il fence anti-revoca.
Il tentativo fallito può aver lasciato un orphan room che avrà il TTL
esistente: non viene dichiarato inesistente né cancellato arbitrariamente.

Regressioni xUnit con `IDatabase DispatchProxy` verificano 17 casi
inclusi tutti i read-back parziali, readback fermo/errore, cancellazione,
collisione/quota/fence, successo e Production TestServer 503→201 con
cookie firmato; le suite Redis reali TLS/ACL/HA/Sentinel validano gli
script contro Redis vero. Nessuna modifica alle regole PvP.
Questo read-back prova lo stato osservato sul connector, **non** una
commit guarantee fra repliche o una prova di failover staging multi-pod;
parent #21 resta aperta.

[CI implementativa #43932571497](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43932571497)
**SUCCESS 3/3** su `3bbbd80`: 855 xUnit standard,
21 Redis TLS/ACL/HA/Sentinel, Linux SIGUSR1, 347 Vitest,
43 Playwright, 35 Python e build.


## Verifica di persistenza dopo CAS delle lobby Redis — #182

Oltre al controllo di `CREATE` introdotto dalla #181, anche il
CAS di aggiornamento `RedisLobbySnapshotStore.CompareExchangeAsync`
deve garantire che la nuova revisione osservata sia realmente
presente sul connettore Redis prima di poter essere pubblicata.
Questo percorso serve ingresso stanza, scelta eroe, cambio squadra,
heartbeat esplicito del roster e cleanup amministrativo delle
membership dopo scadenza/revoca del ticket.

Un primo Lua effettua il confronto byte-per-byte della versione
precedente e aggiorna atomicamente snapshot e, quando previsto,
indici globale/per creatore. **Soltanto su CAS = 1**, un secondo
Lua **read-only**, sullo stesso namespace/hash slot, verifica:
- esatta uguaglianza dei bytes salvati con il nuovo snapshot
  autorevole; record assente o modificato non è una conferma;
- `PEXPIRETIME` della stanza positiva e osservata dal Redis stesso;
- per lobby con quota, entrambi i membri ZSET
  (indice globale e indice creator) presenti con score uguale
  all'expiry assoluta della stanza. Per snapshot legacy non
  quotati non si creano né si richiedono indici.

CAS + verifica condividono **una deadline complessiva di 2s**:
se l'operazione è cancellata dopo il CAS non si invia un secondo
script; esito di persist non confermato, errore o timeout **non**
vengono ripetuti automaticamente. Un eventuale CAS già avvenuto ma
superato da un nuovo CAS concorrente può produrre un conservativo 503,
mai uno snapshot vecchio spacciato come autorevole.
Per CAS = 0 restano le normali collisioni/revision conflict;
per fence = -1 resta il rifiuto guest già revocato.

Una verifica negativa solleva solo la nuova eccezione
`LobbyMutationUnavailableException`, senza payload, player ID,
room ID, password, Redis key, stack privato o eccezione interna.
API HTTP autorizzata rende 503/no-store senza roster o Set-Cookie;
SignalR rende l'errore opaco uniforme `Lobby unavailable.`.
I normali 409 di revisione, 429 quota e il 404 per lobbisti
non autorizzati non vengono riclassificati.

16 nuovi test xUnit deterministici iniettano CAS dichiarati riusciti
ma persi, bytes corrotti, ZSET mancanti/stale, expiry non valida,
letture bloccate/fallite, pre-cancel/cancel nel mezzo, CAS=0,
fence=-1, room non quotate e recupero HTTP Production da 503 a
roster autentico 200. Aggiornati test esistenti di contention
che eseguono una lettura successiva solo su CAS riuscito;
CI Redis TLS/ACL/HA/Sentinel ed E2E browser rimasti obbligatori.

[CI applicativa #43943844639](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43943844639)
**SUCCESS 3/3 su `888575e`**: 871 xUnit, 21 Redis, SIGUSR1,
347 Vitest, 43 Playwright, 35 Python e build.
L'osservazione sul singolo connettore non dimostra un quorum
cross-replica, un deploy multi-pod/staging reale o atomicità tra
sessioni, lobby e ingress; parent #21 ancora aperta.


## Verifica di cancellazione stanza e indici Redis dopo CAS — #183

I follow-up #181 e #182 verificano la persistenza della creazione e
dell'aggiornamento delle lobby PvP. La #183 completa la conferma
della **rimozione dell'ultima membership** e dell'eliminazione della
stanza in `RedisLobbySnapshotStore.DeleteIfCurrentAsync`.
Questo percorso è utilizzato sia da un utente autenticato che lascia
la stanza per ultimo, sia dal cleanup server-side dopo revoca o
scadenza della sessione guest.

Il primo Lua confronta lo snapshot corrente, controlla il fence
di revoca quando necessario ed esegue `DEL` della room e, per
room con quota, `ZREM` da entrambi gli indici globale e creator.
Un risultato Lua pari a 1 non prova da solo che l'intera rimozione
sia stata persistita: un adapter/failover può dichiarare il comando
riuscito quando solo parte della cancellazione è osservabile.

**Solo su DELETE=1**, un secondo Lua **read-only** sullo stesso
connettore/cluster hash slot conferma `GET room = nil`, più
`ZSCORE global = nil` e `ZSCORE creator = nil` per la stessa
chiave stanza se `QuotaTracked` è vero. Per room legacy senza
quota si conferma solo la scomparsa del record. Il risultato verso
.NET è un solo flag numerico, senza roster, playerId, cookie,
inviti o altre informazioni sensibili. Non modifica scadenze né
indice, e non ha side effect di ripristino.

DELETE e verifica condividono **un'unica deadline di 2 secondi**,
con cancellation check fra i dispatch. Se il Lua restituisce 0
(revisione non corrente), non viene inviato alcun readback; se
restituisce -1, resta il rifiuto del guest già revocato.
Se lo snapshot o uno degli indici permane, oppure il readback
fallisce, stalla o viene cancellato, l'operazione **non comunica
successo**: usa l'eccezione opaca
`LobbyMutationUnavailableException` già gestita dall'API con
**503/no-store senza Set-Cookie o roster**, o propaga
errore/timeout in modo fail-closed. Nessun retry automatico di un
DELETE con esito incerto. La successiva valutazione del worker
rimane idempotente e non deve ackare prematuramente il lavoro
che ha appena fallito.

14 nuovi test xUnit deterministici con `IDatabase DispatchProxy`
iniettano DELETE ignorato, room persistente, ZREM globale/creator
ignorato, readback falsato/fallito/bloccato, cancellation fra Lua,
pre-cancel, CAS=0/fence=-1, stato legacy non quotato e
ritentabilità in scenario recuperato. Un TestServer Production con
cookie guest firmato richiede 503/no-store senza leak quando il
DELETE viene ignorato, quindi 204 dopo il ripristino. Le suite
Redis TLS/ACL/HA/Sentinel reali, smoke Linux, Vitest e Playwright
restano obbligatorie.

**Limiti:** un orfano di solo indice (dopo DEL riuscito ma ZREM
perso) può mantenere occupata una quota fino alla scadenza
dell'indice o a una bonifica operativa; questa verifica rileva il
problema ma non promette ripristino automatico di dati già persi.
La conferma sul connettore non dimostra quorum replica,
linearizzabilità fra repliche né staging multi-pod reale:
parent #21 ancora aperta.

[CI implementativa #43957013734](https://github.com/KeyserDSoze/Game.Shirokuro/actions/runs/43957013734)
**SUCCESS 3/3** sull'HEAD codice `0a8d47d`: 885 xUnit standard,
21 Redis TLS/ACL/HA/Sentinel, Linux SIGUSR1, 347 Vitest,
43 Playwright, 35 Python e builds.


## Redis lobby quota integrity events (#189)

The existing `Shirokuro.Api.Multiplayer` meter now publishes
`Counter<long> shirokuro.redis.lobby_quota.integrity_rejections` with
**one fixed `operation` label**: `create`, `cas`, or `delete`.
There is **no** room ID, player/creator ID, cookie, IP, request path, key,
namespace, replica endpoint, exception text or query-supplied label.
The counter reports **rejected operations**, not distinct damaged rooms,
not the number of corrupted entries, and not all HTTP 503 responses.

The signal is emitted only **after an authoritative Redis Lua result**:

- `create`: #188 rejected a due-index pruning candidate because its room
  was still present, had no TTL, had a foreign/non-canonical key or exceeded
  a bounded due window (`-4`), before any index or room mutation.
- `cas`, `delete`: #187 found a missing or mismatched
  `PEXPIRETIME` / global / creator index deadline on a current indexed
  room (`-2`), before changing a room or index.
- Normal capacity denial (429), stale revision conflict (409), guest
  revocation, cancellation, Redis network loss/timeout, uncertain successful
  write and failed post-write confirmation **never** emit this specific
  counter. They retain their existing fail-closed/independent diagnostics.

**Operator usage:** an increase in this counter during a staging soak or
an actual deployment is evidence of a *known quota index inconsistency* and
should trigger investigation of Redis persistence/failover/restore procedures.
Do not treat a zero sample as proof that all keys are healthy: no Lua check
may have run, and metrics export is disabled by default. Do not use this
measurement as an auth/availability gate, automatically rewrite scores,
remove room keys or include room/creator IDs in exporter labels. Review the
two relevant Redis index/room TTL deadlines through a restricted offline
operator process and plan an explicit repair or restore. This counter is
optional OTLP telemetry over the pre-existing explicitly enabled HTTPS
collector; **no public /metrics endpoint** is registered. Only the
application's existing fixed-label meter is exported; this change
does not make real multi-pod ingress or Redis HA production-tested.

xUnit tests capture the actual `MeterListener` counter and validate its
type, unit, exactly three server-controlled operations and rejected unknown
enums; fake Redis confirms that only `-4/-2/-2` generate the metric, not
ordinary limits/fences/conflicts. Real two-connector Redis and independent
Production API TestServers verify a corrupt index produces the expected
counter while preserving HTTP 503/no-store and explicit-repair recovery.
See [issue #189](https://github.com/KeyserDSoze/Game.Shirokuro/issues/189);
the final complete CI gate is recorded there. Parent #21 remains open.


## Ingress preStop, upstream removal and negotiated reconnect (#190)

The CI-only Production integration now exercises the previously separate
Linux SIGUSR1 preStop and Nginx HTTPS edge paths as **one** lifecycle
scenario. Two independent Kestrel OS processes share the same validated
Redis TLS service and persisted Data Protection key ring. Nginx validates
Kestrel HTTPS certificates and uses *only* the Shirokuro signed guest
cookie (not arbitrary cookies, player IDs or request parameters) for
consistent negotiate/WSS affinity. A guest negotiates SignalR, upgrades
to WSS and reads their authorized room; a separate guest receives the same
opaque denial as an unknown room.

The test sends real SIGUSR1 only to the test Kestrel PID owning an active
guest WebSocket, never the CI runner or Redis. It checks that this child
**stays alive** and answers /api/health 200 but /api/ready **503/no-store**
and refuses new SignalR negotiate. The previous WSS must close/abort.
Next, the operator-side fixture marks that upstream `down` in the
bind-mounted Nginx config, validates it using `nginx -t` and reloads the
edge. This is an explicit simulated LB withdrawal: it does **not** rely
on Nginx to replay uncertain POSTs, which could double-create a room.
Only after removing the drained upstream does the browser simulation
open a **new** negotiate+WSS connection via the same signed cookie and
confirm that the other Kestrel process serves the current Redis-backed
private roster. A new authenticated non-member still gets the same
private-safe error for both an existing room and an unknown room.
A subsequent logout on the surviving upstream revokes the recovered
cookie and socket. The graceful preStop itself must **not** revoke a
session or delete a room.

This verifies the important *behavioral contract* of graceful drain
and authenticated reconnect using real TCP/TLS, process signals, an
actual Nginx proxy and Redis persistence. It is NOT a Kubernetes rollout,
nor evidence that a real external ingress controller updates endpoints
fast enough, that any client will automatically retry, or that a real
multi-zone Redis failover will meet production SLOs. Production still
requires operator validation of LB readiness polling, propagation delay,
orchestrator grace periods and client reconnect policy in staging.
The tests run without certificate-validation bypasses, public admin
endpoints, test credentials stored in git, or logging cookies/room IDs.
See [#190](https://github.com/KeyserDSoze/Game.Shirokuro/issues/190)
for the final GitHub Actions SHA gate; parent #21 remains open.


## Bounded negotiate convergence after Nginx reload (#192)

The real-edge CI exercise #190 explicitly removes the drained Kestrel
upstream with `nginx -t` plus `nginx -s reload`. A previously established
connection can still overlap an old Nginx worker while the new routing
configuration is being installed. In [#192](https://github.com/KeyserDSoze/Game.Shirokuro/issues/192),
one such transient was observed: Bob had successfully renegotiated WSS on
the healthy node, but the *first* negotiated connection for a new outsider
received a **503** from the drained/old routing path, even though its
guest issuance had succeeded.

Only inside this **isolated test helper**, and only after a successful
edge reload, a SignalR `negotiate` request may be retried up to eight
total times with 250 ms between attempts, **iff** its reply is an
explicit HTTP **503** with `Cache-Control: no-store`, no `Set-Cookie`
and no `Location`. An HTTP 200 is consumed once, and other responses
(401, 429, 502, 503 without that evidence, etc.) fail immediately.
A bounded series of 503 replies fails with a static message that includes
neither connection token nor guest/room IDs. The test still verifies that
each successful negotiate is served by the **surviving** Kestrel backend,
that the WSS upgrade works, and that an authenticated outsider receives
the same private-safe denial as a missing room. xUnit tests encode this
closed retry predicate independently.

In an additional CI run, an HTTP 200 negotiate reached the healthy node
but the following *new WSS upgrade* still raced an old Nginx worker.
The fixture now releases its prior HTTP keep-alive and WebSocket clients
before reload and permits up to **four fresh negotiate+WSS handshake
pairs** while the old worker drains. A failed WSS upgrade disposes its
socket: the old process-local SignalR token is **never replayed**.
Every new negotiate still follows the 503/no-store-only rule above;
every HTTP 200 must identify the surviving upstream, with strict TLS and
the private roster authorization check still mandatory. A persistent
WSS failure, a wrong upstream or any unexpected HTTP status fails the
fixture. Only this CI reconnection handshake is retried: no room mutation
or uncertain gameplay action is repeated.

This is not automatic retries for lobby CREATE/JOIN/PATCH/DELETE,
not an API/runtime change, and not proof that a real Kubernetes/ingress
controller converges in 2 seconds. CI has ordinary strict TLS validation;
no permissive callback or credential echo is introduced. Operators still
need a staging soak and to define their actual ingress drain/convergence
SLO. Parent #21 stays open.


## Physical TTL confirmation for Redis room readiness probe (#193)

Production readiness already writes and reads back a fresh random nonce on each
independent Redis connector (#172). A successful room-key SET/GET is not proof
that the probe key will expire: a lost expiry would leave permanent technical
keys while misleadingly reporting the process as ready. Following the room
nonce check, readiness now requires the Redis key's **actual remaining TTL** to
be positive and no longer than the requested 10 seconds. Missing, expired,
negative, extended or unreadable TTL, including timeout, means 503/no-store.

The check is read-only, uses the same Redis connector and the **same 2-second
shared deadline** as all other readiness steps; there are no mutation retries.
No identifiers, Redis key, nonce, address or exception detail reach responses
or telemetry. The existing 3-second coalescing/cache, encrypted key-ring
validation, node drain and process liveness remain unchanged. This checks the
room Redis connector only; the abstract `IDistributedCache` interface does
not provide a physical TTL query for the separate guest-session store.
This does not prove staging/multi-pod readiness: see issue #193 and parent #21.


## Staging HTTPS public probe runbook (#194)

The reusable `scripts/staging_probe.py` is a **read-only external** Python
standard-library smoke for existing production `GET /api/health` and
`GET /api/ready` contracts. It is not an actuator for pod drain, Redis,
guest-session issuance, SignalR or gameplay. All targets must be separately
supplied as **bare HTTPS origins** (no URL paths, query, fragment, userinfo,
credentials or insecure HTTP). TLS certificate and hostname verification use
the normal system trust store; redirects are never followed.

For each configured target/sampling round, `/api/health` must return HTTP
200 with the exact JSON object `{"status":"ok","application":"Shirokuro"}`.
`/api/ready` must return exactly either 200 + `{"status":"ready"}`
or 503 + `{"status":"unavailable"}` as requested by the operator.
Both must have `Cache-Control: no-store` and a JSON Content-Type, with
no `Set-Cookie`, `Location` or `Retry-After`. Extra/duplicate JSON fields,
unexpected HTTP statuses, oversized responses (over 1024 bytes), invalid
UTF-8/JSON, redirects, timeout, TLS and network failures are hard failures;
the tool never prints URL, response contents, error text, credentials or
identity. No cookie or Authorization header is sent.

Baseline examples (replace the .invalid placeholders with **actual** staging
origins that resolve and have valid TLS certificates):

```sh
python scripts/staging_probe.py \
  --base-url https://node-a.staging.example.invalid \
  --base-url https://node-b.staging.example.invalid \
  --expected-ready ready --samples 6 --interval 5 --timeout 3
```

For a real **separately orchestrated** preStop, while the draining process is
still alive and reachable via its own HTTPS origin:

```sh
python scripts/staging_probe.py \
  --base-url https://draining-node.staging.example.invalid \
  --expected-ready unavailable --samples 4 --interval 2 --timeout 3
```

Check the surviving node with `--expected-ready ready` independently.
The operator must trigger SIGUSR1, withdraw the ingress/LB endpoint and later
send SIGTERM; **the runner never does any of these actions**. The preStop
contract is health 200 / readiness 503 before the process terminates;
after SIGTERM, health 200 is no longer expected, and this runner should not
be invoked with that expectation. Sample bounds are 1–30, interval 0–30
seconds, per-request timeout 0.25–10 seconds. No automatic retries.

This does **not** verify that two URLs map to distinct pods, ingress endpoint
propagation, signed-cookie affinity, authenticated WSS reconnection, Redis
failover/SLO, guest revocation or match privacy. The operator must provide
separate deployment topology, browser reconnect and Redis HA evidence under
parent #21. The existing GitHub Actions suite still covers .NET xUnit,
real Nginx/Kestrel fixtures, Redis TLS/ACL/HA, Vitest and Playwright.
