"""Three-endpoint public ingress continuity tests (no real network or tokens)."""

import io
import unittest
from unittest.mock import patch

from scripts import staging_drain_watch as watcher
from scripts.tests.test_staging_drain_watch import ScriptedOpener


DRAINING = "https://draining.staging.example"
SURVIVOR = "https://survivor.staging.example"
EDGE = "https://ingress.staging.example"


class TripleOpener:
    def __init__(self, draining, surviving, ingress):
        self.by_origin = {
            DRAINING: ScriptedOpener(draining),
            SURVIVOR: ScriptedOpener(surviving),
            EDGE: ScriptedOpener(ingress),
        }
        self.calls = []

    def open(self, request, timeout):
        self.calls.append((request, timeout))
        for origin, listener in self.by_origin.items():
            if request.full_url.startswith(origin + "/api/"):
                return listener.open(request, timeout)
        raise AssertionError("Unexpected HTTPS origin")


class ThreeWayDrainTests(unittest.TestCase):
    def run_watch(self, transport, *, polls=3, stable=2, notify=None):
        sleeps = []
        watcher.watch_drain(
            DRAINING, polls, stable, 0.25, 1,
            opener=transport, sleep=sleeps.append, notify=notify,
            survivor_origin=SURVIVOR, edge_origin=EDGE)
        return sleeps

    def test_all_three_healthy_before_and_during_drain(self):
        transport = TripleOpener(
            ["ready", "unavailable", "unavailable"], ["ready"] * 3,
            ["ready"] * 3)
        notices = []
        self.assertEqual([0.25, 0.25],
                         self.run_watch(transport, notify=notices.append))
        self.assertEqual(18, len(transport.calls))  # 6 per baseline + poll
        self.assertEqual(1, len(notices))
        self.assertNotIn("staging", notices[0])
        self.assertTrue(all(req.get_method() == "GET" and
                            req.get_header("Cookie") is None and
                            req.get_header("Authorization") is None
                            for req, _ in transport.calls))

    def test_edge_not_ready_at_baseline_never_prompts_operator(self):
        transport = TripleOpener(["ready"], ["ready"], ["unavailable"])
        notices = []
        with self.assertRaises(watcher.probe.ProbeError):
            self.run_watch(transport, notify=notices.append)
        self.assertEqual([], notices)
        self.assertEqual(6, len(transport.calls))

    def test_edge_fails_after_drain_begins(self):
        transport = TripleOpener(["ready", "unavailable", "unavailable"],
                                 ["ready"] * 3, ["ready", "unavailable"])
        with self.assertRaises(watcher.probe.ProbeError):
            self.run_watch(transport)
        self.assertEqual(12, len(transport.calls))

    def test_edge_fails_on_last_required_503_sample(self):
        transport = TripleOpener(["ready", "unavailable", "unavailable"],
                                 ["ready"] * 3,
                                 ["ready", "ready", "unavailable"])
        with self.assertRaises(watcher.probe.ProbeError):
            self.run_watch(transport, polls=2)
        self.assertEqual(18, len(transport.calls))

    def test_edge_recovers_later_but_single_503_is_still_failure(self):
        transport = TripleOpener(["ready", "unavailable", "unavailable"],
                                 ["ready"] * 3,
                                 ["ready", "unavailable", "ready"])
        with self.assertRaises(watcher.probe.ProbeError):
            self.run_watch(transport, polls=2)
        self.assertEqual(12, len(transport.calls))

    def test_no_drain_stays_bounded_at_six_gets_per_round(self):
        transport = TripleOpener(["ready"], ["ready"], ["ready"])
        with self.assertRaises(watcher.probe.ProbeError):
            self.run_watch(transport, polls=2)
        self.assertEqual(18, len(transport.calls))

    def test_edge_invalid_http_schema_and_cache_are_rejected(self):
        for mode in ("redirect", "bad-status", "invalid", "cache"):
            transport = TripleOpener(["ready", "unavailable"],
                                     ["ready", "ready"], ["ready", "ready"])
            edge = transport.by_origin[EDGE]
            if mode in ("redirect", "bad-status"):
                edge.states[1] = mode
            elif mode == "invalid":
                edge.corrupt_at = 4
            else:
                edge.broken_headers_at = 4
            with self.subTest(mode=mode), self.assertRaises(watcher.probe.ProbeError):
                self.run_watch(transport, polls=2)

    def test_requires_survivor_for_edge_and_validates_before_any_io(self):
        transport = TripleOpener(["ready"], ["ready"], ["ready"])
        with self.assertRaises(watcher.probe.ProbeError):
            watcher.watch_drain(DRAINING, 2, 2, 0.25, 1, opener=transport,
                                edge_origin=EDGE)
        self.assertEqual([], transport.calls)

    def test_same_node_alias_for_any_pair_rejected_before_io(self):
        duplicate_sets = (
            (DRAINING, SURVIVOR, "https://DRAINING.staging.example:443"),
            (DRAINING, SURVIVOR, "https://SURVIVOR.staging.example/"),
            ("https://[::1]", SURVIVOR, "https://[0:0:0:0:0:0:0:1]:443"),
            (DRAINING, "https://[::1]", "https://[0:0:0:0:0:0:0:1]"),
        )
        for drained, survivor, edge in duplicate_sets:
            transport = TripleOpener(["ready"], ["ready"], ["ready"])
            with self.subTest(values=(drained, survivor, edge)):
                with self.assertRaises(watcher.probe.ProbeError):
                    watcher.watch_drain(drained, 2, 2, 0.25, 1,
                                        opener=transport,
                                        survivor_origin=survivor,
                                        edge_origin=edge)
            self.assertEqual([], transport.calls)

    def test_invalid_edge_url_rejected_before_io(self):
        for edge in ("http://example.test", "https://user:pass@example.test",
                     "https://example.test/path", "https://example.test?key=x",
                     "https://example.test#fragment"):
            transport = TripleOpener(["ready"], ["ready"], ["ready"])
            with self.subTest(edge=edge), self.assertRaises(watcher.probe.ProbeError):
                watcher.watch_drain(DRAINING, 2, 2, 0.25, 1, opener=transport,
                                    survivor_origin=SURVIVOR, edge_origin=edge)
            self.assertEqual([], transport.calls)

    def test_cli_failure_never_reveals_edge_or_client_secret(self):
        transport = TripleOpener(["ready", "unavailable"],
                                 ["ready", "ready"],
                                 ["ready", "unavailable"])
        with patch.object(watcher.probe, "make_strict_opener",
                          return_value=transport):
            with patch("sys.stderr", new_callable=io.StringIO) as err:
                result = watcher.main([
                    "--base-url", DRAINING,
                    "--survivor-url", SURVIVOR,
                    "--edge-url", EDGE,
                    "--max-polls", "2",
                    "--stable-samples", "2",
                    "--interval", "0.25",
                ])
        self.assertEqual(1, result)
        for name in ("ingress", "survivor", "draining", "cookie", "secret"):
            self.assertNotIn(name, err.getvalue().lower())


if __name__ == "__main__":
    unittest.main()
