# Stock Lidarr completed-client observation: in-process feasibility checkpoint

**Date:** 2026-11-22  
**Host:** Official Lidarr v3.1.6.5079, binary archive SHA-256 `fed16c58f5de797be955e830a3a93994f020c1d8d60f5a6cb061d7647d45b8bc`  
**Result:** Two separately verified tests, **not** a complete integrated fake-client download.

## Test A: real stock host loads a non-overriding read-only observer

Evidence: [disposable host CI #45031455434](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45031455434).

An isolated, non-installable `Lidarr.Plugin.Crescendarr.ObserverProbe.dll` was built against official pinned binaries and loaded from an ephemeral plugin directory under a new empty configuration. It registered `IHandle<ApplicationStartedEvent>` and resolved stock `IDownloadClientFactory` and `IConfigService` without replacing any importer interface. At startup the probe called only `DownloadHandlingEnabled()` to count configured providers (zero) and read the CDH setting (stock default true):

```text
CRESCENDARR_OBSERVER_READY CDH_ENABLED=True CLIENT_COUNT=0 IMPORT_OVERRIDE=NONE
DISPOSABLE_OBSERVER_OK: stock Lidarr loaded observer; factory callable; no configured clients
```

The experiment uses no production secrets, NAS mounts, downloader, music, lyrics, database or custom host binaries. The plugin has **no import, cleanup, download-client polling or write behavior**. The guard requires an ephemeral CI runner, an explicit opt-in and a disposable filesystem marker. The workflow never produces an installable user release.

**Not proved:** CDH off inside this real disposable host, observing a *configured* client, seeing an actual completed item, status/retry behavior, compatibility with multiple plugins or future Lidarr versions.

## Test B: stock native monitor enumerates a mocked completed client with CDH off

Evidence: [native NUnit CI #45031455402](https://github.com/GQuX/lidarr-plugin-crescendarr/actions/runs/45031455402), **26/26** tests passed, including two new cases from [`CrescendarrCompletedClientEnumerationFixture.cs`](../../tests/LidarrNative/CrescendarrCompletedClientEnumerationFixture.cs).

The fixture instantiates **actual stock `DownloadMonitoringService`**, with a mocked Lidarr-configured `IDownloadClientFactory` returning an `IDownloadClient` whose `GetItems()` returns one synthetic completed album. The `TrackedDownload` and all external services are mocks.

| CDH setting | Native service called `GetItems()` and `TrackDownload`? | Items passed to stock `UpdateTrackable` |
|---|---|---|
| **OFF** | Yes, the completed item was read and tracked internally | **0**, completed item excluded from stock queue |
| **ON** | Yes, same item read and tracked | **1**, completed item retained in stock queue |

Source: [`DownloadMonitoringService.Refresh()` and `DownloadIsTrackable`](https://github.com/Lidarr/Lidarr/blob/v3.1.6.5079/src/NzbDrone.Core/Download/TrackedDownloads/DownloadMonitoringService.cs#L60-L153). This is a source-native **mocked-client** behavior test, not evidence that the observer plugin received a live item.

Earlier [stock CDH boundary tests](stock-lidarr-cdh-off-boundary.md) establish that CDH OFF blocks normal automatic importing of pending completed items, but **does not** disable removal notifications for jobs already marked imported and removable. No universal cleanup guarantee should be inferred.

## Narrow result and next gate

An **additive read-only plugin can resolve the configured-client factory** on a real pinned stock Lidarr host, and the **native monitor can still read a completed client item** with CDH OFF in separate mocked-client NUnit tests.

**The combination is NOT yet demonstrated.** Next: register a disposable fake client/provider with a synthetic completed item in the running stock host, turn native CDH OFF in the *disposable configuration only*, verify plugin observation of that item, and assert no importer or cleanup methods ran. No real files or real SABnzbd should be involved.

Only after that should we tackle safe track-level file updates, protection of `.lrc`, MusicBrainz release stability, queue settlement and actual crash/recovery. Stock Lidarr still pre-wipes albums when its normal automatic importer runs; **the plugin is not currently safe or installable for production**. Track under [issue #16](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/16).

Production Lidarr, its completed-download setting, NAS/SMB, Tubifarry and music remain unchanged.