#!/usr/bin/env python3
import argparse
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent
COMPOSE = ROOT / "podman-compose.yml"


def run(args):
    print("+", " ".join(args))
    return subprocess.call(args, cwd=ROOT)


def capture(args):
    return subprocess.run(args, cwd=ROOT, text=True, capture_output=True)


def ensure_podman():
    info = capture(["podman", "info"])
    if info.returncode == 0:
        return 0

    print("Podman is not reachable. Trying to start the Podman machine...")
    started = run(["podman", "machine", "start"])
    if started != 0:
        print("Could not start Podman machine.", file=sys.stderr)
        print("Run: podman machine list", file=sys.stderr)
        print("If no machine exists, run: podman machine init", file=sys.stderr)
        return started

    info = capture(["podman", "info"])
    if info.returncode != 0:
        print("Podman machine started, but the socket is still unreachable.", file=sys.stderr)
        print(info.stderr.strip() or info.stdout.strip(), file=sys.stderr)
        return info.returncode
    return 0


def compose(*args):
    return run(["podman", "compose", "-f", str(COMPOSE), *args])


def require_yes(args, message):
    if args.yes:
        return
    print(message)
    answer = input("Type YES to continue: ")
    if answer != "YES":
        print("Cancelled.")
        sys.exit(1)


def main():
    parser = argparse.ArgumentParser(description="Manage the Arma 3 manager Podman stack.")
    parser.add_argument(
        "command",
        choices=["start", "stop", "restart", "rebuild", "status", "logs", "delete", "reset-volumes"],
    )
    parser.add_argument("--yes", action="store_true", help="Confirm destructive actions.")
    args = parser.parse_args()

    if not COMPOSE.exists():
        print(f"Missing {COMPOSE}", file=sys.stderr)
        return 1

    if args.command in {"start", "restart", "rebuild", "status", "logs", "delete", "reset-volumes"}:
        code = ensure_podman()
        if code != 0:
            return code

    if args.command == "start":
        return compose("up", "-d")
    if args.command == "stop":
        return compose("stop")
    if args.command == "restart":
        code = compose("stop")
        return code if code else compose("up", "-d")
    if args.command == "rebuild":
        return compose("up", "-d", "--build")
    if args.command == "status":
        return compose("ps")
    if args.command == "logs":
        return compose("logs", "--tail", "200")
    if args.command == "delete":
        require_yes(args, "This removes the running containers but keeps named volumes.")
        return compose("down")
    if args.command == "reset-volumes":
        require_yes(args, "This removes containers and named volumes. Server files, mods and SQLite data will be deleted.")
        return compose("down", "-v")

    return 1


if __name__ == "__main__":
    raise SystemExit(main())
