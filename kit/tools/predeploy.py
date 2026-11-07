"""Pre-deploy checks for a QuietThings API patch (stdlib only).

  python predeploy.py --launcher DIR [--framework-dir DIR]
                      [--target-framework net6.0|netcoreapp3.1] [--out DIR]
                      [--make-token PATH]

DIR is the launcher source folder holding Home.cs and Home.csproj. Prints PASS,
FAIL or SKIP (reason) for each check and exits 0 only when no check failed.
It runs only `dotnet` and `python <file>` with explicit file arguments.
"""
import argparse
import hashlib
import json
import os
import re
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time

KIT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(KIT, "tools")
FRAMEWORK_DLLS = ("Tizen.Applications.Common", "Tizen.NUI", "Tizen.System", "Tizen")
PACE = 0.35  # seconds between client calls; the API allows 4 requests per second

NOT_VERIFIED = (
    "P/Invoke bindings (libvconf.so.0, libsource-api.so, libcapi-media-sound-manager-tv.so.0) and their behaviour",
    "readability of memory/eden/source/current_source from the app",
    "UI-thread marshalling",
    "the listener actually binding on port 18765",
    "the DLL loading on the TV's runtime (framework version match)",
    "token owner and SMACK label",
    "real HDMI switching and the 504 path",
    "the rate limiter under real traffic",
)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def read_text(path):
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        return f.read()


def run(cmd, timeout):
    """Run a command list with no shell. Returns (code, combined output)."""
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                           encoding="utf-8", errors="replace")
    except FileNotFoundError:
        return 127, f"{cmd[0]}: not found"
    except subprocess.TimeoutExpired:
        return 124, f"timed out after {timeout} s"
    return p.returncode, (p.stdout or "") + (p.stderr or "")


class Report:
    def __init__(self):
        self.failed = False

    def line(self, n, title, status, detail=""):
        if status == "FAIL":
            self.failed = True
        suffix = f" ({detail})" if detail and status == "SKIP" else (f": {detail}" if detail else "")
        print(f"{n}. {title}: {status}{suffix}", flush=True)


def check_integrity(launcher):
    bad = []
    for name in ("LanApi.cs", "LanApiCore.cs"):
        mine, ref = os.path.join(launcher, name), os.path.join(KIT, "src", name)
        if not os.path.isfile(ref):
            bad.append(f"kit/src/{name} missing")
        elif not os.path.isfile(mine):
            bad.append(f"{name} missing in launcher dir")
        elif sha256_file(mine) != sha256_file(ref):
            bad.append(f"{name} differs from kit/src/{name}")
    return bad


HOME_PATTERNS = (
    ("new LanApi(", r"new\s+LanApi\s*\("),
    ("int SelectSource(", r"\bint\s+SelectSource\s*\("),
    ("volatile bool paused", r"\bvolatile\s+bool\s+paused\b"),
    ("bool SetVolume(int", r"\bbool\s+SetVolume\s*\(\s*int\b"),
    ("bool SetMute(bool", r"\bbool\s+SetMute\s*\(\s*bool\b"),
    ("paused=true", r"\bpaused\s*=\s*true\b"),
    ("paused=false", r"\bpaused\s*=\s*false\b"),
)


def check_hooks(launcher):
    bad = []
    csproj, home = os.path.join(launcher, "Home.csproj"), os.path.join(launcher, "Home.cs")
    if not os.path.isfile(csproj):
        bad.append("Home.csproj missing")
    else:
        text = read_text(csproj)
        for name in ("LanApi.cs", "LanApiCore.cs"):
            if not re.search(r'<Compile\s+Include\s*=\s*"' + re.escape(name) + '"', text):
                bad.append(f"Home.csproj has no Compile item for {name}")
        if not re.search(r"<EnableDefaultCompileItems>\s*false\s*</EnableDefaultCompileItems>", text, re.I):
            bad.append("EnableDefaultCompileItems is not false")
    if not os.path.isfile(home):
        bad.append("Home.cs missing")
    else:
        text = read_text(home)
        for label, pattern in HOME_PATTERNS:
            if not re.search(pattern, text):
                bad.append(f"Home.cs lacks {label}")
    return bad


