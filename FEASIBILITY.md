# M17 feasibility: one additive plugin, unmodified Lidarr, selective per-track import

**Verdict for the defined vertical slice: PROVEN.** The full one-plugin, no-fork product is **not yet usable**: what remains is engineering (see the end), not a host blocker. **Production write-enabled stays NO-GO.**

**Scope:**
- **Host and plugin:** a disposable CI runner running the **official SHA-256-pinned Lidarr v3.1.6.5079 Linux binary**, unmodified, as a real daemon process, with **exactly one** Crescendarr plugin assembly installed.
- **Download path:** the stock Usenet Blackhole client, with Completed Download Handling OFF.
- **Data:** synthetic tagged audio (valid MP3/FLAC read by the stock tag reader) and a fresh SQLite database.

Issue [#46](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/46), draft PR #47, stacked on M16 PR #45.

**Evidence:** CI run [45089449771](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45089449771) (`b2a35b0`, workflow `m17-vertical-slice.yml`). All four real-daemon lanes passed:

| Lane | Checks |
|---|---|
| `three` | 29 |
| `twelve` | 77 |
| `three-crash` | 32 |
| `three-review` | 20 |

Every lane also uploaded its host logs, SQLite copies, journals and plans as artifacts.

## What happened inside the real stock daemon

**Setup (harness, not product).** Synthetic MusicBrainz IDs can't be fetched from the metadata server. So a **separate test-only seeder plugin** builds the catalog (artist, album, two editions, tracks) and the owned library through stock repositories and services, in a setup boot. It is then **removed before Crescendarr is installed** (the CI asserts this). Each Crescendarr boot has exactly one plugin.

**3-track album** (`three`):
- **Starting library:** track 1 MP3-192 + lyric; track 2 **FLAC + lyric (the superior original)**; track 3 missing.
- **Download:** three MP3-256 files.

| Track | Before (row, quality, file, lyric row) | After |
|---|---|---|
| 1 | TrackFile 1, MP3-192, `01 - Synthetic Track 1.mp3` `bab9d492…`, lyric 1 | **fresh TrackFile 3**, MP3-256, incoming bytes `3ec060dc…`, **same `.lrc` re-registered** as lyric 3 |
| 2 | TrackFile 2, **FLAC** `8e5f3c82…`, lyric 2 | **unchanged**: same row, bytes, lyric row |
| 3 | none | **fresh TrackFile 4**, MP3-256, incoming bytes `5f00c14d…` |

Other evidence for the 3-track run:
- The recycle bin holds only the replaced `01 … .mp3`; **no `.lrc` was recycled**.
- The monitored release is unchanged, and the alternate edition stays unmonitored.
- The completed download folder is byte-identical afterwards.

**12-track album** (`twelve`): 11 MP3-192 + lyrics and track 12 FLAC + lyric.
- **11 upgrades**, each with a fresh row, MP3-256 bytes and its original lyric re-registered.
- **Track 12** (FLAC row 12, `fa1c9295…`, lyric 12) is untouched.
- The recycle bin holds the 11 replaced MP3s and no lyrics.

**The pipeline, all inside the daemon:**
1. **Observation (read-only).** The stock `IDownloadClientFactory` gives the blackhole client's `GetItems()`, which reports the item Completed after the stock 30 s grace period.
2. **Plan.** Stock `IMakeImportDecision` (real identification and all stock specifications, `NewDownload=true`) feeds the **pure `Crescendarr.Core.MergePlanner`**, which compiles into the plugin from its single source. Quality comes from the artist's real stock profile via `QualityModelComparer`. The planner chose Upgrade / KeepExisting / AddMissing per track.
3. **Writer.** `CrescendarrImportCommand` is **one plugin `IExecute<T>` with `RequiresDiskAccess=true`** on the stock command executor. Inside that one command it:
   - re-observes and re-plans, and requires the approved plan ID;
   - copies every candidate into operation-owned staging with hash verification (**the download itself is never touched**) and re-decides on the copies;
   - runs the M14 read-only **edition preflight**;
   - backs up **every album lyric, including the unselected superior track's**, with verification;
   - publishes the **M13 journal**;
   - lets the **M13 `Reconciler` choose every step** (backup → detach lyric → stock `IImportApprovedTracks.Import(…, replaceExisting:false, Move)` on the staged copy → relink lyric → complete), reconciling from a fresh host and filesystem snapshot before each one;
   - adds missing tracks with stock `Import`, then verifies album-wide lyric bytes and registrations;
   - removes its own staging, and keeps its backups and journal.
4. **Subsequent stock `RescanFolders`.** No change to any TrackFile, link or lyric row: no cascade.
5. **Repeat dispatch** of the same approved plan ID: refused with "plan no longer current"; nothing imported. Re-observing the same download gives **NoImprovement**.
6. **Wrong edition.** A download tagged with the album's *other* release gets stock "Album release not requested" and planner `EDITION_MISMATCH`. The result is NeedsReview, then refused, with **zero DB or byte changes**.
7. **Guard.** The same installation **without the opt-in** logs `INERT`, and its commands Fail with no changes. All writes are confined to the marked disposable root. Root folders, recycle bin and download folders are re-checked at **startup and in every command**, and CDH must be OFF.

**Restart and recovery** (`three-crash`, `three-review`):
1. A test-owned fault point pauses the plugin command right after the stock native upgrade, and the harness **SIGKILLs the daemon**.
2. On restart, stock **Orphans** the interrupted plugin command and never resumes it (as M16 predicted).
3. A **stock rescan runs before recovery** and changes nothing.
4. `CrescendarrRecoverCommand` (disk access) loads the journal. The **M13 Reconciler recognises "native upgrade observed complete" from fresh observation**, records it, relinks the lyric and completes. It then refills staging from the hash-verified intact download and adds the missing track. The final state is identical to the golden path.
5. **`three-review` adds one change:** before recovery, someone else's bytes are written into a bound `.lrc`. Recovery ends in **RequiresReview** (the "physical lyric bytes differ" blocker). The foreign bytes are **not overwritten**, no rows change, and the pending add is **not** imported. A second recovery is skipped: the review is **sticky**.

## Constraints and what was actually demonstrated

| Constraint | On the real stock daemon? |
|---|---|
| One additive plugin, no fork, binary patch or companion daemon | **Yes.** One assembly. Core, journal and preflight are compiled in from their source of truth. |
| Plugin `Command`/`IExecute`/`IHandle` through stock DI | **Yes** |
| All writes inside one stock `RequiresDiskAccess` command | **Yes.** Startup only observes. |
| Download observed through Lidarr's own client API, CDH OFF, no direct downloader API | **Yes** (Usenet Blackhole) |
| Per-track decision using the real quality profile and recording/release-track identity | **Yes** |
| Native importer, mover, recycler, TrackFile, track and lyric services on valid tagged audio | **Yes** |
| Superior original kept byte- and row-identical | **Yes** (3 and 12 tracks) |
| Every lyric preserved, including the unselected track's, with album-wide backups | **Yes** |
| Monitored MusicBrainz edition preserved; wrong edition refused with no change | **Yes** |
| Subsequent stock rescan is a no-op | **Yes** |
| No double import on repeat dispatch | **Yes** |
| Inert without opt-in | **Yes** |
| SIGKILL mid-operation → journal recovery, with a stock scan before recovery | **Yes**, for one fault point (after the native upgrade) |
| Unexplained change → sticky RequiresReview, unknown file not overwritten | **Yes** (lyric bytes) |
| Download source not deleted early | **Yes.** It is copied, never moved. |

## Stock limitations found (none blocks the one-plugin goal)

1. **`NewDownload=false` imports in place.** `ImportDecisionMaker.cs` sets `localTracks.ForEach(x => x.ExistingFile = !config.NewDownload)`. On the real daemon, an earlier recovery attempt that used `NewDownload=false` made stock register the staged file **where it was**, as a library TrackFile inside the plugin's staging folder. This was observed in runs 45087879746 and 45088204592 (commits `0375090` and `9ed764a`) and fixed in `983c68f`.
   - The plugin's post-import check caught it and failed closed.
   - Rule now: **only approved NEW-download decisions on the plugin's own staged copies** may reach the importer.
   - Recovery re-stages from the intact download instead.
2. **`Artist.RootFolderPath` is not persisted**, so it comes back null. Use `IRootFolderService.GetBestRootFolder(artist.Path)`.
3. **Plugin commands are not durable** (M16): Orphaned or rehydrated as `UnknownCommand` after a restart. Recovery authority is the plugin's own journal.
4. **The blackhole grace period is per process.** A completed folder reads "Downloading" again for 30 s after every daemon start.
5. **The download is never "settled" with CDH OFF.** Stock offers no API to mark a client item imported without CDH. With no grab history the item stays untracked (`queue` totalRecords 0, or "downloading" during the grace period). The plugin therefore never removes it: blackhole `RemoveItem` deletes data. Completed, NoImprovement and RequiresReview are recorded only in the plugin's `outcome.json`. **A real settlement policy is still missing.**
6. **Stock cutoff logic still sees an unmet album.** Kept FLAC plus MP3-256 is below the profile cutoff, so stock wanted/cutoff searches may keep grabbing. Per-track "satisfied" isn't expressible to stock.

## Not demonstrated (do not infer)

- **Real catalog data and real clients:** real MusicBrainz metadata and refresh (the seeder bypasses it); other download clients (SABnzbd/qBittorrent through Lidarr); any network downloader.
- **Library variations:** rename-enabled naming (destination ≠ original path); multi-disc or repeated-recording albums (the planner fails them closed); cover art, NFO and metadata extras; cross-album shared files on the daemon (the reconciler and planner enumerate global sharers, but no fixture shares one).
- **Fault coverage:** fault points other than "after the native upgrade" on the daemon (the rest are covered only by the M13 pure lab); a crash *inside* stock `Import` between the file move and the row insert, which leaves an unregistered audio file and, per M14, an album-wide lyric cascade on the next scan; Windows hosts; power loss; directory fsync; SMB/CIFS/NAS.
- **Concurrency outside the queue:** direct callers, other plugins, users and downloaders.

## Shortest path to a usable plugin

1. **Trigger and approval.** Replace the harness-pushed Plan/Import commands with a plugin-scheduled observe step that pushes our disk-access commands (never writing from handlers), plus an approval policy (auto-apply only `Ready` plans; everything else to review).
2. **Settlement.** Define a per-client policy for "Crescendarr finished with this download" (leave, tag or remove after a verified Completed outcome), and decide how to stop stock re-searching albums that Crescendarr considers satisfied.
3. **Broaden the real-daemon lanes:**
   - real-metadata artists (a recorded metadata fixture or an allowed network lane);
   - rename-enabled naming;
   - more fault points, including inside stock `Import`, using the album-wide lyric backups for automatic restore or review;
   - one non-blackhole client through Lidarr's client API.
4. **Close the open PR #42 review items.**
   - Artist-MBID revalidation is now per step in the executor.
   - Symlink rejection is now in the plugin: link-in-chain checks on every bound path.
   - Still open: repeated-recording identity, and a host-wide lock against out-of-queue writers (a production blocker that stays documented).
5. **Production gate.** SMB/NAS durability and power-loss behaviour stay out of scope and keep production **NO-GO** until they are characterized.
