"""Deterministic read-only two-node preStop observation contract tests."""

import io
import unittest
from unittest.mock import patch

from scripts import staging_drain_watch as watch
from scripts.tests.test_staging_drain_watch import ScriptedOpener


DRAINING = "https://node-a.staging.example"
SURVIVOR = "https://node-b.staging.example"


class PairedOpener:
    def __init__(self, draining_states, survivor_states, **options):
        self.draining = ScriptedOpener(draining_states)
        self.survivor = ScriptedOpener(survivor_states, **options)
        self.all_requests = []

    def open(self, request, timeout):
        self.all_requests.append(request)
        if request.full_url.startswith(DRAINING + "/api/"):
            return self.draining.open(request, timeout)
        if request.full_url.startswith(SURVIVOR + "/api/"):
            return self.survivor.open(request, timeout)
        raise AssertionError("Unexpected probe endpoint")


class StagingDrainPairTests(unittest.TestCase):
    def watch(self, opener, *, max_polls=5, stable=2, notify=None):
        delays = []
        watch.watch_drain(DRAINING, max_polls, stable, 0.5, 1,
                         opener=opener, sleep=delays.append,
                         notify=notify, survivor_origin=SURVIVOR)
        return delays

    def test_successful_drain_preserves_survivor_on_every_sample(self):
        pair = PairedOpener(["ready", "ready", "unavailable", "unavailable"],
                            ["ready"] * 4)
        notices = []
        self.assertEqual([0.5] * 3, self.watch(pair, notify=notices.append))
        self.assertEqual(16, len(pair.all_requests))  # baseline + 3 polls, 4 GET each
        self.assertEqual(8, len(pair.draining.calls))
        self.assertEqual(8, len(pair.survivor.calls))
        self.assertEqual(1, len(notices))
        self.assertNotIn("node-", notices[0])
        self.assertTrue(all(req.get_method() == "GET" for req in pair.all_requests))
        self.assertTrue(all(req.get_header("Cookie") is None and
                            req.get_header("Authorization") is None
                            for req in pair.all_requests))

    def test_unready_survivor_before_baseline_never_prompts_operator(self):
        pair = PairedOpener(["ready"], ["unavailable"])
        notices = []
        with self.assertRaises(watch.probe.ProbeError):
            self.watch(pair, notify=notices.append)
        self.assertEqual([], notices)
        self.assertEqual(4, len(pair.all_requests))

    def test_survivor_outage_after_verified_baseline_rejects_drain(self):
        pair = PairedOpener(["ready", "unavailable", "unavailable"],
                            ["ready", "unavailable"])
        with self.assertRaises(watch.probe.ProbeError):
            self.watch(pair)
        self.assertEqual(8, len(pair.all_requests))

    def test_survivor_outage_on_final_stable_sample_still_fails(self):
        pair = PairedOpener(["ready", "unavailable", "unavailable"],
                            ["ready", "ready", "unavailable"])
        with self.assertRaises(watch.probe.ProbeError):
            self.watch(pair, max_polls=2, stable=2)
        self.assertEqual(12, len(pair.all_requests))

    def test_pair_without_drain_is_bounded(self):
        pair = PairedOpener(["ready"], ["ready"])
        with self.assertRaises(watch.probe.ProbeError):
            self.watch(pair, max_polls=2, stable=2)
        self.assertEqual(12, len(pair.all_requests))

    def test_pair_survivor_redirect_invalid_body_and_cache_are_rejected(self):
        for scenario in ("redirect", "bad-status", "bad-body", "bad-cache"):
            pair = PairedOpener(["ready", "unavailable", "unavailable"],
                                ["ready", "ready"])
            if scenario == "redirect":
                pair.survivor.states[1] = "redirect"
            elif scenario == "bad-status":
                pair.survivor.states[1] = "bad-status"
            elif scenario == "bad-body":
                pair.survivor.corrupt_at = 4
            else:
                pair.survivor.broken_headers_at = 4
            with self.subTest(scenario=scenario), self.assertRaises(watch.probe.ProbeError):
                self.watch(pair, max_polls=2, stable=2)

    def test_same_node_denied_even_with_case_default_port_or_ipv6_alias(self):
        duplicates = (
            ("https://node.example", "https://NODE.EXAMPLE:443"),
            ("https://node.example/", "https://node.example"),
            ("https://[::1]:443", "https://[0:0:0:0:0:0:0:1]"),
        )
        for draining, survivor in duplicates:
            with self.subTest(draining=draining, survivor=survivor):
                with self.assertRaises(watch.probe.ProbeError):
                    watch.watch_drain(draining, 2, 2, 0.5, 1,
                                     opener=PairedOpener(["ready"], ["ready"]),
                                     survivor_origin=survivor)

    def test_invalid_survivor_origin_fails_before_any_request(self):
        for url in ("http://node-b.example", "https://person:secret@node-b.example",
                    "https://node-b.example/path", "https://node-b.example?code=x"):
            pair = PairedOpener(["ready"], ["ready"])
            with self.subTest(url=url), self.assertRaises(watch.probe.ProbeError):
                watch.watch_drain(DRAINING, 2, 2, 0.5, 1,
                                 opener=pair, survivor_origin=url)
            self.assertEqual([], pair.all_requests)

    def test_cli_reports_no_node_identity_or_underlying_error(self):
        pair = PairedOpener(["ready", "unavailable"], ["ready", "unavailable"])
        with patch.object(watch.probe, "make_strict_opener", return_value=pair):
            with patch("sys.stderr", new_callable=io.StringIO) as error:
                result = watch.main([
                    "--base-url", DRAINING, "--survivor-url", SURVIVOR,
                    "--max-polls", "2", "--stable-samples", "2",
                    "--interval", "0.5",
                ])
        self.assertEqual(1, result)
        self.assertNotIn("node-a", error.getvalue())
        self.assertNotIn("node-b", error.getvalue())
        self.assertNotIn("Redis", error.getvalue())


if __name__ == "__main__":
    unittest.main()
