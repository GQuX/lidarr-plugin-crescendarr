# M27: release readiness for v0.25.0-audit-beta.1 (draft PR #68)

**Nothing published.** No tag and no release exist. Production write-enabled stays **NO-GO**.

## Environment actually tested

Run [45261466845](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45261466845), workflow `m27-lsio-release-readiness.yml`.

| Item | Value |
|---|---|
| Lidarr | `lscr.io/linuxserver/lidarr:3.1.6.5079-ls49`, digest `sha256:8fb40ada166dd1880cb1b166df5bcbb081107523f20ab60df05c313e2052a467`. Label: `Linuxserver.io version:- 3.1.6.5079-ls49 Build-date:- 2026-11-18`. Alpine, .NET 8.0.27. The API reports `isDocker=true` and `appData=/config`. |
| /config | Persistent host directory, kept across 6 container restarts |
| Download clients | `SABnzbd` (stock client, real SABnzbd 5.1.3, synthetic NZBs from a localhost NNTP fixture, default removal settings); `Blackhole` and `Other Blackhole` (stock Usenet Blackhole). All have ordinary names. |
| Completed Download Handling | **ON** (stock default) |
| Library | Kernel CIFS mount (Samba 4.19 on loopback, SMB 3.1.1, `soft`), bind-mounted into the container |
| Plugin installation | **Lidarr's real installer** (`InstallPlugin` / `UninstallPlugin`), pointed at a local mock of `api.github.com`. A throwaway CA is trusted only inside the container. Nothing is published. |

## Results

| Check | Result |
|---|---|
| Release asset contents | **PROVEN.** `Crescendarr-v0.25.0-audit-beta.1.net8.0.zip` holds exactly one entry, byte-identical to the DLL that `tools/audit-scan` passed. |
| Stock installer | **PROVEN.** It queried `/repos/GQuX/lidarr-plugin-crescendarr/releases`, chose the `net8.0.zip` asset, and parsed tag `v0.25.0-audit-beta.1`. It placed exactly that DLL in `/config/plugins/GQuX/lidarr-plugin-crescendarr/`. System > Plugins shows `GQuX/lidarr-plugin-crescendarr 0.25.0.0 (audit-beta.1)` with no update offered. |
| Startup | **PROVEN.** `AUDIT_BUILD_READY version=0.25.0-audit-beta.1+<commit> libraryWrites=impossible` |
| Default Disabled | **PROVEN.** Nothing was observed for 70 s after install, nor with a default Connect entry. |
| Audit previews | **PROVEN.** Good job: 1 Upgrade, 2 Keep (superior FLAC), 3 Add. Wrong edition: NeedsReview with "planner: EDITION_MISMATCH \| Lidarr: Album release not requested". |
| Symlinked download path (selected client) | **FAIL-CLOSED.** `RefusedUnsafePath`, "reached through a symbolic link". |
| Unselected client | **PROVEN.** Its completed download is never observed. |
| Writer-command injection (8 names) | **FAIL-CLOSED.** Stock rejects each as an unknown command (HTTP 500), and no command row is created. |
| GuardedAuto mode written into the database | **FAIL-CLOSED.** The audit cycle is skipped as "invalid", and health shows an error. |
| SMB server frozen (every `smbd` SIGSTOPped) | **PROVEN, slow.** Stock logged `EHOSTDOWN`. Depending on timing, the audit cycle either waits on the hung CIFS call or completes. In both cases no album preview became a false Upgrade, Add or Missing, and after resume the previews were identical. |
| Container restarts with persistent /config | **PROVEN.** The audit state persists. |
| Update detection | **PROVEN.** A newer mock pre-release is shown as `0.25.0.0 (audit-beta.2)` with `updateAvailable=true`. |
| In-place upgrade through the installer | **PROVEN.** The installer overwrites the DLL while Lidarr runs, and the new build runs after restart with the Connect settings kept. |
| Rollback | **PROVEN.** Reinstalling the older release (when it is the newest published) works. Manual DLL replacement is documented. |
| Uninstall through stock | **PROVEN.** Removes exactly `plugins/GQuX/lidarr-plugin-crescendarr`. After restart no Crescendarr code runs, the plugin list is empty, the Connect API answers with no orphan entry, and Lidarr is healthy. `crescendarr-audit/` is left in place (documented). |
| **Library, lyrics, DB rows, settings, download sources** | **PROVEN for Crescendarr.** Snapshots before and after every step showed no change in any step where stock had nothing to do. The one change is listed below. |

