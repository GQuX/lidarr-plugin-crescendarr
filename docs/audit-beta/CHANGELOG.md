# Crescendarr change log (operator-facing)

## 0.25.0-audit-beta.1 (release candidate, unpublished)

First **audit-only** build: it previews selective imports and cannot write to the library.

**New**
- Previews, per track, whether a completed download would **Upgrade**, **Keep** (your better file stays), **Add** a missing track, or needs **Review**. Each row shows the current and incoming quality and whether the existing lyric is kept.
- Observes only the Lidarr download clients you select, with ordinary names, through Lidarr's own client integrations. No downloader credentials or APIs are used directly.
- Refuses download folders outside your allowed folders, reached through symbolic links, or claimed by two clients.
- Never mistakes an unreadable library (unmounted share, no permission) for missing tracks.
- Reports through System > Status, `crescendarr-audit/report.md` and `report.json`, plus a redacted diagnostics file you can share.
- Re-evaluates each download when your library, quality profile or monitored edition changes (M24), without repeating unchanged log lines.

**Safety**
- No importer, recovery, salvage or approval code is compiled into this build. A metadata/IL scan in CI rejects any build that could mutate the library.
- Settings default to **Disabled**. Only *Disabled* and *Audit only* exist. A mode injected through the database is rejected.
- Files are written only under `<AppData>/crescendarr-audit/`.

**Known limitations:** see `README-AUDIT-BETA.md`.
