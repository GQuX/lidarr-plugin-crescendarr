# Stock Lidarr: completed download observed with native Completed Download Handling OFF

**Date:** 2026-11-22. **Experiment:** disposable in-process observer, not an importer.
**Host:** official stock Lidarr v3.1.6.5079 linux-core-x64, archive SHA-256 `fed16c58f5de797be955e830a3a93994f020c1d8d60f5a6cb061d7647d45b8bc`.
**CI:** [stock host integration run #45033044145](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45033044145) **passed**, including completed-item observation **and** a restart of the same host with the observer removed. The first observation-only run [#45032801185](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45032801185) passed separately.

## Setup, with no production systems

A GitHub Actions Ubuntu runner created a brand-new Lidarr configuration, SQLite database, isolated home, and loopback-only HTTP API (`127.0.0.1:18688`). It staged a single sentinel plus a tiny text-only `01-fake-lyrics.lrc` inside an isolated synthetic `watch/Crescendarr-Synthetic-Album/` path. It mounted **no NAS** and configured **no SABnzbd**, real album, artist, music file, download service, credentials, or production host.

The CI script [`run_disposable_configured_observer.py`](../../tools/experiments/run_disposable_configured_observer.py) reads the freshly generated API key only from its own ephemeral config and uses Lidarr's normal local HTTP API to:

1. Read `config/downloadclient`; change **only the disposable instance's** `enableCompletedDownloadHandling` to `false`; read back to prove this value persisted.
2. Retrieve the built-in **Usenet Blackhole** provider schema and register a client named `Crescendarr Synthetic Blackhole` with `nzbFolder` and `watchFolder` rooted under the synthetic CI sentinel.
3. Explicitly disable `removeCompletedDownloads` and `removeFailedDownloads` on this synthetic provider.
4. Wait for the stock Blackhole client's 30-second file-stability grace period; update the provider priority via Lidarr's normal API to trigger a second event and client read.

No extra server or direct SABnzbd connection is used. The real final plugin would use the operator's existing Lidarr-configured downloader; Blackhole is simply controlled test input.

## Real-host evidence

A non-installable, disposable-CI-only plugin [`ObserverStartupCheck.cs`](../../src/Crescendarr.Lidarr.ObserverProbe/ObserverStartupCheck.cs) handled stock `ProviderAddedEvent<IDownloadClient>` and `ProviderUpdatedEvent<IDownloadClient>`, resolved `IDownloadClientFactory`, and **read** `GetItems()` only from the exact named built-in Blackhole with a watch folder under its synthetic root. The plugin registers **no `IDownloadedTracksImportService`, no `IImportApprovedTracks` override, and no file/DB writer**.

From [successful CI #45032801185](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45032801185):

```text
CRESCENDARR_SYNTHETIC_CLIENT_ITEM phase=added CDH_ENABLED=False STATUS=Downloading ID_MATCH=True CAN_REMOVE=False
CRESCENDARR_SYNTHETIC_CLIENT_ITEM phase=updated CDH_ENABLED=False STATUS=Completed ID_MATCH=True CAN_REMOVE=False
DISPOSABLE_CDH_OFF_COMPLETED_OBSERVER_OK
```

The CI script also asserts that the fake `.lrc` bytes and temporary album inventory remained unchanged, the CDH configuration still read as OFF, and the test-only synthetic sentinel remained intact.

## Plugin absent: same disposable Lidarr restarted

After the first host shut down, the CI harness **removed the observer plugin directory** and restarted the exact same stock Lidarr appdata and built-in Usenet Blackhole provider, with no Crescendarr observer opt-in or assembly present.

The restart completed, and the harness verified:
- `enableCompletedDownloadHandling` remained **false** after restart, read back via stock API.
- The synthetic built-in Blackhole provider remained configured.
- The tiny staged `.lrc` was still byte-identical, and no other file appeared in its synthetic album directory.
- The plugin's startup marker was absent in the new host logs.

CI emitted `DISPOSABLE_CDH_OFF_PLUGIN_ABSENT_RESTART_OK` in [run #45033044145](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45033044145).

This establishes a **narrow fallback**: CDH OFF persisted across an ordinary uninstall/restart while the synthetic staging path survived. It does **not** simulate a plugin assembly-load exception, a real SABnzbd item, enabled manual imports or in-progress audio writes.

## What this proves

- A **real configured stock download client** inside an unmodified Lidarr runtime can expose a completed item to an **in-process additive plugin** while native Completed Download Handling is OFF.
- The plugin can inspect the item via Lidarr's own `IDownloadClientFactory` abstraction without bypassing Lidarr's configured providers or implementing a separate downloader API.
- The original stock importer is not replaced by Crescendarr in this experiment; no actual downloaded audio is imported.
- Native [24/24 CDH gate tests](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45030379118) separately established that stock import processing skips pending completed items when CDH OFF; [26/26 native client enumeration tests](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45031455402) separately established that stock queue filtering hides those completed items.

## What this does NOT prove

- An ordinary SABnzbd job has been seen, or that every client/provider will correctly expose completed items after network errors or restarts.
- That matching from a completed job to a MusicBrainz album and edition is correct, or that its downloaded audio has been safely imported.
- Actual queue settlement, clearing the downloader item, duplicate avoidance, retries, native history, lossless backup, and `.lrc` or album preservation.
- That all stock Lidarr **manual imports or rescans** become non-destructive with CDH OFF. They are **not** disabled by this setting.
- That an importer plugin is safe during startup failure; only the stock automatic CDH gate has been tested in native mocks.
- That a stock configuration remains fully safe on a real SMB share, after a power loss, or after a plugin version mismatch.

**Decision:** the *completed-download discovery seam* is technically feasible on the pinned stock host. A write-enabled per-track merge **remains NO-GO** until its host-native side effects, edition/lyric retention and failure recovery are tested with local synthetic media and database transactions.

Next: characterize a read-only queue inventory and restart-safe error/retry state machine using completed jobs from supported Lidarr clients. The host-wide disabled-CDH fallback is now tested for ordinary observer removal; failure during plugin construction, actual imports and manual processing remain untested. Preserve the single-plugin and stock-Lidarr requirement; no production installs, config updates or real media are authorized.
