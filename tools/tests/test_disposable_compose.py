"""Test that accidental production mounts/privileges cannot pass the lab Compose guard."""
from __future__ import annotations

from copy import deepcopy
import importlib.util
from pathlib import Path
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/check_disposable_compose.py"
spec = importlib.util.spec_from_file_location("compose_guard", SCRIPT)
assert spec is not None and spec.loader is not None
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
GOOD = yaml.safe_load((ROOT / "deploy/portainer/disposable-lidarr-stable.compose.yml").read_text())


class DisposableComposeTests(unittest.TestCase):
    def service(self, obj):
        return obj["services"]["crescendarr-lidarr-lab"]

    def test_reference_compose_passes(self):
        self.assertEqual([], module.check_compose(GOOD))

    def test_production_config_bind_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["volumes"] = ["/srv/lidarr/config:/config"]
        self.assertTrue(module.check_compose(obj))

    def test_nas_bind_mount_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["volumes"].append("/mnt/nas/music:/music")
        self.assertTrue(module.check_compose(obj))

    def test_docker_socket_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["volumes"].append("/var/run/docker.sock:/var/run/docker.sock")
        self.assertTrue(module.check_compose(obj))

    def test_external_volume_is_rejected(self):
        obj = deepcopy(GOOD)
        obj["volumes"]["crescendarr_lidarr_lab_config"] = {"external": True}
        self.assertTrue(module.check_compose(obj))

    def test_prod_host_port_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["ports"] = ["8686:8686"]
        self.assertTrue(module.check_compose(obj))

    def test_host_network_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["network_mode"] = "host"
        self.assertTrue(module.check_compose(obj))

    def test_privileged_mode_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["privileged"] = True
        self.assertTrue(module.check_compose(obj))

    def test_extra_plugin_credential_env_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["environment"]["SABNZBD_API_KEY"] = "fake"
        self.assertTrue(module.check_compose(obj))

    def test_renamed_container_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["container_name"] = "lidarr"
        self.assertTrue(module.check_compose(obj))

    def test_wrong_image_tag_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["image"] = "lscr.io/linuxserver/lidarr:nightly"
        self.assertTrue(module.check_compose(obj))

    def test_pinned_official_image_digest_is_accepted(self):
        obj = deepcopy(GOOD)
        self.service(obj)["image"] = "lscr.io/linuxserver/lidarr@sha256:" + "a" * 64
        self.assertEqual([], module.check_compose(obj))

    def test_malformed_image_digest_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["image"] = "lscr.io/linuxserver/lidarr@sha256:BADHASH"
        self.assertTrue(module.check_compose(obj))

    def test_non_official_digest_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["image"] = "example.com/lidarr@sha256:" + "a" * 64
        self.assertTrue(module.check_compose(obj))

    def test_lan_webui_publication_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["ports"] = ["8687:8686"]
        self.assertTrue(module.check_compose(obj))

    def test_watchtower_label_missing_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj).pop("labels")
        self.assertTrue(module.check_compose(obj))

    def test_watchtower_label_true_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["labels"]["com.centurylinklabs.watchtower.enable"] = "true"
        self.assertTrue(module.check_compose(obj))

    def test_watchtower_label_unknown_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["labels"]["some.other"] = "something"
        self.assertTrue(module.check_compose(obj))

    def test_additional_service_is_rejected(self):
        obj = deepcopy(GOOD)
        obj["services"]["sabnzbd"] = {"image": "test"}
        self.assertTrue(module.check_compose(obj))

    def test_unexpected_key_is_rejected(self):
        obj = deepcopy(GOOD)
        self.service(obj)["devices"] = ["/dev/sda:/dev/sda"]
        self.assertTrue(module.check_compose(obj))


if __name__ == "__main__":
    unittest.main()
