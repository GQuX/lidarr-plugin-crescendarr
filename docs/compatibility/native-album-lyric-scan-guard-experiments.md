# P0: album-wide lyric and quarantined-original scan controls (test-only)

**Status:** EXPERIMENTAL / UNVERIFIED on this branch until source-pinned native CI runs. **Production write-enabled gate: NO-GO.**

Refs: [issue #41](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/41), [M14 PR #39](https://github.com/GQuX/lidarr-plugin-crescendarr/pull/39), [ADR-026](../architecture/adr-026-crash-safe-operation-journal.md). Unmodified pinned Lidarr v3.1.6.5079 with migrated SQLite and runtime-generated tagged silent audio on a disposable runner-local filesystem only.

## What M14 established

A fully registered 12-track album scanned by stock `DiskScanService` produced zero import decisions and kept every registered lyric. A single unregistered tagged audio file in the album caused a populated scan that re-imported all existing album tracks under fresh `TrackFile` IDs and physically recycled every **still-registered** `.lrc`, including the lyric for the protected higher-quality FLAC. Stock event subscribers can detach an orphaned `TrackFileId` to zero after the sixth transfer failure. None of those native observations establishes a safe plugin protocol.

## New P0 hypotheses and test design

### Control A: all-album registered lyrics detached before the unknown-file rescan

- Build the same valid 12-track local fixture with one unregistered sixth audio path. Every available registered `.lrc` has a host-relative path matching its real physical path.
- Independently back up **all 12 physical lyrics**, including the protected FLAC's lyric, verify each backup byte-for-byte, then unlink all 11 registered lyric rows using the real stock DB-only `LyricFileService.Delete`. Do **not** unlink audio files, make edits to Lidarr source, or use a downloader.
- Invoke the actual tagged stock `DiskScanService` and native import/event path. Assert the full audio re-import still happens, but that every physical lyric and independent backup survives intact; the scanner must not silently invent/misbind lyric rows.
- In a controlled test-only post-scan operation, insert **fresh** lyric rows against the newly read `TrackFileId` for each recording and re-verify authoritative artist-root/relative paths and physical lyric bytes.
- **Not proven:** a crash during the album-wide detach, durable backup storage, an atomic host snapshot, an idempotent production journal, a complete real daemon subscriber graph, or an unattended safe commit. The interim absence of all lyric DB rows is itself a failure state requiring recovery.

### Control B: hidden original quarantine before a pending fresh-row repair

- Run the real sixth native incoming-transfer fault, with stock delete-event subscribers. Target-track lyrics are already DB-detached; the higher-quality protected FLAC lyric remains registered.
- Leave the deleted original library path **missing**; keep its verified audio backup in a folder matching stock `DiskScanService.ExcludedSubFoldersRegex`, under disposable local test storage.
- Invoke the same native scanner twice without any Crescendarr action. Assert it makes no unknown-file import decisions, never re-links the orphan, does not replace protected file IDs, and does not recycle the protected lyric. The sixth track is **not yet recovered**.
- Deliberately promote the original into the visible library before creating/linking an authoritative `TrackFile` row. Invoke the scan again. Assert the existing M14 harmful full-album re-import and protected lyric recycle reproduce, proving that **quarantine alone is not an atomic finalization protocol**.
- **Not proven:** an atomic way to pair path promotion with a new DB row under stock Lidarr locks, scanner exclusion of every possible backup/temporary folder, hostile pathname races, or durability on SMB.

## Decision criteria

A green test suite can establish how these specific *synthetic* controls behave. Neither test authorizes an unattended importer; a future protocol must protect every album sidecar and the unselected superior track across plugin absence, native scan and crash points, and also satisfy M15's **cross-album reference** contract.

If either experiment fails, preserve the red evidence and investigate it. Do not weaken the assertions or turn a destructive safety outcome into a claim of production readiness.
