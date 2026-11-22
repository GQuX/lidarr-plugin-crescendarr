# P0: album-wide lyric and quarantined-original scan controls (test-only)

**Status:** FIRST TWO CONTROLS VERIFIED, test-only. Source-pinned Linux native CI [45069316595](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45069316595) passed **67/67** main native tests, plus **1/1** checkpoint prepare and **1/1** separate-process recovery; pure planner [45069316627](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45069316627) passed. **Production write-enabled gate: NO-GO.**

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

## Observations from the verified native run

- **Album-wide detach:** The stock scanner still re-imported every album file with fresh TrackFile identities, but **none of the physical lyrics were recycled** after all registered lyric rows were detached. Every one of twelve physical `.lrc` files and independent backup copies retained its original bytes; a test-only explicit `Upsert` with new lyric IDs and fresh TrackFile IDs rebuilt all twelve associations with verified artist-relative paths. Log marker: `P0_ALBUM_LYRIC_DETACH`. This does **not** verify durable journaling or safe intermediate states.
- **Quarantine while the plugin is absent:** After a real sixth-transfer fault with synchronous stock deletion handlers, two repeated native scans observed only already-linked library files; neither created new import decisions or recycled the protected FLAC's still-registered lyric. The original bytes persisted in a stock-excluded hidden directory, but track six remained unlinked. Log marker: `P0_QUARANTINE_SAFE`.
- **Premature visible promotion:** A move of the hidden original to the final visible path **before** a valid fresh row/link triggered exactly the destructive tagged-audio whole-album rescan established in M14; the protected lyric was physically recycled and DB associations changed. Log marker: `P0_QUARANTINE_UNSAFE_PROMOTION`. This establishes that the quarantine trick on its own is **not a safe recovery protocol**.

Next proof needed before any unattended write-enable: a crash or plugin absence midway through **partial lyric detachment**, verified recovery from independent durable backups, and an ordering mechanism (or fail-closed guard) that prevents a stock scan between quarantine promotion and authoritative file linkage. No such atomicity has been demonstrated.

## Decision criteria

A green test suite can establish how these specific *synthetic* controls behave. Neither test authorizes an unattended importer; a future protocol must protect every album sidecar and the unselected superior track across plugin absence, native scan and crash points, and also satisfy M15's **cross-album reference** contract.

If either experiment fails, preserve the red evidence and investigate it. Do not weaken the assertions or turn a destructive safety outcome into a claim of production readiness.
