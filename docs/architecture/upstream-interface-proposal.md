> **Stable target update:** Evaluate against Lidarr v3.1.6.5079 before proposing changes; source includes plugin loading but there is no proven supported pre-import hook.

# Optional Lidarr upstream extension: pre-import planning and completion protocol

**Status: proposed / unsubmitted / NOT an existing Lidarr API.**

## Problem

The current automatic importer operates at album scope and may remove old files before selection; zero-improvement and intentionally partial imports cannot be represented safely by filtering `ImportResult` alone. Edition switching and sidecar cleanup are independent hazards.

## Proposed minimal host-owned contract

```csharp
// Illustrative pseudocode only. Neither the interface nor these host types exist today.
public interface IOptionalTrackImportPlanner
{
    ImportPlanResult Plan(ImportCandidatesSnapshot download,
                          CurrentAlbumSnapshot album,
                          HostQualityPolicy quality);
}

public enum ImportPlanResultState { UseStockBehavior, HoldForReview, NoImprovement, SelectedSubset }
public enum ImportSettlement { FullyApplied, PartiallyApplied, NoImprovement, RequiresReview, RecoveryRequired }
```

**Host requirements:** a planner runs *before* `replaceExisting` deletion and before permissive album-level rejections; core validates selected files against actual profile/edition/recording IDs; core owns atomic-as-possible staging, durable rollback, link updates, monitored release lock, `.lrc` source disposition, queue/history settlement and retries. A missing plugin behaves identically to stock Lidarr. No separate service or additional plugin required.

## Compatibility acceptance

- Opt-in interface, explicit supported API and version compatibility matrix.
- No false positive `Imported` for subset-only results and no indefinite pending for a verified no-op.
- No automatic edition switch, no album-wide existing-track deletion, no `.lrc` cleanup before retention is verified.
- Rollback and recovery semantics documented for SMB/CIFS and cross-volume moves.
- Good errors for unsupported import source or quality comparisons.

Do not submit upstream until the disposable host experiment validates that this cannot be done safely within supported extension points. Consult Lidarr PR #6166 before proposing overlapping changes.
