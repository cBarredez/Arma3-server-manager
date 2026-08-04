import os
import stat
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import manager_driver as driver


class ManagerDriverTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.env = patch.dict(os.environ, {"GSM_STATE_ROOT": self.temp.name})
        self.env.start()

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def seed_manifest(self, instance_id: str) -> dict:
        manifest = {
            "contractVersion": "1.0",
            "instanceId": instance_id,
            "managerId": "arma3-server-manager",
            "gameType": "arma3",
            "displayName": "Arma 3",
            "controllerRevision": 0,
            "resources": {
                "containers": {"api": "arma3-api", "frontend": "arma3-frontend"},
                "volumes": list(driver.VOLUMES),
                "ports": {"web": 38080},
            },
            "secrets": [{"id": "manager-secrets", "provider": "podman", "reference": "arma3-manager-secrets"}],
        }
        driver.atomic_private_json(driver.manifest_path(instance_id), manifest)
        return manifest

    def test_instance_id_and_metadata_are_private(self):
        instance_id = driver.ensure_instance_id()
        self.assertEqual(32, len(instance_id))
        mode = stat.S_IMODE(driver.id_file().stat().st_mode)
        self.assertEqual(0o600, mode)

    def test_claim_is_compare_and_swap_and_idempotent_for_owner(self):
        instance_id = "a" * 32
        manifest = self.seed_manifest(instance_id)
        healthy = {"manifest": manifest, "health": {"issues": []}}
        with patch.object(driver, "command_inspect", return_value=healthy):
            first = driver.command_claim({"instanceId": instance_id, "controllerId": "hub-one", "expectedRevision": 0})
            second = driver.command_claim({"instanceId": instance_id, "controllerId": "hub-one", "expectedRevision": 0})
            self.assertEqual(first["controller"], second["controller"])
            with self.assertRaises(driver.DriverError) as conflict:
                driver.command_claim({"instanceId": instance_id, "controllerId": "hub-two", "expectedRevision": 0})
            self.assertEqual("already_claimed", conflict.exception.code)
        self.assertEqual(0o600, stat.S_IMODE(driver.controller_path(instance_id).stat().st_mode))

    def test_release_requires_matching_controller_revision(self):
        instance_id = "b" * 32
        manifest = self.seed_manifest(instance_id)
        with patch.object(driver, "command_inspect", return_value={"manifest": manifest, "health": {"issues": []}}):
            claimed = driver.command_claim({"instanceId": instance_id, "controllerId": "hub", "expectedRevision": 0})
        with self.assertRaises(driver.DriverError):
            driver.command_release({"instanceId": instance_id, "controllerId": "hub", "revision": 999})
        released = driver.command_release({
            "instanceId": instance_id,
            "controllerId": "hub",
            "revision": claimed["controller"]["revision"],
        })
        self.assertTrue(released["released"])
        self.assertFalse(driver.controller_path(instance_id).exists())

    def test_manual_deploy_operation_and_hub_claim_are_mutually_exclusive(self):
        instance_id = "d" * 32
        manifest = self.seed_manifest(instance_id)
        operation = driver.command_begin_deploy(instance_id)
        with patch.object(driver, "command_inspect", return_value={"manifest": manifest, "health": {"issues": []}}):
            with self.assertRaises(driver.DriverError) as conflict:
                driver.command_claim({"instanceId": instance_id, "controllerId": "hub", "expectedRevision": 0})
            self.assertEqual("operation_conflict", conflict.exception.code)
        driver.command_end_deploy(instance_id, operation["operationId"])
        with patch.object(driver, "command_inspect", return_value={"manifest": manifest, "health": {"issues": []}}):
            claimed = driver.command_claim({"instanceId": instance_id, "controllerId": "hub", "expectedRevision": 0})
        with self.assertRaises(driver.DriverError):
            driver.command_begin_deploy(instance_id)
        self.assertTrue(claimed["claimed"])

    def test_operation_recovery_requires_the_exact_operation_id(self):
        instance_id = "e" * 32
        operation = driver.command_begin_deploy(instance_id)
        with self.assertRaises(driver.DriverError):
            driver.command_end_deploy(instance_id, "f" * 32)
        self.assertTrue(driver.operation_path(instance_id).exists())
        driver.command_end_deploy(instance_id, operation["operationId"])
        self.assertFalse(driver.operation_path(instance_id).exists())

    def test_manifest_contains_secret_reference_but_no_secret_value(self):
        manifest = self.seed_manifest("c" * 32)
        serialized = str(manifest)
        self.assertIn("arma3-manager-secrets", serialized)
        self.assertNotIn("password", serialized.lower())
        self.assertNotIn("token", serialized.lower())

    def test_runtime_preflight_validates_mounts_ports_network_and_secret_reference(self):
        manifest = self.seed_manifest("f" * 32)
        manifest.update({
            "config": {"managerPath": "/srv/config/manager.toml"},
            "resources": {
                **manifest["resources"],
                "networks": ["arma3-net"],
                "ports": {
                    "web": 8080,
                    "port": 2302,
                    "queryPort": 2303,
                    "battleyePort": 2304,
                    "vonPort": 2305,
                },
            },
        })
        api = {
            "Mounts": [
                {"Name": "arma3-server", "Destination": "/arma3"},
                {"Name": "steam-home", "Destination": "/home/arma3/Steam"},
                {"Name": "steam-config", "Destination": "/home/arma3/.steam"},
                {"Name": "aspnet-keys", "Destination": "/home/arma3/.aspnet"},
                {"Source": "/srv/config/manager.toml", "Destination": "/app/config/manager.toml"},
            ],
            "NetworkSettings": {
                "Networks": {"arma3-net": {}},
                "Ports": {f"{port}/udp": [{"HostPort": str(port)}] for port in range(2302, 2306)},
            },
            "HostConfig": {"NetworkMode": "arma3-net"},
            "Config": {"Secrets": [{"Name": "arma3-manager-secrets"}]},
        }
        frontend = {
            "Mounts": [],
            "NetworkSettings": {"Networks": {"arma3-net": {}}, "Ports": {"8080/tcp": [{"HostPort": "8080"}]}},
        }
        with patch.object(driver, "podman_object_exists", return_value=True):
            self.assertEqual([], driver.validate_runtime_resources(manifest, api, frontend))
            api["Config"]["Secrets"] = []
            self.assertIn(
                "api does not mount Podman secret arma3-manager-secrets",
                driver.validate_runtime_resources(manifest, api, frontend),
            )


if __name__ == "__main__":
    unittest.main()
