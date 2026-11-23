// M19 (issue #50): explicit, bounded operator workflow through stock queued commands (RequiresDiskAccess).
//   CrescendarrOperatorStatus  read-only: every operation, its tracks, conflicts, hashes, rows, lyric and backup state
//   CrescendarrPropose         dry run against a FRESH snapshot; returns a token bound to operation + generation + action + snapshot
//   CrescendarrApprove         re-proposes from a fresh snapshot and applies ONLY when the typed token still matches
// Nothing here deletes an operation folder, a journal or a backup. "resolve" releases the album block without
// claiming import completion or download settlement, and only when the album is verifiably consistent.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crescendarr.Core.Journal;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Plugins.Crescendarr
{
    public sealed partial class CrescendarrImportExecutor
    {
        private static readonly string[] OperatorActions = { "register-placed", "link-placed-row", "salvage-lyrics", "resolve" };

        private sealed class Proposal
        {
            public bool Allowed;
            public string Token;
            public long Generation;
            public string Digest;
            public int? TrackId;
            public int? RowId;
            public List<string> Reasons = new List<string>();
            public List<string> Plan = new List<string>();
        }

        public void Execute(CrescendarrOperatorStatusCommand message)
        {
            var root = CrescendarrGuard.RequireRoot();
            CrescendarrGuard.RequireHostConfined(root, _rootFolders, _config);
            var ops = Path.Combine(root, "crescendarr", "ops");
            var rows = new List<object>();
            if (Directory.Exists(ops))
            {
                Disk.RequireConfined(root, ops, "operation folder");
                foreach (var opRoot in Directory.GetDirectories(ops).OrderBy(d => d, StringComparer.Ordinal))
                {
                    var context = new OperationContext { Root = root, OperationId = Path.GetFileName(opRoot), OperationRoot = opRoot };
                    var state = OperationState(opRoot);
                    var prepared = SafePrepare(context);
                    List<string> problems = null;
                    int salvageActions = 0;
                    if (prepared == null)
                    {
                        var host = HostSnapshot(context.Manifest, context.Artist.Id);
                        var fs = Observe(context, context.Manifest);
                        problems = Reconciler.AlbumConsistency(context.Manifest, host, fs).ToList();
                        salvageActions = Reconciler.Salvage(context.Manifest, host, fs).Actions.Length;
                    }

                    rows.Add(new
                    {
                        operationId = context.OperationId, state, journal = context.Manifest == null ? null : new { status = context.Manifest.Status.ToString(), context.Manifest.Generation },
                        reasons = context.Manifest?.ReviewReasons.ToList(), loadError = prepared, albumConsistent = problems != null && problems.Count == 0,
                        consistencyProblems = problems, pendingSalvageActions = salvageActions,
                        backupsKept = Directory.Exists(Path.Combine(opRoot, "album-lyrics")) ? Directory.GetFiles(Path.Combine(opRoot, "album-lyrics")).Length : 0,
                        blocksAlbum = state is "Active" or "OperatorApprovalRequired" or "RequiresReview"
                    });
                    CrescendarrGuard.Emit(_logger, $"OPERATOR_STATUS op={context.OperationId} state={state} albumConsistent={problems != null && problems.Count == 0} problems={problems?.Count} pendingSalvage={salvageActions}");
                }
            }

            Disk.WriteDurable(Disk.RequireConfined(root, Path.Combine(root, "crescendarr", "status.json"), "status file"),
                JsonSerializer.Serialize(new { utc = DateTime.UtcNow, operations = rows }, new JsonSerializerOptions { WriteIndented = true }), replace: true);
        }

        public void Execute(CrescendarrProposeCommand message)
        {
            var root = CrescendarrGuard.RequireRoot();
            CrescendarrGuard.RequireHostConfined(root, _rootFolders, _config);
            var context = OperatorContext(root, message.OperationId);
            Propose(context, message.Action);
        }

        public void Execute(CrescendarrApproveCommand message)
        {
            var root = CrescendarrGuard.RequireRoot();
            CrescendarrGuard.RequireHostConfined(root, _rootFolders, _config);
            var context = OperatorContext(root, message.OperationId);
            var action = message.Action ?? string.Empty;
            var fresh = Propose(context, action);
            var given = Encoding.ASCII.GetBytes(message.Token ?? string.Empty);
            var expected = Encoding.ASCII.GetBytes(fresh.Token ?? "-");
            if (!fresh.Allowed || !CryptographicOperations.FixedTimeEquals(given, expected))
            {
                var why = !fresh.Allowed ? "not allowed on the fresh snapshot: " + string.Join("; ", fresh.Reasons) : "token does not match the fresh snapshot (stale or forged)";
                Audit(context, $"REJECTED action={action} reason={why}");
                CrescendarrGuard.Emit(_logger, $"APPROVAL_REJECTED op={context.OperationId} action={action} reason=\"{why}\"");
                return;
            }

            Audit(context, $"APPLY action={action} generation={fresh.Generation} digest={fresh.Digest}");
            switch (action)
            {
                case "register-placed":
                case "link-placed-row":
                {
                    var ok = action == "register-placed"
                        ? RegisterPlaced(context, context.Manifest, Required(fresh.TrackId, "approval track"))
                        : LinkPlacedRow(context, context.Manifest, Required(fresh.TrackId, "approval track"), Required(fresh.RowId, "approval row"));
                    CrescendarrGuard.Emit(_logger, $"APPROVAL_APPLIED op={context.OperationId} action={action} hostWriteOk={ok}");
                    Audit(context, $"APPLIED action={action} hostWriteOk={ok}");
                    string outcome;
                    try
                    {
                        outcome = RunJournal(context);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        outcome = FailClosed(context, error);
                    }

                    if (!Finish(context, outcome)) throw new CommandFailedException($"Crescendarr operation {context.OperationId} ended {outcome} after the approved action.");
                    return;
                }

                case "salvage-lyrics":
                    RunSalvage(context);
                    WriteSummary(context, OperationState(context.OperationRoot), null);
                    CrescendarrGuard.Emit(_logger, $"APPROVAL_APPLIED op={context.OperationId} action={action} salvaged=[{string.Join(",", context.Salvaged)}]");
                    Audit(context, $"APPLIED action={action} salvaged={string.Join(",", context.Salvaged)}");
                    return;
                case "resolve":
                    Disk.WriteDurable(Path.Combine(context.OperationRoot, "resolution.json"), JsonSerializer.Serialize(new
                    {
                        resolvedBy = "operator", operationId = context.OperationId, journalStatus = context.Manifest.Status.ToString(), journalGeneration = fresh.Generation,
                        snapshotDigest = fresh.Digest, importCompleted = false, downloadSettled = false,
                        meaning = "The album block is released because every bound audio file and album lyric was verified present and linked. " +
                            "This is NOT import completion and NOT download settlement. The journal, outcome, backups and evidence are kept.",
                        utc = DateTime.UtcNow
                    }), replace: false);
                    CrescendarrGuard.Emit(_logger, $"APPROVAL_APPLIED op={context.OperationId} action=resolve albumBlockReleased=true importCompleted=false downloadSettled=false");
                    Audit(context, "APPLIED action=resolve");
                    return;
            }
        }

        /// <summary>Dry run. Never writes anything except proposal.json and the audit log.</summary>
        private Proposal Propose(OperationContext context, string action)
        {
            var p = new Proposal();
            var state = OperationState(context.OperationRoot);
            var prepared = SafePrepare(context);
            if (!OperatorActions.Contains(action)) p.Reasons.Add("unknown action (allowed: " + string.Join(", ", OperatorActions) + ")");
            else if (prepared != null) p.Reasons.Add("operation cannot be loaded: " + prepared);
            else if (File.Exists(Path.Combine(context.OperationRoot, "resolution.json"))) p.Reasons.Add("operation is already resolved");
            else
            {
                var host = HostSnapshot(context.Manifest, context.Artist.Id);
                var fs = Observe(context, context.Manifest);
                p.Generation = context.Manifest.Generation;
                p.Digest = Digest(context.Manifest, host, fs);
                switch (action)
                {
                    case "register-placed":
                    case "link-placed-row":
                    {
                        var wanted = action == "register-placed" ? RecoveryAction.InsertFreshTrackFileForIncoming : RecoveryAction.LinkPlacedRow;
                        var r = Reconciler.Reconcile(context.Manifest, host, fs);
                        if (state != "OperatorApprovalRequired" && state != "Active") p.Reasons.Add($"operation is {state}; host-row actions apply only to an interrupted, non-terminal operation");
                        else if (r.Verdict != Verdict.OperatorApprovalRequired || r.Next?.Action != wanted) p.Reasons.Add($"the fresh reconciliation does not propose {wanted} (verdict {r.Verdict}, next {r.Next?.Action})");
                        else
                        {
                            p.Allowed = true;
                            p.TrackId = r.Next.TrackId;
                            p.RowId = r.Next.NewTrackFileId;
                            p.Plan.Add($"{wanted} for track {r.Next.TrackId}" + (p.RowId is { } row ? $" -> row {row}" : string.Empty) + $": {r.Next.Reason}");
                            p.Plan.Add("then continue the journal from a fresh reconciliation");
                        }

                        break;
                    }

                    case "salvage-lyrics":
                    {
                        var plan = Reconciler.Salvage(context.Manifest, host, fs);
                        p.Reasons.AddRange(plan.Reasons);
                        p.Reasons.AddRange(plan.Skipped);
                        p.Allowed = plan.Applicable && plan.Actions.Length > 0;
                        if (plan.Applicable && plan.Actions.Length == 0) p.Reasons.Add("nothing to salvage");
                        p.Plan.AddRange(plan.Actions.Select(a => $"{a.Action} track={a.TrackId} lyric={a.AlbumLyricId}" + (a.NewTrackFileId is { } tf ? $" -> row {tf}" : string.Empty)));
                        break;
                    }

                    case "resolve":
                    {
                        if (state != "RequiresReview") p.Reasons.Add($"operation is {state}; only a sticky RequiresReview operation can be resolved");
                        else
                        {
                            var problems = Reconciler.AlbumConsistency(context.Manifest, host, fs);
                            p.Reasons.AddRange(problems);
                            p.Allowed = problems.IsEmpty;
                            if (p.Allowed) p.Plan.Add("release the album block; keep journal, outcome, backups and evidence; importCompleted=false; downloadSettled=false");
                        }

                        break;
                    }
                }
            }

            if (p.Allowed) p.Token = Disk.Sha(Encoding.UTF8.GetBytes($"{context.OperationId}|{action}|{p.Generation}|{p.Digest}"))[..32];
            if (Directory.Exists(context.OperationRoot))
            {
                Disk.WriteDurable(Path.Combine(context.OperationRoot, "proposal.json"), JsonSerializer.Serialize(new
                {
                    operationId = context.OperationId, action, state, allowed = p.Allowed, token = p.Token, journalGeneration = p.Generation, snapshotDigest = p.Digest,
                    plan = p.Plan, reasons = p.Reasons, utc = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }), replace: true);
                Audit(context, $"PROPOSE action={action} allowed={p.Allowed} generation={p.Generation} digest={p.Digest}");
            }

            CrescendarrGuard.Emit(_logger, $"PROPOSAL op={context.OperationId} action={action} allowed={p.Allowed} token={p.Token ?? "none"} generation={p.Generation} " +
                $"plan=[{string.Join("; ", p.Plan)}] reasons=\"{string.Join("; ", p.Reasons)}\"");
            return p;
        }

        private OperationContext OperatorContext(string root, string operationId)
        {
            if (!JournalValidation.IsUuid(operationId)) throw new CommandFailedException("OperationId must be a lowercase canonical UUID.");
            var opRoot = Disk.RequireConfined(root, Path.Combine(root, "crescendarr", "ops", operationId), "operation folder");
            if (!Directory.Exists(opRoot)) throw new CommandFailedException("Unknown operation.");
            return new OperationContext { Root = root, OperationId = operationId, OperationRoot = opRoot };
        }

        private string SafePrepare(OperationContext context)
        {
            try
            {
                return PrepareContext(context);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return error.GetType().Name + ": " + error.Message;
            }
        }

        /// <summary>Durable state of one operation as an operator sees it.</summary>
        internal static string OperationState(string opRoot)
        {
            if (File.Exists(Path.Combine(opRoot, "resolution.json"))) return "Resolved";
            var outcome = Path.Combine(opRoot, "outcome.json");
            if (File.Exists(outcome)) return JsonDocument.Parse(File.ReadAllText(outcome)).RootElement.GetProperty("outcome").GetString();
            var review = Path.Combine(opRoot, "review.json");
            if (File.Exists(review) && JsonDocument.Parse(File.ReadAllText(review)).RootElement.GetProperty("status").GetString() is OperatorApprovalRequired or StorageUnavailable)
                return JsonDocument.Parse(File.ReadAllText(review)).RootElement.GetProperty("status").GetString();
            return File.Exists(Path.Combine(opRoot, "journal.json")) ? "Active" : "Unpublished";
        }

        /// <summary>Digest of everything an approval relies on: journal generation, host rows and observed bytes.</summary>
        private static string Digest(OperationManifest manifest, HostSnapshot host, FilesystemSnapshot fs)
        {
            var text = new StringBuilder();
            text.Append("gen=").Append(manifest.Generation).Append(';').Append("status=").Append(manifest.Status).Append(';');
            foreach (var r in host.Releases.OrderBy(r => r.ReleaseId)) text.Append("r:").Append(r.ReleaseId).Append(':').Append(r.ReleaseMbid).Append(':').Append(r.Monitored).Append(';');
            foreach (var t in host.Tracks.OrderBy(t => t.TrackId)) text.Append("t:").Append(t.TrackId).Append(':').Append(t.RecordingMbid).Append(':').Append(t.TrackFileId).Append(';');
            foreach (var f in host.TrackFiles.OrderBy(f => f.TrackFileId)) text.Append("f:").Append(f.TrackFileId).Append(':').Append(f.Path).Append(';');
            foreach (var l in host.Lyrics.OrderBy(l => l.LyricFileId)) text.Append("l:").Append(l.LyricFileId).Append(':').Append(l.TrackFileId).Append(':').Append(l.RelativePath).Append(';');
            foreach (var (path, o) in fs.Files.OrderBy(k => k.Key, StringComparer.Ordinal)) text.Append("p:").Append(path).Append(':').Append(o.Exists).Append(':').Append(o.Sha256).Append(':').Append(o.Escapes).Append(';');
            text.Append("host=").Append(host.Obtained).Append(";fs=").Append(fs.Available);
            return Disk.Sha(Encoding.UTF8.GetBytes(text.ToString()));
        }

        private static void Audit(OperationContext context, string line)
        {
            if (!Directory.Exists(context.OperationRoot)) return;
            var path = Path.Combine(context.OperationRoot, "audit.log");
            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            Disk.WriteDurable(path, existing + DateTime.UtcNow.ToString("O") + " " + line + "\n", replace: true);
        }
    }
}
