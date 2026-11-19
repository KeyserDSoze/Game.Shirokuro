"""Offline tests for the strictly read-only staging HTTPS smoke runner."""

import io
import json
import os
import ssl
import unittest
from email.message import Message
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import HTTPSHandler, ProxyHandler, Request

from scripts import staging_probe as probe


def headers(**values):
    value = Message()
    for key, content in values.items():
        value[key.replace("_", "-")] = content
    return value


VALID_HEADERS = lambda: headers(
    Cache_Control="no-store", Content_Type="application/json; charset=utf-8")


class FakeResponse:
    def __init__(self, status, body, header=None):
        self.status = status
        self.headers = header or VALID_HEADERS()
        self.body = body

    def getcode(self):
        return self.status

    def read(self, amount):
        return self.body[:amount]

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


class FakeOpener:
    def __init__(self, expected="ready", fail_at=None):
        self.calls = []
        self.expected = expected
        self.fail_at = fail_at

    def open(self, request, timeout):
        self.calls.append((request, timeout))
        assert isinstance(request, Request)
        assert request.get_method() == "GET"
        assert request.get_header("Cookie") is None
        assert request.get_header("Authorization") is None
        if len(self.calls) == self.fail_at:
            raise OSError("SECRET staging origin and password")
        if request.full_url.endswith("/api/health"):
            return FakeResponse(200, b'{"status":"ok","application":"Shirokuro"}')
        if self.expected == "ready":
            return FakeResponse(200, b'{"status":"ready"}')
        # urllib raises HTTPError for 503, unlike 2xx responses.
        return HTTPError(request.full_url, 503, "sensitive upstream",
                         VALID_HEADERS(), io.BytesIO(b'{"status":"unavailable"}'))


