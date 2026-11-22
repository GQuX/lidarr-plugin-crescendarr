# Disposable host experiment: protocol, not a production installation guide

**STOP:** Do not use the actual Lidarr database, its config directory, its download client, any NAS shares, existing SMB/CIFS mounts, or personal music. A copy of your production DB still contains private data and may trigger unexpected integrations. Start with a **new empty database** and **fake music**.

## Before building the test probe

1. Record the isolated Lidarr binary version and SHA, `dotnet --info`, the original DI behavior and plugin list.
2. Verify the disposable host has **no download clients configured**, no access to production music paths, no NAS mount, no real library database and no credentials. Ideally use an isolated container or VM with no access to the NAS network.
3. Use an empty, dedicated local path, e.g. `/tmp/crescendarr-disposable-fixture-<unique-id>`, not a symlink or bind mount, with a marker text file named `.crescendarr-disposable-host` containing precisely `CRESCENDARR_DISPOSABLE_HOST_V1`.
4. Only after independent inspection, set the host environment variables:

   ```text
   CRESCENDARR_PROBE_OPT_IN=DISPOSABLE_SYNTHETIC_ONLY
   CRESCENDARR_PROBE_SYNTHETIC_ROOT=/absolute/path/to/empty/synthetic/root
   ```

5. Copy/build the test-only assembly from source **solely into this separate host**. The assembly has no release/install package and should never be passed to Lidarr's production plugin installer.

## Building on a computer with the .NET 8 SDK (NOT done here)

```sh
dotnet build src/Crescendarr.Lidarr.Probe/Crescendarr.Lidarr.Probe.csproj \
  -c Debug \
  -p:CrescendarrDisposableHostBuild=true \
  -p:LidarrBinDir=/absolute/path/to/disposable/lidarr/bin
```

Inspect the host references and compilation diagnostics. This is a **proposed command**, not a verified working build instruction on the user's environment.

## Test sequence

- Snapshot the disposable DB and every test file's hash/identity before launch.
- Start host without probe. Record normal service resolution, event logs and healthy startup.
- Stop host, enable only test probe, restart. Record the constructor-selection line, any DI exception and exactly-once method interception for **synthetic, nonimportable fixture paths**.
- Because this probe deliberately returns no `ImportResult`s, the host may still mark a download incomplete or change its queue/history state. Inspect those changes; do not claim it is a genuine safe DryRun.
- Confirm no plugin-initiated filesystem writes, no default-import delegation and no unexpected cleanup. Check DB and stage snapshots, not just the log.
- Uninstall test assembly from disposable host, restart, and verify stock behavior and clean unload.
- Do not test successful media imports through this probe. A future separate experiment will require a proven coordinator and fully synthetic copies.

## Probe's limited safety model

The probe constructor refuses to resolve unless the opt-in and local marker both exist. For method calls, it checks that the path is lexically contained under the synthetic root. **Lexical containment is not a symlink/bind-mount/remote-mount security boundary**; inspect the host filesystem and network isolation first. The returned empty result can still influence Lidarr's download state, so treat the entire host as disposable.

## Verdict labels

- `NOT_RUN` (current status)
- `PROBE_FAILED` (startup/registration/interception/delegation graph failed)
- `EXPERIMENTAL_INTERCEPTION_ONLY` (selected and nonrecursive, but unsupported API and incomplete lifecycle)
- `SUPPORTED_SEAM_FOUND` (requires independent supported hook validation, **not** just a probe log)
- `UPSTREAM_EXTENSION_REQUIRED` (no safe stock-host route)

See `docs/compatibility/m1-probe-report.template.json` for an evidence checklist.
