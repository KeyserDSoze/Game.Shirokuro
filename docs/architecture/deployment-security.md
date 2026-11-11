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

- a read-only Redis `PING` on the **lobby/mutation/expiry** multiplexer;
- a read-only missing-key lookup through the **separate guest session
  `IDistributedCache`** client (neither check writes session/lobby state);
- a small round-trip through the configured purpose-isolated ASP.NET Core
  **Data Protection** key ring to confirm the node can protect and unprotect.

All checks must pass. Responses are intentionally identical across failures:
`200 {"status":"ready"}` or `503 {"status":"unavailable"}`, always
`Cache-Control: no-store`. Exceptions, secret paths, Redis credentials,
player identities and invitation details are never returned to clients or
written to readiness logs. Each process coalesces probes behind a gate and
caches both success and failure for **3 seconds**, with a **2-second**
probe timeout. This is a bounded operational signal, not a full health audit.
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
| `shirokuro.guest_expiry.memberships_removed` | `mode=local\|redis` | Number of stale **lobby memberships** removed after a successful sweep, not the number of accounts, sessions or rooms. |

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
