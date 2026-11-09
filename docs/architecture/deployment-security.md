# Deployment security: explicit Origin trust — #28

This is an **intermediate hardening step**, not a production-ready
multi-node deployment. Shirokuro currently holds its room/session registries
in memory on each process; persistent accounts, distributed session revocation,
Data Protection key-ring persistence and shared room state remain #21.

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

The allowlist provides CSRF authorization, **not a Host-header firewall**.
Deployments must separately configure ASP.NET Core `AllowedHosts`,
TLS/HTTPS termination, and a reverse proxy that sanitizes external headers.

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

The proxy setting does not validate incoming `Host` headers or constitute
a complete public deployment. Configure and test `AllowedHosts`, trusted
edge TLS termination/HTTPS and the external host separately under #21.
A separate frontend origin also requires an explicit CORS policy review:
this feature does **not** enable cross-origin production credentialed CORS.

In `Development`, only loopback same-origin traffic and the two fixed Vite
origins (`http://localhost:5173` and `http://127.0.0.1:5173`)
are accepted. This prevents a forged `Host: evil.example` matching
`Origin: http://evil.example` from granting CSRF trust even in dev.

## Security regression coverage

`WebMutationGuardTests` uses real TestServer endpoints in Production,
Staging and Development. It checks unconfigured fail-closed behavior;
configured HTTPS issuance, lobby mutation and logout; preservation of
cookies/roster after rejected writes; malformed and spoofed origins;
host and forwarded-header spoof attempts; and the local Vite flow.
The Playwright local-browser workflows remain unchanged.

This does not solve shared Data Protection keys, full ingress/Host filtering,
distributed rate limiting, session persistence or durable player accounts:
all remain open in #21.
