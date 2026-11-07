"""Command line client for the QuietThings API (stdlib only).

  python api_client.py --host TV_ADDRESS --token-file TOKEN health
  python api_client.py --host TV_ADDRESS --token-file TOKEN state
  python api_client.py --host TV_ADDRESS --token-file TOKEN hdmi 2
  python api_client.py --host TV_ADDRESS --token-file TOKEN volume 20
  python api_client.py --host TV_ADDRESS --token-file TOKEN mute off

Prints the JSON response. Exit code 0 only on HTTP 2xx. Never prints the token.
The API allows 4 requests per second; leave 300 ms or more between calls.
"""
import argparse
import json
import sys
import urllib.error
import urllib.request

DEFAULT_PORT = 18765
GET_TIMEOUT = 10
HDMI_TIMEOUT = 12


def call(host, port, token, method, path, timeout, payload=None):
    """One request. Returns (status, body bytes). Raises on connection errors."""
    headers = {"Authorization": f"Bearer {token}"}
    data = None
    if method == "POST":
        data = b"" if payload is None else json.dumps(payload).encode("utf-8")
        if payload is not None:
            headers["Content-Type"] = "application/json"
    req = urllib.request.Request(
        f"http://{host}:{port}{path}", method=method, data=data, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as err:
        return err.code, err.read()


def build_parser():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--host", required=True)
    p.add_argument("--port", type=int, default=DEFAULT_PORT)
    p.add_argument("--token-file", required=True)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("health")
    sub.add_parser("state")
    h = sub.add_parser("hdmi")
    h.add_argument("n", type=int, choices=range(1, 5), metavar="N")
    v = sub.add_parser("volume")
    v.add_argument("level", type=int, choices=range(0, 101), metavar="N")
    m = sub.add_parser("mute")
    m.add_argument("state", choices=("on", "off"))
    return p


def main(argv=None):
    args = build_parser().parse_args(argv)
    token = ""
    try:
        with open(args.token_file, encoding="utf-8") as f:
            token = f.read().strip()
        if not token:
            raise ValueError("token file is empty")
        if args.cmd == "hdmi":
            status, body = call(args.host, args.port, token, "POST", f"/hdmi/{args.n}", HDMI_TIMEOUT)
        elif args.cmd == "volume":
            status, body = call(args.host, args.port, token, "POST", "/volume", GET_TIMEOUT,
                                {"level": args.level})
        elif args.cmd == "mute":
            status, body = call(args.host, args.port, token, "POST", "/mute", GET_TIMEOUT,
                                {"mute": args.state == "on"})
        else:
            status, body = call(args.host, args.port, token, "GET", f"/{args.cmd}", GET_TIMEOUT)
    except Exception as exc:
        msg = type(exc).__name__ + ": " + str(exc)
        print(json.dumps({"error": msg.replace(token, "***") if token else msg}), file=sys.stderr)
        return 1
    text = body.decode("utf-8", "replace")
    if token:
        text = text.replace(token, "***")
    try:
        text = json.dumps(json.loads(text))
    except ValueError:
        pass
    print(text)
    return 0 if 200 <= status < 300 else 1


if __name__ == "__main__":
    sys.exit(main())
