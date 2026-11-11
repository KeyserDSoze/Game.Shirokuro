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
`/metrics` route, telemetry exporter, external collector, or any secrets
in configuration. This code does not claim an operational monitoring backend
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
