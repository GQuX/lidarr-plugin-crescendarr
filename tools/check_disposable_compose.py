#!/usr/bin/env python3
"""Fail-closed inspection of the *example* Crescendarr disposable Docker Compose.

This is a lint guard, not proof of host isolation. It does not contact Docker and
cannot detect pre-existing volumes, host-level routing, plugin side effects,
or compromised host configuration. Requires PyYAML in the developer environment.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import sys
from typing import Any

try:
    import yaml
except ImportError as exc:
    raise SystemExit("PyYAML is required for development validation: python -m pip install pyyaml") from exc

EXPECTED_IMAGE = "lscr.io/linuxserver/lidarr:latest"
EXPECTED_SERVICE = "crescendarr-lidarr-lab"
EXPECTED_VOLUME = "crescendarr_lidarr_lab_config"
EXPECTED_CONTAINER = "crescendarr-lidarr-lab"
EXPECTED_PUBLISHED_PORT = 8687
EXPECTED_TARGET_PORT = 8686
EXPECTED_CONFIG_TARGET = "/config"


def check_compose(document: Any) -> list[str]:
    """Return policy violations. Default-deny all unexplained settings."""
    errors: list[str] = []
    if not isinstance(document, dict):
        return ["Compose document must be a mapping"]

    if set(document) != {"services", "volumes"}:
        errors.append("Top-level keys must be exactly services and volumes")
    services = document.get("services")
    volumes = document.get("volumes")
    if not isinstance(services, dict) or set(services) != {EXPECTED_SERVICE}:
        return errors + ["Only the dedicated disposable Lidarr service is allowed"]
    if not isinstance(volumes, dict) or set(volumes) != {EXPECTED_VOLUME}:
        errors.append("Only the dedicated, distinct config volume is allowed")
    else:
        if volumes[EXPECTED_VOLUME] not in (None, {}):
            errors.append("Config volume may not use external/source/driver options")

    service = services[EXPECTED_SERVICE]
    if not isinstance(service, dict):
        return errors + ["Disposable service must be a mapping"]
    permitted = {"image", "container_name", "environment", "volumes", "ports", "restart", "labels"}
    extra = set(service) - permitted
    if extra:
        errors.append("Forbidden/unreviewed service keys: " + ", ".join(sorted(extra)))
    if service.get("image") != EXPECTED_IMAGE:
        errors.append("Expected the official LinuxServer stable image")
    if service.get("container_name") != EXPECTED_CONTAINER:
        errors.append("Lab must have its own container name")
    if service.get("restart") != "unless-stopped":
        errors.append("Unexpected restart policy")

    if service.get("labels") != {"com.centurylinklabs.watchtower.enable": "false"}:
        errors.append("Watchtower must explicitly be disabled for the test host")

    env = service.get("environment")
    if not isinstance(env, dict) or set(env) != {"PUID", "PGID", "TZ"}:
        errors.append("Only PUID, PGID and TZ are allowed in the initial lab")
    else:
        for key in ("PUID", "PGID"):
            if not str(env[key]).isdigit():
                errors.append(f"{key} must be a numeric UID/GID")
        if not isinstance(env["TZ"], str) or not env["TZ"].strip():
            errors.append("TZ must be a nonempty timezone")

    mounts = service.get("volumes")
    if mounts != [f"{EXPECTED_VOLUME}:{EXPECTED_CONFIG_TARGET}"]:
        errors.append("The ONLY allowed mount is a fresh named volume at /config")

    ports = service.get("ports")
    if not isinstance(ports, list) or len(ports) != 1:
        errors.append("Exactly one test WebUI port publication is required")
    else:
        port = ports[0]
        if not isinstance(port, str) or port != f"{EXPECTED_PUBLISHED_PORT}:{EXPECTED_TARGET_PORT}":
            errors.append("Only the dedicated 8687:8686 test port mapping is allowed")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?", type=Path, default=Path(__file__).resolve().parents[1]
                        / "deploy/portainer/disposable-lidarr-stable.compose.yml")
    args = parser.parse_args()
    try:
        doc = yaml.safe_load(args.path.read_text(encoding="utf-8"))
    except (OSError, yaml.YAMLError) as exc:
        print(f"FAIL: cannot load compose: {exc}", file=sys.stderr)
        return 1
    errors = check_compose(doc)
    if errors:
        for error in errors:
            print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print("PASS: example uses isolated container name, new config volume, WebUI 8687, and no production bind mounts")
    print("NOT_PROVEN: Docker/runtime isolation, network reachability, .NET compilation, plugin load, or import safety")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
