import argparse
import tempfile
import unittest
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import patch

import deploy


class DeployConfigTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.originals = deploy.DEPLOY_FILE, deploy.MANAGER_FILE, deploy.SECRETS_FILE
        deploy.DEPLOY_FILE = root / "deploy.toml"
        deploy.MANAGER_FILE = root / "manager.toml"
        deploy.SECRETS_FILE = root / "manager.secrets.toml"
        deploy.DEPLOY_FILE.write_text('[dev]\nserver="10.0.0.5"\nusername="arma3"\n', encoding="utf-8")
        deploy.MANAGER_FILE.write_text(
            '[web]\nport=8080\npublic_port=8080\nbind_ip="0.0.0.0"\n'
            '[server]\narma3_dir="/arma3"\nport=2302\nquery_port=2303\n'
            'battleye_port=2304\nvon_port=2305\nrcon_port=2301\nnetwork_mode="bridge"\n',
            encoding="utf-8",
        )
        deploy.SECRETS_FILE.write_text(
            '[web]\npassword="a-secure-password"\n'
            'session_secret="01234567890123456789012345678901"\n',
            encoding="utf-8",
        )
        deploy.SECRETS_FILE.chmod(0o600)

    def tearDown(self):
        deploy.DEPLOY_FILE, deploy.MANAGER_FILE, deploy.SECRETS_FILE = self.originals
        self.temp.cleanup()

    def test_valid_configuration(self):
        target = deploy.validate_local("dev")
        self.assertEqual("arma3@10.0.0.5", target.ssh)

    @patch.object(deploy, "remote_capture", return_value="aarch64")
    @patch.object(deploy, "remote")
    def test_remote_arm_host_is_rejected_before_build(self, _remote, _capture):
        with self.assertRaisesRegex(SystemExit, "x86_64 Linux server"):
            deploy.verify_remote_host(deploy.Target("dev", "10.0.0.5", "arma3"))

    def test_insecure_secret_file_permissions_are_rejected(self):
        deploy.SECRETS_FILE.chmod(0o644)
        with self.assertRaisesRegex(SystemExit, "mode 0600"):
            deploy.validate_secrets()

    def test_force_allows_insecure_secret_file_permissions(self):
        deploy.SECRETS_FILE.chmod(0o644)

        secrets = deploy.validate_secrets(allow_insecure_permissions=True)

        self.assertEqual("a-secure-password", secrets["web"]["password"])

    def test_duplicate_game_ports_are_rejected(self):
        text = deploy.MANAGER_FILE.read_text(encoding="utf-8").replace("query_port=2303", "query_port=2302")
        deploy.MANAGER_FILE.write_text(text, encoding="utf-8")
        with self.assertRaises(SystemExit):
            deploy.manager_config()

    def test_rcon_port_in_arma_reserved_range_is_rejected(self):
        text = deploy.MANAGER_FILE.read_text(encoding="utf-8").replace("rcon_port=2301", "rcon_port=2306")
        deploy.MANAGER_FILE.write_text(text, encoding="utf-8")
        with self.assertRaises(SystemExit):
            deploy.manager_config()

    @patch.object(deploy, "wait_healthy", return_value=True)
    @patch.object(deploy, "remote")
    @patch.object(deploy, "current_image", return_value="localhost/frontend:old")
    def test_backend_deploy_restarts_existing_frontend(self, current_image, remote, wait_healthy):
        remote.return_value = CompletedProcess([], 0)
        target = deploy.Target("dev", "10.0.0.5", "arma3")

        deploy.restart_frontend_proxy(target)

        current_image.assert_called_once_with(target, "arma3-frontend")
        remote.assert_called_once_with(target, ["podman", "restart", "arma3-frontend"], check=False)
        wait_healthy.assert_called_once_with(target, "arma3-frontend")

    @patch.object(deploy, "remote")
    @patch.object(deploy, "current_image", return_value=None)
    def test_backend_deploy_skips_restart_when_frontend_is_absent(self, current_image, remote):
        target = deploy.Target("dev", "10.0.0.5", "arma3")

        deploy.restart_frontend_proxy(target)

        current_image.assert_called_once_with(target, "arma3-frontend")
        remote.assert_not_called()

    @patch.object(deploy, "secret_mount", return_value=["--secret", "test-secret"])
    def test_backend_command_mounts_host_sysfs_read_only(self, _secret_mount):
        target = deploy.Target("dev", "10.0.0.5", "arma3")

        command = deploy.api_command(target, "localhost/api:test", "/release")

        self.assertIn("/sys:/host-sys:ro", command)

    def test_frontend_uses_configured_backend_port(self):
        text = deploy.MANAGER_FILE.read_text(encoding="utf-8").replace("port=8080", "port=8181", 1)
        deploy.MANAGER_FILE.write_text(text, encoding="utf-8")

        command = deploy.frontend_command("localhost/frontend:test")

        self.assertIn("ARMA3_API_BACKEND=arma3-api:8181", command)

    @patch.object(deploy, "remote")
    @patch.object(deploy, "remote_capture")
    def test_existing_podman_secret_is_preserved(self, remote_capture, remote):
        remote_capture.return_value = "existing-secret-id"
        remote.return_value = CompletedProcess([], 0)
        target = deploy.Target("dev", "10.0.0.5", "arma3")

        deploy.ensure_runtime(target)

        secret_creates = [
            call for call in remote.call_args_list
            if call.args[1][:3] == ["podman", "secret", "create"]
        ]
        self.assertEqual([], secret_creates)

    @patch.object(deploy, "remote_capture", return_value="/old/release/config/manager.toml")
    def test_frontend_only_deploy_can_reuse_backend_config_mount(self, remote_capture):
        target = deploy.Target("dev", "10.0.0.5", "arma3")

        self.assertEqual("/old/release/config/manager.toml", deploy.current_manager_config(target))
        self.assertEqual("arma3-api", remote_capture.call_args.args[1][-1])

    @patch.object(deploy, "remote")
    def test_build_does_not_prune_before_container_replacement(self, remote):
        target = deploy.Target("dev", "10.0.0.5", "arma3")

        image = deploy.build_image(target, "/release", "20260714010000", "api")

        self.assertEqual("localhost/arma3-manager-api:20260714010000", image)
        command = remote.call_args.args[1]
        self.assertEqual("podman", command[0])
        self.assertEqual("build", command[1])
        self.assertIn("--build-arg", command)
        self.assertEqual("linux/amd64", command[command.index("--platform") + 1])
        self.assertEqual("/release/Containerfile.api", command[command.index("--file") + 1])
        self.assertEqual("localhost/arma3-manager-api:20260714010000", command[command.index("--tag") + 1])
        self.assertEqual("/release", command[-1])

    def test_contract_labels_identify_instance_and_role(self):
        labels = deploy.contract_labels("a" * 32, "api")
        self.assertIn("io.gameserver-manager.contract.version=1.0", labels)
        self.assertIn("io.gameserver-manager.instance.id=" + "a" * 32, labels)
        self.assertIn("io.gameserver-manager.role=api", labels)

    @patch.object(deploy, "remote")
    def test_prune_is_all_unused_images_but_only_for_project_label(self, remote):
        remote.return_value = CompletedProcess([], 0)
        target = deploy.Target("prod", "10.0.0.6", "arma3")

        deploy.prune_project_images(target)

        remote.assert_called_once_with(
            target,
            [
                "podman", "image", "prune", "-a", "-f", "--filter",
                "label=project=arma3-manager",
            ],
            check=False,
        )

    def test_every_containerfile_stage_is_scoped_to_project_prune(self):
        for name in ("Containerfile.api", "Containerfile.frontend"):
            with self.subTest(containerfile=name):
                lines = (deploy.ROOT / name).read_text(encoding="utf-8").splitlines()
                stages = [index for index, line in enumerate(lines) if line.startswith("FROM ")]
                self.assertGreaterEqual(len(stages), 2)
                for index in stages:
                    self.assertEqual("LABEL project=arma3-manager", lines[index + 1])

    def test_dev_and_prod_prune_only_after_successful_deploy(self):
        for environment in ("dev", "prod"):
            with self.subTest(environment=environment):
                target = deploy.Target(environment, "10.0.0.5", "arma3")
                events = []
                args = argparse.Namespace(environment=environment, backend=True, frontend=False, yes=True, force=False)
                with (
                    patch.object(deploy, "validate_local", return_value=target),
                    patch.object(deploy, "verify_tools"),
                    patch.object(deploy, "verify_remote_host"),
                    patch.object(deploy, "remote", return_value=CompletedProcess([], 0)),
                    patch.object(deploy, "confirm_backend"),
                    patch.object(deploy, "upload_release", return_value="/release"),
                    patch.object(deploy, "upload_secrets", side_effect=lambda *unused: events.append("secrets")),
                    patch.object(deploy, "ensure_runtime", side_effect=lambda *unused: events.append("runtime")),
                    patch.object(deploy, "prepare_contract_runtime", return_value=("a" * 32, "b" * 32)),
                    patch.object(deploy, "finish_contract_runtime", side_effect=lambda *unused: events.append("finish")),
                    patch.object(deploy, "sync_contract_runtime", side_effect=lambda *unused: events.append("sync")),
                    patch.object(deploy, "build_image", return_value="localhost/arma3-manager-api:new"),
                    patch.object(deploy, "api_command", return_value=["podman", "run", "image"]),
                    patch.object(deploy, "current_image", return_value="localhost/arma3-manager-api:old"),
                    patch.object(deploy, "replace", side_effect=lambda *unused: events.append("replace")),
                    patch.object(deploy, "restart_frontend_proxy", side_effect=lambda *unused: events.append("restart")),
                    patch.object(deploy, "prune_project_images", side_effect=lambda *unused: events.append("prune")),
                ):
                    self.assertEqual(0, deploy.deploy(args))

                self.assertEqual(["secrets", "runtime", "replace", "restart", "prune", "sync", "finish"], events)


if __name__ == "__main__":
    unittest.main()
