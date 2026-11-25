"""Real HTTP/socket checks for the dependency-free LAN release server.

Builds a temporary host with the Windows .NET compiler; never uses a product
installation directory or changes machine firewall/network configuration.
"""
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import unittest


class DeliveryServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="JmdDeliveryServerTests-")
        cls.root = Path(cls.temporary.name)
        cls.public = cls.root / "public"
        (cls.public / "monitor").mkdir(parents=True)
        (cls.public / "monitor" / "latest.json").write_bytes(b'{"test":true}')
        (cls.public / "test" / "monitor").mkdir(parents=True)
        (cls.public / "test" / "monitor" / "latest.json").write_bytes(b'test-latest')
        (cls.public / "test" / "monitor" / "monitor-1.0.0.1.zip").write_bytes(b'test-zip')
        (cls.public / "monitor" / "monitor-1.0.0.0.zip").write_bytes(b'official-zip')
        (cls.public / "monitor" / "fallback.json").write_bytes(b'official-fallback')
        (cls.public / "stableonly").mkdir()
        (cls.public / "stableonly" / "latest.json").write_bytes(b'official-latest')
        (cls.public / "private.xml").write_bytes(b"private-key-must-not-be-served")
        (cls.root / "outside.json").write_bytes(b"outside-root")
        harness = cls.root / "ServerTestHost.cs"
        harness.write_text(
            "using System; using System.Threading; using JmdUpdater; "
            "class TestHost { static int Main(string[] args) { try { "
            "LocalUpdateServer.Run(args[0], \"127.0.0.1\", Int32.Parse(args[1]), "
            "new string[] { args[2] }, CancellationToken.None, null, "
            "new System.Collections.Generic.Dictionary<string,string> { { args[3], args[4] } }, "
            "args.Length > 5 ? args[5].Split(',') : null); return 0; "
            "} catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 2; } } }",
            encoding="utf-8",
        )
        compiler = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET" / "Framework" / "v4.0.30319" / "csc.exe"
        cls.executable = cls.root / "ServerTestHost.exe"
        source = Path(__file__).resolve().parents[1] / "Publisher" / "LocalUpdateServer.cs"
        subprocess.run(
            [str(compiler), "/nologo", "/target:exe", "/out:" + str(cls.executable), str(harness), str(source)],
            check=True, capture_output=True, text=True,
        )

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def setUp(self):
        self.processes = []
        self.port = self.start_server("127.0.0.0/8")

    def tearDown(self):
        for process in self.processes:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=10)
            process.stdout.close()
            process.stderr.close()

    def start_server(self, cidr, ip="127.0.0.2", channel="test", denied=None):
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        process = subprocess.Popen(
            [str(self.executable), str(self.public), str(port), cidr, ip, channel] + ([denied] if denied else []),
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self.processes.append(process)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise AssertionError(process.stderr.read().decode(errors="replace"))
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                    return port
            except OSError:
                time.sleep(0.05)
        self.fail("test server did not start")

    def request(self, target, method="GET", port=None):
        connection = http.client.HTTPConnection("127.0.0.1", port or self.port, timeout=5)
        try:
            connection.request(method, target)
            response = connection.getresponse()
            return response.status, dict(response.getheaders()), response.read()
        finally:
            connection.close()

    def test_get_and_head(self):
        status, headers, body = self.request("/monitor/latest.json")
        self.assertEqual(status, 200)
        self.assertEqual(body, b'{"test":true}')
        self.assertEqual(headers["Cache-Control"], "no-store")
        self.assertEqual(self.request("/monitor/latest.json", "HEAD")[2], b"")

    def test_rejects_post_and_private_extensions(self):
        self.assertEqual(self.request("/monitor/latest.json", "POST")[0], 405)
        self.assertEqual(self.request("/private.xml")[0], 404)
        self.assertEqual(self.request("/monitor/missing.json")[0], 404)

    def test_rejects_encoded_traversal_and_windows_aliases(self):
        for target in (
            "/%2e%2e/outside.json", "/monitor/%2e%2e/%2e%2e/outside.json",
            "/monitor%5c..%5coutside.json", "/monitor/latest.json:stream",
            "/monitor/CON.json", "/monitor/LPT1.json", "/monitor/latest.json.",
            "/monitor/latest.json%20", "//monitor/latest.json",
        ):
            with self.subTest(target=target):
                self.assertIn(self.request(target)[0], (400, 403, 404))

    def test_cidr_is_enforced(self):
        forbidden_port = self.start_server("192.168.32.0/24")
        try:
            self.assertEqual(self.request("/monitor/latest.json", port=forbidden_port)[0], 403)
        except (ConnectionAbortedError, ConnectionResetError):
            # The server rejects the address before consuming its request. Windows
            # can reset a socket closed with unread data instead of delivering 403.
            # Both outcomes deny access; a successful/public response would fail.
            pass

    def test_denied_overrides_allowed(self):
        # Bare IPv4 means /32; an unrelated denied entry leaves the allowed client untouched.
        open_port = self.start_server("127.0.0.0/8", denied="127.0.0.9,10.0.0.0/8")
        self.assertEqual(self.request("/monitor/latest.json", port=open_port)[0], 200)
        for denied in ("127.0.0.1", "127.0.0.0/24"):
            with self.subTest(denied=denied):
                port = self.start_server("127.0.0.0/8", denied=denied)
                try:
                    self.assertEqual(self.request("/monitor/latest.json", port=port)[0], 403)
                except (ConnectionAbortedError, ConnectionResetError):
                    pass

    def test_ambiguous_denied_address_rejected_before_listen(self):
        for denied in ("127.0.0.010", "127.0.0.010/32", "127.1", "127.0.0.1/33"):
            with self.subTest(denied=denied):
                with self.assertRaises(AssertionError):
                    self.start_server("127.0.0.0/8", denied=denied)

    def test_client_channel_latest_zip_fallback_and_explicit_url(self):
        port = self.start_server("127.0.0.0/8", "127.0.0.1")
        self.assertEqual(self.request("/monitor/latest.json", port=port)[2], b'test-latest')
        self.assertEqual(self.request("/monitor/monitor-1.0.0.1.zip", port=port)[2], b'test-zip')
        self.assertEqual(self.request("/monitor/monitor-1.0.0.0.zip", port=port)[2], b'official-zip')
        self.assertEqual(self.request("/monitor/fallback.json", port=port)[2], b'official-fallback')
        self.assertEqual(self.request("/stableonly/latest.json", port=port)[2], b'official-latest')
        self.assertEqual(self.request("/monitor/latest.json", "HEAD", port=port)[1]['Content-Length'], str(len(b'test-latest')))
        self.assertEqual(self.request("/test/monitor/latest.json", port=port)[2], b'test-latest')
        self.assertEqual(self.request("/official/monitor/latest.json", port=port)[2], b'{"test":true}')
        self.assertEqual(self.request("/official/monitor/monitor-1.0.0.0.zip", port=port)[2], b'official-zip')
        self.assertEqual(self.request("/monitor/latest.json")[2], b'{"test":true}')
        self.assertEqual(self.request("/test/monitor/latest.json")[2], b'test-latest')
        for target in ('/monitor/%2e%2e/outside.json', '/test/%2e%2e/outside.json', '/monitor%5c..%5coutside.json'):
            self.assertIn(self.request(target, port=port)[0], (400, 403, 404))

    def test_channel_reparse_cannot_escape_root(self):
        junction = self.public / "test" / "linked"
        command = "New-Item -ItemType Junction -Path '" + str(junction).replace("'", "''") + "' -Target '" + str(self.root).replace("'", "''") + "' | Out-Null"
        subprocess.run(["powershell.exe", "-NoProfile", "-Command", command], check=True, capture_output=True)
        try:
            port = self.start_server("127.0.0.0/8", "127.0.0.1")
            self.assertEqual(self.request("/linked/outside.json", port=port)[0], 403)
            self.assertEqual(self.request("/test/linked/outside.json", port=port)[0], 403)
        finally:
            os.rmdir(junction)  # Removes the junction itself, never the target contents.

    def test_invalid_channel_or_ip_rejected_before_listen(self):
        for ip, channel in (("127.0.0.1", "../test"), ("127.0.0.1", ""), ("127.0.0.1", "a" * 33),
                            ("127.0.0.1", "test/"), ("127.1", "test"), ("::1", "test"), ("127.0.0.1", "official"), ("127.0.0.1", "OFFICIAL")):
            result = subprocess.run([str(self.executable), str(self.public), "18765", "127.0.0.0/8", ip, channel],
                                    capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 2)
            self.assertIn(b'Invalid ClientChannels', result.stderr)

    def test_check_validates_without_listening(self):
        updater = Path(__file__).resolve().parents[1] / "bin" / "JmdUpdater.exe"
        config = self.root / "check.json"
        base = dict(RootDirectory=str(self.public), BindAddress="127.0.0.1", Port=self.port,
                    AllowedCidrs=["127.0.0.0/8"], ClientChannels={"127.0.0.1": "test"})
        # Occupied port is intentional: check must never listen.
        for invalid in (None, {"Port": 0}, {"RootDirectory": str(self.root / "absent")},
                        {"BindAddress": "192.0.2.123"}, {"AllowedCidrs": ["broken"]},
                        {"ClientChannels": {"127.0.0.1": "official"}}):
            candidate = dict(base)
            if invalid: candidate.update(invalid)
            config.write_text(json.dumps(candidate), encoding="utf-8")
            result = subprocess.run([str(updater), "serve", "--config", str(config), "--check"],
                                    capture_output=True, timeout=10)
            self.assertEqual(result.returncode == 0, invalid is None, result.stderr)

    def test_oversized_header_is_rejected(self):
        with socket.create_connection(("127.0.0.1", self.port), timeout=5) as client:
            try:
                client.sendall(b"GET /monitor/latest.json HTTP/1.1\r\nX-Fill: " + b"x" * 17000 + b"\r\n\r\n")
                self.assertTrue(client.recv(1024).startswith(b"HTTP/1.1 400 "))
            except (ConnectionAbortedError, ConnectionResetError):
                # As in test_cidr_is_enforced: the server stops reading at the limit, and Windows may reset a
                # socket closed with unread data instead of delivering the 400. Both outcomes reject the request.
                pass


if __name__ == "__main__":
    unittest.main(verbosity=2)
