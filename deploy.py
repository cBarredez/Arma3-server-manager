#!/usr/bin/env python3
"""Build and deploy selected Arma 3 Manager containers through SSH and Podman."""

from __future__ import annotations

import argparse
import ipaddress
import os
import shlex
import shutil
import subprocess
import sys
import time
import tomllib
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DEPLOY_FILE = ROOT / "deploy.toml"
MANAGER_FILE = ROOT / "config" / "manager.toml"
SECRETS_FILE = ROOT / "config" / "manager.secrets.toml"
NETWORK = "arma3-net"
PODMAN_SECRET = "arma3-manager-secrets"


@dataclass(frozen=True)
class Target:
    environment: str
    server: str
    username: str

    @property
    def ssh(self) -> str:
        return f"{self.username}@{self.server}"

    @property
    def remote_root(self) -> str:
        home = "/root" if self.username == "root" else f"/home/{self.username}"
        return f"{home}/.local/share/arma3-manager"


_SSH_SOCKETS: dict[str, str] = {}


def _ssh_opts(target: Target) -> list[str]:
    if target.ssh not in _SSH_SOCKETS:
        _SSH_SOCKETS[target.ssh] = f"/tmp/arma3-deploy-{target.server}.sock"
    return ["-o", "ControlMaster=auto", "-o", f"ControlPath={_SSH_SOCKETS[target.ssh]}", "-o", "ControlPersist=60s"]


def run(args: list[str], *, check: bool = True, input_data: bytes | None = None) -> subprocess.CompletedProcess:
    print("+", shlex.join(args))
    return subprocess.run(args, cwd=ROOT, check=check, input=input_data)


def capture(args: list[str], *, check: bool = True) -> str:
    result = subprocess.run(args, cwd=ROOT, check=check, text=True, capture_output=True)
    return result.stdout.strip()


def remote(target: Target, args: list[str], *, check: bool = True) -> subprocess.CompletedProcess:
    return run(["ssh"] + _ssh_opts(target) + [target.ssh, shlex.join(args)], check=check)


def remote_capture(target: Target, args: list[str], *, check: bool = True) -> str:
    return capture(["ssh"] + _ssh_opts(target) + [target.ssh, shlex.join(args)], check=check)


def load_target(environment: str) -> Target:
    if not DEPLOY_FILE.exists():
        raise SystemExit(f"Missing {DEPLOY_FILE.name}; copy deploy.example.toml to deploy.toml")
    data = tomllib.loads(DEPLOY_FILE.read_text(encoding="utf-8"))
    section = data.get(environment, {})
    server = str(section.get("server", "")).strip()
    username = str(section.get("username", "")).strip()
    if not server or not username:
        raise SystemExit(f"[{environment}] must define server and username")
    try:
        ipaddress.ip_address(server)
    except ValueError:
        if not all(part and part.replace("-", "").isalnum() for part in server.split(".")):
            raise SystemExit(f"Invalid server address: {server}")
    if not username.replace("-", "").replace("_", "").isalnum():
        raise SystemExit(f"Invalid username: {username}")
    return Target(environment, server, username)


def manager_config() -> dict:
    if not MANAGER_FILE.exists():
        raise SystemExit(f"Missing {MANAGER_FILE}")
    config = tomllib.loads(MANAGER_FILE.read_text(encoding="utf-8"))
    web = config.get("web", {})
    server = config.get("server", {})
    for name, value in {
        "web.port": web.get("port"),
        "web.public_port": web.get("public_port"),
        "server.port": server.get("port"),
        "server.query_port": server.get("query_port"),
        "server.battleye_port": server.get("battleye_port"),
        "server.von_port": server.get("von_port"),
        "server.rcon_port": server.get("rcon_port"),
    }.items():
        if not isinstance(value, int) or not 1 <= value <= 65535:
            raise SystemExit(f"{name} must be an integer between 1 and 65535")
    if server.get("network_mode") not in {"bridge", "host"}:
        raise SystemExit("server.network_mode must be 'bridge' or 'host'")
    try:
        ipaddress.ip_address(str(web.get("bind_ip", "")))
    except ValueError as error:
        raise SystemExit("web.bind_ip must be a valid IP address") from error
    if not str(server.get("arma3_dir", "")).startswith("/"):
        raise SystemExit("server.arma3_dir must be an absolute Linux path")
    game_ports = [server.get(name) for name in ("port", "query_port", "battleye_port", "von_port", "rcon_port")]
    if len(set(game_ports)) != len(game_ports):
        raise SystemExit("server UDP ports must be different")
    if server.get("network_mode") == "host" and web.get("port") == web.get("public_port"):
        raise SystemExit("host mode requires different web.port and web.public_port values")
    return config


