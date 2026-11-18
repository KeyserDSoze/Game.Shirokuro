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
from urllib.request import HTTPRedirectHandler, HTTPSHandler, Request, build_opener

MAX_RESPONSE_BYTES = 1024
MAX_SAMPLES = 30


class ProbeError(Exception):
    """A deliberately non-sensitive validation error."""


class DenyRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def validate_base_urls(values):
    """Accept only bare HTTPS origins; never a URL carrying a secret."""
    origins = []
    for value in values:
        if not value or value != value.strip() or any(c.isspace() for c in value):
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
    try:
        parsed = json.loads(body.decode("utf-8"))
    except (ValueError, UnicodeDecodeError) as exc:
        raise ProbeError("Invalid probe JSON") from exc
    if type(parsed) is not dict or parsed != expected_body:
        raise ProbeError("Unexpected probe response schema")


def probe_one(opener, origin, path, timeout, expected_ready):
    request = Request(origin + path, method="GET", headers={"Accept": "application/json"})
    try:
        response = opener.open(request, timeout=timeout)
    except HTTPError as error:
        # An expected 503 readiness is a real HTTP error in urllib. Inspect
        # only its strict public contract; never print the error or URL.
        response = error
    with response:
        status = response.getcode()
        body = response.read(MAX_RESPONSE_BYTES + 1)
        validate_response(path, status, response.headers, body, expected_ready)


def run_checks(origins, expected_ready, samples, interval, timeout, *,
               opener=None, sleep=time.sleep):
    if (not isinstance(samples, int) or not 1 <= samples <= MAX_SAMPLES or
            not 0 <= interval <= 30 or not 0.25 <= timeout <= 10):
        raise ProbeError("Invalid staging probe bounds")
    if expected_ready not in ("ready", "unavailable"):
        raise ProbeError("Invalid readiness expectation")
    if opener is None:
        # Default, strict certificate/hostname verification. Do NOT add an
        # allow-untrusted-certificate option for staging or CI convenience.
        opener = build_opener(DenyRedirect(),
                              HTTPSHandler(context=ssl.create_default_context()))
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
