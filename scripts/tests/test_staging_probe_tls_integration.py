"""Real loopback HTTPS integration for the external, read-only staging probes.

The test generates its own short-lived certificate and key in a temporary
directory. The HTTPS servers listen ONLY on 127.0.0.1; no staging or other
external network is contacted, and no committed key material is used.
"""

import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import patch
from urllib.error import URLError

from scripts import staging_drain_watch as drain
from scripts import staging_probe as probe


class PublicProbeHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        server = self.server
        with server.state_lock:
            server.request_count += 1
            if self.path == "/api/ready":
                index = server.ready_reads
                server.ready_reads += 1
                if server.ready_sequence is not None:
                    mode = server.ready_sequence[min(index,
                                                      len(server.ready_sequence) - 1)]
                else:
                    mode = server.mode
            else:
                mode = server.mode
            bad_cache = server.bad_cache
            cache_override = server.cache_control_override
            extra_headers = server.extra_headers

        if self.path not in ("/api/health", "/api/ready"):
            status, payload = 404, b'{"status":"missing"}'
        elif self.path == "/api/health":
            status, payload = 200, b'{"status":"ok","application":"Shirokuro"}'
        elif mode == "redirect":
            status, payload = 302, b'{"status":"ready"}'
        elif mode == "invalid":
            status, payload = 200, b'{"status":"ready","private":"must-not-import"}'
        else:
            status = 503 if mode == "unavailable" else 200
            payload = json.dumps({"status": mode}, separators=(",", ":")).encode()

        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", cache_override if cache_override is not None
                         else ("max-age=30" if bad_cache else "no-store"))
        for name, value in extra_headers:
            # Simulate a real edge/proxy adding a second conflicting field.
            self.send_header(name, value)
        if status == 302:
            # This test must never issue a network request to that location.
            self.send_header("Location", "https://redirect-not-allowed.invalid/")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_):
        # A local test server must not print URLs, payloads or TLS errors.
        pass


class LocalTlsNode:
    def __init__(self, context):
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), PublicProbeHandler)
        self.server.daemon_threads = True
        self.server.state_lock = threading.Lock()
        self.server.mode = "ready"
        self.server.ready_sequence = None
        self.server.ready_reads = 0
        self.server.request_count = 0
        self.server.bad_cache = False
        self.server.cache_control_override = None
        self.server.extra_headers = ()
        self.server.socket = context.wrap_socket(self.server.socket, server_side=True)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def origin(self):
        # The certificate SAN is DNS:localhost, not IP:127.0.0.1.
        return "https://localhost:" + str(self.server.server_port)

    def reset(self):
        with self.server.state_lock:
            self.server.mode = "ready"
            self.server.ready_sequence = None
            self.server.ready_reads = 0
            self.server.request_count = 0
            self.server.bad_cache = False
            self.server.cache_control_override = None
            self.server.extra_headers = ()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


