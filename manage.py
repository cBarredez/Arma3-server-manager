#!/usr/bin/env python3
"""
manage.py — Manage the Arma 3 Manager container stack with Podman.
Uses podman-compose (pure Python, no Docker required).
Install it if missing:  pip install podman-compose
"""
import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path


ROOT         = Path(__file__).resolve().parent
COMPOSE      = ROOT / "podman-compose.yml"
COMPOSE_HOST = ROOT / "podman-compose.host.yml"


def run(args):
    print("+", " ".join(str(a) for a in args))
    return subprocess.call([str(a) for a in args], cwd=ROOT)


def capture(args):
    return subprocess.run([str(a) for a in args], cwd=ROOT, text=True, capture_output=True)


def compose_bin():
    """
    Find the best available Podman compose command.

    On Linux (production server):
      → podman-compose  (pure Python, no Docker)
      → python -m podman_compose  (same, fallback if not in PATH)

    On Windows/macOS (local development):
      → podman compose  (uses Docker Compose as provider — this is fine for
                         local dev, the server never runs Docker)
    The podman-compose Python package has a known path-translation bug when
    calling `podman build` across the WSL2 boundary on Windows.
    """
    import platform
    if platform.system() == "Linux":
        if shutil.which("podman-compose"):
            return ["podman-compose"]
        try:
            check = subprocess.run(
                [sys.executable, "-m", "podman_compose", "--version"],
                capture_output=True, text=True, timeout=5
            )
            if check.returncode == 0:
                return [sys.executable, "-m", "podman_compose"]
        except Exception:
            pass
    # Windows / macOS: fall back to podman compose (Docker Compose provider)
    return ["podman", "compose"]


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


def compose(env_file, *args):
    import platform
    # On Windows, prefer native podman CLI to avoid Docker Compose UDP NAT bug
    if platform.system() == "Windows" and _should_use_native_podman(args):
        return podman_native(env_file, *args)
    host_net = is_host_network_mode(env_file)
    command = compose_bin()
    if env_file:
        command.extend(["--env-file", str(env_file)])
    command.extend(["-f", str(COMPOSE)])
    if host_net and COMPOSE_HOST.exists():
        command.extend(["-f", str(COMPOSE_HOST)])
        print("[host-network] Including", COMPOSE_HOST.name)
    command.extend(args)
    return run(command)


def _should_use_native_podman(args):
    """Only use native podman for run/start/stop/status — not for build or complex ops."""
    return args and args[0] in {"up", "down", "stop", "ps", "start"}


def read_env(env_file=None):
    """Parse key=value pairs from .env file, skip comments and blanks."""
    result = {}
    for path in filter(None, [env_file, ROOT / ".env"]):
        p = Path(path)
        if not p.exists():
            continue
        for line in p.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, _, v = line.partition("=")
            k = k.strip()
            v = v.strip()
            if k and k not in result:          # first file wins
                result[k] = v
    return result


