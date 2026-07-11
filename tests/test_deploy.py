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
            'battleye_port=2304\nvon_port=2305\nrcon_port=2306\nnetwork_mode="bridge"\n',
            encoding="utf-8",
        )
        deploy.SECRETS_FILE.write_text(
            '[web]\npassword="a-secure-password"\n'
            'session_secret="01234567890123456789012345678901"\n',
            encoding="utf-8",
        )

    def tearDown(self):
        deploy.DEPLOY_FILE, deploy.MANAGER_FILE, deploy.SECRETS_FILE = self.originals
        self.temp.cleanup()

    def test_valid_configuration(self):
        target = deploy.validate_local("dev")
        self.assertEqual("arma3@10.0.0.5", target.ssh)

    def test_duplicate_game_ports_are_rejected(self):
        text = deploy.MANAGER_FILE.read_text(encoding="utf-8").replace("query_port=2303", "query_port=2302")
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


if __name__ == "__main__":
    unittest.main()
