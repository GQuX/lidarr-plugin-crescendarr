# Crescendarr audit-only beta: read-only preview for Lidarr

> **Release candidate for review. Not published.** Install it only after the project owner approves a first field
> test. Even then, try it on a copy of your library first.

## What it does

Crescendarr looks at completed downloads from the Lidarr download clients **you select**. For each one it shows, track by
track, what a selective import would do:

- **Upgrade**: the incoming track is better according to the artist's Lidarr quality profile.
- **Keep**: your existing file is better or equal, so it would stay.
- **Add**: the track is missing from your library and present in the download.
- **Review**: something is ambiguous, for example a different edition, a shared file or a quality that can't be compared. In this case a write-enabled build would not act on its own.

It works out the identification and the quality comparison with Lidarr's own matching and your quality profile.

## What it cannot do

This build **cannot write to your library**. The code that imports, moves, deletes, recycles, relinks or registers
audio or lyric files is not in this DLL at all, and the build pipeline checks that mechanically. Specifically:

- It never moves, deletes, renames, retags or recycles audio or lyric files.
- It never changes TrackFile, Track, lyric or monitoring rows, or any Lidarr setting.
- It never removes anything from a download client and never marks a queue item imported.

Even if someone edits its settings in the database or sends Crescendarr import commands through the API, nothing is
imported. Those commands do not exist in this build.

Crescendarr writes only its own diagnostic files to `<Lidarr AppData>/crescendarr-audit/`.

**Lidarr itself still does what you configured it to do.** If Completed Download Handling is on, Lidarr imports
downloads its own way. Scheduled rescans and metadata refreshes can also change your library. Crescendarr cannot
prevent any of that.

## Requirements

| Requirement | Value |
|---|---|
| Lidarr | Official stable **v3.1.6.5079**, the only tested version. Other versions may refuse to load the plugin. |
| Platform | **Linux** only (including Docker images). On other operating systems the plugin stays inactive and reports an error. |
| Storage | Local disks were tested. A kernel-mounted SMB/CIFS library was tested with the write-enabled build in M23. See *Limitations*. |

## Installation options

**GitHub URL installation (pending release publication):** The native installer-compatible audit-only package is being validated under [M26](../release-gates/m26-native-installer.md). Only **after an approved, published GitHub pre-release** should you use Lidarr's **System > Plugins > Install** with:

`https://github.com/GQuX/lidarr-plugin-crescendarr`

Lidarr will download the compatible `net8.0.zip` release asset to its plugin folder and ask for a restart. With no published release, the URL **does not install anything**. After restart, verify the audit-only build identity in logs and leave its Connect mode Disabled until you intend to preview.

**Manual install (CI artifact only, for controlled testing):**

1. Verify the download:

   ```sh
   sha256sum -c crescendarr-audit-beta-<version>.zip.sha256
   ```

2. Stop Lidarr.
3. Unzip **only** `Lidarr.Plugin.Crescendarr.dll` into `<Lidarr AppData>/plugins/GQuX/Crescendarr/`. In Docker, AppData is usually `/config`.
4. Check that the DLL matches the `SHA256SUMS` file inside the zip:

   ```sh
   sha256sum <that folder>/Lidarr.Plugin.Crescendarr.dll
   ```

5. Start Lidarr. The log shows:

   ```
   CRESCENDARR AUDIT_BUILD_READY version=... libraryWrites=impossible
   ```

Installing changes nothing. Crescendarr does nothing until you configure it.

## Configure (Settings > Connect > + > Crescendarr)

| Field | What to enter |
|---|---|
| Mode | **Disabled** is the default and does nothing. **Audit only** turns the preview on. |
| Download clients to observe | Pick the client(s) explicitly. Downloads from other clients are never read. |
| Allowed download folders | The absolute folder(s) where those clients' completed downloads appear **as Lidarr sees them**, after remote path mappings. Separate several folders with `;`. Anything outside them is refused and listed as refused. |
| Audit interval | Minutes between automatic previews. A preview also runs at startup and when you run the `CrescendarrAuditCycle` command. |

## Read the results

- **System > Status**: a yellow warning when downloads *would* change your library, for example *"3 upgrade(s), 1 addition(s); 1 better existing file(s) would be kept"*. It also reports downloads needing review and clients that could not be read. A red error means the configuration is invalid.
- **`<AppData>/crescendarr-audit/report.md`**: a readable table per album with each track's action, current and incoming quality, the reason, and whether its lyric would be kept.
- **`report.json`**: the same data, machine-readable.
- **`diagnostics-redacted.json`**: safe to share in a bug report. It contains only counts, states, reason codes and quality names, with no paths, titles, artist names or download IDs.

## Upgrade, roll back, uninstall

- **Upgrade**: stop Lidarr, replace the DLL with the newer one (verify it first), start Lidarr. Your settings and `crescendarr-audit/` are kept.
- **Roll back**: the same steps with the previous DLL. The state files are compatible.
- **Uninstall**:
  1. Delete the Crescendarr entry in Settings > Connect.
  2. Stop Lidarr.
  3. Delete `<AppData>/plugins/GQuX/Crescendarr/`.
  4. Start Lidarr.
  5. Optionally delete `<AppData>/crescendarr-audit/` to remove all audit data.

## Troubleshooting

| Symptom | Meaning and fix |
|---|---|
| Error: "not observing anything: invalid … settings" | Only Disabled and Audit only exist in this build. Re-save the Connect entry. |
| "selected download client N is missing or disabled" | Re-select the client, or enable it in Settings > Download Clients. |
| `RefusedUnsafePath`: outside the allowed download folders | Add the folder Lidarr actually sees to *Allowed download folders*, or fix the remote path mapping. |
| `SourceUnavailable` | The download folder is offline, moved or unreadable by Lidarr. |
| `LibraryUnavailable` | The artist folder or a tracked file can't be read: an unmounted share, a stale SMB mount or missing permissions. Crescendarr will **not** treat unreadable files as missing. |
| `AmbiguousSource` | Two clients or items report the same folder. Nothing is inspected. Give each client its own folder. |

## Privacy

- Nothing is sent anywhere.
- The local report contains your artist and album names and folder names, and stays on your machine.
- Logs contain download IDs and short reasons.
- No download-client credentials are read or written by Crescendarr. Lidarr's own client implementation talks to the client.

## Limitations (read before trusting a preview)

- The preview is what *this* plugin would decide. It is not a promise that a future write-enabled build will import.
- Only the incoming **MP3** files' frame integrity is verified. For FLAC and other formats Crescendarr relies on what the client reports as complete.
- Lidarr's quality profile decides upgrades. Same-quality re-encodes are never treated as upgrades.
- On an SMB/CIFS library, Lidarr's own rescans can re-import whole albums after a mtime drift (a stock Lidarr behaviour documented in M23). This build does not cause or prevent that.
- A remote path mapping problem shows up as `RefusedUnsafePath`. It is never silently ignored.
