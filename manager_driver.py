#!/usr/bin/env python3
"""Server Manager Contract v1 driver for a local Arma 3 Manager instance.

The driver is intentionally local-only. It emits JSON on stdout, diagnostics on
stderr, never emits secret values, and serializes every per-instance mutation
with flock. deploy.py remains the SSH transport and calls the internal
``ensure-id``, ``can-deploy`` and ``sync`` commands on the target host.
"""

from __future__ import annotations

import argparse
import fcntl
import json
import os
import secrets
import subprocess
import sys
import tempfile
import tomllib
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator

CONTRACT_VERSION = "1.0"
PROTOCOL_VERSION = "1.0"
MANAGER_ID = "arma3-server-manager"
GAME_TYPE = "arma3"
API_CONTAINER = "arma3-api"
FRONTEND_CONTAINER = "arma3-frontend"
VOLUMES = ("arma3-server", "steam-home", "steam-config", "aspnet-keys")
NETWORK = "arma3-net"
SECRET_NAME = "arma3-manager-secrets"
LABEL_PREFIX = "io.gameserver-manager"


class DriverError(RuntimeError):
    def __init__(self, code: str, message: str, *, exit_code: int = 2):
        super().__init__(message)
        self.code = code
        self.exit_code = exit_code


def state_root() -> Path:
    override = os.environ.get("GSM_STATE_ROOT")
    if override:
        return Path(override).expanduser().resolve()
    return Path.home() / ".local" / "share" / "game-server-managers"


def registry_dir() -> Path:
    return state_root() / "instances"


def id_file() -> Path:
    return state_root() / MANAGER_ID / "standalone-instance-id"


def instance_dir(instance_id: str) -> Path:
    validate_instance_id(instance_id)
    return registry_dir() / instance_id


def manifest_path(instance_id: str) -> Path:
    return instance_dir(instance_id) / "instance.json"


def controller_path(instance_id: str) -> Path:
    return instance_dir(instance_id) / "controller.json"


def operation_path(instance_id: str) -> Path:
    return instance_dir(instance_id) / "operation.json"


def validate_instance_id(value: str) -> None:
    if len(value) != 32 or any(ch not in "0123456789abcdef" for ch in value):
        raise DriverError("invalid_request", "instanceId must be a 32-character lowercase hex identifier")


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def read_request() -> dict[str, Any]:
    if sys.stdin.isatty():
        return {}
    raw = sys.stdin.read()
    if not raw.strip():
        return {}
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as error:
        raise DriverError("invalid_json", f"request body is not valid JSON: {error}") from error
    if not isinstance(value, dict):
        raise DriverError("invalid_request", "request body must be a JSON object")
    return value


def emit(value: Any) -> None:
    print(json.dumps(value, separators=(",", ":"), sort_keys=True))


def atomic_private_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(path.parent, 0o700)
    fd, temporary = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(value, handle, separators=(",", ":"), sort_keys=True)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
        os.chmod(path, 0o600)
        fsync_directory(path.parent)
    except Exception:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass
        raise


