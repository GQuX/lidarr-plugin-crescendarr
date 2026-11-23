# M26: native Lidarr GitHub URL installer, audit-only candidate

**Status: release-compatible packaging implemented and verified in CI once the M26 build is green. Not published. No GitHub Release, tag, Docker or NAS changes. Production writes remain NO-GO.**

## Exact pinned host contract (Lidarr v3.1.6.5079)

The implementation is based on **the tagged upstream source**, not assumptions about other plugins:

- [PluginService.cs](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Plugins/PluginService.cs): `ParseRepositoryInput` accepts `https://github.com/GQuX/lidarr-plugin-crescendarr`; `GetRemotePlugin` calls `https://api.github.com/repos/GQuX/lidarr-plugin-crescendarr/releases`. It chooses the most recently **published**, non-draft release whose `target_commitish` is `main` or `master` (for the URL without a tree suffix), whose asset name contains **`net8.0.zip`**, and whose optional `Minimum Lidarr Version` requirement is not higher than the host. The version is parsed from the Git tag (e.g. `v0.25.0-audit-beta.1`).
- [InstallPluginService.cs](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Plugins/InstallPluginService.cs): downloads that ZIP and extracts it into `<Lidarr AppData>/plugins/GQuX/lidarr-plugin-crescendarr/`. The installer does **not** read or require `plugin.json` or `manifest.json`. It asks for a restart; uninstall removes the same folder.
- [Plugin.cs](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Plugins/Plugin.cs): `IPlugin.Owner` and `IPlugin.Name` are used for installed-plugin tracking and uninstall; these now match `GQuX` and `lidarr-plugin-crescendarr` exactly. The friendly user-facing name stays in the Settings > Connect provider.

## Installer package

`tools/package_lidarr_native.py` creates a reproducible `Crescendarr-v0.25.0-audit-beta.1.net8.0.zip` with exactly one entry: `Lidarr.Plugin.Crescendarr.dll` **from the pre-scanned M25 audit build**. That binary contains no writer commands and is not the CI-only import-executor build. An external `.zip.sha256` is produced. The existing M25 human-readable beta ZIP remains unchanged for offline inspection.

The M25 workflow builds the DLL twice; the IL scanner verifies it excludes all forbidden writer APIs and rejects a real writer DLL as a positive control. The M26 packaging step then builds native installer ZIPs from both scanned-build copies, requires byte-for-byte identical ZIPs, compares the packaged DLL with the exact scanned DLL, and checks the SHA-256. The full M25 stock-daemon test still executes against the audit DLL.

## Exact release conditions (NOT EXECUTED)

When approved by the owner, the future **GitHub pre-release** must be created with:

- `tag_name: v0.25.0-audit-beta.1` (matches the DLL's version string);
- `target_commitish: main` and `draft: false` (GitHub pre-release is compatible as long as not a draft);
- a release asset named `Crescendarr-v0.25.0-audit-beta.1.net8.0.zip` containing the exact checksum-verified CI artifact, **not a locally rebuilt ZIP**;
- release notes with `Minimum Lidarr Version: 3.1.6.5079`, explicit Linux / .NET 8 and audit-only limitations, known stock-CIFS rescan risk, and the no-write production policy.

**Caution:** Lidarr's installer chooses the newest compatible GitHub release based on publish time, not the most stable semantic version. Do not publish a writer-build ZIP with a `net8.0.zip` name from this repository or the same install URL could deliver it.

## Acceptance matrix

| Case | Status |
| --- | --- |
| Exact stock source contract and asset selection, pinned Lidarr 3.1.6.5079 | SOURCE-VERIFIED |
| Native ZIP only contains the scanned read-only DLL | CI-TESTED once green |
| Two clean release asset ZIPs byte-identical with checksums | CI-TESTED once green |
| Installed-plugin identity for tracked updates and uninstall | SOURCE-VERIFIED; needs real GitHub release test |
| Audit build disabled on first boot, refuses native writer command injection, writes no library bytes | PROVEN in M25 stock daemon; rerun with M26 changed assembly |
| Installation via **actual** stock System > Plugins GitHub URL | **NOT TESTED** until an approved published release is available |
| LinuxServer.io Docker `3.1.6.5079-ls49` field installation | **NOT TESTED** |
| Audit build with real SABnzbd, kernel CIFS and CDH ON | **NOT TESTED** |

Nothing above authorizes installing into the owner's live Lidarr or publishing an asset. Publish and field-test only with explicit approval.

## User's Docker setup

Lidarr is `lscr.io/linuxserver/lidarr:latest` currently reporting `3.1.6.5079-ls49`, with `/config` mapped to `/opt/lidarr/config`. Their Watchtower has `WATCHTOWER_LABEL_ENABLE=true` and Lidarr explicitly carries `com.centurylinklabs.watchtower.enable: "false"`, so **Watchtower excludes Lidarr**. The `latest` tag alone does not bypass that exclusion; a separate manual pull/redeploy can still update Lidarr.