def validate_secrets() -> dict:
    if not SECRETS_FILE.exists():
        raise SystemExit("Missing config/manager.secrets.toml; copy the example and set secure values")
    secrets = tomllib.loads(SECRETS_FILE.read_text(encoding="utf-8"))
    web = secrets.get("web", {})
    password = str(web.get("password", ""))
    session_secret = str(web.get("session_secret", ""))
    if len(password) < 12 or password.startswith("replace-with"):
        raise SystemExit("web.password must contain at least 12 non-placeholder characters")
    if len(session_secret) < 32 or session_secret.startswith("replace-with"):
        raise SystemExit("web.session_secret must contain at least 32 non-placeholder characters")
    return secrets


def validate_local(environment: str) -> Target:
    target = load_target(environment)
    config = manager_config()
    validate_secrets()
    print(f"Configuration OK: {environment} -> {target.ssh}")
    print(f"Network: {config['server']['network_mode']}; panel port: {config['web']['public_port']}")
    return target


def verify_tools() -> None:
    for binary in ("ssh", "tar", "scp"):
        if shutil.which(binary) is None:
            raise SystemExit(f"Required command not found: {binary}")


def confirm_backend(target: Target, yes: bool) -> None:
    if yes:
        return
    print(f"Backend replacement on {target.environment} stops any running Arma 3 process.")
    if input("Type DEPLOY to continue: ") != "DEPLOY":
        raise SystemExit("Cancelled")


def upload_release(target: Target, release: str) -> str:
    remote_dir = f"{target.remote_root}/releases/{release}"
    remote(target, ["mkdir", "-p", remote_dir, f"{target.remote_root}/config"])
    archive = subprocess.Popen(
        ["tar", "-czf", "-", "--exclude=.git", "--exclude=node_modules", "--exclude=dist", "--exclude=bin", "--exclude=obj", "--exclude=._*", "--exclude=manager.secrets.toml", "."],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        env={**os.environ, "COPYFILE_DISABLE": "1"},
    )
    assert archive.stdout is not None
    extract = subprocess.run(["ssh"] + _ssh_opts(target) + [target.ssh, f"tar -xzf - -C {shlex.quote(remote_dir)}"], stdin=archive.stdout)
    archive.stdout.close()
    archive_code = archive.wait()
    if archive_code or extract.returncode:
        raise SystemExit("Project transfer failed")
    if SECRETS_FILE.exists():
        run(["scp"] + _ssh_opts(target) + [str(SECRETS_FILE), f"{target.ssh}:{target.remote_root}/config/manager.secrets.toml"])
        remote(target, ["chmod", "600", f"{target.remote_root}/config/manager.secrets.toml"])
    return remote_dir


def ensure_runtime(target: Target) -> None:
    remote(target, ["podman", "network", "create", NETWORK], check=False)
    for volume in ("arma3-server", "steam-home", "steam-config", "aspnet-keys"):
        remote(target, ["podman", "volume", "create", volume], check=False)
    secret_file = f"{target.remote_root}/config/manager.secrets.toml"
    marker = remote_capture(target, ["sh", "-c", f"test -f {secret_file} && echo yes || true"], check=False)
    if marker == "yes":
        remote(target, ["podman", "secret", "create", "--replace", PODMAN_SECRET, secret_file])


def build_image(target: Target, remote_dir: str, release: str, service: str) -> str:
    image = f"localhost/arma3-manager-{service}:{release}"
    containerfile = "Containerfile.api" if service == "api" else "Containerfile.frontend"
    remote(target, ["podman", "build", "--file", f"{remote_dir}/{containerfile}", "--tag", image, remote_dir])
    return image


def current_image(target: Target, container: str) -> str | None:
    value = remote_capture(target, ["podman", "inspect", "--format", "{{.ImageName}}", container], check=False)
    return value or None


def secret_mount(target: Target) -> list[str]:
    marker = remote_capture(target, ["podman", "secret", "inspect", "--format", "{{.ID}}", PODMAN_SECRET], check=False)
    return ["--secret", f"{PODMAN_SECRET},target=manager.secrets.toml"] if marker else []