def private_text_once(path: Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    try:
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError:
        return
    with os.fdopen(fd, "w", encoding="utf-8") as handle:
        handle.write(value)
        handle.flush()
        os.fsync(handle.fileno())
    os.chmod(path, 0o600)
    fsync_directory(path.parent)


def fsync_directory(path: Path) -> None:
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


@contextmanager
def instance_lock(instance_id: str) -> Iterator[None]:
    directory = instance_dir(instance_id)
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(directory, 0o700)
    lock_path = directory / ".lock"
    with lock_path.open("a+", encoding="utf-8") as handle:
        os.chmod(lock_path, 0o600)
        fcntl.flock(handle.fileno(), fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def ensure_instance_id() -> str:
    path = id_file()
    if path.exists():
        value = path.read_text(encoding="utf-8").strip()
        validate_instance_id(value)
        os.chmod(path, 0o600)
        return value
    value = secrets.token_hex(16)
    private_text_once(path, value + "\n")
    persisted = path.read_text(encoding="utf-8").strip()
    validate_instance_id(persisted)
    return persisted


def load_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise DriverError("not_found", f"metadata does not exist: {path}") from error
    except json.JSONDecodeError as error:
        raise DriverError("invalid_metadata", f"invalid JSON in {path}: {error}") from error
    if not isinstance(value, dict):
        raise DriverError("invalid_metadata", f"metadata is not an object: {path}")
    return value


def podman(args: list[str], *, check: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(["podman", *args], text=True, capture_output=True)
    if check and result.returncode != 0:
        raise DriverError("podman_failed", result.stderr.strip() or f"podman {args[0]} failed")
    return result


def inspect_container(name: str) -> dict[str, Any] | None:
    result = podman(["inspect", name], check=False)
    if result.returncode != 0:
        return None
    try:
        rows = json.loads(result.stdout)
        return rows[0] if isinstance(rows, list) and rows and isinstance(rows[0], dict) else None
    except json.JSONDecodeError as error:
        raise DriverError("podman_invalid_json", f"podman inspect returned invalid JSON for {name}") from error


def container_state(row: dict[str, Any] | None) -> str:
    if row is None:
        return "missing"
    status = str((row.get("State") or {}).get("Status") or "stopped")
    return "running" if status == "running" else "stopped"


def image_name(row: dict[str, Any] | None) -> str:
    if row is None:
        return ""
    return str((row.get("Config") or {}).get("Image") or row.get("ImageName") or "")


def label_value(row: dict[str, Any] | None, key: str) -> str:
    if row is None:
        return ""
    return str(((row.get("Config") or {}).get("Labels") or {}).get(key) or "")


def validate_container(row: dict[str, Any] | None, instance_id: str, role: str) -> list[str]:
    issues: list[str] = []
    if row is None:
        return [f"missing {role} container"]
    expected = {
        f"{LABEL_PREFIX}.contract.version": CONTRACT_VERSION,
        f"{LABEL_PREFIX}.manager": MANAGER_ID,
        f"{LABEL_PREFIX}.game": GAME_TYPE,
        f"{LABEL_PREFIX}.instance.id": instance_id,
        f"{LABEL_PREFIX}.role": role,
    }
    for key, value in expected.items():
        if label_value(row, key) != value:
            issues.append(f"{role} container label {key} does not equal {value}")
    return issues


def podman_object_exists(kind: str, name: str) -> bool:
    return podman([kind, "inspect", name], check=False).returncode == 0


def mounted_sources(row: dict[str, Any] | None) -> dict[str, str]:
    if row is None:
        return {}
    result: dict[str, str] = {}
    for mount in row.get("Mounts") or []:
        if not isinstance(mount, dict):
            continue
        destination = str(mount.get("Destination") or mount.get("destination") or "")
        source = str(mount.get("Name") or mount.get("Source") or mount.get("source") or "")
        if destination and source:
            result[destination] = source
    return result


def attached_networks(row: dict[str, Any] | None) -> set[str]:
    if row is None:
        return set()
    networks = (row.get("NetworkSettings") or {}).get("Networks") or {}
    return set(networks) if isinstance(networks, dict) else set()


def published_host_ports(row: dict[str, Any] | None) -> set[int]:
    result: set[int] = set()

    def visit(value: Any) -> None:
        if isinstance(value, list):
            for child in value:
                visit(child)
        elif isinstance(value, dict):
            for key, child in value.items():
                if key.lower() == "hostport":
                    try:
                        result.add(int(child))
                    except (TypeError, ValueError):
                        pass
                else:
                    visit(child)

    if row is not None:
        visit((row.get("NetworkSettings") or {}).get("Ports") or {})
        visit((row.get("HostConfig") or {}).get("PortBindings") or {})
    return result


def has_secret_reference(row: dict[str, Any] | None, secret_name: str) -> bool:
    """Find an exact mounted-secret name in Podman's version-dependent inspect shape."""
    found = False

    def visit(value: Any) -> None:
        nonlocal found
        if found:
            return
        if isinstance(value, list):
            for child in value:
                visit(child)
        elif isinstance(value, dict):
            for key, child in value.items():
                if key.lower() in {"name", "secretname", "secret_name"} and child == secret_name:
                    found = True
                    return
                visit(child)

    if row is not None:
        # Limit traversal to fields where Podman versions expose secret mounts;
        # do not accept an arbitrary matching environment value as evidence.
        for key in ("Secrets", "SecretReferences"):
            visit(row.get(key))
        config = row.get("Config") or {}
        if isinstance(config, dict):
            for key in ("Secrets", "SecretReferences"):
                visit(config.get(key))
    return found


def validate_runtime_resources(
    manifest: dict[str, Any], api: dict[str, Any] | None, frontend: dict[str, Any] | None
) -> list[str]:
    issues: list[str] = []
    resources = manifest.get("resources") or {}
    for volume in resources.get("volumes") or []:
        if not podman_object_exists("volume", str(volume)):
            issues.append(f"missing volume {volume}")
    for network in resources.get("networks") or []:
        if not podman_object_exists("network", str(network)):
            issues.append(f"missing network {network}")

    api_mounts = mounted_sources(api)
    expected_mounts = {
        "/arma3": "arma3-server",
        "/home/arma3/Steam": "steam-home",
        "/home/arma3/.steam": "steam-config",
        "/home/arma3/.aspnet": "aspnet-keys",
    }
    for destination, source in expected_mounts.items():
        if api_mounts.get(destination) != source:
            issues.append(f"api mount {destination} does not reference {source}")

    manager_path = str((manifest.get("config") or {}).get("managerPath") or "")
    config_source = api_mounts.get("/app/config/manager.toml", "")
    if not manager_path or Path(config_source).resolve() != Path(manager_path).resolve():
        issues.append("api manager.toml mount does not match the manifest")

    if NETWORK not in attached_networks(frontend):
        issues.append(f"frontend is not attached to {NETWORK}")
    network_mode = str((api.get("HostConfig") or {}).get("NetworkMode") or "") if api else ""
    if network_mode != "host" and NETWORK not in attached_networks(api):
        issues.append(f"api is not attached to {NETWORK} or host networking")

    ports = resources.get("ports") or {}
    web_port = ports.get("web")
    if isinstance(web_port, int) and web_port not in published_host_ports(frontend):
        issues.append(f"frontend does not publish web port {web_port}")
    if network_mode != "host":
        api_ports = published_host_ports(api)
        for key in ("port", "queryPort", "battleyePort", "vonPort"):
            value = ports.get(key)
            if isinstance(value, int) and value not in api_ports:
                issues.append(f"api does not publish {key} {value}")

    if not podman_object_exists("secret", SECRET_NAME):
        issues.append(f"missing Podman secret {SECRET_NAME}")
    elif not has_secret_reference(api, SECRET_NAME):
        issues.append(f"api does not mount Podman secret {SECRET_NAME}")
    return issues


def load_controller(instance_id: str) -> dict[str, Any] | None:
    path = controller_path(instance_id)
    return load_json(path) if path.exists() else None


def require_controller(instance_id: str, request: dict[str, Any]) -> dict[str, Any]:
    current = load_controller(instance_id)
    if current is None:
        raise DriverError("not_claimed", "instance is not claimed by a controller")
    controller_id = request.get("controllerId")
    revision = request.get("revision")
    if controller_id != current.get("controllerId") or revision != current.get("revision"):
        raise DriverError("controller_conflict", "controller identity or revision does not match current ownership", exit_code=3)
    return current


def instance_from_request(request: dict[str, Any]) -> str:
    value = request.get("instanceId")
    if not isinstance(value, str):
        raise DriverError("invalid_request", "instanceId is required")
    validate_instance_id(value)
    return value


def command_describe() -> dict[str, Any]:
    return {
        "protocolVersion": PROTOCOL_VERSION,
        "contractVersion": CONTRACT_VERSION,
        "managerId": MANAGER_ID,
        "gameType": GAME_TYPE,
        "displayName": "Arma 3",
        "capabilities": ["discover", "adopt", "lifecycle", "health", "detach"],
    }


def command_discover() -> dict[str, Any]:
    candidates: list[dict[str, Any]] = []
    root = registry_dir()
    if not root.exists():
        return {"candidates": candidates}
    for path in sorted(root.glob("*/instance.json")):
        try:
            manifest = load_json(path)
            if manifest.get("managerId") != MANAGER_ID:
                continue
            instance_id = str(manifest.get("instanceId") or "")
            validate_instance_id(instance_id)
            api = inspect_container(str(manifest["resources"]["containers"]["api"]))
            frontend = inspect_container(str(manifest["resources"]["containers"]["frontend"]))
            issues = (
                validate_container(api, instance_id, "api")
                + validate_container(frontend, instance_id, "frontend")
                + validate_runtime_resources(manifest, api, frontend)
            )
            controller = load_controller(instance_id)
            active_operation = operation_path(instance_id).exists()
            if active_operation:
                issues.append("a manual deployment operation is in progress")
            status = "partial" if issues and not active_operation else "conflict" if active_operation else "already-claimed" if controller else "ready"
            candidates.append({
                "candidateId": instance_id,
                "instanceId": instance_id,
                "managerId": MANAGER_ID,
                "gameType": GAME_TYPE,
                "displayName": manifest.get("displayName", "Arma 3"),
                "status": status,
                "issues": issues,
                "controller": controller,
                "manifest": manifest,
            })
        except (DriverError, KeyError, TypeError) as error:
            print(f"ignoring invalid runtime manifest {path}: {error}", file=sys.stderr)
    return {"candidates": candidates}


def command_inspect(request: dict[str, Any]) -> dict[str, Any]:
    instance_id = instance_from_request(request)
    manifest = load_json(manifest_path(instance_id))
    containers = manifest.get("resources", {}).get("containers", {})
    api = inspect_container(str(containers.get("api", API_CONTAINER)))
    frontend = inspect_container(str(containers.get("frontend", FRONTEND_CONTAINER)))
    issues = (
        validate_container(api, instance_id, "api")
        + validate_container(frontend, instance_id, "frontend")
        + validate_runtime_resources(manifest, api, frontend)
    )
    return {
        "manifest": manifest,
        "controller": load_controller(instance_id),
        "health": {
            "api": container_state(api),
            "frontend": container_state(frontend),
            "status": "healthy" if not issues and container_state(api) == container_state(frontend) == "running" else "degraded",
            "issues": issues,
        },
    }


def command_claim(request: dict[str, Any]) -> dict[str, Any]:
    instance_id = instance_from_request(request)
    controller_id = request.get("controllerId")
    expected_revision = request.get("expectedRevision", 0)
    if not isinstance(controller_id, str) or not controller_id.strip():
        raise DriverError("invalid_request", "controllerId is required")
    with instance_lock(instance_id):
        if operation_path(instance_id).exists():
            raise DriverError("operation_conflict", "a manual deployment is in progress", exit_code=3)
        manifest = load_json(manifest_path(instance_id))
        inspection = command_inspect({"instanceId": instance_id})
        if inspection["health"]["issues"]:
            raise DriverError("validation_failed", "instance resources do not satisfy the v1 contract")
        current = load_controller(instance_id)
        if current is not None:
            if current.get("controllerId") == controller_id:
                return {"claimed": True, "controller": current, "manifest": manifest}
            raise DriverError("already_claimed", "instance is already claimed by another controller", exit_code=3)
        manifest_revision = int(manifest.get("controllerRevision", 0))
        if expected_revision != manifest_revision:
            raise DriverError("revision_conflict", f"expected revision {expected_revision}, current revision is {manifest_revision}", exit_code=3)
        controller = {"controllerId": controller_id, "revision": manifest_revision + 1, "claimedAt": utc_now()}
        atomic_private_json(controller_path(instance_id), controller)
        manifest["controllerRevision"] = controller["revision"]
        atomic_private_json(manifest_path(instance_id), manifest)
        return {"claimed": True, "controller": controller, "manifest": manifest}


def command_release(request: dict[str, Any]) -> dict[str, Any]:
    instance_id = instance_from_request(request)
    with instance_lock(instance_id):
        current = require_controller(instance_id, request)
        controller_path(instance_id).unlink(missing_ok=True)
        fsync_directory(instance_dir(instance_id))
        manifest = load_json(manifest_path(instance_id))
        manifest["controllerRevision"] = int(current["revision"]) + 1
        atomic_private_json(manifest_path(instance_id), manifest)
        return {"released": True, "revision": manifest["controllerRevision"]}


def lifecycle(request: dict[str, Any], action: str) -> dict[str, Any]:
    instance_id = instance_from_request(request)
    with instance_lock(instance_id):
        require_controller(instance_id, request)
        manifest = load_json(manifest_path(instance_id))
        containers = manifest["resources"]["containers"]
        order = [containers["api"], containers["frontend"]]
        if action == "stop":
            order.reverse()
        for container in order:
            current = container_state(inspect_container(container))
            if action == "start" and current != "running":
                podman(["start", container])
            elif action == "stop" and current == "running":
                podman(["stop", container])
            elif action == "restart" and current != "missing":
                podman(["restart", container])
        return {"ok": True, "health": command_inspect({"instanceId": instance_id})["health"]}


def command_sync(args: argparse.Namespace) -> dict[str, Any]:
    instance_id = args.instance_id
    validate_instance_id(instance_id)
    api = inspect_container(API_CONTAINER)
    frontend = inspect_container(FRONTEND_CONTAINER)
    if api is None or frontend is None:
        raise DriverError("partial", "both arma3-api and arma3-frontend must exist before syncing metadata")
    config_path = Path(args.config_path).resolve()
    with config_path.open("rb") as handle:
        config = tomllib.load(handle)
    ports = {
        "web": int(config.get("web", {}).get("public_port", 8080)),
        "port": int(config.get("server", {}).get("port", 2302)),
        "queryPort": int(config.get("server", {}).get("query_port", 2303)),
        "battleyePort": int(config.get("server", {}).get("battleye_port", 2304)),
        "vonPort": int(config.get("server", {}).get("von_port", 2305)),
        "rconPort": int(config.get("server", {}).get("rcon_port", 2301)),
    }
    previous_revision = 0
    if manifest_path(instance_id).exists():
        previous_revision = int(load_json(manifest_path(instance_id)).get("controllerRevision", 0))
    manifest = {
        "contractVersion": CONTRACT_VERSION,
        "instanceId": instance_id,
        "managerId": MANAGER_ID,
        "gameType": GAME_TYPE,
        "displayName": "Arma 3",
        "driver": {"protocolVersion": PROTOCOL_VERSION, "command": [sys.executable, str(Path(args.driver_path).resolve())]},
        "capabilities": ["discover", "adopt", "lifecycle", "health", "detach"],
        "resources": {
            "containers": {"api": API_CONTAINER, "frontend": FRONTEND_CONTAINER},
            "volumes": list(VOLUMES),
            "networks": [NETWORK],
            "ports": ports,
            "primaryMetricsContainer": API_CONTAINER,
            "sizeableVolumes": ["arma3-server"],
        },
        "images": {"api": image_name(api), "frontend": image_name(frontend)},
        "config": {"managerPath": str(config_path)},
        "secrets": [{"id": "manager-secrets", "provider": "podman", "reference": SECRET_NAME}],
        "health": {"type": "containers", "requiredRoles": ["api", "frontend"]},
        "controllerRevision": previous_revision,
        "updatedAt": utc_now(),
    }
    with instance_lock(instance_id):
        if load_controller(instance_id) is not None:
            raise DriverError("already_claimed", "manual deployment is disabled while the hub owns this instance", exit_code=3)
        operation = load_json(operation_path(instance_id))
        if operation.get("operationId") != args.operation_id:
            raise DriverError("operation_conflict", "operationId does not own the active deployment", exit_code=3)
        atomic_private_json(manifest_path(instance_id), manifest)
    return {"synced": True, "manifest": manifest}


def command_can_deploy(instance_id: str) -> dict[str, Any]:
    validate_instance_id(instance_id)
    controller = load_controller(instance_id)
    if controller:
        raise DriverError("already_claimed", f"instance is controlled by {controller.get('controllerId')}; detach it before manual deployment", exit_code=3)
    return {"allowed": True}


def command_begin_deploy(instance_id: str) -> dict[str, Any]:
    validate_instance_id(instance_id)
    with instance_lock(instance_id):
        controller = load_controller(instance_id)
        if controller:
            raise DriverError("already_claimed", f"instance is controlled by {controller.get('controllerId')}; detach it before manual deployment", exit_code=3)
        if operation_path(instance_id).exists():
            raise DriverError("operation_conflict", "another manual deployment is already in progress", exit_code=3)
        operation = {"operationId": secrets.token_hex(16), "kind": "manual-deploy", "startedAt": utc_now()}
        atomic_private_json(operation_path(instance_id), operation)
        return operation


def command_end_deploy(instance_id: str, operation_id: str) -> dict[str, Any]:
    validate_instance_id(instance_id)
    with instance_lock(instance_id):
        current = load_json(operation_path(instance_id))
        if current.get("operationId") != operation_id:
            raise DriverError("operation_conflict", "operationId does not own the active deployment", exit_code=3)
        operation_path(instance_id).unlink(missing_ok=True)
        fsync_directory(instance_dir(instance_id))
        return {"ended": True}


def unsupported(command: str) -> dict[str, Any]:
    raise DriverError("unsupported", f"{command} is not advertised by this v1 driver")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=(
        "describe", "discover", "inspect", "claim", "release", "start", "stop", "restart", "health",
        "provision", "recreate", "update", "rotate-secrets", "destroy",
        "ensure-id", "can-deploy", "begin-deploy", "end-deploy", "recover-deploy", "sync",
    ))
    parser.add_argument("--instance-id")
    parser.add_argument("--config-path")
    parser.add_argument("--driver-path")
    parser.add_argument("--operation-id")
    args = parser.parse_args()
    try:
        request = read_request()
        if args.command == "describe":
            result = command_describe()
        elif args.command == "discover":
            result = command_discover()
        elif args.command in {"inspect", "health"}:
            result = command_inspect(request)
        elif args.command == "claim":
            result = command_claim(request)
        elif args.command == "release":
            result = command_release(request)
        elif args.command in {"start", "stop", "restart"}:
            result = lifecycle(request, args.command)
        elif args.command == "ensure-id":
            result = {"instanceId": ensure_instance_id()}
        elif args.command == "can-deploy":
            result = command_can_deploy(args.instance_id or ensure_instance_id())
        elif args.command == "begin-deploy":
            result = command_begin_deploy(args.instance_id or ensure_instance_id())
        elif args.command in {"end-deploy", "recover-deploy"}:
            if not args.instance_id or not args.operation_id:
                raise DriverError("invalid_request", f"{args.command} requires --instance-id and --operation-id")
            result = command_end_deploy(args.instance_id, args.operation_id)
        elif args.command == "sync":
            if not args.instance_id or not args.config_path or not args.driver_path or not args.operation_id:
                raise DriverError("invalid_request", "sync requires --instance-id, --config-path, --driver-path and --operation-id")
            result = command_sync(args)
        else:
            result = unsupported(args.command)
        emit(result)
        return 0
    except DriverError as error:
        print(json.dumps({"error": {"code": error.code, "message": str(error)}}), file=sys.stderr)
        return error.exit_code


if __name__ == "__main__":
    raise SystemExit(main())