def podman_native(env_file, *args):
    """
    Run/stop/status using native `podman` CLI instead of Docker Compose.
    This bypasses Docker Desktop's UDP NAT bug so same-machine Arma 3
    connections work on Windows.
    """
    action = args[0] if args else ""
    env = read_env(env_file)

    NET       = "arma3-net"
    API_IMG   = "localhost/arma3-manager-api:latest"
    FRONT_IMG = "localhost/arma3-manager-frontend:latest"
    API_NAME  = env.get("API_CONTAINER_NAME", "arma3-api")
    FRONT_NAME= env.get("FRONTEND_CONTAINER_NAME", "arma3-frontend")

    web_port   = env.get("WEB_PORT", "8080")
    web_bind_ip= env.get("WEB_BIND_IP", "127.0.0.1")
    srv_port   = env.get("SERVER_PORT", "2302")
    query_port = env.get("SERVER_QUERY_PORT", "2303")
    be_port    = env.get("BATTLEYE_PORT", "2304")
    von_port   = env.get("VON_PORT", "2305")
    mem_limit  = env.get("SERVER_MEM_LIMIT", "14g")

    def env_flags(keys):
        """Return -e KEY=VALUE flags for the given env key list."""
        flags = []
        for k in keys:
            if k in env:
                flags += ["-e", f"{k}={env[k]}"]
        return flags

    if action in {"up", "start"}:
        # Ensure network exists
        run(["podman", "network", "create", NET])   # harmless if already exists

        # Ensure volumes exist
        for vol in ["arma3-server", "steam-home", "steam-config", "aspnet-keys"]:
            run(["podman", "volume", "create", vol])

        # ── API container ──────────────────────────────────────────────────
        api_cmd = [
            "podman", "run", "-d", "--replace",
            "--name", API_NAME,
            "--network", NET,
            "--memory", mem_limit,
            # Game ports — native podman, NOT Docker Compose
            "-p", f"{srv_port}:{srv_port}/udp",
            "-p", f"{query_port}:{query_port}/udp",
            "-p", f"{be_port}:{be_port}/udp",
            "-p", f"{von_port}:{von_port}/udp",
            # Volumes
            "-v", "arma3-server:/arma3",
            "-v", "steam-home:/home/arma3/Steam",
            "-v", "steam-config:/home/arma3/.steam",
            "-v", "aspnet-keys:/home/arma3/.aspnet",
        ]
        api_cmd += env_flags([
            "STEAM_USER", "STEAM_PASS", "WEB_PORT", "WEB_USERNAME", "WEB_PASSWORD",
            "SESSION_SECRET", "SERVER_PORT", "SERVER_NAME", "SERVER_PASSWORD",
            "SERVER_PASSWORD_ADMIN", "SERVER_MAX_PLAYERS", "BASE_URL",
            "PUBLIC_JOIN_HOST", "CREATOR_DLC_APP_IDS", "STEAM_OWNER_IDS", "ARMA3_DIR", "TZ",
        ])
        api_cmd += ["-e", "WEB_PORT=8080", "-e", "ARMA3_DIR=/arma3", API_IMG]
        code = run(api_cmd)
        if code:
            return code

        # ── Frontend container ─────────────────────────────────────────────
        front_cmd = [
            "podman", "run", "-d", "--replace",
            "--name", FRONT_NAME,
            "--network", NET,
            "-p", f"{web_bind_ip}:{web_port}:8080/tcp",
            "-e", "ARMA3_API_BASE=",
            "-e", "ARMA3_REST_ONLY=true",
            "-e", f"ARMA3_API_BACKEND={API_NAME}:8080",
            FRONT_IMG,
        ]
        return run(front_cmd)

    if action == "stop":
        run(["podman", "stop", API_NAME])
        run(["podman", "stop", FRONT_NAME])
        return 0

    if action == "down":
        run(["podman", "rm", "-f", API_NAME])
        run(["podman", "rm", "-f", FRONT_NAME])
        if "-v" in args:
            for vol in ["arma3-server", "steam-home", "steam-config", "aspnet-keys"]:
                run(["podman", "volume", "rm", vol])
        return 0

    if action == "ps":
        return run(["podman", "ps", "--filter", f"network={NET}",
                    "--format", "table {{.Names}}\t{{.Status}}\t{{.Ports}}"])

    # Fallback to compose for anything else
    return compose_bin_run(env_file, *args)


def compose_bin_run(env_file, *args):
    """Raw compose call, no native override."""
    host_net = is_host_network_mode(env_file)
    command = compose_bin()
    if env_file:
        command.extend(["--env-file", str(env_file)])
    command.extend(["-f", str(COMPOSE)])
    if host_net and COMPOSE_HOST.exists():
        command.extend(["-f", str(COMPOSE_HOST)])
    command.extend(args)
    return run(command)


