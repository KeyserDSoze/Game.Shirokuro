#!/usr/bin/env python3
"""Read-only, bounded external HTTPS contract probe for Shirokuro staging.

This never starts drain, uses credentials, follows redirects or retries a
mutation. It is NOT proof of a multi-pod or Redis failover deployment.
"""

import argparse
import json
import ssl
import sys
import time
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, HTTPSHandler, ProxyHandler, Request, build_opener

MAX_RESPONSE_BYTES = 1024
MAX_SAMPLES = 30
MAX_TARGETS = 8


class ProbeError(Exception):
    """A deliberately non-sensitive validation error."""


class DenyRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def validate_base_urls(values):
    """Accept only bare HTTPS origins; never a URL carrying a secret."""
    if len(values) > MAX_TARGETS:
        raise ProbeError("Too many staging targets")
    origins = []
    for value in values:
        if (not value or value != value.strip() or
                any(c.isspace() for c in value) or
                "?" in value or "#" in value):
            raise ProbeError("Invalid staging configuration")
        try:
            parsed = urlsplit(value)
            port = parsed.port
        except ValueError as exc:
            raise ProbeError("Invalid staging configuration") from exc
        if (parsed.scheme != "https" or not parsed.hostname or
                parsed.username is not None or parsed.password is not None or
                parsed.path not in ("", "/") or parsed.query or parsed.fragment or
                (port is not None and port < 1)):
            raise ProbeError("Invalid staging configuration")
        origin = "https://" + parsed.netloc
        if origin in origins:
            raise ProbeError("Duplicate staging target")
        origins.append(origin)
    if not origins:
        raise ProbeError("Missing staging target")
    return origins


def validate_response(path, status, headers, body, expected_ready):
    """Validate the exact public, no-store JSON contract; never echo body."""
    expected_status = 200 if path == "/api/health" or expected_ready == "ready" else 503
    expected_body = ({"status": "ok", "application": "Shirokuro"}
                     if path == "/api/health" else {"status": expected_ready})
    if path not in ("/api/health", "/api/ready") or status != expected_status:
        raise ProbeError("Unexpected probe status")
    directives = [part.strip().lower()
                  for part in (headers.get("Cache-Control") or "").split(",")]
    if ("no-store" not in directives or
            headers.get("Set-Cookie") is not None or
            headers.get("Location") is not None or
            headers.get("Retry-After") is not None or
            (headers.get("Content-Type") or "").split(";", 1)[0].strip().lower()
            != "application/json"):
        raise ProbeError("Unexpected probe headers")
    if len(body) > MAX_RESPONSE_BYTES:
        raise ProbeError("Oversized probe response")
    def unique_fields(pairs):
        document = {}
        for key, value in pairs:
            if key in document:
                raise ValueError("Duplicate JSON member")
            document[key] = value
        return document

    try:
        parsed = json.loads(body.decode("utf-8"),
                            object_pairs_hook=unique_fields)
    except (ValueError, UnicodeDecodeError) as exc:
        raise ProbeError("Invalid probe JSON") from exc
    if type(parsed) is not dict or parsed != expected_body:
        raise ProbeError("Unexpected probe response schema")


def fetch_public_probe(opener, origin, path, timeout):
    """One read-only GET; do not follow redirects or expose response internals."""
    if path not in ("/api/health", "/api/ready"):
        raise ProbeError("Invalid public probe path")
    request = Request(origin + path, method="GET", headers={"Accept": "application/json"})
    try:
        response = opener.open(request, timeout=timeout)
    except HTTPError as error:
        # urllib represents legitimate unready 503 as HTTPError. Always
        # validate its complete public contract before accepting it.
        response = error
    with response:
        return response.getcode(), response.headers, response.read(MAX_RESPONSE_BYTES + 1)


def probe_one(opener, origin, path, timeout, expected_ready):
    status, headers, body = fetch_public_probe(opener, origin, path, timeout)
    validate_response(path, status, headers, body, expected_ready)
    return status


def make_strict_opener():
    """Direct HTTPS only: bypass implicit proxy environment and system config.

    urllib's build_opener() automatically installs a ProxyHandler from
    HTTPS_PROXY/ALL_PROXY unless one is explicitly provided. A preStop
    test must observe the intended direct node, not a proxy or LB path.
    Certificates and hostnames still use the default system trust policy.
    """
    return build_opener(ProxyHandler({}), DenyRedirect(),
                        HTTPSHandler(context=ssl.create_default_context()))


def run_checks(origins, expected_ready, samples, interval, timeout, *,
               opener=None, sleep=time.sleep):
    if (not isinstance(samples, int) or not 1 <= samples <= MAX_SAMPLES or
            not 0 <= interval <= 30 or not 0.25 <= timeout <= 10):
        raise ProbeError("Invalid staging probe bounds")
    if expected_ready not in ("ready", "unavailable"):
        raise ProbeError("Invalid readiness expectation")
    if opener is None:
        opener = make_strict_opener()
    for index in range(samples):
        for origin in origins:
            probe_one(opener, origin, "/api/health", timeout, expected_ready)
            probe_one(opener, origin, "/api/ready", timeout, expected_ready)
        if index + 1 < samples:
            sleep(interval)


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Bounded, unauthenticated HTTPS liveness/readiness staging probe")
    parser.add_argument("--base-url", action="append", required=True,
                        help="Bare HTTPS origin (repeat for each independently routed node)")
    parser.add_argument("--expected-ready", choices=("ready", "unavailable"),
                        default="ready")
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--interval", type=float, default=5)
    parser.add_argument("--timeout", type=float, default=3)
    arguments = parser.parse_args(argv)
    try:
        origins = validate_base_urls(arguments.base_url)
        run_checks(origins, arguments.expected_ready, arguments.samples,
                   arguments.interval, arguments.timeout)
    except Exception:
        # Never echo a supplied URL (possibly an internal host), response
        # payload, TLS exception, HTTP path, secret or other server detail.
        print("Staging probe failed: configuration, TLS/network or public contract.",
              file=sys.stderr)
        return 1
    print("Staging probe passed: " + str(arguments.samples) +
          " sample(s), " + str(len(origins)) + " target(s); expected readiness " +
          arguments.expected_ready + ".")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
