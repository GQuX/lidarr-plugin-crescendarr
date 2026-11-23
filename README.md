# Crescendarr

**Selective, quality-aware track upgrades for [Lidarr](https://github.com/Lidarr/Lidarr).**

Crescendarr is a single additive Lidarr plugin designed to improve individual tracks from completed album downloads, rather than replacing an entire album and discarding files you already prefer. It uses Lidarr's matching and quality profiles, respects the monitored MusicBrainz edition, and treats existing audio and `.lrc` lyrics as assets to protect.

## Current status

| Build | Status | What it can do |
| --- | --- | --- |
| **Audit-only beta** | **Built and CI-tested; not published** | Preview upgrades, files to keep, missing tracks and reasons for review. Cannot import or change library media, lyric links or Lidarr release records. |
| **Write-enabled importer** | **Disposable CI only; production NO-GO** | Selective importing and recovery have passed extensive synthetic tests, but physical power loss, non-MP3 payload integrity and stock Lidarr SMB rescan behavior remain release blockers. |

**There is no public release or supported production installer yet.** The next step is [GitHub URL installation (M26)](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/65). Until that is implemented and tested, **pasting this repository URL into Lidarr's Plugins page will not install Crescendarr**.

The audit-only candidate is available as a GitHub Actions artifact for controlled testing, **not** as a published GitHub release. A GitHub-installer-compatible audit ZIP is being prepared, but it cannot be installed by URL until an approved pre-release is published. See [audit beta documentation](docs/audit-beta/README-AUDIT-BETA.md), [release evidence](docs/release-gates/m25-audit-beta.md) and [native installer contract](docs/release-gates/m26-native-installer.md).

## What would it do?

For each track in a completed album download, the planner can report:

| Preview | Meaning |
| --- | --- |
| **Upgrade** | The incoming track is better under the artist's Lidarr quality profile. |
| **Keep** | The existing file is better or equal, so it should not be replaced. |
| **Add** | The track is missing from the library. |
| **Review** | The edition, identity, quality or file ownership is ambiguous, so no automatic change should be made. |

The audit build produces a readable `report.md`, machine-readable `report.json` and redacted diagnostics under `<Lidarr AppData>/crescendarr-audit/`. It starts **Disabled**. Users explicitly select which configured Lidarr download clients and completed-download folders it may inspect.

**Read-only means Crescendarr itself does not write to the music library.** Lidarr's own Completed Download Handling, rescans and other scheduled tasks can still change files independently.

## Compatibility and boundaries

- **Tested host:** unmodified Lidarr **3.1.6.5079**, Linux / .NET 8. Other versions and platforms have not been validated for this beta.
- **One plugin:** no Lidarr fork, companion daemon, torrent client, or direct SABnzbd API integration. Downloader access uses Lidarr's configured clients.
- **Safeguards:** edition matching, better-file preservation, global shared-file checks, lyric backups and journaled recovery are exercised by the disposable writer integration tests. These tests **do not** authorize real-library writes.
- **Outstanding limits:** the audit build still needs field verification with a real SABnzbd client and kernel-mounted SMB library; the write-enabled build remains blocked for production.

## Documentation

Start with the **[documentation guide](docs/README.md)** for setup, release evidence, architecture, development tests and historical research.

- [Audit-only beta usage and limitations](docs/audit-beta/README-AUDIT-BETA.md)
- [Audit beta release gate and proof](docs/release-gates/m25-audit-beta.md)
- [Kernel CIFS/SMB durability evidence](docs/release-gates/m23-cifs-durability.md)
- [Development history and findings](FEASIBILITY.md)

## Development

The main code is in `src/Crescendarr.Core/` (pure planner and registry policy), `src/Crescendarr.Lidarr.AuditPlugin/` (read-only beta flavor) and `src/Crescendarr.Lidarr.Plugin/` (CI-gated experimental importer). Synthetic integration harnesses and safety tests live in `tests/` and `tools/experiments/`.

Crescendarr is licensed under [0BSD](LICENSE). Contributions and issue reports are welcome, especially reproducible compatibility and safety cases.