def check_contract_tests():
    project = os.path.join(KIT, "tests", "LanApiTests.csproj")
    if not os.path.isfile(project):
        return "kit/tests/LanApiTests.csproj missing", ""
    code, out = run(["dotnet", "run", "--project", project], 600)
    last = [ln for ln in out.splitlines() if ln.strip()][-1:] or [""]
    if code != 0:
        return f"dotnet run exited {code}: {last[0]}", ""
    return None, last[0]


def check_compile(launcher, framework_dir, tf, out_dir):
    missing = [n for n in FRAMEWORK_DLLS if not os.path.isfile(os.path.join(framework_dir, n + ".dll"))]
    if missing:
        return f"framework dir lacks {', '.join(n + '.dll' for n in missing)}", ""
    os.makedirs(out_dir, exist_ok=True)
    code, out = run(["dotnet", "build", os.path.join(launcher, "Home.csproj"), "-c", "Release",
                     f"-p:TargetFramework={tf}", f"-p:TargetFrameworks={tf}",
                     f"-p:TV_FRAMEWORK_DIR={framework_dir}", "-o", out_dir], 900)
    if code != 0:
        errs = [ln.strip() for ln in out.splitlines() if " error " in ln][:3]
        return f"dotnet build exited {code}: " + (" | ".join(errs) or "see dotnet output"), ""
    dll = os.path.join(out_dir, "BixbyPromotion.dll")
    if not os.path.isfile(dll):
        return "build succeeded but BixbyPromotion.dll is not in the output folder", ""
    digest = sha256_file(dll)
    with open(os.path.join(out_dir, "BixbyPromotion.sha256"), "w", encoding="utf-8") as f:
        f.write(digest + "\n")
    return None, digest


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def write_secret(path, value):
    """Create a new file with mode 0600 where the OS supports it. Never overwrites."""
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(value + "\n")
    try:
        os.chmod(path, 0o600)
    except OSError:
        pass


def start_mock(token_path, tmp):
    """Start mock_server.py on a free port. Returns (proc, port, error).

    The port is probed then released, so another process can take it before the child
    binds. The child prints a "listening" line only after a successful bind; if it exits
    first, its stderr is returned as the error. Tries twice on fresh ports.
    """
    mock = os.path.join(TOOLS, "mock_server.py")
    error = "mock server did not start"
    for attempt in range(2):
        port = free_port()
        out_path = os.path.join(tmp, f"mock{attempt}.out")
        err_path = os.path.join(tmp, f"mock{attempt}.err")
        with open(out_path, "w") as fo, open(err_path, "w") as fe:
            proc = subprocess.Popen([sys.executable, "-I", mock, "--port", str(port),
                                     "--token-file", token_path], stdout=fo, stderr=fe)
        end = time.monotonic() + 10
        while time.monotonic() < end:
            if "listening" in read_text(out_path):
                return proc, port, None
            if proc.poll() is not None:
                break
            time.sleep(0.1)
        if proc.poll() is None:
            proc.kill()
            proc.wait()
        tail = [ln for ln in read_text(err_path).splitlines() if ln.strip()][-1:]
        error = f"mock server failed to start on port {port}: " + (tail[0] if tail else "no output")
    return None, 0, error


