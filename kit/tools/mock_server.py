"""Mock of the QuietThings API, api_version 1 (stdlib only).

  python mock_server.py --token-file TOKEN [--port 18765] [--host 127.0.0.1]
  python mock_server.py --token VALUE

Follows the contract in kit/CONTRACT.md: Bearer auth on every path, 4 requests
per second (429), 1024 byte body cap (413), the same error texts and shapes.
Port 3 plays a port with no signal: POST /hdmi/3 answers 504 "not confirmed".
Use it to try the client or to point the Home Assistant integration at.
"""
import argparse
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

API_VERSION = 1
MAX_BODY = 1024
MAX_PER_SECOND = 4
NAMES = {1: "HDMI 1", 2: "Game console", 3: "HDMI 3", 4: "HDMI 4"}
CONNECTED = {1: True, 2: True, 3: False, 4: True}
NO_SIGNAL_PORTS = (3,)


class MockState:
    """Mutable TV state plus the rate limiter, shared by all requests."""

    def __init__(self, token, rate_limit=True, clock=time.monotonic):
        self.token = token
        self.rate_limit = rate_limit
        self.clock = clock
        self.lock = threading.Lock()
        self.started = clock()
        self.port = 1
        self.source_id = 13
        self.level = 15
        self.mute = False
        self.window_start = None
        self.count = 0
        self.seen = []

    def allow(self):
        if not self.rate_limit:
            return True
        now = self.clock()
        if self.window_start is None or now - self.window_start >= 1.0 or now < self.window_start:
            self.window_start = now
            self.count = 0
        if self.count >= MAX_PER_SECOND:
            return False
        self.count += 1
        return True

    def input_obj(self, port):
        return {"port": port, "id": 12 + port, "name": NAMES[port], "connected": CONNECTED[port]}

    def selected(self):
        return self.input_obj(self.port) if 1 <= self.port <= 4 else None

    def signal(self):
        active = 1 <= self.port <= 4 and self.source_id == 12 + self.port
        return {"source_id": self.source_id, "active": active}

    def volume(self):
        return {"level": self.level, "mute": self.mute}


