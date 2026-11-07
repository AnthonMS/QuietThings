"""Unit tests for mock_server and api_client.  Run: python -I kit/tools/test_tools.py"""
import contextlib
import http.client
import io
import json
import os
import socket
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import api_client  # noqa: E402
import mock_server  # noqa: E402
import predeploy  # noqa: E402

TOKEN = "t0ken-for-tests-1234567890"


def start(rate_limit=False):
    server, state = mock_server.make_server(TOKEN, "127.0.0.1", 0, rate_limit=rate_limit)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, state


def raw(server, method, path, body=None, token=TOKEN, headers=None):
    conn = http.client.HTTPConnection("127.0.0.1", server.server_address[1], timeout=5)
    hdrs = dict(headers or {})
    if token is not None:
        hdrs["Authorization"] = f"Bearer {token}"
    conn.request(method, path, body=body, headers=hdrs)
    resp = conn.getresponse()
    data = resp.read()
    conn.close()
    return resp.status, json.loads(data), resp


class MockShapes(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv, cls.state = start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()

    def test_health(self):
        code, body, resp = raw(self.srv, "GET", "/health")
        self.assertEqual(code, 200)
        self.assertEqual(body["app"], "quiet-home")
        self.assertEqual(body["api_version"], 1)
        self.assertTrue(body["ok"])
        self.assertEqual(set(body), {"ok", "app", "api_version", "uptime_s", "paused"})
        self.assertEqual(resp.getheader("Content-Type"), "application/json")

    def test_state_shape(self):
        code, body, _ = raw(self.srv, "GET", "/state")
        self.assertEqual(code, 200)
        self.assertEqual(body["api_version"], 1)
        self.assertEqual([i["port"] for i in body["inputs"]], [1, 2, 3, 4])
        self.assertEqual([i["id"] for i in body["inputs"]], [13, 14, 15, 16])
        self.assertEqual(set(body["selected"]), {"port", "id", "name", "connected"})
        self.assertEqual(set(body["signal"]), {"source_id", "active"})
        self.assertEqual(set(body["volume"]), {"level", "mute"})

    def test_hdmi_200_and_504(self):
        code, body, _ = raw(self.srv, "POST", "/hdmi/2", body=b"")
        self.assertEqual(code, 200)
        self.assertEqual(body["selected"]["port"], 2)
        self.assertEqual(body["signal"], {"source_id": 14, "active": True})
        # A 504 carries the selection that is still current, not the requested port.
        code, body, _ = raw(self.srv, "POST", "/hdmi/3", body=b"")
        self.assertEqual(code, 504)
        self.assertEqual((body["ok"], body["error"]), (False, "not confirmed"))
        self.assertEqual(body["selected"]["port"], 2)
        self.assertEqual(body["signal"], {"source_id": 14, "active": True})
        self.assertEqual(raw(self.srv, "GET", "/state")[1]["selected"]["port"], 2)

    def test_hdmi_bad_port_and_method(self):
        self.assertEqual(raw(self.srv, "POST", "/hdmi/5", body=b"")[:2],
                         (400, {"ok": False, "error": "hdmi must be 1-4"}))
        self.assertEqual(raw(self.srv, "GET", "/hdmi/1")[:2],
                         (405, {"ok": False, "error": "method not allowed"}))

    def test_volume_and_mute_echo_reread_state(self):
        code, body, _ = raw(self.srv, "POST", "/volume", body=b'{"level": 20}')
        self.assertEqual((code, body), (200, {"ok": True, "volume": {"level": 20, "mute": False}}))
        code, body, _ = raw(self.srv, "POST", "/volume", body=b'{"level": 250}')
        self.assertEqual(body["volume"]["level"], 100)
        code, body, _ = raw(self.srv, "POST", "/mute", body=b'{"mute": true}')
        self.assertEqual(body["volume"], {"level": 100, "mute": True})
        raw(self.srv, "POST", "/mute", body=b'{"mute": false}')
        self.assertFalse(raw(self.srv, "GET", "/state")[1]["volume"]["mute"])

    def test_body_errors(self):
        post = lambda path, body: raw(self.srv, "POST", path, body=body)[:2]
        self.assertEqual(post("/volume", b""), (400, {"ok": False, "error": "body required"}))
        self.assertEqual(post("/volume", b"{"), (400, {"ok": False, "error": "malformed json"}))
        self.assertEqual(post("/volume", b"[1]"), (400, {"ok": False, "error": "body must be an object"}))
        self.assertEqual(post("/volume", b'{"level": "5"}'), (400, {"ok": False, "error": "level must be a number"}))
        self.assertEqual(post("/volume", b'{"level": 5.5}'), (400, {"ok": False, "error": "level must be an integer"}))
        self.assertEqual(post("/mute", b'{"mute": 1}'), (400, {"ok": False, "error": "mute must be true or false"}))

    def test_auth_404_405_413(self):
        code, body, resp = raw(self.srv, "GET", "/health", token="wrong")
        self.assertEqual((code, body), (401, {"ok": False, "error": "unauthorized"}))
        self.assertEqual(resp.getheader("WWW-Authenticate"), "Bearer")
        self.assertEqual(raw(self.srv, "GET", "/health", token=None)[0], 401)
        self.assertEqual(raw(self.srv, "GET", "/nothing")[:2], (404, {"ok": False, "error": "not found"}))
        self.assertEqual(raw(self.srv, "POST", "/state", body=b"")[0], 405)
        big = raw(self.srv, "POST", "/volume", body=b"x" * 2000)
        self.assertEqual(big[:2], (413, {"ok": False, "error": "body too large"}))

    def test_bad_content_length_beats_auth(self):
        for value in ("-5", "+5", "5_0", "abc", "99999999999"):
            for token in (TOKEN, "wrong"):
                code, body, _ = raw(self.srv, "POST", "/volume", token=token,
                                    headers={"Content-Length": value})
                self.assertEqual((code, body), (400, {"ok": False, "error": "bad content-length"}), value)

    def test_transfer_encoding_rejected(self):
        code, body, _ = raw(self.srv, "POST", "/volume", token="wrong",
                            headers={"Transfer-Encoding": "chunked"})
        self.assertEqual((code, body), (400, {"ok": False, "error": "transfer-encoding not supported"}))

    def test_short_body_times_out_without_blocking_others(self):
        sock = socket.create_connection(("127.0.0.1", self.srv.server_address[1]), timeout=10)
        sock.sendall(b"POST /volume HTTP/1.1\r\nHost: x\r\nContent-Length: 50\r\n\r\n{")
        # Another client is served while the first one is stalled.
        self.assertEqual(raw(self.srv, "GET", "/health")[0], 200)
        data = b""
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            data += chunk
        sock.close()
        self.assertTrue(data.startswith(b"HTTP/1.1 408"), data[:40])
        self.assertIn(b'"error":"timeout"', data)

    def test_scheme_case_insensitive(self):
        code, _, _ = raw(self.srv, "GET", "/health", token=None,
                         headers={"Authorization": f"bearer {TOKEN}"})
        self.assertEqual(code, 200)


class MockRateLimit(unittest.TestCase):
    def test_429_comes_before_auth_and_parse(self):
        srv, _ = start(rate_limit=True)
        try:
            codes = [raw(srv, "GET", "/health", token="wrong", headers={"Content-Length": "-5"})[0]
                     for _ in range(5)]
        finally:
            srv.shutdown()
            srv.server_close()
        self.assertEqual(codes, [400] * 4 + [429])

    def test_fifth_request_in_a_second_is_429(self):
        srv, _ = start(rate_limit=True)
        try:
            codes = [raw(srv, "GET", "/health")[0] for _ in range(6)]
        finally:
            srv.shutdown()
            srv.server_close()
        self.assertEqual(codes[:4], [200] * 4)
        self.assertEqual(codes[4], 429)

    def test_limiter_window_with_fake_clock(self):
        now = [0.0]
        st = mock_server.MockState(TOKEN, clock=lambda: now[0])
        self.assertEqual([st.allow() for _ in range(5)], [True] * 4 + [False])
        now[0] = 1.0
        self.assertTrue(st.allow())


class ClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv, cls.state = start()
        cls.port = cls.srv.server_address[1]
        fd, cls.good = tempfile.mkstemp()
        os.write(fd, (TOKEN + "\n").encode())
        os.close(fd)
        fd, cls.bad = tempfile.mkstemp()
        os.write(fd, b"wrong-token\n")
        os.close(fd)
        fd, cls.empty = tempfile.mkstemp()
        os.close(fd)

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()
        for f in (cls.good, cls.bad, cls.empty):
            os.unlink(f)

    def run_client(self, tokfile, *cmd):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = api_client.main(["--host", "127.0.0.1", "--port", str(self.port),
                                    "--token-file", tokfile, *cmd])
        return code, out.getvalue(), err.getvalue()

    def test_all_subcommands_exit_zero(self):
        self.assertEqual(json.loads(self.run_client(self.good, "health")[1])["api_version"], 1)
        self.assertEqual(json.loads(self.run_client(self.good, "state")[1])["api_version"], 1)
        code, out, _ = self.run_client(self.good, "hdmi", "2")
        self.assertEqual((code, json.loads(out)["selected"]["port"]), (0, 2))
        code, out, _ = self.run_client(self.good, "volume", "20")
        self.assertEqual((code, json.loads(out)["volume"]["level"]), (0, 20))
        code, out, _ = self.run_client(self.good, "mute", "on")
        self.assertEqual((code, json.loads(out)["volume"]["mute"]), (0, True))
        code, out, _ = self.run_client(self.good, "mute", "off")
        self.assertEqual((code, json.loads(out)["volume"]["mute"]), (0, False))
        self.assertIn(("POST", "/hdmi/2"), self.state.seen)
        self.assertIn(("POST", "/volume"), self.state.seen)
        self.assertIn(("POST", "/mute"), self.state.seen)

    def test_failures_exit_nonzero(self):
        self.assertEqual(self.run_client(self.good, "hdmi", "3")[0], 1)
        self.assertEqual(self.run_client(self.bad, "health")[0], 1)
        self.assertEqual(self.run_client(self.good + ".missing", "health")[0], 1)
        self.assertEqual(self.run_client(self.empty, "health")[0], 1)

    def test_never_prints_token(self):
        for tok, cmd in ((self.good, ("health",)), (self.bad, ("health",)), (self.good, ("state",)),
                         (self.good, ("volume", "10"))):
            _, out, err = self.run_client(tok, *cmd)
            self.assertNotIn(TOKEN, out + err)
        self.assertNotIn("wrong-token", self.run_client(self.bad, "health")[1])

    def test_connection_error_hides_token(self):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = api_client.main(["--host", "127.0.0.1", "--port", "1", "--token-file", self.good,
                                    "health"])
        self.assertEqual(code, 1)
        self.assertNotIn(TOKEN, err.getvalue())

    def test_parser_rejects_out_of_range(self):
        for argv in (["hdmi", "5"], ["hdmi", "0"], ["volume", "101"], ["volume", "-1"], ["mute", "maybe"]):
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                api_client.main(["--host", "x", "--token-file", self.good, *argv])

    def test_default_port(self):
        args = api_client.build_parser().parse_args(["--host", "x", "--token-file", "t", "health"])
        self.assertEqual(args.port, 18765)


class PredeployStart(unittest.TestCase):
    def test_port_taken_explains_itself(self):
        tmp = tempfile.mkdtemp()
        token = os.path.join(tmp, "t")
        predeploy.write_secret(token, "a" * 64)
        blocker = socket.socket()
        blocker.bind(("127.0.0.1", 0))
        blocker.listen(1)
        taken = blocker.getsockname()[1]
        real = predeploy.free_port
        predeploy.free_port = lambda: taken
        try:
            proc, port, err = predeploy.start_mock(token, tmp)
        finally:
            predeploy.free_port = real
            blocker.close()
        self.assertIsNone(proc)
        self.assertIn("failed to start", err)
        self.assertIn(str(taken), err)

    def test_retry_on_a_new_port(self):
        tmp = tempfile.mkdtemp()
        token = os.path.join(tmp, "t")
        predeploy.write_secret(token, "a" * 64)
        blocker = socket.socket()
        blocker.bind(("127.0.0.1", 0))
        blocker.listen(1)
        ports = iter([blocker.getsockname()[1], real_free()])
        real = predeploy.free_port
        predeploy.free_port = lambda: next(ports)
        try:
            proc, port, err = predeploy.start_mock(token, tmp)
        finally:
            predeploy.free_port = real
            blocker.close()
        try:
            self.assertIsNone(err)
            self.assertIsNotNone(proc)
        finally:
            if proc:
                proc.kill()
                proc.wait()


def real_free():
    return predeploy.free_port()


if __name__ == "__main__":
    unittest.main()