def check_client_vs_mock():
    tmp = tempfile.mkdtemp(prefix="qt-predeploy-")
    token_path = os.path.join(tmp, "mock.token")
    write_secret(token_path, secrets.token_hex(32))
    client = os.path.join(TOOLS, "api_client.py")
    proc = None
    try:
        proc, port, error = start_mock(token_path, tmp)
        if error:
            return error
        steps = (
            (("health",), lambda d: d.get("ok") is True and d.get("api_version") == 1),
            (("state",), lambda d: d.get("api_version") == 1 and len(d.get("inputs", [])) == 4
                and "selected" in d and "signal" in d and isinstance(d.get("volume"), dict)),
            (("hdmi", "2"), lambda d: d.get("ok") is True and d["selected"]["port"] == 2
                and d["signal"]["active"] is True),
            (("volume", "20"), lambda d: d.get("ok") is True and d["volume"]["level"] == 20),
            (("mute", "off"), lambda d: d.get("ok") is True and d["volume"]["mute"] is False),
        )
        for args, ok in steps:
            time.sleep(PACE)
            code, out = run([sys.executable, "-I", client, "--host", "127.0.0.1", "--port", str(port),
                             "--token-file", token_path, *args], 30)
            name = " ".join(args)
            if code != 0:
                return f"api_client {name} exited {code}"
            try:
                data = json.loads(out)
                good = ok(data)
            except (ValueError, KeyError, TypeError):
                good = False
            if not good:
                return f"api_client {name} returned unexpected fields"
        return None
    finally:
        if proc is not None:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        shutil.rmtree(tmp, ignore_errors=True)


def make_token(path):
    try:
        write_secret(path, secrets.token_hex(32))
    except FileExistsError:
        return f"{path} already exists; choose a new path (the old token is not overwritten)"
    except OSError as exc:
        return f"cannot write {path}: {exc.strerror or exc}"
    return None


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--launcher", required=True, metavar="DIR")
    p.add_argument("--framework-dir", metavar="DIR")
    p.add_argument("--target-framework", choices=("net6.0", "netcoreapp3.1"), default="net6.0")
    p.add_argument("--out", metavar="DIR")
    p.add_argument("--make-token", metavar="PATH")
    args = p.parse_args(argv)
    launcher = os.path.abspath(args.launcher)
    rep = Report()
    print(f"QuietThings pre-deploy checks, launcher dir: {launcher}", flush=True)

    bad = check_integrity(launcher)
    rep.line(1, "Kit integrity", "FAIL" if bad else "PASS", "; ".join(bad))

    bad = check_hooks(launcher)
    rep.line(2, "Hooks", "FAIL" if bad else "PASS", "; ".join(bad))

    err, last = check_contract_tests()
    rep.line(3, "Contract tests", "FAIL" if err else "PASS", err or last)

    digest = None
    if not args.framework_dir:
        rep.line(4, "Compile", "SKIP", "no --framework-dir given")
    else:
        out_dir = os.path.abspath(args.out) if args.out else os.path.join(
            tempfile.gettempdir(), "quietthings-predeploy")
        err, digest = check_compile(launcher, os.path.abspath(args.framework_dir),
                                    args.target_framework, out_dir)
        rep.line(4, "Compile", "FAIL" if err else "PASS", err or "")
        if digest:
            print(f"   BixbyPromotion.dll sha256 {digest}", flush=True)
            print(f"   (EXPECTED_SHA256 for install-api.sh; also in {os.path.join(out_dir, 'BixbyPromotion.sha256')})",
                  flush=True)

    err = check_client_vs_mock()
    rep.line(5, "Client versus mock", "FAIL" if err else "PASS", err or "")

    if args.make_token:
        err = make_token(args.make_token)
        rep.line(6, "Token", "FAIL" if err else "PASS",
                 err or f"written to {args.make_token} (mode 0600 where supported); value not shown")
    else:
        rep.line(6, "Token", "SKIP", "no --make-token given")

    print("\nNot verified by this script (check on the TV with recipes/verify-api.sh and api_client.py):")
    for item in NOT_VERIFIED:
        print(f"  - {item}")
    print("\nResult: " + ("FAIL" if rep.failed else "no check failed"))
    return 1 if rep.failed else 0


if __name__ == "__main__":
    sys.exit(main())
