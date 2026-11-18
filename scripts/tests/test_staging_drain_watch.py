"""Deterministic, network-free public HTTPS preStop transition tests."""

import io
import unittest
from email.message import Message
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request

from scripts import staging_drain_watch as watch


def headers(**items):
    result = Message()
    for key, value in items.items():
        result[key.replace("_", "-")] = value
    return result


def safe_headers():
    return headers(Content_Type="application/json; charset=utf-8",
                   Cache_Control="no-store")


class Response:
    def __init__(self, status, payload, response_headers=None):
        self.status = status
        self.body = payload
        self.headers = response_headers if response_headers is not None else safe_headers()

    def getcode(self):
        return self.status

    def read(self, limit):
        return self.body[:limit]

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


class ScriptedOpener:
    def __init__(self, states, *, wrong_health_at=None,
                 corrupt_at=None, broken_headers_at=None, throw_at=None):
        self.states = states
        self.calls = []
        self.reads = 0
        self.wrong_health_at = wrong_health_at
        self.corrupt_at = corrupt_at
        self.broken_headers_at = broken_headers_at
        self.throw_at = throw_at

    def open(self, request, timeout):
        self.calls.append((request, timeout))
        if len(self.calls) == self.throw_at:
            raise OSError("secret Redis endpoint and token")
        if request.full_url.endswith("/api/health"):
            status = 503 if len(self.calls) == self.wrong_health_at else 200
            return Response(status, b'{"status":"ok","application":"Shirokuro"}')
        index = self.reads
        self.reads += 1
        state = self.states[min(index, len(self.states) - 1)]
        status = {"ready": 200, "unavailable": 503,
                  "redirect": 302, "bad-status": 502}[state]
        body = ('{"status":"' + state + '"}').encode()
        if len(self.calls) == self.corrupt_at:
            body = b'{"status":"unavailable","private":"secret"}'
        head = (headers(Cache_Control="public", Content_Type="application/json")
                if len(self.calls) == self.broken_headers_at else safe_headers())
        if status == 503:
            return HTTPError(request.full_url, status, "secret upstream",
                             head, io.BytesIO(body))
        return Response(status, body, head)


