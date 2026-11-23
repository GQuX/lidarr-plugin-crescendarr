# Crescendarr documentation

This is the guide to the useful documents. The repository also contains detailed **historical investigations** from before the current one-plugin prototype existed. Those reports remain for traceability, but older "not implemented" or "unsafe to install" statements describe the state **at the time of each experiment**, not necessarily today's audit-only build.

## Start here

| Goal | Read |
| --- | --- |
| Understand the project and current release status | [Main README](../README.md) |
| Evaluate the audit-only beta | [Audit beta guide](audit-beta/README-AUDIT-BETA.md) |
| Verify the read-only guarantees, test coverage and limitations | [M25 acceptance matrix](release-gates/m25-audit-beta.md) |
| Understand what blocks real-library writes | [M23 CIFS/SMB durability evidence](release-gates/m23-cifs-durability.md) and [M22 real SABnzbd evidence](release-gates/m22-real-sabnzbd-evidence.md) |
| Follow GitHub URL installation work | [M26 installer issue](https://github.com/GQuX/lidarr-plugin-crescendarr/issues/65) |

## Engineering references

- **Design:** [architecture blueprint](architecture/blueprint.md), [stock-Lidarr plugin-only feasibility](architecture/stock-lidarr-plugin-only-feasibility-v0.1.md) and [command-queue serialization decision](architecture/adr-027-stock-command-queue-serialization.md).
- **Release and recovery evidence:** [M24 registry re-evaluation](release-gates/m24-registry-reevaluation.md), [M23 kernel CIFS](release-gates/m23-cifs-durability.md), [M22 real SABnzbd](release-gates/m22-real-sabnzbd-evidence.md) and [M21 compatibility](release-gates/m21-acceptance-matrix.md).
- **Code quality:** [post-M23 maintainability audit](reviews/post-m23-maintainability-audit.md).
- **Development lab:** [disposable-host protocol](safety/disposable-host.md) and [Portainer lab](portainer/lidarr-stable-lab.md). These describe isolated development environments, **not** instructions for installing a write-enabled plugin in production.

## Research archive

The `architecture/`, `compatibility/` and `reviews/` directories contain source inspections, experiments, false starts, and individual milestone reports. Keep these available for reproducibility; they are **not** the recommended reading path for someone who simply wants an audit preview.

[FEASIBILITY.md](../FEASIBILITY.md) is the chronological project investigation log. It includes many older observations and draft-PR references. The latest release-gate files above should take precedence when determining current supported behavior.

## Safety and contributions

The **audit-only** assembly has no importer or recovery writer commands and may only write its own diagnostics inside Lidarr AppData. The separate **write-enabled** assembly is a guarded CI experiment and is **not approved** for production.

When reporting a bug, share the audit build's `diagnostics-redacted.json` when possible. The local `report.md` can contain music titles and folder names; don't attach it publicly without reviewing it. Please do not post API keys, credentials or personal library contents in an issue.