def is_host_network_mode(env_file):
    """Return True if API_NETWORK_MODE=host is set in .env or environment."""
    if os.environ.get("API_NETWORK_MODE", "").lower() == "host":
        return True
    for path in filter(None, [env_file, ROOT / ".env"]):
        path = Path(path)
        if path == env_file and path == ROOT / ".env":
            continue  # don't read twice
        if not path.exists():
            continue
        for line in path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line.startswith("#") or "=" not in line:
                continue
            k, _, v = line.partition("=")
            if k.strip() == "API_NETWORK_MODE" and v.strip().lower() == "host":
                return True
    return False


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
        choices=["start", "stop", "restart", "rebuild", "status", "logs",
                 "delete", "reset-volumes", "wsl-test"],
    )
    parser.add_argument("--env-file", help="Use a specific compose env file, for example .env.lan or .env.test.")
    parser.add_argument("--yes", action="store_true", help="Confirm destructive actions.")
    args = parser.parse_args()

    if not COMPOSE.exists():
        print(f"Missing {COMPOSE}", file=sys.stderr)
        return 1

    env_file = None
    if args.env_file:
        env_file = Path(args.env_file)
        if not env_file.is_absolute():
            env_file = ROOT / env_file
        if not env_file.exists():
            print(f"Missing env file: {env_file}", file=sys.stderr)
            return 1

    if args.command in {"start", "restart", "rebuild", "status", "logs", "delete", "reset-volumes"}:
        code = ensure_podman()
        if code != 0:
            return code

    if args.command == "start":
        return compose(env_file, "up", "-d")
    if args.command == "stop":
        return compose(env_file, "stop")
    if args.command == "restart":
        code = compose(env_file, "stop")
        return code if code else compose(env_file, "up", "-d")
    if args.command == "rebuild":
        import platform
        if platform.system() == "Windows":
            # Build images with native podman build (avoids Docker Compose path bugs)
            print("[podman] Building API image...")
            code = run(["podman", "build", "--file", "Containerfile.api",
                        "--tag", "localhost/arma3-manager-api:latest", "."])
            if code:
                return code
            print("[podman] Building frontend image...")
            code = run(["podman", "build", "--file", "Containerfile.frontend",
                        "--tag", "localhost/arma3-manager-frontend:latest", "."])
            if code:
                return code
            return compose(env_file, "up", "-d")
        return compose(env_file, "up", "-d", "--build")
    if args.command == "status":
        return compose(env_file, "ps")
    if args.command == "logs":
        return compose(env_file, "logs", "--tail", "200")
    if args.command == "delete":
        require_yes(args, "This removes the running containers but keeps named volumes.")
        return compose(env_file, "down")
    if args.command == "reset-volumes":
        require_yes(args, "This removes containers and named volumes. Server files, mods and SQLite data will be deleted.")
        return compose(env_file, "down", "-v")

    if args.command == "wsl-test":
        # Step 1: Extract the compiled API binary from the Windows-side Podman container
        extract_dir = Path(r"C:\temp\arma3-manager-api")
        api_dll_win = extract_dir / "Arma3Manager.Api.dll"

        if not api_dll_win.exists():
            print("[wsl-test] Extracting API binary from container (Windows Podman)...")
            extract_dir.mkdir(parents=True, exist_ok=True)
            code = run(["podman", "cp", "arma3-api:/app/.", str(extract_dir)])
            if code != 0:
                print("ERROR: Could not extract from container.", file=sys.stderr)
                print("Make sure containers are running: python manage.py rebuild", file=sys.stderr)
                return 1
            print(f"[wsl-test] Binary extracted to {extract_dir}")
        else:
            print(f"[wsl-test] Using cached binary: {api_dll_win}")

        # Step 2: Run the API natively in WSL2 pointing at the extracted binary
        api_dll_wsl = "/mnt/c/temp/arma3-manager-api/Arma3Manager.Api.dll"
        env = read_env(env_file)
        web_port = env.get("WEB_PORT", "8082")

        # Build environment string for WSL2
        wsl_vars = " ".join(
            f'{k}="{v}"'
            for k, v in {
                "ARMA3_DIR"       : env.get("ARMA3_DIR", "/home/hela/arma3-test"),
                "WEB_PORT"        : web_port,
                "WEB_USERNAME"    : env.get("WEB_USERNAME", "admin"),
                "WEB_PASSWORD"    : env.get("WEB_PASSWORD", "admin"),
                "SESSION_SECRET"  : env.get("SESSION_SECRET", "wsl-test-secret"),
                "STEAM_OWNER_IDS" : env.get("STEAM_OWNER_IDS", ""),
                "ASPNETCORE_URLS" : f"http://0.0.0.0:{web_port}",
                "DOTNET_NOLOGO"   : "1",
            }.items()
        )

        print(f"[wsl-test] Starting API in WSL2 on port {web_port}...")
        print(f"  Panel:         http://localhost:{web_port}")
        print(f"  Direct Connect (Arma 3): 127.0.0.1:2302  ← works from same PC!")
        print(f"  Press Ctrl+C to stop.")
        print()

        # Fix server.cfg mission before starting
        fix_cfg = (
            "CFG=/home/hela/arma3-test/server.cfg; "
            "if [ -f \"$CFG\" ]; then "
            "sed -i 's/template = \".*\"/template = \"empty.VR\"/' \"$CFG\"; fi"
        )
        run(["wsl", "bash", "-c", fix_cfg])

        return run(["wsl", "bash", "-c",
                    f"{wsl_vars} $HOME/.dotnet/dotnet {api_dll_wsl}"])

    return 1


if __name__ == "__main__":
    raise SystemExit(main())