def api_command(target: Target, image: str, remote_dir: str) -> list[str]:
    config = manager_config()
    web = config.get("web", {})
    server = config.get("server", {})
    network_mode = server.get("network_mode", "bridge")
    command = ["podman", "run", "-d", "--replace", "--name", "arma3-api", "--restart", "unless-stopped", "--memory", str(server.get("memory_limit", "14g")), "--health-cmd", f"curl -f http://127.0.0.1:{web.get('port', 8080)}/api/health", "--health-interval", "30s", "--health-retries", "3"]
    if network_mode == "host":
        command += ["--network", "host"]
    else:
        command += ["--network", NETWORK]
        for port, protocol in ((server.get("port", 2302), "udp"), (server.get("query_port", 2303), "udp"), (server.get("battleye_port", 2304), "udp"), (server.get("von_port", 2305), "udp")):
            command += ["-p", f"{port}:{port}/{protocol}"]
    command += ["-v", f"{remote_dir}/config/manager.toml:/app/config/manager.toml:ro,Z"]
    secret = secret_mount(target)
    if not secret:
        raise SystemExit("Backend deploy requires config/manager.secrets.toml locally or on the remote server")
    command += secret
    command += ["-v", "arma3-server:/arma3", "-v", "steam-home:/home/arma3/Steam", "-v", "steam-config:/home/arma3/.steam", "-v", "aspnet-keys:/home/arma3/.aspnet", image]
    return command


def frontend_command(image: str) -> list[str]:
    config = manager_config()
    web = config.get("web", {})
    server = config.get("server", {})
    backend = f"host.containers.internal:{web.get('port', 8080)}" if server.get("network_mode") == "host" else "arma3-api:8080"
    return ["podman", "run", "-d", "--replace", "--name", "arma3-frontend", "--restart", "unless-stopped", "--network", NETWORK, "-p", f"{web.get('bind_ip', '127.0.0.1')}:{web.get('public_port', 8080)}:8080/tcp", "-e", "ARMA3_API_BASE=", "-e", "ARMA3_REST_ONLY=true", "-e", f"ARMA3_API_BACKEND={backend}", image]


def wait_healthy(target: Target, container: str, timeout: int = 120) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state = remote_capture(target, ["podman", "inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}", container], check=False)
        if state in {"healthy", "running"}:
            print(f"{container}: {state}")
            return True
        if state in {"unhealthy", "exited", "dead"}:
            return False
        time.sleep(3)
    return False


def replace(target: Target, container: str, image: str, command: list[str], previous: str | None) -> None:
    result = remote(target, command, check=False)
    if result.returncode == 0 and wait_healthy(target, container):
        return
    print(f"Deployment of {container} failed; attempting rollback", file=sys.stderr)
    if previous:
        rollback = command[:-1] + [previous]
        remote(target, rollback, check=False)
    raise SystemExit(1)


def deploy(args: argparse.Namespace) -> int:
    target = validate_local(args.environment)
    verify_tools()
    remote(target, ["podman", "info"])
    if args.backend:
        confirm_backend(target, args.yes)
    release = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    remote_dir = upload_release(target, release)
    ensure_runtime(target)
    if args.backend:
        image = build_image(target, remote_dir, release, "api")
        replace(target, "arma3-api", image, api_command(target, image, remote_dir), current_image(target, "arma3-api"))
    if args.frontend:
        image = build_image(target, remote_dir, release, "frontend")
        replace(target, "arma3-frontend", image, frontend_command(image), current_image(target, "arma3-frontend"))
    print(f"Deployment {release} to {target.environment} completed")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("environment", choices=("dev", "prod"))
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--check", action="store_true", help="validate local TOML files without connecting")
    group.add_argument("--status", action="store_true")
    group.add_argument("--logs", choices=("frontend", "backend"))
    parser.add_argument("--frontend", action="store_true")
    parser.add_argument("--backend", action="store_true")
    parser.add_argument("--yes", action="store_true", help="accept backend downtime")
    args = parser.parse_args()
    if args.check:
        validate_local(args.environment)
        return 0
    target = load_target(args.environment)
    if args.status:
        return remote(target, ["podman", "ps", "--filter", "name=arma3-"]).returncode
    if args.logs:
        container = "arma3-frontend" if args.logs == "frontend" else "arma3-api"
        return remote(target, ["podman", "logs", "--tail", "200", container]).returncode
    if not args.frontend and not args.backend:
        parser.error("select --frontend, --backend, --check, --status, or --logs")
    return deploy(args)


if __name__ == "__main__":
    raise SystemExit(main())
