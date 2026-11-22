# M16: stock command-queue disk-access serialization for an additive plugin (issue #44)

**Status: DRAFT, test-only. Production write-enabled stays NO-GO.**

**Verified on** disposable CI run [45084467908](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45084467908) at `23b405a` (workflow `command-queue.yml`):
- the queue experiment passed every check;
- the guard-negative run passed.

## Setup (what is real)

**Real:**
- The **official, SHA-256-pinned Lidarr v3.1.6.5079 Linux binary** (`fed16c58…`), unmodified.
- Each boot is a real daemon process with a fresh, empty data folder under `RUNNER_TEMP`, bound to `127.0.0.1`.
- The stock DryIoc container, `KnownTypes`, `CommandQueueManager`, `CommandQueue`, the three-thread `CommandExecutor`, the HTTP API, `DiskScanService`, and its SQLite `Commands` table.

**Plugin:** exactly one additive test plugin, `src/Crescendarr.Lidarr.QueueProbe`, loaded by the stock loader from `data/plugins/GQuX/CrescendarrQueueProbe/Lidarr.Plugin.Crescendarr.QueueProbe.dll`. It does nothing unless `CRESCENDARR_QUEUE_PROBE_OPT_IN=DISPOSABLE_SYNTHETIC_ONLY` is set and a marker file exists. It is **read-only**: it blocks on test-owned barrier files, logs, and (in the negative control only) calls the stock scanner. It writes no music, database rows, config or host files. It contains:
- `CrescendarrQueueProbeCommand`, with `RequiresDiskAccess=true`;
- its `IExecute<T>`;
- a negative-control `CrescendarrDirectScanProbeCommand`, with `RequiresDiskAccess=false`, that calls `IDiskScanService.Scan` directly;
- an `IHandle<CommandUpdatedEvent>` that holds a **stock** `RescanFolders` in its Started state;
- an `IHandle<ApplicationStartedEvent>` that reports registration and pushes one command through `IManageCommandQueue`.

**Synthetic data:** one root folder containing a single text file and no audio, so there are no metadata lookups.

**Harness:** `tools/experiments/run_disposable_command_queue.py`. Every wait has a hard deadline. Ordering is proven from the stock `Commands` table timestamps, which are read from a **copy** of the database. The observation windows are extra checks, not the proof.

**How the stock scan is held:** `CommandExecutor.ExecuteCommand` calls `CommandQueueManager.Start` and then publishes `CommandUpdatedEvent` **synchronously** before calling the handler. A plugin subscriber can therefore keep a stock `RescanFolders` Started, and its worker thread busy, without touching the scan. The scan body runs only after the barrier is released.

## Results