class StagingProbeTests(unittest.TestCase):
    def test_accepts_bare_https_origins(self):
        self.assertEqual(["https://game.example", "https://node.example:8443"],
                         probe.validate_base_urls(
                             ["https://game.example/", "https://node.example:8443"]))
        self.assertEqual(["https://[::1]:8443"],
                         probe.validate_base_urls(["https://[::1]:8443"]))

    def test_rejects_untrusted_url_forms(self):
        for url in ("http://game.example", "https://user:secret@game.example",
                    "https://game.example/api/ready", "https://game.example/?ticket=x",
                    "https://game.example/#secret", "https://game.example?",
                    "https://game.example#", "https://game.example:bad",
                    "https://", " https://game.example", "https://game.example\n"):
            with self.subTest(url=url), self.assertRaises(probe.ProbeError):
                probe.validate_base_urls([url])
        with self.assertRaises(probe.ProbeError):
            probe.validate_base_urls(["https://a.example", "https://a.example/"])
        with self.assertRaises(probe.ProbeError):
            probe.validate_base_urls([
                "https://node-" + str(i) + ".example" for i in range(9)
            ])

    def test_equivalent_https_origin_aliases_are_rejected(self):
        # Repeated direct targets are not independent evidence of readiness.
        for first, second in (
                ("https://node.example", "https://NODE.EXAMPLE:443"),
                ("https://node.example/", "https://node.example"),
                ("https://[::1]:443", "https://[0:0:0:0:0:0:0:1]"),
                ("https://127.0.0.1", "https://127.0.0.1:443")):
            with self.subTest(first=first, second=second):
                with self.assertRaises(probe.ProbeError):
                    probe.validate_base_urls([first, second])

    def test_direct_run_checks_validates_https_origins_before_any_io(self):
        # A Python caller must receive the same guarantees as CLI main().
        invalid_origins = (
            None, [], "https://node.example", [42],
            ["http://node.example"],
            ["https://user:secret@node.example"],
            ["https://node.example/api/health"],
            ["https://node.example?ticket=x"],
            ["https://node.example#token"],
            ["https://node.example", "https://NODE.EXAMPLE:443"],
            ["https://[::1]", "https://[0:0:0:0:0:0:0:1]:443"],
            ["https://node-" + str(i) + ".example" for i in range(9)],
        )
        for origins in invalid_origins:
            with self.subTest(origins=origins):
                opener = FakeOpener()
                sleeps = []
                with self.assertRaises(probe.ProbeError):
                    probe.run_checks(origins, "ready", 2, 0, 2,
                                     opener=opener, sleep=sleeps.append)
                self.assertEqual([], opener.calls)
                self.assertEqual([], sleeps)

    def test_invalid_direct_config_never_constructs_a_transport(self):
        with patch.object(probe, "make_strict_opener",
                          side_effect=AssertionError("no network transport")) as build:
            with self.assertRaises(probe.ProbeError):
                probe.run_checks(["http://node.example"], "ready", 1, 0, 2)
        build.assert_not_called()

    def test_exact_public_health_and_ready_contract(self):
        probe.validate_response("/api/health", 200, VALID_HEADERS(),
                                b'{"status":"ok","application":"Shirokuro"}', "ready")
        probe.validate_response("/api/ready", 200, VALID_HEADERS(),
                                b'{"status":"ready"}', "ready")
        probe.validate_response("/api/ready", 503, VALID_HEADERS(),
                                b'{"status":"unavailable"}', "unavailable")

    def test_rejects_wrong_status_and_schema(self):
        for path, status, body, mode in (
            ("/api/ready", 503, b'{"status":"unavailable"}', "ready"),
            ("/api/ready", 200, b'{"status":"ready"}', "unavailable"),
            ("/api/ready", 302, b'{"status":"ready"}', "ready"),
            ("/api/ready", 200, b'{"status":"ready","private":"secret"}', "ready"),
            ("/api/ready", 200, b'{"status":"ready","status":"ready"}', "ready"),
            ("/api/health", 200, b'{"status":"ready"}', "ready"),
            ("/api/ready", 200, b'invalid', "ready"),
            ("/api/ready", 200, b'{"status":"ready"}' + b" " * 1024, "ready"),
        ):
            with self.subTest(status=status, body=body[:50]):
                # Duplicate JSON keys are not used as an authority; the
                # canonical single-key contract should reject them.
                with self.assertRaises(probe.ProbeError):
                    probe.validate_response(path, status, VALID_HEADERS(), body, mode)

    def test_rejects_cache_and_privacy_header_deviations(self):
        for header in (
            headers(Cache_Control="max-age=30", Content_Type="application/json"),
            headers(Cache_Control="no-store", Content_Type="text/html"),
            headers(Cache_Control="no-store", Content_Type="application/json",
                    Set_Cookie="session=secret"),
            headers(Cache_Control="no-store", Content_Type="application/json",
                    Location="https://foreign.example"),
            headers(Cache_Control="no-store", Content_Type="application/json",
                    Retry_After="60"),
        ):
            with self.subTest(header=str(header)), self.assertRaises(probe.ProbeError):
                probe.validate_response("/api/ready", 200, header,
                                        b'{"status":"ready"}', "ready")

    def test_rejects_duplicate_security_headers_even_if_first_looks_valid(self):
        # email.message.Message (as used by urllib HTTPMessage) preserves
        # repeated fields but .get() returns only the FIRST occurrence.
        for name, extra in (
                ("cache-control", "public, max-age=300"),
                ("CACHE-CONTROL", "no-store"),
                ("content-type", "text/html"),
                ("CONTENT-TYPE", "application/json")):
            with self.subTest(header=name, extra=extra):
                header = VALID_HEADERS()
                self.assertEqual("no-store", header.get("Cache-Control"))
                self.assertEqual("application/json; charset=utf-8",
                                 header.get("Content-Type"))
                header.add_header(name, extra)
                self.assertEqual(2, len(header.get_all(name)))
                with self.assertRaises(probe.ProbeError):
                    probe.validate_response("/api/ready", 200, header,
                                            b'{"status":"ready"}', "ready")

    def test_mapping_only_headers_still_validate_exact_public_contract(self):
        # A lightweight caller without HTTPMessage.get_all has one value
        # per name; do not weaken the normal validation of single fields.
        payload = b'{"status":"ready"}'
        probe.validate_response("/api/ready", 200, {
            "Cache-Control": "private, no-store",
            "Content-Type": "application/json; charset=utf-8",
        }, payload, "ready")
        with self.assertRaises(probe.ProbeError):
            probe.validate_response("/api/ready", 200, {
                "Cache-Control": "public, max-age=300",
                "Content-Type": "application/json",
            }, payload, "ready")

    def test_bounded_gets_and_expected_drain(self):
        for mode in ("ready", "unavailable"):
            with self.subTest(mode=mode):
                opener = FakeOpener(mode)
                sleeps = []
                probe.run_checks(["https://game.example", "https://node.example"],
                                 mode, 3, 0.5, 2, opener=opener,
                                 sleep=sleeps.append)
                self.assertEqual(12, len(opener.calls))
                self.assertEqual([0.5, 0.5], sleeps)
                self.assertTrue(all(v == 2 for _, v in opener.calls))

    def test_rejects_unbounded_configuration(self):
        for count, interval, timeout in (
                (0, 0, 2), (31, 0, 2), (True, 0, 2),
                (1, -1, 2), (1, 31, 2),
                (1, False, 2), (1, float("nan"), 2),
                (1, float("inf"), 2), (1, -float("inf"), 2),
                (1, 0, 0), (1, 0, 11), (1, 0, True),
                (1, 0, float("nan")), (1, 0, float("inf")),
                (1, 0, -float("inf"))):
            with self.subTest(config=(count, interval, timeout)):
                opener = FakeOpener()
                sleeps = []
                with self.assertRaises(probe.ProbeError):
                    probe.run_checks(["https://game.example"], "ready",
                                     count, interval, timeout,
                                     opener=opener, sleep=sleeps.append)
                self.assertEqual([], opener.calls)
                self.assertEqual([], sleeps)

    def test_never_exposes_failing_url_or_exception(self):
        with patch.object(probe, "make_strict_opener", return_value=FakeOpener(fail_at=1)):
            with patch("sys.stderr", new_callable=io.StringIO) as err:
                result = probe.main(["--base-url", "https://internal.staging.example",
                                     "--samples", "1", "--interval", "0"])
        self.assertEqual(1, result)
        self.assertNotIn("internal.staging", err.getvalue())
        self.assertNotIn("SECRET", err.getvalue())
        self.assertNotIn("password", err.getvalue())

    def test_default_https_transport_ignores_implicit_proxies_and_keeps_tls(self):
        # Python urllib normally consults HTTPS_PROXY/ALL_PROXY, even when
        # its caller uses a bare HTTPS origin. Direct-node health must not.
        with patch.dict(os.environ, {
            "HTTPS_PROXY": "http://hidden-proxy.example:3128",
            "HTTP_PROXY": "http://hidden-proxy.example:3128",
            "ALL_PROXY": "http://hidden-proxy.example:3128",
        }, clear=True):
            opener = probe.make_strict_opener()
        self.assertFalse(any(isinstance(h, ProxyHandler) for h in opener.handlers))
        tls = [h for h in opener.handlers if isinstance(h, HTTPSHandler)]
        self.assertEqual(1, len(tls))
        self.assertEqual(ssl.CERT_REQUIRED, tls[0]._context.verify_mode)
        self.assertTrue(tls[0]._context.check_hostname)
        redirects = [h for h in opener.handlers if isinstance(h, probe.DenyRedirect)]
        self.assertEqual(1, len(redirects))

    def test_redirect_policy_never_follows(self):
        self.assertIsNone(probe.DenyRedirect().redirect_request(
            None, None, 302, "redirect", {}, "https://other.example"))


if __name__ == "__main__":
    unittest.main()