### The one library change, attributed to stock

With CDH on, **stock Lidarr imported the good SABnzbd job itself.** Crescendarr had previewed track 2 as **Keep** (the existing FLAC is better). Stock's own History then shows:
- `TrackFileDeleted` for `02 - Synthetic Track 2.flac` and for `01 - Synthetic Track 1.mp3` (twice);
- `TrackFileImported` for tracks 1 and 3;
- `DownloadImported`.

Both lyric files disappeared with it. Every changed path is covered by those stock events. The audit build contains no write code (scan), and it left no file outside `crescendarr-audit/`.

**This is the data loss Crescendarr exists to prevent, caused by stock Lidarr, not by Crescendarr.** The release notes say so plainly.

### Stock behaviour recorded

- A bare `Artist - Album` job title that Lidarr did not grab gets stock "Artist name mismatch, automatic import is not possible". A release-style title is imported by stock CDH.
- The wrong edition gets `AlbumImportIncomplete` and "Album release not requested". Stock does not import it.

## Defects found and fixed in this pass

1. **Wrong install folder in the operator README.** It said `plugins/GQuX/Crescendarr/`. Stock **Uninstall** removes `plugins/GQuX/lidarr-plugin-crescendarr/`, so a manual install would have survived uninstall and could load the plugin twice next to a native install. The README and the M25 lifecycle lane now use the installer's folder.
2. **The M22 restart-lane flake, root-caused.** It was a harness race (SABnzbd publishes `Completed` before filling in `storage`), now fixed. See the M22 doc.

## Release checklist

| | Item | Status |
|---|---|---|
| ✔ | Lidarr source contract (3.1.6.5079 tag `da7b4df`): asset name, tag-based version parsing, `Minimum Lidarr Version` 4-part regex, folder `plugins/<owner>/<repo>`, owner/name matching for uninstall | Verified against source and exercised end to end via the mock |
| ✔ | Asset contains only the scanned audit DLL; reproducible zip; scan rejects the writer build | M25 run on this PR's head is green |
| ✔ | LinuxServer.io, real SABnzbd, kernel CIFS, CDH on, real installer, restarts, uninstall | Run 45261466845 |
| ✔ | M17–M24 and specs still green on this PR's head | See PR #68 |
| ☐ | **Build the release asset from the exact commit that will be tagged.** The informational version embeds the commit, so bytes differ per commit. After merging, re-run M25 (and M27) on that `main` commit, and upload *that* run's `Crescendarr-v0.25.0-audit-beta.1.net8.0.zip` and `.sha256`. Never upload a local build. | Owner |
| ☐ | Create tag `v0.25.0-audit-beta.1` on that commit; `target_commitish: main`; `draft: false`; `prerelease: true`; body from `docs/release/v0.25.0-audit-beta.1.md`, which includes `Minimum Lidarr Version: 3.1.6.5079` | **Owner approval. Not done.** |
| ☐ | First post-publication smoke test: install from `https://github.com/GQuX/lidarr-plugin-crescendarr` through the real GitHub API in a disposable LinuxServer.io container | **NOT TESTED** (needs the published release) |

## Not tested, and not claimed

- Real `api.github.com` and GitHub's CDN download (mocked here; differences are possible for rate limits, redirects and `published_at` ordering).
- Lidarr versions other than 3.1.6.5079; LinuxServer.io tags other than `ls49`; non-Docker installs on other distributions.
- Windows, macOS and ARM images.
- Real music libraries or real NAS hardware.
- Torrent clients; Lidarr remote path mappings with differing container and host paths (the paths here are identical).
- Physical power loss.
- Many-thousand-album libraries (performance).
- Version suffix ordering beyond `audit-beta.9`: stock compares suffixes as text, so `audit-beta.10` would sort below `audit-beta.2`. Numbering must avoid this.
