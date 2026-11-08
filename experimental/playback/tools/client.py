"""Small local companion client. Token values are accepted only through a file."""

import argparse
import json
import math
import urllib.error
import urllib.request
from pathlib import Path


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--port", type=int, default=18766)
    parser.add_argument("--token-file", type=Path, required=True)
    parser.add_argument(
        "command",
        choices=("snapshot", "capabilities", "play", "pause", "stop", "seek"),
        nargs="?",
        default="snapshot",
    )
    parser.add_argument("--position", type=float)
    args = parser.parse_args()
    if not 1 <= args.port <= 65535 or "/" in args.host or "@" in args.host:
        parser.error("Use a plain host and valid port")
    token = args.token_file.read_text(encoding="utf-8").strip()
    if not 16 <= len(token) <= 256 or not token.isascii() or not token.isprintable():
        parser.error("Token file has an invalid length")
    action = args.command
    if action == "seek" and (
        args.position is None or not math.isfinite(args.position) or args.position < 0
    ):
        parser.error("Seek needs a non-negative finite --position")
    payload = None
    endpoint = "playback" if action == "snapshot" else "capabilities"
    if action in {"play", "pause", "stop", "seek"}:
        endpoint = "command"
        payload = {"action": action}
        if action == "seek":
            payload["position_seconds"] = args.position
    request = urllib.request.Request(
        f"http://{args.host}:{args.port}/v1/{endpoint}",
        data=json.dumps(payload).encode() if payload else None,
        headers={
            "Authorization": "Bearer " + token,
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.build_opener(NoRedirect).open(
            request, timeout=20
        ) as response:
            print(json.dumps(json.load(response), indent=2))
    except urllib.error.HTTPError as error:
        raise SystemExit(f"Companion rejected request (HTTP {error.code})") from None
    except (urllib.error.URLError, TimeoutError):
        raise SystemExit("Could not reach playback companion") from None


if __name__ == "__main__":
    main()