@unittest.skipUnless(shutil.which("openssl"), "OpenSSL binary required for real TLS fixture")
class RealStagingHttpsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="shirokuro-tls-fixture-")
        cls.certificate = Path(cls.temporary.name) / "loopback.crt"
        key = Path(cls.temporary.name) / "loopback.key"
        try:
            subprocess.run(
                ["openssl", "req", "-x509", "-newkey", "rsa:2048", "-sha256",
                 "-nodes", "-days", "1", "-keyout", str(key), "-out",
                 str(cls.certificate), "-subj", "/CN=localhost",
                 "-addext", "subjectAltName=DNS:localhost",
                 "-addext", "basicConstraints=critical,CA:TRUE"],
                check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                timeout=20,
            )
            tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            tls.load_cert_chain(str(cls.certificate), str(key))
            cls.nodes = [LocalTlsNode(tls), LocalTlsNode(tls)]
            cls.edge = LocalTlsNode(tls)
        except Exception:
            cls.temporary.cleanup()
            raise

    @classmethod
    def tearDownClass(cls):
        for node in cls.nodes:
            node.close()
        cls.edge.close()
        cls.temporary.cleanup()

    def setUp(self):
        for node in [*self.nodes, self.edge]:
            node.reset()

    def trusted_opener(self, **environment):
        # Trust is installed only for this locally generated test CA.
        variables = {
            "SSL_CERT_FILE": str(self.certificate),
            "HTTPS_PROXY": "http://127.0.0.1:1",
            "ALL_PROXY": "http://127.0.0.1:1",
            "HTTP_PROXY": "http://127.0.0.1:1",
            "NO_PROXY": "",
        }
        variables.update(environment)
        with patch.dict(os.environ, variables):
            return probe.make_strict_opener()

    def test_real_tls_ready_and_unavailable_with_proxy_environment(self):
        node = self.nodes[0]
        opener = self.trusted_opener()
        probe.run_checks([node.origin], "ready", 1, 0, 2, opener=opener)
        self.assertEqual(2, node.server.request_count)
        node.server.mode = "unavailable"
        probe.run_checks([node.origin], "unavailable", 1, 0, 2, opener=opener)
        self.assertEqual(4, node.server.request_count)

    def test_untrusted_self_signed_certificate_is_denied(self):
        node = self.nodes[0]
        with patch.dict(os.environ, {"SSL_CERT_FILE": os.devnull}):
            opener = probe.make_strict_opener()
        with self.assertRaises(URLError) as error:
            probe.probe_one(opener, node.origin, "/api/ready", 2, "ready")
        self.assertIsInstance(error.exception.reason,
                              ssl.SSLCertVerificationError)

    def test_mismatched_hostname_is_denied_even_with_trusted_ca(self):
        node = self.nodes[0]
        wrong_host = node.origin.replace("localhost", "127.0.0.1")
        with self.assertRaises(URLError) as error:
            probe.probe_one(self.trusted_opener(), wrong_host,
                            "/api/ready", 2, "ready")
        self.assertIsInstance(error.exception.reason,
                              ssl.SSLCertVerificationError)

    def test_real_https_redirect_is_not_followed(self):
        node = self.nodes[0]
        node.server.mode = "redirect"
        with self.assertRaises(probe.ProbeError):
            probe.probe_one(self.trusted_opener(), node.origin,
                            "/api/ready", 2, "ready")
        self.assertEqual(1, node.server.request_count)

    def test_real_https_bad_headers_and_extra_json_are_rejected(self):
        node = self.nodes[0]
        opener = self.trusted_opener()
        node.server.mode = "invalid"
        with self.assertRaises(probe.ProbeError):
            probe.probe_one(opener, node.origin, "/api/ready", 2, "ready")
        node.server.mode = "ready"
        node.server.bad_cache = True
        with self.assertRaises(probe.ProbeError):
            probe.probe_one(opener, node.origin, "/api/ready", 2, "ready")

    def test_real_tls_combined_cache_directives_fail_for_health_and_503(self):
        node = self.nodes[0]
        opener = self.trusted_opener()
        for policy in ("no-store, public", "no-store, max-age=60",
                       "private, no-store, immutable", "no-store, no-store"):
            with self.subTest(policy=policy):
                node.reset()
                node.server.cache_control_override = policy
                with self.assertRaises(probe.ProbeError):
                    probe.run_checks([node.origin], "ready", 1, 0, 2,
                                     opener=opener)
                self.assertEqual(1, node.server.request_count)
                # Even an expected 503 after drain cannot be trusted when
                # its single Cache-Control field contradicts no-store.
                node.server.mode = "unavailable"
                with self.assertRaises(probe.ProbeError):
                    probe.probe_one(opener, node.origin, "/api/ready", 2,
                                    "unavailable")
                self.assertEqual(2, node.server.request_count)

    def test_three_node_drain_rejects_cacheable_edge_baseline_before_notice(self):
        drained, survivor = self.nodes
        edge = self.edge
        edge.server.cache_control_override = "no-store, public, max-age=60"
        notices = []
        with self.assertRaises(probe.ProbeError):
            drain.watch_drain(drained.origin, 2, 2, .25, 2,
                              opener=self.trusted_opener(), sleep=lambda _: None,
                              notify=notices.append,
                              survivor_origin=survivor.origin,
                              edge_origin=edge.origin)
        self.assertEqual([], notices)
        self.assertEqual(2, drained.server.request_count)
        self.assertEqual(2, survivor.server.request_count)
        self.assertEqual(1, edge.server.request_count)

    def test_actual_https_duplicate_cache_or_content_type_is_rejected(self):
        node = self.nodes[0]
        opener = self.trusted_opener()
        for name, extra in (
                ("Cache-Control", "public, max-age=300"),
                ("Cache-Control", "no-store"),
                ("Content-Type", "text/html"),
                ("Content-Type", "application/json")):
            with self.subTest(name=name, extra=extra):
                node.reset()
                node.server.extra_headers = ((name, extra),)
                with self.assertRaises(probe.ProbeError):
                    probe.run_checks([node.origin], "ready", 1, 0, 2,
                                     opener=opener)
                # The initial health GET is already untrustworthy; never
                # advance to readiness or claim a healthy edge.
                self.assertEqual(1, node.server.request_count)

    def test_three_way_drain_rejects_ambiguous_ingress_baseline(self):
        drained, survivor = self.nodes
        edge = self.edge
        edge.server.extra_headers = (("Cache-Control", "public, max-age=300"),)
        notices = []
        with self.assertRaises(probe.ProbeError):
            drain.watch_drain(drained.origin, 2, 2, .25, 2,
                              opener=self.trusted_opener(), sleep=lambda _: None,
                              notify=notices.append,
                              survivor_origin=survivor.origin,
                              edge_origin=edge.origin)
        self.assertEqual([], notices)
        self.assertEqual(2, drained.server.request_count)
        self.assertEqual(2, survivor.server.request_count)
        self.assertEqual(1, edge.server.request_count)

    def test_paired_watcher_real_tls_surviving_node_remains_ready(self):
        drained, survivor = self.nodes
        opener = self.trusted_opener()
        announcements = []

        def external_drain(message):
            announcements.append(message)
            # This is an in-test, explicit operator simulation, NOT a signal
            # sent by the production watcher nor a real deployment action.
            self.assertEqual(2, drained.server.request_count)
            self.assertEqual(2, survivor.server.request_count)
            drained.server.mode = "unavailable"

        drain.watch_drain(drained.origin, 2, 2, .25, 2, opener=opener,
                          sleep=lambda _: None, notify=external_drain,
                          survivor_origin=survivor.origin)
        self.assertEqual(1, len(announcements))
        self.assertEqual(6, drained.server.request_count)
        self.assertEqual(6, survivor.server.request_count)

    def test_three_way_ingress_continuity_with_real_tls(self):
        drained, survivor = self.nodes
        edge = self.edge
        opener = self.trusted_opener()
        announcements = []

        def external_drain(message):
            announcements.append(message)
            self.assertEqual(2, drained.server.request_count)
            self.assertEqual(2, survivor.server.request_count)
            self.assertEqual(2, edge.server.request_count)
            drained.server.mode = "unavailable"

        drain.watch_drain(drained.origin, 2, 2, .25, 2, opener=opener,
                          sleep=lambda _: None, notify=external_drain,
                          survivor_origin=survivor.origin,
                          edge_origin=edge.origin)
        self.assertEqual(1, len(announcements))
        for node in (drained, survivor, edge):
            self.assertEqual(6, node.server.request_count)

    def test_three_way_ingress_503_at_final_sample_fails_real_tls(self):
        drained, survivor = self.nodes
        edge = self.edge
        opener = self.trusted_opener()
        edge.server.ready_sequence = ["ready", "ready", "unavailable"]

        def external_drain(_):
            drained.server.mode = "unavailable"

        with self.assertRaises(probe.ProbeError):
            drain.watch_drain(drained.origin, 2, 2, .25, 2, opener=opener,
                              sleep=lambda _: None, notify=external_drain,
                              survivor_origin=survivor.origin,
                              edge_origin=edge.origin)
        for node in (drained, survivor, edge):
            self.assertEqual(6, node.server.request_count)

    def test_paired_watcher_rejects_survivor_503_on_final_sample(self):
        drained, survivor = self.nodes
        opener = self.trusted_opener()
        survivor.server.ready_sequence = ["ready", "ready", "unavailable"]

        def external_drain(_):
            drained.server.mode = "unavailable"

        with self.assertRaises(probe.ProbeError):
            drain.watch_drain(drained.origin, 2, 2, .25, 2,
                              opener=opener, sleep=lambda _: None,
                              notify=external_drain,
                              survivor_origin=survivor.origin)
        self.assertEqual(6, drained.server.request_count)
        self.assertEqual(6, survivor.server.request_count)


if __name__ == "__main__":
    unittest.main()
