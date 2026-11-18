#!/usr/bin/env python3
"""Observe a real, externally initiated preStop; never initiate one.

This checks only public, unauthenticated HTTPS GETs. A real staging node
must remain alive throughout the observation; it is not a Kubernetes or
multi-pod ingress test and must not be run after SIGTERM.
"""

import argparse
import ipaddress
import math
import sys
import time
from urllib.parse import urlsplit

if __package__:
    from . import staging_probe as probe
else:
    import staging_probe as probe

MAX_POLLS = 30
MAX_STABLE_SAMPLES = 6


def validated_bounds(max_polls, stable_samples, interval, timeout):
    if (type(max_polls) is not int or not 2 <= max_polls <= MAX_POLLS or
            type(stable_samples) is not int or
            not 2 <= stable_samples <= min(MAX_STABLE_SAMPLES, max_polls) or
            not isinstance(interval, (int, float)) or
            not math.isfinite(interval) or not 0.25 <= interval <= 10 or
            not isinstance(timeout, (int, float)) or
            not math.isfinite(timeout) or not 0.25 <= timeout <= 10):
        raise probe.ProbeError("Invalid drain watcher bounds")


def read_state(opener, origin, timeout):
    # Health must remain 200 even after the separately orchestrated SIGUSR1.
    # Both requests have strict public JSON/no-store/no-cookie contracts.
    probe.probe_one(opener, origin, "/api/health", timeout, "ready")
    status, headers, body = probe.fetch_public_probe(
        opener, origin, "/api/ready", timeout)
    if status == 200:
        state = "ready"
    elif status == 503:
        state = "unavailable"
    else:
        raise probe.ProbeError("Unexpected readiness HTTP status")
    probe.validate_response("/api/ready", status, headers, body, state)
    return state


def same_endpoint(first, second):
    """Reject trivially identical direct targets; this is NOT pod attestation."""
    def key(origin):
        parsed = urlsplit(origin)
        hostname = parsed.hostname
        try:
            hostname = str(ipaddress.ip_address(hostname))
        except ValueError:
            hostname = hostname.lower()
        return hostname, parsed.port or 443
    return key(first) == key(second)


def watch_drain(origin, max_polls=12, stable_samples=3,
                interval=2.0, timeout=3.0, *,
                opener=None, sleep=time.sleep, notify=None,
                survivor_origin=None):
    validated_bounds(max_polls, stable_samples, interval, timeout)
    # Optional second *direct* HTTPS origin, never an LB fallback. The same
    # SSL-verified, no-environment-proxy opener is shared by both nodes.
    targets = probe.validate_base_urls(
        [origin] if survivor_origin is None else [origin, survivor_origin])
    origin = targets[0]
    survivor_origin = targets[1] if len(targets) == 2 else None
    if survivor_origin is not None and same_endpoint(origin, survivor_origin):
        raise probe.ProbeError("Duplicate node endpoint")
    if opener is None:
        opener = probe.make_strict_opener()

    def observe():
        draining = read_state(opener, origin, timeout)
        if survivor_origin is not None and (
                read_state(opener, survivor_origin, timeout) != "ready"):
            # A healthy drain is not evidence of availability if its only
            # surviving node is also unready. Check even on the final sample.
            raise probe.ProbeError("Surviving node unavailable")
        return draining

    # Both nodes must pass their baseline before prompting external preStop.
    if observe() != "ready":
        raise probe.ProbeError("Missing ready baseline")
    if notify is not None:
        notify("Baseline verified. Initiate preStop externally; keep node alive.")

    consecutive_unavailable = 0
    for _ in range(max_polls):
        sleep(interval)
        state = observe()
        if state == "unavailable":
            consecutive_unavailable += 1
            if consecutive_unavailable == stable_samples:
                return
        elif consecutive_unavailable:
            # A 200 after any accepted 503 inside the watch window violates
            # the node's monotonic drain contract. Never reset the counter.
            raise probe.ProbeError("Readiness returned after drain")
    raise probe.ProbeError("No stable drain before bounded deadline")


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Watch an external preStop through strict public HTTPS GETs")
    parser.add_argument("--base-url", required=True,
                        help="Direct, certificate-verified draining-node HTTPS origin")
    parser.add_argument("--survivor-url",
                        help="Optional separate direct survivor-node HTTPS origin")
    parser.add_argument("--max-polls", type=int, default=12)
    parser.add_argument("--stable-samples", type=int, default=3)
    parser.add_argument("--interval", type=float, default=2)
    parser.add_argument("--timeout", type=float, default=3)
    args = parser.parse_args(argv)
    try:
        watch_drain(args.base_url, args.max_polls, args.stable_samples,
                    args.interval, args.timeout, notify=print,
                    survivor_origin=args.survivor_url)
    except Exception:
        # No origin URL, HTTP body, response paths, cookie, bearer token,
        # TLS error or Redis details are ever reported.
        print("Staging drain watch failed: baseline, TLS/network or probe contract.",
              file=sys.stderr)
        return 1
    print("Staging drain verified: monotonic unavailable readiness with liveness.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