class DrainWatchTests(unittest.TestCase):
    ORIGIN = "https://node.staging.example"

    def watch(self, opener, *, polls=6, stable=3):
        delays = []
        notices = []
        watch.watch_drain(self.ORIGIN, polls, stable, 0.5, 2.0,
                         opener=opener, sleep=delays.append,
                         notify=notices.append)
        return delays, notices

    def test_ready_then_three_unavailable_completes_and_only_gets(self):
        transport = ScriptedOpener(
            ["ready", "ready", "unavailable", "unavailable", "unavailable"])
        delays, notices = self.watch(transport)
        self.assertEqual([0.5] * 4, delays)
        self.assertEqual(1, len(notices))
        self.assertNotIn("node.staging", notices[0])
        self.assertEqual(10, len(transport.calls))
        self.assertTrue(all(req.get_method() == "GET" for req, _ in transport.calls))
        self.assertTrue(all(req.get_header("Authorization") is None and
                            req.get_header("Cookie") is None
                            for req, _ in transport.calls))
        self.assertTrue(all(timeout == 2.0 for _, timeout in transport.calls))

    def test_baseline_503_does_not_announce_or_start_watching(self):
        transport = ScriptedOpener(["unavailable"])
        notices = []
        with self.assertRaises(watch.probe.ProbeError):
            watch.watch_drain(self.ORIGIN, 4, 2, 0.5, 1,
                             opener=transport, sleep=lambda _: None,
                             notify=notices.append)
        self.assertEqual([], notices)
        self.assertEqual(2, len(transport.calls))

    def test_missing_transition_is_bounded(self):
        transport = ScriptedOpener(["ready"])
        with self.assertRaises(watch.probe.ProbeError):
            self.watch(transport, polls=5, stable=2)
        self.assertEqual(12, len(transport.calls))

    def test_reversion_to_200_fails_without_resetting_counter(self):
        transport = ScriptedOpener(
            ["ready", "unavailable", "ready", "unavailable", "unavailable"])
        with self.assertRaises(watch.probe.ProbeError):
            self.watch(transport)
        self.assertEqual(6, len(transport.calls))

    def test_no_success_when_transition_too_late_to_prove_stability(self):
        transport = ScriptedOpener(
            ["ready", "ready", "ready", "unavailable"])
        with self.assertRaises(watch.probe.ProbeError):
            self.watch(transport, polls=3, stable=2)

    def test_health_failure_during_drain_is_not_accepted(self):
        transport = ScriptedOpener(
            ["ready", "unavailable", "unavailable", "unavailable"],
            wrong_health_at=5)
        with self.assertRaises(watch.probe.ProbeError):
            self.watch(transport)

    def test_unexpected_readiness_status_redirect_schema_and_cache_fail(self):
        cases = [
            ScriptedOpener(["ready", "redirect"]),
            ScriptedOpener(["ready", "bad-status"]),
            ScriptedOpener(["ready", "unavailable"], corrupt_at=4),
            ScriptedOpener(["ready", "unavailable"], broken_headers_at=4),
        ]
        for transport in cases:
            with self.subTest(case=cases.index(transport)):
                with self.assertRaises(watch.probe.ProbeError):
                    self.watch(transport, polls=3, stable=2)

    def test_transport_errors_are_not_swallowed_as_503(self):
        transport = ScriptedOpener(["ready", "unavailable"], throw_at=4)
        with self.assertRaises(OSError):
            self.watch(transport)

    def test_bounds_reject_nans_non_numeric_and_unbounded_polls(self):
        for max_polls, stable, interval, timeout in (
            (1, 2, 1, 1), (31, 2, 1, 1), (4, 1, 1, 1),
            (4, 7, 1, 1), (4, 5, 1, 1), (4, 2, 0, 1),
            (4, 2, 11, 1), (4, 2, float("nan"), 1),
            (4, 2, 1, float("nan")), (4, 2, 1, 11),
        ):
            with self.subTest(bounds=(max_polls, stable, interval, timeout)):
                with self.assertRaises(watch.probe.ProbeError):
                    watch.watch_drain(self.ORIGIN, max_polls, stable,
                                     interval, timeout, opener=ScriptedOpener(["ready"]))

    def test_direct_origin_only(self):
        for value in ("http://node.example", "https://user:pass@node.example",
                      "https://node.example/health", "https://node.example?secret=1"):
            with self.subTest(value=value), self.assertRaises(watch.probe.ProbeError):
                watch.watch_drain(value, 2, 2, 1, 1,
                                 opener=ScriptedOpener(["ready"]))

    def test_main_reports_no_origin_or_underlying_error(self):
        fake = ScriptedOpener(["ready"], throw_at=1)
        with patch.object(watch.probe, "make_strict_opener", return_value=fake):
            with patch("sys.stderr", new_callable=io.StringIO) as err:
                result = watch.main(["--base-url", "https://private-node.staging.example",
                                     "--max-polls", "2", "--stable-samples", "2"])
        self.assertEqual(1, result)
        self.assertNotIn("private-node", err.getvalue())
        self.assertNotIn("secret", err.getvalue())
        self.assertNotIn("Redis", err.getvalue())

    def test_watch_uses_shared_direct_node_https_opener_only_once(self):
        transport = ScriptedOpener(["ready", "unavailable", "unavailable"])
        with patch.object(watch.probe, "make_strict_opener",
                          return_value=transport) as make:
            watch.watch_drain(self.ORIGIN, 2, 2, 0.5, 1,
                             sleep=lambda _: None)
        make.assert_called_once_with()
        self.assertEqual(6, len(transport.calls))

    def test_shared_probe_transport_rejects_invalid_path(self):
        with self.assertRaises(watch.probe.ProbeError):
            watch.probe.fetch_public_probe(
                ScriptedOpener(["ready"]), self.ORIGIN,
                "/api/sessions/guest", 1)


if __name__ == "__main__":
    unittest.main()
