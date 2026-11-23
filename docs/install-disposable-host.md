# Installing Crescendarr on a DISPOSABLE stock Lidarr host

**Not a production release. Production write-enabled is NO-GO. Do not install on your real library or NAS.**

## Build

```
python3 tools/package/build_plugin.py <lidarr-3.1.6.5079-bin-dir> <out-dir>
```

The build is deterministic: CI builds it twice and requires identical bytes. The output is:
- `Lidarr.Plugin.Crescendarr.dll`
- `crescendarr-plugin-<version>-lidarr-3.1.6.5079.zip`, containing the dll, `SHA256SUMS` and `INSTALL.txt`
- `<zip>.sha256`

## Install, upgrade, rollback, uninstall

The workflow below is exercised by the M21 `install-cycle` lane.

1. **Verify:** `sha256sum -c <zip>.sha256`, then inside the zip `sha256sum -c SHA256SUMS`.
2. **Install:** stop Lidarr, copy the dll to `<lidarr-data>/plugins/GQuX/Crescendarr/`, then start Lidarr.
   - No Lidarr setting changes.
   - The plugin logs `CRESCENDARR INERT` unless the disposable opt-in is present.
3. **Configure:** Settings > Connect > Crescendarr. **Mode** defaults to **Audit only**. See [`operator-guide.md`](operator-guide.md).
4. **Upgrade:** stop Lidarr, replace the dll (after verifying it), then start Lidarr. Recovery runs first; journals, backups and the download registry are kept.
5. **Rollback:** put the previous dll back the same way.
6. **Uninstall:** remove the Connect entry, stop Lidarr, delete the plugin folder, then start Lidarr. Settings stay unchanged and the Connect provider disappears.
   - **Do not delete `crescendarr/ops`.** It can hold the only copy of recycled lyrics.
