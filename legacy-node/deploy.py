#!/usr/bin/env python3
"""
deploy.py — Fast Docker deploy for Arma 3 Server Manager
=========================================================

Usage:
  python deploy.py              # full deploy: build → up → prune
  python deploy.py --restart    # just restart the container (no rebuild)
  python deploy.py --no-prune   # deploy without cleaning old images
  python deploy.py --skip-build # skip image rebuild, re-up with existing image
  python deploy.py --logs       # stream container logs after deploy

Requires: Docker + docker compose v2
"""

import argparse
import subprocess
import sys
import time


# ─── Helpers ─────────────────────────────────────────────────────────────────

BOLD  = "\033[1m"
GREEN = "\033[92m"
CYAN  = "\033[96m"
RED   = "\033[91m"
DIM   = "\033[2m"
RESET = "\033[0m"


def header(msg: str) -> None:
    print(f"\n{BOLD}{CYAN}▶ {msg}{RESET}")


def ok(msg: str) -> None:
    print(f"{GREEN}  ✔ {msg}{RESET}")


def err(msg: str) -> None:
    print(f"{RED}  ✘ {msg}{RESET}", file=sys.stderr)


def run(cmd: list[str], check: bool = True, capture: bool = False) -> subprocess.CompletedProcess:
    print(f"{DIM}  $ {' '.join(cmd)}{RESET}")
    return subprocess.run(cmd, check=check, capture_output=capture, text=True)


def step(n: int, total: int, label: str) -> None:
    print(f"\n{BOLD}[{n}/{total}] {label}{RESET}")


# ─── Deploy steps ─────────────────────────────────────────────────────────────

def build():
    header("Building Docker image")
    run(["docker", "compose", "build", "--pull"])
    ok("Image built")


def deploy():
    header("Deploying container")
    run(["docker", "compose", "up", "-d", "--remove-orphans", "--force-recreate"])
    ok("Container started")


def restart():
    header("Restarting container")
    run(["docker", "compose", "restart"])
    ok("Container restarted")


def prune():
    header("Cleaning unused images and build cache")
    result = run(
        ["docker", "system", "prune", "-af", "--filter", "until=24h"],
        check=False,
        capture=True,
    )
    if result.returncode == 0:
        # Show how much space was reclaimed
        for line in result.stdout.splitlines():
            if "Total reclaimed" in line or "space" in line.lower():
                ok(line.strip())
                break
        else:
            ok("Cleanup complete")
    else:
        print(f"  {DIM}prune skipped (non-critical){RESET}")


def wait_healthy(timeout: int = 60) -> bool:
    """Poll until the container reports healthy or timeout."""
    header(f"Waiting for container to become healthy (max {timeout}s)")
    deadline = time.time() + timeout
    while time.time() < deadline:
        result = run(
            ["docker", "inspect", "--format", "{{.State.Health.Status}}", "arma3-server"],
            check=False, capture=True,
        )
        status = result.stdout.strip()
        if status == "healthy":
            ok("Container is healthy")
            return True
        if status in ("unhealthy", "none", ""):
            break
        print(f"  status: {status} — waiting…")
        time.sleep(3)
    return False


def show_status():
    header("Container status")
    run(["docker", "compose", "ps"])


def stream_logs():
    header("Streaming logs (Ctrl+C to stop)")
    try:
        subprocess.run(["docker", "compose", "logs", "-f", "--tail", "50"])
    except KeyboardInterrupt:
        print("\n  (stopped)")


# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Deploy Arma 3 Server Manager Docker container",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("--restart",    action="store_true", help="Just restart (no rebuild)")
    parser.add_argument("--no-prune",   action="store_true", help="Skip docker system prune")
    parser.add_argument("--skip-build", action="store_true", help="Skip image rebuild")
    parser.add_argument("--logs",       action="store_true", help="Stream logs after deploy")
    parser.add_argument("--no-wait",    action="store_true", help="Don't wait for healthcheck")
    args = parser.parse_args()

    t0 = time.time()
    print(f"\n{BOLD}{'='*50}")
    print(" Arma 3 Server Manager — Deploy")
    print(f"{'='*50}{RESET}")

    try:
        if args.restart:
            restart()
        else:
            steps = []
            if not args.skip_build:
                steps.append(("Build image",   build))
            steps.append(("Deploy",            deploy))
            if not args.no_prune:
                steps.append(("Prune old images", prune))

            total = len(steps)
            for i, (label, fn) in enumerate(steps, 1):
                step(i, total, label)
                fn()

        if not args.no_wait:
            wait_healthy()

        show_status()

        elapsed = time.time() - t0
        print(f"\n{GREEN}{BOLD}✔ Deploy complete in {elapsed:.1f}s{RESET}\n")

        if args.logs:
            stream_logs()

    except subprocess.CalledProcessError as e:
        err(f"Command failed: {e}")
        sys.exit(1)
    except KeyboardInterrupt:
        print("\n  cancelled")
        sys.exit(130)


if __name__ == "__main__":
    main()