| Question | Observed (run 45084467908) |
|---|---|
| Are plugin `Command` types discovered? | **Yes.** Stock `KnownTypes` lists `CrescendarrDirectScanProbeCommand` and `CrescendarrQueueProbeCommand` (`AutoAddServices` adds every loaded plugin assembly's exported types). |
| Is the plugin `IExecute<T>` resolved? | **Yes.** `IServiceFactory.Build(IExecute<CrescendarrQueueProbeCommand>)` returns the plugin executor. The stock `(dynamic)` dispatch in `CommandExecutor` crossed the plugin load context without error. |
| Can the plugin push through `IManageCommandQueue`? | **Yes.** A startup push (id 1) ran once on a stock executor thread and Completed. The API `POST /api/v1/command` path also works and binds the plugin body (`label`). |
| Forward: plugin disk-access command Started first | A stock `RescanFolders` stayed **Queued** for the whole hold. It started at `…36.9184969Z`, after the plugin command ended at `…36.9174811Z`. |
| Reverse: stock `RescanFolders` Started first | The plugin command stayed **Queued**. It started at `…41.1791452Z`, after the scan ended at `…41.1783415Z`. The plugin executor saw no other disk-access command Started. |
| Two disk-access plugin commands | The second waited behind the first. |
| Duplicate scheduling | Stock de-duplicated an identical queued `RescanFolders` (same id) and an identical queued plugin command (same label, same id). A different label is a separate command. |
| Negative control: direct `IDiskScanService.Scan` | **Not serialized.** A non-disk plugin command ran the stock scanner (`DiskScanService: Scanning …/synthetic-root`, logged between the probe's BEGIN and END markers). At that moment the plugin disk-access command was still **Started** and a stock `RescanFolders` was still **Queued**. |
| Failure path | A deliberate `CommandFailedException` gave Status **Failed**, and the queue moved on. |
| No overlap | For every pair of disk-access commands that finished in boot 1 (stock and plugin), the `[StartedAt, EndedAt]` intervals don't overlap. |
| Adding a root folder | It queued a stock `RescanFolders` (id 2) by itself. That scan was serialized like the others. |
| **SIGKILL restart**, with one plugin command Started and one plugin plus one stock disk-access command Queued | At the kill, the database held Started / Queued / Queued, including the plugin label. On the second boot, with the same data and the plugin still installed: |
| …Started plugin command | **Orphaned** by stock `OrphanStarted`, and **never re-executed**. |
| …Queued plugin command | **Rehydrated as stock `UnknownCommand`.** `CommandConverter` resolves the type only in `Lidarr.Core` (the probe confirmed in-process that `coreAssemblyResolvesPersistedName=False`). The stock `UnknownCommandExecutor` **skipped** it (message "Skipped", status Completed). It reported `requiresDiskAccess=false`, and the plugin executor **never ran**. It even overlapped the requeued stock scan: unknown `…43.3798–43.3938Z` vs `RescanFolders` `…43.3802–43.4348Z`. |
| …Queued stock `RescanFolders` | Requeued and ran normally. |
| Guard negative (plugin installed, no opt-in) | It logged DISABLED. A pushed probe command **Failed** without reaching its body. |

## What this does and does not protect

**Protected:** a plugin command with `RequiresDiskAccess=true` is mutually exclusive *in live scheduling* with every other **queued** disk-access command, in both directions. The stock commands with `RequiresDiskAccess => true` in v3.1.6.5079 are:
- `RescanFolders`, `DownloadedAlbumsScan` and `ProcessMonitoredDownloads`;
- `ManualImport`;
- `RenameFiles`, `RenameArtist`, `RetagFiles` and `RetagArtist`;
- `MoveArtist` and `BulkMoveArtist`.

Stock code that wants a rescan generally **pushes** `RescanFoldersCommand` rather than calling the scanner. That covers the root-folder watcher, `RefreshArtistService`, `RefreshAlbumService` and `AlbumEditedService`. Completed-download import runs inside `ProcessMonitoredDownloads`.

**Not protected** (source reading for v3.1.6.5079 plus the negative control):
- **Direct service calls.** Any code, including our own plugin or another plugin, that calls `IDiskScanService`, `IImportApprovedTracks`, `IDownloadedTracksImportService`, `IMediaFileService` or a repository outside a disk-access command bypasses the queue entirely. This was demonstrated for `Scan`.
- **Read-path callers**, such as the API `FileSystemController`, the blackhole `ScanWatchFolder` and `ExistingExtraFileService`, run outside the disk-access rule. They are read-mostly, but they are not serialized.
- **Event handlers** run on whatever thread publishes the event. Asynchronous `IHandleAsync` handlers run on the thread pool, outside the queue.
- **Commands that don't claim disk access** can run beside ours. The queue allows two of three workers to run concurrently. `IsExclusive`, `IsTypeExclusive` and `IsLongRunning` filtering is separate.
- **Anything outside the process:** NAS/SMB clients, users editing files, download clients writing into the library, other Lidarr instances, and backups or restores.
- **Before enqueue and after a crash.** A crash before the push leaves nothing queued. A crash while Started leaves the command **Orphaned, never resumed**.
- **Restart.** A queued plugin command is **silently turned into a skipped no-op** that no longer claims disk access. The "lock" is not durable.
- **Plugin absent** (unloaded, failed to load, or the host fell back to no plugins): the same `UnknownCommand` path applies, and nothing runs.
- **Duplicate startup push (source, not observed).** `CommandQueueManager` also handles `ApplicationStartedEvent` (`OrphanStarted` plus `Requeue`). A plugin pushing during that same event could race the requeue. In this run the startup push executed **once**. Even so, the design must not push from `ApplicationStartedEvent` and assume exactly-once execution.

## Consequences for the per-track importer (summary; see ADR-027)

1. A write-enabled Crescendarr operation can run as **one plugin `IExecute<T>` with `RequiresDiskAccess=true`**. All of its host-visible steps then run while no stock rescan, import, rename, retag or move command can start. That closes the **queued-scan** interleaving characterized in #41 (M14 `ScanInterleavedInsideImporter`) for stock-queued activity only.
2. The operation must do all of its host mutation **inside that one command body**. It must never call stock import, scan or delete services from an event handler, a timer or a non-disk command.
3. The queue must not be treated as durable. On startup, recovery must come from the plugin's **own durable journal** (M13), never from the `Commands` table. Our own Orphaned or Skipped rows mean "the operation may be partially done": reconcile from fresh observation (PR #42 lanes D and R), and never by blindly re-pushing.
4. Recovery itself should run as a disk-access command pushed **after** startup, not inside the `ApplicationStartedEvent` handler. Until it completes, the plugin should block its own new operations. It cannot block stock scans that are already queued and run first; the PR #42 evidence shows those scans can recycle registered lyrics. Album-wide backups therefore stay mandatory.
5. Out-of-process actors (users, NAS/SMB clients, downloaders) are untouched by the queue. Fail-closed verification under the command (hash, identity and path re-checks immediately before each mutation) stays necessary, and unattended imports stay **NO-GO** until that residual window is accepted explicitly.

## Limits of this evidence

- One runner, one plugin, one synthetic root with no audio, and three worker threads.
- The ordering timestamps are stock `DateTime.UtcNow` values at sub-millisecond resolution. Equal timestamps would not prove ordering, but none occurred.
- Not exercised:
  - graceful shutdown while a command is Started (only SIGKILL was tested);
  - Lidarr's own update/restart path;
  - Postgres;
  - real downloads, real imports or real audio;
  - the Windows host;
  - SMB.
- The queue was observed through the API and the `Commands` table, not through internal locks.
