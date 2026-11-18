#!/usr/bin/env python3
"""Observe a real, externally initiated preStop; never initiate one.

This checks only public, unauthenticated HTTPS GETs. A real staging node
must remain alive throughout the observation; it is not a Kubernetes or
multi-pod ingress test and must not be run after SIGTERM.
"""

import argparse
import math
import sys
import time

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


def watch_drain(origin, max_polls=12, stable_samples=3,
                interval=2.0, timeout=3.0, *,
                opener=None, sleep=time.sleep, notify=None):
    validated_bounds(max_polls, stable_samples, interval, timeout)
    # Exactly one direct-node HTTPS origin; never a query, bearer token,
    # room identifier, URL path, insecure origin or multi-target LB set.
    origin = probe.validate_base_urls([origin])[0]
    if opener is None:
        opener = probe.make_strict_opener()

    # A 503 before the initial 200 is not a verified transition. Do not
    # tell the operator to act until both public baseline responses pass.
    if read_state(opener, origin, timeout) != "ready":
        raise probe.ProbeError("Missing ready baseline")
    if notify is not None:
        notify("Baseline verified. Initiate preStop externally; keep node alive.")

    consecutive_unavailable = 0
    for _ in range(max_polls):
        sleep(interval)
        state = read_state(opener, origin, timeout)
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
                        help="One direct, certificate-verified node HTTPS origin")
    parser.add_argument("--max-polls", type=int, default=12)
    parser.add_argument("--stable-samples", type=int, default=3)
    parser.add_argument("--interval", type=float, default=2)
    parser.add_argument("--timeout", type=float, default=3)
    args = parser.parse_args(argv)
    try:
        watch_drain(args.base_url, args.max_polls, args.stable_samples,
                    args.interval, args.timeout, notify=print)
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