def make_handler(state):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        timeout = 3  # seconds per socket read, like the real server's IO timeout

        def log_message(self, *args):
            pass

        def reply(self, code, obj):
            body = json.dumps(obj, separators=(",", ":")).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Connection", "close")
            if code == 401:
                self.send_header("WWW-Authenticate", "Bearer")
            self.end_headers()
            self.wfile.write(body)
            self.close_connection = True

        def fail(self, code, text, **extra):
            self.reply(code, {"ok": False, "error": text, **extra})

        def authorized(self):
            auth = self.headers.get("Authorization") or ""
            return (state.token != "" and auth[:7].lower() == "bearer "
                    and auth[7:].strip() == state.token)

        def read_json_object(self, body):
            if not body:
                self.fail(400, "body required")
                return None
            try:
                doc = json.loads(body)
            except ValueError:
                self.fail(400, "malformed json")
                return None
            if not isinstance(doc, dict):
                self.fail(400, "body must be an object")
                return None
            return doc

        def handle_any(self):
            # Same order as the real server: rate limit, request parse (400, 408),
            # auth, body size, route. Only state changes hold the lock, never a socket read.
            with state.lock:
                state.seen.append((self.command, self.path.split("?", 1)[0]))
                allowed = state.allow()
            if not allowed:
                return self.fail(429, "rate limited")
            length = 0
            for name, value in self.headers.items():
                lname = name.lower()
                if lname == "content-length":
                    value = value.strip()
                    # NumberStyles.None: ASCII digits only, no sign or separators, int range.
                    if not (value.isascii() and value.isdigit()) or int(value) > 2147483647:
                        return self.fail(400, "bad content-length")
                    length = int(value)
                elif lname == "transfer-encoding":
                    return self.fail(400, "transfer-encoding not supported")
            body = b""
            if length <= MAX_BODY:
                try:
                    body = self.rfile.read(length) if length else b""
                except OSError:
                    return self.fail(408, "timeout")
                if len(body) < length:
                    return self.fail(408, "timeout")
            with state.lock:
                self.route(length, body)
            if length > MAX_BODY:
                self.drain(length)

        def drain(self, length):
            """Best effort, bounded read of an oversize body so the 413 is not lost to a reset."""
            try:
                left = min(length, 65536)
                while left > 0:
                    chunk = self.rfile.read(min(left, 4096))
                    if not chunk:
                        break
                    left -= len(chunk)
            except OSError:
                pass

        def route(self, length, body):
            method = self.command
            path = self.path.split("?", 1)[0]
            if not self.authorized():
                return self.fail(401, "unauthorized")
            if length > MAX_BODY:
                return self.fail(413, "body too large")
            if path == "/health":
                if method != "GET":
                    return self.fail(405, "method not allowed")
                return self.reply(200, {"ok": True, "app": "quiet-home", "api_version": API_VERSION,
                                        "uptime_s": int(state.clock() - state.started), "paused": False})
            if path == "/state":
                if method != "GET":
                    return self.fail(405, "method not allowed")
                return self.reply(200, {
                    "ok": True, "api_version": API_VERSION, "paused": False,
                    "selected": state.selected(), "signal": state.signal(),
                    "inputs": [state.input_obj(p) for p in range(1, 5)],
                    "volume": state.volume()})
            if path.startswith("/hdmi/"):
                if method != "POST":
                    return self.fail(405, "method not allowed")
                num = path[6:]
                if len(num) != 1 or num not in "1234":
                    return self.fail(400, "hdmi must be 1-4")
                port = int(num)
                if port in NO_SIGNAL_PORTS:
                    # Selection never reaches the port: the body carries the previous selection.
                    return self.reply(504, {"ok": False, "error": "not confirmed",
                                            "selected": state.selected(), "signal": state.signal()})
                state.port = port
                state.source_id = 12 + port
                return self.reply(200, {"ok": True, "selected": state.selected(),
                                        "signal": state.signal()})
            if path == "/volume":
                if method != "POST":
                    return self.fail(405, "method not allowed")
                doc = self.read_json_object(body)
                if doc is None:
                    return
                level = doc.get("level")
                if isinstance(level, bool) or not isinstance(level, (int, float)):
                    return self.fail(400, "level must be a number")
                if level != int(level):
                    return self.fail(400, "level must be an integer")
                state.level = max(0, min(100, int(level)))
                return self.reply(200, {"ok": True, "volume": state.volume()})
            if path == "/mute":
                if method != "POST":
                    return self.fail(405, "method not allowed")
                doc = self.read_json_object(body)
                if doc is None:
                    return
                mute = doc.get("mute")
                if not isinstance(mute, bool):
                    return self.fail(400, "mute must be true or false")
                state.mute = mute
                return self.reply(200, {"ok": True, "volume": state.volume()})
            return self.fail(404, "not found")

        do_GET = do_POST = do_PUT = do_DELETE = do_PATCH = handle_any

    return Handler


def make_server(token, host="127.0.0.1", port=0, rate_limit=True):
    """Build (server, state). The caller runs serve_forever()."""
    state = MockState(token, rate_limit=rate_limit)
    server = ThreadingHTTPServer((host, port), make_handler(state))
    server.daemon_threads = True
    return server, state


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=18765)
    p.add_argument("--token", help="token value (visible in the process list; prefer --token-file)")
    p.add_argument("--token-file")
    p.add_argument("--no-rate-limit", action="store_true")
    args = p.parse_args(argv)
    token = args.token or ""
    if args.token_file:
        with open(args.token_file, encoding="utf-8") as f:
            token = f.read().strip()
    if len(token) < 16:
        print("a token of at least 16 characters is required (--token or --token-file)", file=sys.stderr)
        return 2
    server, _ = make_server(token, args.host, args.port, rate_limit=not args.no_rate_limit)
    print(f"QuietThings mock listening on {args.host}:{server.server_address[1]}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
