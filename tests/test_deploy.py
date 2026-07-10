import tempfile
import unittest
from pathlib import Path

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
            'battleye_port=2304\nvon_port=2305\nnetwork_mode="bridge"\n',
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


if __name__ == "__main__":
    unittest.main()
