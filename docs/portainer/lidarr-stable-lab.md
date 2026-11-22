# Portainer deployment: disposable Lidarr stable for Crescendarr

**Scope:** an independent, empty Lidarr instance for plugin integration research. It is NOT an upgrade of production Lidarr 3.1.6.5079, and not proof that Crescendarr is safe to install. **Do not mount production `/config`, music, downloads, SMB/CIFS, or Docker socket.**

## Recommended deployment

1. In Portainer choose Docker environment > **Stacks** > **Add stack** > **Web editor**. **Do not edit or redeploy your multi-service production stack** for the lab. Your current stack has Watchtower and multiple moving `latest`/`develop` tags, so an indiscriminate re-pull can update other production applications.
2. Name the stack `crescendarr-lab` and paste [`deploy/portainer/disposable-lidarr-stable.compose.yml`](../../deploy/portainer/disposable-lidarr-stable.compose.yml).
3. If necessary adjust `PUID` and `PGID` to the permissions requirements of the host. Leave the dedicated Docker volume and port mapping unchanged.
4. Ensure the lab service has `com.centurylinklabs.watchtower.enable: "false"`. This explicitly excludes the lab from your Watchtower, which uses `WATCHTOWER_LABEL_ENABLE=true`. Select **Deploy the stack**, then open `http://DOCKER_HOST_IP:8687` on your LAN. Set up an admin account if prompted. Avoid exposing the test WebUI to the public internet.
5. In Lidarr **System > Status**, note the exact version/.NET and verify **System > Plugins** is present. Do not configure download clients, NAS folders, or production credentials.
6. In Portainer, inspect the container: confirm mounts list **only** the stack-specific Docker named volume attached to `/config`. Confirm no music or download bind mounts. Record exact Docker image digest for a reproducible test.
7. Before any binary/probe work, read [`docs/safety/disposable-host.md`](../safety/disposable-host.md). Do not install or execute the source-only probe until both the binary compatibility and root-marker safety checks can be independently verified.

**User-provided production stack, observed 2026-11-22:** Lidarr has `image: lscr.io/linuxserver/lidarr:latest`, `container_name: lidarr`, `/opt/lidarr/config:/config`, and production `/mnt/arr`, `/mnt/nas-nvme/usenet-downloads`, `/mnt/nas-hdd/media` mounts. Production Watchtower polls every 21,600 seconds and follows `com.centurylinklabs.watchtower.enable: "true"`. Lab must use a new config volume, name and port, NO copied credentials, no production mounts, and disabled Watchtower auto-updates. These observations come from the supplied Compose text, not from inspecting the running host.

**Safety limits:** Dedicated `/config` prevents database migration of production. No production mounts prevents direct filesystem access through mounts. However the test container may still reach network devices on the LAN; never give it real NAS or downloader credentials. No plugin should be installed on the production host.

## Guarded configuration lint (development environment only)

```sh
python -m pip install pyyaml
python tools/check_disposable_compose.py
python -m unittest discover -s tools/tests -v
```

These tests validate the **example YAML**, not the running Docker host or installed Lidarr. If you customize the YAML, the validator intentionally rejects unreviewed changes; treat that as a safety review request, not a bug to bypass.

## If you choose to update production instead

1. Back up production Lidarr at **System > Backup**, download the backup archive, and take an independent backup of the Docker host's `/config` directory/volume (with Lidarr stopped for the filesystem copy).
2. Portainer **Stacks > production stack > Editor**: replace *only* the Lidarr image reference with `lscr.io/linuxserver/lidarr:latest` while keeping every existing mount, port, and network as-is. Re-pull image on stack update.
3. For a standalone Portainer container, use **Containers > lidarr > Duplicate/Edit**, change the image tag to `:stable`, verify existing settings, then replace the original container. `Recreate` with unchanged `:latest` alone will not switch to stable.
4. Confirm Lidarr logs and version; check `/system/plugins`. Do not use the application's in-container update button. **Stable DB migration can block downgrading**; restoring the **pre-upgrade** database backup may be required to return to stable.

This second option is not recommended for Crescendarr testing. Don't upgrade the existing production database solely for an unfinished experimental plugin.

## Source references (checked 2026-11-22)

- LinuxServer image tags and example: https://docs.linuxserver.io/images/docker-lidarr/
- Servarr Wiki plugin branch and downgrade caution: https://github.com/Servarr/Wiki/blob/master/lidarr/plugins.md
- Portainer stack creation: https://docs.portainer.io/user/docker/stacks/add
- Portainer stack editing: https://docs.portainer.io/user/docker/stacks/edit
- Portainer image update semantics: https://www.portainer.io/blog/pull-latest-image-feature-in-ce

**Note:** The user decided to keep production on stable. This lab is optional; never mount production files.
