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
TestServer tests. **Guest authorization still uses per-process session
registries:** a cookie cryptographically readable on instance B is
correctly denied there if its guest session belongs only to instance A.
Shared session revocation, lobby persistence and multi-node reconnect
remain #21; this feature alone does not make multiplayer multi-node-ready.

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

This does not solve distributed session revocation, edge TLS/network validation,
distributed rate limiting, lobby persistence or durable player accounts:
all remain open in #21.
