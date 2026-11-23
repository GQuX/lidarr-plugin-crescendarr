// P0 issue #41 / draft PR #42: REAL separate-process crash recovery for album-wide lyric DB detachment.
// Source-pinned stock Lidarr v3.1.6.5079 test project only, disposable migrated SQLite, synthetic tagged
// audio generated at runtime. Each phase runs in its OWN `dotnet test` process, selected by the
// CRESCENDARR_P0_PHASE environment variable, and exchanges state only through a disposable runner-temp
// root (CRESCENDARR_P0_ROOT). Crash phases terminate the test host with SIGKILL
// (Process.Kill on the current process): no finally blocks, NUnit teardown, flushes or exception
// handlers run, so nothing here is an in-process exception standing in for a crash.
//
// The reconciler below is a TEST-ONLY reference model of what an eventual in-process plugin adapter
// would have to do. It is NOT production code, NOT wired into Crescendarr, and does NOT authorize
// write-enabled operation. No server, NAS, SMB, downloader, user library or Lidarr modification.
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Test.MediaFiles
{
    public partial class CrescendarrTaggedAudioRescanFixture
    {
        private const string P0PhaseVariable = "CRESCENDARR_P0_PHASE";
        private const string P0RootVariable = "CRESCENDARR_P0_ROOT";
        private const string P0RootPrefix = "crescendarr-p0-";
        private const int P0DetachJournaledBeforeKill = 5;
        private const int P0KillAfterReattachInserts = 3;

        // Every registered album lyric, INCLUDING the unselected superior FLAC (12). Six is unregistered.
        private static readonly int[] P0DetachOrder = { 1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 12 };

        public enum P0NegativeCase
        {
            ConflictingBytesAtMissingLyricPath,
            AudioBytesChanged,
            CrossAlbumSharedTrackFile,
            MonitoredReleaseSwitched,
            StaleLyricRowAtPath,
            TruncatedManifest,
            TamperedManifest,
            CorruptedLyricBackup,
        }

        public enum P0Outcome
        {
            Recovered,
            AlreadyRecovered,
            RequiresReview,
        }

        /// <summary>Durable per-track binding. Identity is release + medium + recording + release-track MBID, never a row ID.</summary>
        public sealed class P0Binding
        {
            public int MediumNumber { get; set; }
            public int AbsoluteTrackNumber { get; set; }
            public string RecordingMbid { get; set; }
            public string ReleaseTrackMbid { get; set; }
            public bool SelectedForUpgrade { get; set; }
            public string AudioRelativePath { get; set; }
            public string AudioSha256 { get; set; }
            public int OriginalTrackFileId { get; set; }
            public bool LyricRegistered { get; set; }
            public int OriginalLyricId { get; set; }
            public string LyricRelativePath { get; set; }
            public string LyricSha256 { get; set; }
            public string LyricBackupRelativePath { get; set; }
            public string LyricBackupSha256 { get; set; }
        }

        public sealed class P0Manifest
        {
            public int Version { get; set; }
            public int ArtistId { get; set; }
            public string ArtistPath { get; set; }
            public string ArtistMbid { get; set; }
            public int AlbumId { get; set; }
            public string ReleaseGroupMbid { get; set; }
            public string ReleaseMbid { get; set; }
            public List<P0Binding> Bindings { get; set; }
            public string Sha256 { get; set; }
        }

        private sealed class P0Step
        {
            public P0Binding Binding;
            public int TrackId;
            public int TrackFileId;
            public string LyricPath;
            public string BackupPath;
            public bool RestorePhysical;
        }

        private sealed class P0Plan
        {
            public readonly List<string> Reviews = new List<string>();
            public readonly List<P0Step> Steps = new List<P0Step>();
            public int Attached;
            public int Untouched;
            public int FreshTrackFileIds;
        }

        // ------------------------------------------------------------------ process entry points

        [Test]
        public void p0_crash_recovery_phase()
        {
            var phase = Environment.GetEnvironmentVariable(P0PhaseVariable);
            var root = P0ValidatedRoot();
            Log($"P0_CRASH_PHASE phase={phase} pid={Environment.ProcessId} root={root}");
            switch (phase)
            {
                case "detach-crash":
                    P0DetachThenHardKill(root);
                    break;
                case "absent-scan":
                    P0PluginAbsentScanAfterDetachCrash(root);
                    break;
                case "recover":
                    P0RecoverAfterPluginAbsentScan(root);
                    break;
                case "recover-crash":
                    P0RecoverThenHardKillMidReattach(root);
                    break;
                case "absent-scan-recover":
                    P0ScanThenRecoverAfterReattachCrash(root);
                    break;
                case "replay":
                    P0ReplayRestart(root);
                    break;
                case "review-replay":
                    P0StickyReviewAcrossRestart(root);
                    break;
                default:
                    Assert.Fail($"{P0PhaseVariable} must name an explicit CI phase, got '{phase}'");
                    break;
            }
        }

        [TestCase(P0NegativeCase.ConflictingBytesAtMissingLyricPath, "refusing to overwrite")]
        [TestCase(P0NegativeCase.AudioBytesChanged, "physical audio bytes differ")]
        [TestCase(P0NegativeCase.CrossAlbumSharedTrackFile, "shared by")]
        [TestCase(P0NegativeCase.MonitoredReleaseSwitched, "monitored release")]
        [TestCase(P0NegativeCase.StaleLyricRowAtPath, "conflicting or stale lyric row")]
        [TestCase(P0NegativeCase.TruncatedManifest, "manifest unreadable")]
        [TestCase(P0NegativeCase.TamperedManifest, "manifest hash mismatch")]
        [TestCase(P0NegativeCase.CorruptedLyricBackup, "backup unverifiable")]
        public void p0_crash_recovery_negative(P0NegativeCase negative, string expectedReason)
        {
            Assert.That(Environment.GetEnvironmentVariable(P0PhaseVariable), Is.EqualTo("negative"));
            var root = P0ValidatedRoot();
            Assert.That(File.Exists(Path.Combine(root, "absent-scan.done")), Is.True, "negatives need the post-crash, post-scan state");
            Assert.That(File.Exists(Path.Combine(root, "recover.done")), Is.False, "negatives must run before the real recovery mutates the world");

            // Relocated copy of the genuine post-crash, post-scan state, so each case is independent.
            var caseRoot = Path.Combine(root, "neg-" + negative);
            var caseWorld = Path.Combine(caseRoot, "world");
            Assert.That(Directory.Exists(caseRoot), Is.False);
            P0CopyDirectory(Path.Combine(root, "world"), caseWorld);
            File.Copy(Path.Combine(root, "journal.log"), Path.Combine(caseRoot, "journal.log"));
            P0ImportDatabase(Path.Combine(root, "after-scan.db"), null);
            P0RelocateDatabasePaths(Path.Combine(root, "world"), caseWorld);
            var manifest = P0LoadManifest(root, out var problem);
            Assert.That(problem, Is.Null);
            manifest.ArtistPath = Path.Combine(caseWorld, Path.GetRelativePath(Path.Combine(root, "world"), manifest.ArtistPath));
            P0WriteManifest(caseRoot, manifest);

            var w = P0AttachWorld(caseRoot, caseWorld);
            WireRealEventsAndStockDisk(w, expectRegisteredLyrics: false);
            var b8 = manifest.Bindings.Single(b => b.AbsoluteTrackNumber == 8);
            switch (negative)
            {
                case P0NegativeCase.ConflictingBytesAtMissingLyricPath:
                    Assert.That(File.Exists(w.LyricPaths[8]), Is.False);
                    File.WriteAllBytes(w.LyricPaths[8], Encoding.UTF8.GetBytes("UNRELATED USER FILE AT A LYRIC PATH\n"));
                    break;
                case P0NegativeCase.AudioBytesChanged:
                    File.WriteAllBytes(w.OwnedPaths[9], w.IncomingBytes[9]);
                    break;
                case P0NegativeCase.CrossAlbumSharedTrackFile:
                    P0InsertForeignAlbumTrackSharing(w, Db.All<Track>().Single(t => t.AbsoluteTrackNumber == 10).TrackFileId);
                    break;
                case P0NegativeCase.MonitoredReleaseSwitched:
                    P0SwitchMonitoredRelease(w);
                    break;
                case P0NegativeCase.StaleLyricRowAtPath:
                    Db.Insert(new LyricFile
                    {
                        ArtistId = w.Artist.Id,
                        AlbumId = w.Album.Id,
                        TrackFileId = b8.OriginalTrackFileId,
                        RelativePath = b8.LyricRelativePath,
                        Extension = ".lrc",
                        Added = DateTime.UtcNow,
                        LastUpdated = DateTime.UtcNow
                    });
                    break;
                case P0NegativeCase.TruncatedManifest:
                    var full = File.ReadAllBytes(Path.Combine(caseRoot, "manifest.json"));
                    File.WriteAllBytes(Path.Combine(caseRoot, "manifest.json"), full.Take(full.Length / 2).ToArray());
                    break;
                case P0NegativeCase.TamperedManifest:
                    var text = File.ReadAllText(Path.Combine(caseRoot, "manifest.json"));
                    Assert.That(text.Contains(RecordingMbid(11)), Is.True);
                    File.WriteAllText(Path.Combine(caseRoot, "manifest.json"), text.Replace(RecordingMbid(11), RecordingMbid(99)));
                    break;
                case P0NegativeCase.CorruptedLyricBackup:
                    File.WriteAllBytes(Path.Combine(caseWorld, b8.LyricBackupRelativePath), Encoding.UTF8.GetBytes("[00:00.00] CORRUPTED BACKUP\n"));
                    break;
            }

            var filesBefore = P0SnapshotFiles(caseWorld);
            var rowsBefore = P0LyricRowSnapshot();
            var linksBefore = P0LinkSnapshot();
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                Assert.That(P0Recover(w, caseRoot, caseWorld), Is.EqualTo(P0Outcome.RequiresReview), $"{negative} attempt {attempt}");
                Assert.That(P0SnapshotFiles(caseWorld), Is.EqualTo(filesBefore), $"{negative}: RequiresReview must not touch any file");
                Assert.That(P0LyricRowSnapshot(), Is.EqualTo(rowsBefore), $"{negative}: RequiresReview must not write lyric rows");
                Assert.That(P0LinkSnapshot(), Is.EqualTo(linksBefore), $"{negative}: RequiresReview must not relink tracks");
            }

            var journal = P0ReadJournal(caseRoot);
            Assert.That(journal.Any(l => l.StartsWith("REQUIRES_REVIEW ") && l.Contains(expectedReason)), Is.True,
                $"{negative}: expected a durable REQUIRES_REVIEW naming '{expectedReason}'");
            Assert.That(journal.Count(l => l.StartsWith("RECOVERY_START")), Is.EqualTo(1), "second attempt was sticky before observing anything");
            Log($"P0_CRASH_NEGATIVE case={negative} outcome=RequiresReview filesUnchanged=true rowsUnchanged=true sticky=true");

            if (negative == P0NegativeCase.ConflictingBytesAtMissingLyricPath)
            {
                P0ExportDatabase(Path.Combine(caseRoot, "state.db"));
                P0WriteDurable(Path.Combine(caseRoot, "sticky.case"), Encoding.UTF8.GetBytes(negative.ToString()));
            }
        }

        // ------------------------------------------------------------------ lane D: crash mid-detach

        private void P0DetachThenHardKill(string root)
        {
            var worldRoot = Path.Combine(root, "world");
            Assert.That(Directory.Exists(worldRoot), Is.False, "crash lanes must start from a fresh disposable root");
            Directory.CreateDirectory(root);
            P0WriteDurable(Path.Combine(root, "live-db.detach-crash.path"), Encoding.UTF8.GetBytes(TestFolderInfo.GetDatabase()));

            var w = BuildWorld(sixthUnlinkedFromStart: true, worldRoot: worldRoot);
            WireRealEventsAndStockDisk(w);
            var manifest = P0PublishManifest(w, root, worldRoot);
            P0AppendJournal(root, "MANIFEST_PUBLISHED " + manifest.Sha256);

            var journaled = 0;
            foreach (var i in P0DetachOrder)
            {
                var lyricId = w.LyricIds[i];
                P0AppendJournal(root, $"DETACH_INTENT recording={RecordingMbid(i)} lyricRow={lyricId}");
                w.Lyrics.Delete(lyricId);
                if (journaled == P0DetachJournaledBeforeKill)
                {
                    P0HardKill(root, "detach-crash",
                        $"track={i} lyricRow={lyricId} DB delete committed; DETACHED record NOT written; " +
                        $"registeredLyricRows={Db.All<LyricFile>().Count}");
                }

                P0AppendJournal(root, $"DETACHED recording={RecordingMbid(i)} lyricRow={lyricId}");
                journaled++;
            }

            Assert.Fail("unreachable: the test host should have been killed mid-detach");
        }

        private void P0PluginAbsentScanAfterDetachCrash(string root)
        {
            P0AssertKilled(root, "detach-crash");
            var journal = P0ReadJournal(root);
            Assert.That(journal.Count(l => l.StartsWith("DETACH_INTENT ")), Is.EqualTo(P0DetachJournaledBeforeKill + 1));
            Assert.That(journal.Count(l => l.StartsWith("DETACHED ")), Is.EqualTo(P0DetachJournaledBeforeKill));

            P0ImportDatabase(File.ReadAllText(Path.Combine(root, "live-db.detach-crash.path")), Path.Combine(root, "crashed-db-detach"));
            var w = P0AttachWorld(root, Path.Combine(root, "world"));
            WireRealEventsAndStockDisk(w);
            var registered = P0RegisteredLyricTracks();
            Assert.That(registered, Is.EquivalentTo(new[] { 8, 9, 10, 11, 12 }),
                "after SIGKILL: track 7's committed lyric delete survived although its DETACHED record was never written");
            Log("P0_CRASH_AFTER_KILL lane=detach registeredLyricTracks=[" + string.Join(",", registered) + "] journalDetached=5 dbDetached=6");

            // The plugin is ABSENT: a normal stock scan sees the still-unregistered sixth audio file.
            var recorder = WireRealScan(w);
            var scan = RunRealScan(w, recorder, "after-detach-crash-plugin-absent");
            AssertStockFullAlbumRescanCascade(w, scan, Scenario.PartialAlbumLyricDetach);
            Assert.That(scan.RegisteredLyricTracksBefore, Is.EquivalentTo(new[] { 8, 9, 10, 11, 12 }));
            foreach (var i in new[] { 1, 2, 3, 4, 5, 6, 7 })
            {
                Assert.That(File.ReadAllBytes(w.LyricPaths[i]), Is.EqualTo(w.LyricBytes[i]), $"DB-detached lyric {i} survived the scan");
            }

            Log("P0_CRASH_ABSENT_SCAN lane=detach decisions=12 relinkedAll=true recycledLyrics=[8,9,10,11,12] includingUnselectedSuperiorFlac=true lyricRowsAfter=0");
            P0ExportDatabase(Path.Combine(root, "after-scan.db"));
            P0WriteDurable(Path.Combine(root, "absent-scan.done"), Encoding.UTF8.GetBytes("ok"));
        }

        private void P0RecoverAfterPluginAbsentScan(string root)
        {
            Assert.That(File.Exists(Path.Combine(root, "absent-scan.done")), Is.True);
            P0ImportDatabase(Path.Combine(root, "after-scan.db"), null);
            var worldRoot = Path.Combine(root, "world");
            var w = P0AttachWorld(root, worldRoot);
            WireRealEventsAndStockDisk(w, expectRegisteredLyrics: false);
            Assert.That(Db.All<LyricFile>(), Is.Empty);

            var before = P0SnapshotFiles(worldRoot);
            Assert.That(P0Recover(w, root, worldRoot), Is.EqualTo(P0Outcome.Recovered));
            P0AssertRecovered(w, root, worldRoot, before, new[] { 8, 9, 10, 11, 12 });
            P0ExportDatabase(Path.Combine(root, "recovered.db"));
            P0WriteDurable(Path.Combine(root, "recover.done"), Encoding.UTF8.GetBytes("ok"));
        }

        // ------------------------------------------------------------------ lane R: crash mid-reattach

        private void P0RecoverThenHardKillMidReattach(string root)
        {
            P0AssertKilled(root, "detach-crash");
            P0ImportDatabase(File.ReadAllText(Path.Combine(root, "live-db.detach-crash.path")), Path.Combine(root, "crashed-db-detach"));
            P0WriteDurable(Path.Combine(root, "live-db.recover-crash.path"), Encoding.UTF8.GetBytes(TestFolderInfo.GetDatabase()));
            var worldRoot = Path.Combine(root, "world");
            var w = P0AttachWorld(root, worldRoot);
            WireRealEventsAndStockDisk(w);
            Assert.That(P0RegisteredLyricTracks(), Is.EquivalentTo(new[] { 8, 9, 10, 11, 12 }));

            // Restarted plugin recovers BEFORE any scan; it is killed after the third committed insert.
            P0Recover(w, root, worldRoot, P0KillAfterReattachInserts, "recover-crash");
            Assert.Fail("unreachable: the test host should have been killed mid-reattach");
        }

        private void P0ScanThenRecoverAfterReattachCrash(string root)
        {
            P0AssertKilled(root, "recover-crash");
            var journal = P0ReadJournal(root);
            Assert.That(journal.Count(l => l.StartsWith("REATTACH_INTENT ")), Is.EqualTo(P0KillAfterReattachInserts));
            Assert.That(journal.Count(l => l.StartsWith("REATTACHED ")), Is.EqualTo(P0KillAfterReattachInserts - 1));

            P0ImportDatabase(File.ReadAllText(Path.Combine(root, "live-db.recover-crash.path")), Path.Combine(root, "crashed-db-reattach"));
            var worldRoot = Path.Combine(root, "world");
            var w = P0AttachWorld(root, worldRoot);
            WireRealEventsAndStockDisk(w);
            var registered = P0RegisteredLyricTracks();
            Assert.That(registered, Is.EquivalentTo(new[] { 1, 2, 3, 8, 9, 10, 11, 12 }),
                "after SIGKILL: the third committed reattach insert survived without its REATTACHED record");
            Log("P0_CRASH_AFTER_KILL lane=reattach registeredLyricTracks=[" + string.Join(",", registered) + "] journalReattached=2 dbReattached=3");

            // Plugin absent again; the sixth audio file is STILL unregistered, so the scan cascades.
            var recorder = WireRealScan(w);
            var scan = RunRealScan(w, recorder, "after-reattach-crash-plugin-absent");
            AssertStockFullAlbumRescanCascade(w, scan, Scenario.PartialAlbumLyricDetach);
            Assert.That(scan.RegisteredLyricTracksBefore, Is.EquivalentTo(new[] { 1, 2, 3, 8, 9, 10, 11, 12 }));
            foreach (var i in new[] { 4, 5, 6, 7 })
            {
                Assert.That(File.ReadAllBytes(w.LyricPaths[i]), Is.EqualTo(w.LyricBytes[i]), $"still-detached lyric {i} survived the scan");
            }

            Log("P0_CRASH_ABSENT_SCAN lane=reattach recycledLyrics=[1,2,3,8,9,10,11,12] reattachedLyricsRecycledAgain=[1,2,3]");

            // Plugin starts again after the host scanned: observation-based recovery from fresh host state.
            var before = P0SnapshotFiles(worldRoot);
            Assert.That(P0Recover(w, root, worldRoot), Is.EqualTo(P0Outcome.Recovered));
            P0AssertRecovered(w, root, worldRoot, before, new[] { 1, 2, 3, 8, 9, 10, 11, 12 });
            P0ExportDatabase(Path.Combine(root, "recovered.db"));
            P0WriteDurable(Path.Combine(root, "recover.done"), Encoding.UTF8.GetBytes("ok"));
        }

        // ------------------------------------------------------------------ restarts after recovery

        private void P0ReplayRestart(string root)
        {
            Assert.That(File.Exists(Path.Combine(root, "recover.done")), Is.True);
            P0ImportDatabase(Path.Combine(root, "recovered.db"), null);
            var worldRoot = Path.Combine(root, "world");
            var w = P0AttachWorld(root, worldRoot);
            WireRealEventsAndStockDisk(w);

            var files = P0SnapshotFiles(worldRoot);
            var rows = P0LyricRowSnapshot();
            var links = P0LinkSnapshot();
            var reattached = P0ReadJournal(root).Count(l => l.StartsWith("REATTACHED "));

            Assert.That(P0Recover(w, root, worldRoot), Is.EqualTo(P0Outcome.AlreadyRecovered), "replay after restart must be a no-op");
            P0AssertUnchanged(files, rows, links, worldRoot, "replay");
            Assert.That(P0ReadJournal(root).Count(l => l.StartsWith("REATTACHED ")), Is.EqualTo(reattached));

            // A real stock rescan of the recovered, fully linked album.
            var recorder = WireRealScan(w);
            var scan = RunRealScan(w, recorder, "after-recovery-restart");
            Log($"P0_CRASH_REPLAY_SCAN decisions={scan.Decisions.Count} approved={scan.Decisions.Count(d => d.Approved)}");
            P0AssertUnchanged(files, rows, links, worldRoot, "stock rescan of the recovered album");
            Assert.That(P0Recover(w, root, worldRoot), Is.EqualTo(P0Outcome.AlreadyRecovered), "replay after the rescan");
            P0AssertUnchanged(files, rows, links, worldRoot, "second replay");
            Log("P0_CRASH_REPLAY outcome=AlreadyRecovered twice; stock rescan changed no link, row, lyric or byte");
        }

        private void P0StickyReviewAcrossRestart(string root)
        {
            var caseRoot = Path.Combine(root, "neg-" + P0NegativeCase.ConflictingBytesAtMissingLyricPath);
            var caseWorld = Path.Combine(caseRoot, "world");
            Assert.That(File.Exists(Path.Combine(caseRoot, "sticky.case")), Is.True);
            P0ImportDatabase(Path.Combine(caseRoot, "state.db"), null);
            var w = P0AttachWorld(caseRoot, caseWorld);
            WireRealEventsAndStockDisk(w, expectRegisteredLyrics: false);

            // The conflict disappears (e.g. someone deletes the unrelated file) before the restart.
            File.Delete(w.LyricPaths[8]);
            var files = P0SnapshotFiles(caseWorld);
            var rows = P0LyricRowSnapshot();
            var links = P0LinkSnapshot();
            Assert.That(P0Recover(w, caseRoot, caseWorld), Is.EqualTo(P0Outcome.RequiresReview),
                "a durable RequiresReview stays sticky across a process restart, even if the observed conflict vanished");
            P0AssertUnchanged(files, rows, links, caseWorld, "sticky review restart");
            Assert.That(Db.All<LyricFile>(), Is.Empty);
            Log("P0_CRASH_STICKY_REVIEW outcome=RequiresReview acrossRestart=true mutations=0");
        }

        // ------------------------------------------------------------------ test-only reference reconciler

        private P0Outcome P0Recover(World w, string root, string worldRoot, int killAfterInserts = 0, string killPhase = null)
        {
            if (P0ReadJournal(root).Any(l => l.StartsWith("REQUIRES_REVIEW ")))
            {
                Log("P0_CRASH_RECOVERY sticky RequiresReview; nothing observed or changed");
                return P0Outcome.RequiresReview;
            }

            var manifest = P0LoadManifest(root, out var problem);
            P0AppendJournal(root, "RECOVERY_START pid=" + Environment.ProcessId);
            if (manifest == null)
            {
                P0AppendJournal(root, "REQUIRES_REVIEW " + problem);
                return P0Outcome.RequiresReview;
            }

            var plan = P0Observe(manifest, worldRoot);
            Log($"P0_CRASH_PLAN attached={plan.Attached} untouched={plan.Untouched} steps={plan.Steps.Count} " +
                $"restores={plan.Steps.Count(s => s.RestorePhysical)} freshTrackFileIds={plan.FreshTrackFileIds} reviews={plan.Reviews.Count}");
            if (plan.Reviews.Count > 0)
            {
                foreach (var review in plan.Reviews)
                {
                    P0AppendJournal(root, "REQUIRES_REVIEW " + review);
                }

                return P0Outcome.RequiresReview;
            }

            if (plan.Steps.Count == 0)
            {
                P0AppendJournal(root, "RECOVERED already-consistent");
                return P0Outcome.AlreadyRecovered;
            }

            var inserted = 0;
            foreach (var step in plan.Steps)
            {
                var b = step.Binding;

                // Re-read host state immediately before each mutation. Any drift is ambiguous.
                var current = Db.All<Track>().Single(t => t.Id == step.TrackId);
                var lyricRows = Db.All<LyricFile>();
                if (current.TrackFileId != step.TrackFileId ||
                    lyricRows.Any(l => l.TrackFileId == step.TrackFileId || P0SamePath(manifest.ArtistPath, l.RelativePath, step.LyricPath)))
                {
                    P0AppendJournal(root, $"REQUIRES_REVIEW recording={b.RecordingMbid}: host state drifted between observation and use");
                    return P0Outcome.RequiresReview;
                }

                if (step.RestorePhysical)
                {
                    var bytes = File.ReadAllBytes(step.BackupPath);
                    var temporary = Path.Combine(Path.GetDirectoryName(step.LyricPath), ".crescendarr-restore-" + Path.GetFileName(step.LyricPath) + ".partial");
                    if (P0Sha(bytes) != b.LyricSha256 || File.Exists(temporary) || File.Exists(step.LyricPath))
                    {
                        P0AppendJournal(root, $"REQUIRES_REVIEW recording={b.RecordingMbid}: restore preconditions changed");
                        return P0Outcome.RequiresReview;
                    }

                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }

                    try
                    {
                        // .NET on Unix implements no-overwrite moves with link()+unlink(): EEXIST, never a replace.
                        File.Move(temporary, step.LyricPath, false);
                    }
                    catch (IOException error)
                    {
                        File.Delete(temporary);
                        P0AppendJournal(root, $"REQUIRES_REVIEW recording={b.RecordingMbid}: lyric path appeared during restore ({error.GetType().Name})");
                        return P0Outcome.RequiresReview;
                    }

                    Assert.That(P0ShaFile(step.LyricPath), Is.EqualTo(b.LyricSha256));
                    P0AppendJournal(root, $"RESTORED_LYRIC recording={b.RecordingMbid}");
                }

                P0AppendJournal(root, $"REATTACH_INTENT recording={b.RecordingMbid} trackFile={step.TrackFileId}");
                w.Lyrics.Upsert(new LyricFile
                {
                    ArtistId = manifest.ArtistId,
                    AlbumId = manifest.AlbumId,
                    TrackFileId = step.TrackFileId,
                    RelativePath = b.LyricRelativePath,
                    Extension = ".lrc"
                });
                inserted++;
                if (killAfterInserts > 0 && inserted == killAfterInserts)
                {
                    P0HardKill(root, killPhase, $"recording={b.RecordingMbid} reattach insert #{inserted} committed; REATTACHED record NOT written");
                }

                var row = Db.All<LyricFile>().Single(l => l.TrackFileId == step.TrackFileId);
                P0AppendJournal(root, $"REATTACHED recording={b.RecordingMbid} lyricRow={row.Id} trackFile={step.TrackFileId}");
            }

            var verify = P0Observe(manifest, worldRoot);
            if (verify.Reviews.Count > 0 || verify.Steps.Count > 0)
            {
                P0AppendJournal(root, "REQUIRES_REVIEW post-recovery verification failed: " + string.Join("; ", verify.Reviews));
                return P0Outcome.RequiresReview;
            }

            P0AppendJournal(root, $"RECOVERED inserted={inserted}");
            return P0Outcome.Recovered;
        }

        /// <summary>Pure observation of FRESH host state against the durable manifest. Never trusts stored row IDs.</summary>
        private P0Plan P0Observe(P0Manifest m, string worldRoot)
        {
            var plan = new P0Plan();
            var artists = Db.All<Artist>().Where(a => a.Id == m.ArtistId).ToList();
            if (artists.Count != 1 || Path.GetFullPath(artists[0].Path) != Path.GetFullPath(m.ArtistPath))
            {
                plan.Reviews.Add("artist id/path differs from the manifest");
            }

            var album = Db.All<Album>().SingleOrDefault(a => a.Id == m.AlbumId);
            if (album == null || album.ForeignAlbumId != m.ReleaseGroupMbid)
            {
                plan.Reviews.Add("album release-group identity differs from the manifest");
            }

            var monitored = Db.All<AlbumRelease>().Where(r => r.AlbumId == m.AlbumId && r.Monitored).ToList();
            if (monitored.Count != 1 || monitored[0].ForeignReleaseId != m.ReleaseMbid)
            {
                plan.Reviews.Add("monitored release is not exactly the manifest release");
            }

            if (plan.Reviews.Count > 0)
            {
                return plan;
            }

            var release = monitored[0];
            var tracks = Db.All<Track>();
            var files = Db.All<TrackFile>().ToDictionary(f => f.Id);
            var lyrics = Db.All<LyricFile>();
            foreach (var b in m.Bindings)
            {
                var label = $"medium={b.MediumNumber} recording={b.RecordingMbid}";
                var matches = tracks.Where(t => t.AlbumReleaseId == release.Id && t.MediumNumber == b.MediumNumber &&
                    t.ForeignRecordingId == b.RecordingMbid && t.ForeignTrackId == b.ReleaseTrackMbid).ToList();
                if (matches.Count != 1)
                {
                    plan.Reviews.Add($"{label}: {matches.Count} tracks match the recorded identity");
                    continue;
                }

                var track = matches[0];
                var audioPath = Path.GetFullPath(Path.Combine(m.ArtistPath, b.AudioRelativePath));
                var lyricPath = Path.GetFullPath(Path.Combine(m.ArtistPath, b.LyricRelativePath));
                var backupPath = Path.Combine(worldRoot, b.LyricBackupRelativePath);
                if (!File.Exists(audioPath) || P0ShaFile(audioPath) != b.AudioSha256)
                {
                    plan.Reviews.Add($"{label}: physical audio bytes differ from the manifest");
                    continue;
                }

                TrackFile file = null;
                if (track.TrackFileId > 0 && !files.TryGetValue(track.TrackFileId, out file))
                {
                    plan.Reviews.Add($"{label}: track points at missing TrackFile {track.TrackFileId}");
                    continue;
                }

                if (file == null)
                {
                    if (b.OriginalTrackFileId != 0 || b.LyricRegistered)
                    {
                        plan.Reviews.Add($"{label}: bound track has no TrackFile row");
                        continue;
                    }
                }
                else
                {
                    var sharers = tracks.Count(t => t.TrackFileId == file.Id);
                    if (Path.GetFullPath(file.Path) != audioPath || file.AlbumId != m.AlbumId)
                    {
                        plan.Reviews.Add($"{label}: TrackFile {file.Id} path/album differs from the manifest");
                        continue;
                    }

                    if (sharers != 1)
                    {
                        plan.Reviews.Add($"{label}: TrackFile {file.Id} shared by {sharers} tracks");
                        continue;
                    }

                    if (file.Id != b.OriginalTrackFileId)
                    {
                        plan.FreshTrackFileIds++;
                    }
                }

                var rowsAtPath = lyrics.Where(l => P0SamePath(m.ArtistPath, l.RelativePath, lyricPath)).ToList();
                var rowsForFile = file == null ? new List<LyricFile>() : lyrics.Where(l => l.TrackFileId == file.Id).ToList();
                var physicalExists = File.Exists(lyricPath);
                var physicalMatches = physicalExists && P0ShaFile(lyricPath) == b.LyricSha256;
                if (!b.LyricRegistered)
                {
                    if (rowsAtPath.Count != 0 || !physicalMatches)
                    {
                        plan.Reviews.Add($"{label}: originally unregistered lyric changed");
                    }
                    else
                    {
                        plan.Untouched++;
                    }

                    continue;
                }

                if (rowsAtPath.Count == 1 && rowsForFile.Count == 1 && rowsAtPath[0].Id == rowsForFile[0].Id && rowsAtPath[0].AlbumId == m.AlbumId)
                {
                    if (physicalMatches)
                    {
                        plan.Attached++;
                    }
                    else
                    {
                        plan.Reviews.Add($"{label}: attached lyric bytes differ from the manifest");
                    }

                    continue;
                }

                if (rowsAtPath.Count != 0 || rowsForFile.Count != 0)
                {
                    plan.Reviews.Add($"{label}: conflicting or stale lyric row(s) [{string.Join(",", rowsAtPath.Concat(rowsForFile).Select(l => l.Id + "->" + l.TrackFileId))}]");
                    continue;
                }

                if (physicalExists && !physicalMatches)
                {
                    plan.Reviews.Add($"{label}: unrelated bytes occupy the lyric path; refusing to overwrite");
                    continue;
                }

                if (!physicalExists && (!File.Exists(backupPath) || P0ShaFile(backupPath) != b.LyricSha256 || b.LyricBackupSha256 != b.LyricSha256))
                {
                    plan.Reviews.Add($"{label}: lyric missing and backup unverifiable");
                    continue;
                }

                plan.Steps.Add(new P0Step
                {
                    Binding = b,
                    TrackId = track.Id,
                    TrackFileId = file.Id,
                    LyricPath = lyricPath,
                    BackupPath = backupPath,
                    RestorePhysical = !physicalExists
                });
            }

            return plan;
        }

        private P0Manifest P0PublishManifest(World w, string root, string worldRoot)
        {
            var bindings = new List<P0Binding>();
            for (var i = 1; i <= Total; i++)
            {
                var track = Db.All<Track>().Single(t => t.AbsoluteTrackNumber == i);
                var lyricBackup = Path.Combine(w.Backups, $"crash-lyric-{i:D2}.lrc");
                var lyricBytes = File.ReadAllBytes(w.LyricPaths[i]);
                Assert.That(lyricBytes, Is.EqualTo(w.LyricBytes[i]), $"track {i}: physical lyric verified before backup");
                P0WriteDurable(lyricBackup, lyricBytes);
                Assert.That(P0ShaFile(lyricBackup), Is.EqualTo(P0Sha(lyricBytes)), $"track {i}: backup re-read and verified");
                bindings.Add(new P0Binding
                {
                    MediumNumber = track.MediumNumber,
                    AbsoluteTrackNumber = i,
                    RecordingMbid = track.ForeignRecordingId,
                    ReleaseTrackMbid = track.ForeignTrackId,
                    SelectedForUpgrade = i != Total,
                    AudioRelativePath = Path.GetRelativePath(w.Artist.Path, w.OwnedPaths[i]),
                    AudioSha256 = P0ShaFile(w.OwnedPaths[i]),
                    OriginalTrackFileId = track.TrackFileId,
                    LyricRegistered = w.LyricIds.ContainsKey(i),
                    OriginalLyricId = w.LyricIds.TryGetValue(i, out var lyricId) ? lyricId : 0,
                    LyricRelativePath = Path.GetRelativePath(w.Artist.Path, w.LyricPaths[i]),
                    LyricSha256 = P0Sha(lyricBytes),
                    LyricBackupRelativePath = Path.GetRelativePath(worldRoot, lyricBackup),
                    LyricBackupSha256 = P0ShaFile(lyricBackup)
                });
            }

            var manifest = new P0Manifest
            {
                Version = 1,
                ArtistId = w.Artist.Id,
                ArtistPath = w.Artist.Path,
                ArtistMbid = ArtistMbid,
                AlbumId = w.Album.Id,
                ReleaseGroupMbid = ReleaseGroupMbid,
                ReleaseMbid = ReleaseMbid,
                Bindings = bindings
            };
            P0WriteManifest(root, manifest);
            Assert.That(P0LoadManifest(root, out var problem), Is.Not.Null, problem);
            Log($"P0_CRASH_MANIFEST bindings={bindings.Count} registered={bindings.Count(b => b.LyricRegistered)} unselectedSuperior={bindings.Count(b => !b.SelectedForUpgrade)} sha={manifest.Sha256}");
            return manifest;
        }

        private static void P0WriteManifest(string root, P0Manifest manifest)
        {
            manifest.Sha256 = P0ManifestHash(manifest);
            Directory.CreateDirectory(root);
            P0WriteDurable(Path.Combine(root, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string P0ManifestHash(P0Manifest manifest)
        {
            var copy = JsonSerializer.Deserialize<P0Manifest>(JsonSerializer.Serialize(manifest));
            copy.Sha256 = string.Empty;
            return P0Sha(JsonSerializer.SerializeToUtf8Bytes(copy));
        }

        private static P0Manifest P0LoadManifest(string root, out string problem)
        {
            problem = null;
            var path = Path.Combine(root, "manifest.json");
            if (!File.Exists(path))
            {
                problem = "manifest unreadable: missing";
                return null;
            }

            P0Manifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<P0Manifest>(File.ReadAllBytes(path));
            }
            catch (JsonException error)
            {
                problem = "manifest unreadable: " + error.Message.Split('\n')[0];
                return null;
            }

            static bool Hex(string s) => s != null && s.Length == 64 && s.All(Uri.IsHexDigit);
            static bool Relative(string s) => !string.IsNullOrEmpty(s) && !Path.IsPathRooted(s) && !s.Split('/', '\\').Contains("..");
            if (manifest == null || manifest.Version != 1 || manifest.Bindings == null)
            {
                problem = "manifest unreadable: wrong version or empty";
            }
            else if (manifest.Sha256 != P0ManifestHash(manifest))
            {
                problem = "manifest hash mismatch";
            }
            else if (manifest.Bindings.Count != Total ||
                     manifest.Bindings.Select(b => (b.MediumNumber, b.RecordingMbid)).Distinct().Count() != Total ||
                     manifest.Bindings.Select(b => b.ReleaseTrackMbid).Distinct().Count() != Total)
            {
                problem = "manifest bindings incomplete or duplicated";
            }
            else if (manifest.Bindings.Any(b => !Hex(b.AudioSha256) || !Hex(b.LyricSha256) || !Hex(b.LyricBackupSha256) ||
                     !Relative(b.AudioRelativePath) || !Relative(b.LyricRelativePath) || !Relative(b.LyricBackupRelativePath)))
            {
                problem = "manifest binding fields malformed";
            }

            return problem == null ? manifest : null;
        }

        // ------------------------------------------------------------------ world reattachment and evidence

        private World P0AttachWorld(string root, string worldRoot)
        {
            var manifest = P0LoadManifest(root, out var problem);
            Assert.That(manifest, Is.Not.Null, problem);
            var w = new World { Temp = worldRoot };
            w.Library = Path.Combine(w.Temp, "library", "synthetic-artist", "synthetic-album");
            w.Staging = Path.Combine(w.Temp, "staging");
            w.Recycle = Path.Combine(w.Temp, "synthetic-recycle");
            w.Backups = Path.Combine(w.Temp, "verified-copies");
            Mocker.SetConstant<IExtraFileRepository<LyricFile>>(Mocker.Resolve<ExtraFileRepository<LyricFile>>());

            w.Artist = Db.All<Artist>().Single();
            var metadata = Db.All<ArtistMetadata>().Single(x => x.Id == w.Artist.ArtistMetadataId);
            var profile = Db.All<QualityProfile>().Single(x => x.Id == w.Artist.QualityProfileId);
            w.Artist.Metadata = metadata;
            w.Artist.QualityProfile = profile;
            Assert.That(w.Artist.Path, Is.EqualTo(Path.GetDirectoryName(w.Library)), "reopened DB names this disposable world");
            w.Album = Db.All<Album>().Single(x => x.Id == manifest.AlbumId);
            w.Album.Artist = w.Artist;
            w.Album.ArtistMetadata = metadata;
            var releases = Db.All<AlbumRelease>().Where(r => r.AlbumId == w.Album.Id).ToList();
            w.Release = releases.Single(r => r.ForeignReleaseId == ReleaseMbid);
            w.Release.Album = w.Album;
            w.Album.AlbumReleases = releases;

            foreach (var b in manifest.Bindings)
            {
                var i = b.AbsoluteTrackNumber;
                var stem = i.ToString("D2") + "-synthetic";
                w.OwnedPaths[i] = Path.Combine(w.Artist.Path, b.AudioRelativePath);
                w.IncomingPaths[i] = Path.Combine(w.Staging, stem + ".mp3");
                w.LyricPaths[i] = Path.Combine(w.Artist.Path, b.LyricRelativePath);
                w.BackupPaths[i] = Path.Combine(w.Backups, Path.GetFileName(w.OwnedPaths[i]));
                w.OwnedBytes[i] = File.ReadAllBytes(w.BackupPaths[i]);
                w.IncomingBytes[i] = File.ReadAllBytes(w.IncomingPaths[i]);
                w.LyricBytes[i] = Encoding.UTF8.GetBytes($"[00:00.00] SYNTHETIC LYRIC {i}\n");
                Assert.That(P0Sha(w.LyricBytes[i]), Is.EqualTo(b.LyricSha256));
                Assert.That(P0Sha(w.OwnedBytes[i]), Is.EqualTo(b.AudioSha256), $"track {i}: verified audio backup");
                if (b.LyricRegistered)
                {
                    w.LyricIds[i] = b.OriginalLyricId;
                }

                if (b.OriginalTrackFileId > 0)
                {
                    w.Previous[i] = new TrackFile { Id = b.OriginalTrackFileId, Path = w.OwnedPaths[i] };
                }

                w.Tracks[i] = Db.All<Track>().Single(t => t.AbsoluteTrackNumber == i && t.AlbumReleaseId == w.Release.Id);
            }

            Mocker.GetMock<IArtistService>().Setup(x => x.GetArtists(It.IsAny<IEnumerable<int>>())).Returns(new List<Artist> { w.Artist });
            Mocker.GetMock<IArtistService>().Setup(x => x.GetArtist(w.Artist.Id)).Returns(w.Artist);
            Mocker.GetMock<IAlbumService>().Setup(x => x.GetAlbum(w.Album.Id)).Returns(w.Album);
            Mocker.GetMock<IRootFolderService>().Setup(x => x.GetBestRootFolder(It.IsAny<string>()))
                .Returns(new RootFolder { Path = Path.Combine(w.Temp, "library"), DefaultQualityProfileId = profile.Id });
            return w;
        }

        private void P0AssertRecovered(World w, string root, string worldRoot, Dictionary<string, string> filesBefore, int[] expectRestored)
        {
            var manifest = P0LoadManifest(root, out _);
            var lyrics = Db.All<LyricFile>();
            var registered = manifest.Bindings.Where(b => b.LyricRegistered).ToList();
            Assert.That(lyrics.Count, Is.EqualTo(registered.Count), "every originally registered album lyric re-registered, no extras");
            Assert.That(lyrics.Select(l => l.Id).Intersect(registered.Select(b => b.OriginalLyricId)), Is.Empty, "deleted lyric row IDs never reused");
            foreach (var b in manifest.Bindings)
            {
                var track = Db.All<Track>().Single(t => t.ForeignRecordingId == b.RecordingMbid && t.AlbumReleaseId == w.Release.Id);
                var lyricPath = Path.Combine(w.Artist.Path, b.LyricRelativePath);
                Assert.That(P0ShaFile(lyricPath), Is.EqualTo(b.LyricSha256), $"track {b.AbsoluteTrackNumber}: physical lyric bytes");
                Assert.That(P0ShaFile(Path.Combine(w.Artist.Path, b.AudioRelativePath)), Is.EqualTo(b.AudioSha256), $"track {b.AbsoluteTrackNumber}: audio bytes");
                Assert.That(track.TrackFileId, Is.GreaterThan(0));
                Assert.That(track.TrackFileId, Is.Not.EqualTo(b.OriginalTrackFileId), $"track {b.AbsoluteTrackNumber}: the stock rescan issued a fresh TrackFile ID");
                var forFile = lyrics.Where(l => l.TrackFileId == track.TrackFileId).ToList();
                if (b.LyricRegistered)
                {
                    Assert.That(forFile.Count, Is.EqualTo(1), $"track {b.AbsoluteTrackNumber}: lyric bound to the FRESH TrackFile ID");
                    Assert.That(Path.GetFullPath(Path.Combine(w.Artist.Path, forFile[0].RelativePath)), Is.EqualTo(Path.GetFullPath(lyricPath)));
                }
                else
                {
                    Assert.That(forFile, Is.Empty, $"track {b.AbsoluteTrackNumber}: originally unregistered lyric stays unregistered");
                }
            }

            var after = P0SnapshotFiles(worldRoot);
            foreach (var entry in filesBefore)
            {
                Assert.That(after.TryGetValue(entry.Key, out var sha) && sha == entry.Value, Is.True, $"recovery changed or removed an unrelated file: {entry.Key}");
            }

            var created = after.Keys.Except(filesBefore.Keys).OrderBy(x => x).ToList();
            var expected = expectRestored.Select(i => Path.GetFullPath(w.LyricPaths[i])).OrderBy(x => x).ToList();
            Assert.That(created, Is.EqualTo(expected), "recovery created exactly the missing lyric files and nothing else");
            Assert.That(Db.All<AlbumRelease>().Single(r => r.Monitored).ForeignReleaseId, Is.EqualTo(ReleaseMbid));
            Log($"P0_CRASH_RECOVERED restored=[{string.Join(",", expectRestored)}] lyricRows={lyrics.Count} allFreshTrackFileIds=true unrelatedFilesUnchanged=true");
        }

        private void P0AssertUnchanged(Dictionary<string, string> files, List<string> rows, List<string> links, string worldRoot, string stage)
        {
            Assert.That(P0SnapshotFiles(worldRoot), Is.EqualTo(files), stage + ": files changed");
            Assert.That(P0LyricRowSnapshot(), Is.EqualTo(rows), stage + ": lyric rows changed");
            Assert.That(P0LinkSnapshot(), Is.EqualTo(links), stage + ": track links changed");
        }

        private List<int> P0RegisteredLyricTracks()
        {
            var tracks = Db.All<Track>();
            return Db.All<LyricFile>().Select(l => tracks.FirstOrDefault(t => t.TrackFileId == l.TrackFileId)?.AbsoluteTrackNumber ?? -1)
                .OrderBy(x => x).ToList();
        }

        private List<string> P0LyricRowSnapshot() =>
            Db.All<LyricFile>().OrderBy(l => l.Id).Select(l => $"{l.Id}:{l.TrackFileId}:{l.RelativePath}").ToList();

        private List<string> P0LinkSnapshot() =>
            Db.All<Track>().OrderBy(t => t.Id).Select(t => $"{t.Id}:{t.TrackFileId}").ToList();

        private void P0InsertForeignAlbumTrackSharing(World w, int trackFileId)
        {
            var album = Builder<Album>.CreateNew()
                .With(x => x.Id = 0).With(x => x.ArtistMetadataId = w.Artist.ArtistMetadataId)
                .With(x => x.ForeignAlbumId = "aaaaaaaa-0000-4000-8000-000000000001").With(x => x.Title = "Other Synthetic Album")
                .With(x => x.CleanTitle = "othersyntheticalbum").With(x => x.AlbumType = "Album")
                .With(x => x.SecondaryTypes = new List<SecondaryAlbumType>()).With(x => x.Genres = new List<string>())
                .With(x => x.Images = new List<MediaCover.MediaCover>()).With(x => x.Links = new List<Links>())
                .With(x => x.OldForeignAlbumIds = new List<string>()).With(x => x.Monitored = true)
                .Build();
            Db.Insert(album);
            var release = Builder<AlbumRelease>.CreateNew()
                .With(x => x.Id = 0).With(x => x.AlbumId = album.Id).With(x => x.ForeignReleaseId = "aaaaaaaa-0000-4000-8000-000000000002")
                .With(x => x.Label = new List<string>()).With(x => x.Country = new List<string>())
                .With(x => x.OldForeignReleaseIds = new List<string>())
                .With(x => x.Media = new List<Medium> { new Medium { Number = 1, Name = "", Format = "Digital Media" } })
                .With(x => x.Monitored = true)
                .Build();
            Db.Insert(release);
            var track = Builder<Track>.CreateNew()
                .With(x => x.Id = 0).With(x => x.ArtistMetadataId = w.Artist.ArtistMetadataId).With(x => x.AlbumReleaseId = release.Id)
                .With(x => x.TrackFileId = trackFileId).With(x => x.MediumNumber = 1).With(x => x.AbsoluteTrackNumber = 1)
                .With(x => x.ForeignTrackId = "aaaaaaaa-0000-4000-8000-000000000003").With(x => x.ForeignRecordingId = "aaaaaaaa-0000-4000-8000-000000000004")
                .With(x => x.OldForeignTrackIds = new List<string>()).With(x => x.OldForeignRecordingIds = new List<string>())
                .Build();
            Db.Insert(track);
        }

        private void P0SwitchMonitoredRelease(World w)
        {
            var other = Builder<AlbumRelease>.CreateNew()
                .With(x => x.Id = 0).With(x => x.AlbumId = w.Album.Id).With(x => x.ForeignReleaseId = "bbbbbbbb-0000-4000-8000-000000000001")
                .With(x => x.Label = new List<string>()).With(x => x.Country = new List<string>())
                .With(x => x.OldForeignReleaseIds = new List<string>())
                .With(x => x.Media = new List<Medium> { new Medium { Number = 1, Name = "", Format = "CD" } })
                .With(x => x.Monitored = true)
                .Build();
            Db.Insert(other);
            var original = Db.All<AlbumRelease>().Single(r => r.ForeignReleaseId == ReleaseMbid);
            original.Monitored = false;
            Db.Update(original);
        }

        private void P0RelocateDatabasePaths(string oldWorld, string newWorld)
        {
            string Move(string path) => Path.Combine(newWorld, Path.GetRelativePath(oldWorld, path));
            var artist = Db.All<Artist>().Single();
            artist.Path = Move(artist.Path);
            Db.Update(artist);
            foreach (var file in Db.All<TrackFile>())
            {
                file.Path = Move(file.Path);
                Db.Update(file);
            }
        }

        // ------------------------------------------------------------------ process, file and SQLite plumbing

        private static string P0ValidatedRoot()
        {
            var root = Environment.GetEnvironmentVariable(P0RootVariable);
            Assert.That(root, Is.Not.Null.And.Not.Empty, P0RootVariable);
            Assert.That(Path.IsPathFullyQualified(root), Is.True);
            Assert.That(Path.GetFileName(root.TrimEnd('/', '\\')).StartsWith(P0RootPrefix), Is.True, "only a disposable crescendarr-p0-* root");
            return root;
        }

        private static void P0HardKill(string root, string phase, string detail)
        {
            var evidence = Path.Combine(root, $"kill-{phase}.evidence");
            P0WriteDurable(evidence, Encoding.UTF8.GetBytes($"pid={Environment.ProcessId} utc={DateTime.UtcNow:O} {detail}\n"));
            Log($"P0_CRASH_SIGKILL phase={phase} {detail}");
            Process.GetCurrentProcess().Kill();
            Thread.Sleep(Timeout.Infinite);
        }

        private static void P0AssertKilled(string root, string phase)
        {
            var evidence = Path.Combine(root, $"kill-{phase}.evidence");
            Assert.That(File.Exists(evidence), Is.True, $"the {phase} process must have reached its SIGKILL point");
            Assert.That(int.Parse(File.ReadAllText(evidence).Split(' ')[0].Substring(4)), Is.Not.EqualTo(Environment.ProcessId),
                "recovery runs in a DIFFERENT process from the one that was killed");
            Log("P0_CRASH_PREVIOUS_KILL " + File.ReadAllText(evidence).Trim());
        }

        private static void P0WriteDurable(string path, byte[] bytes)
        {
            var temporary = path + ".partial";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            File.Move(temporary, path, false);
        }

        private static void P0AppendJournal(string root, string line)
        {
            using (var stream = new FileStream(Path.Combine(root, "journal.log"), FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            Log("P0_CRASH_JOURNAL " + line);
        }

        private static List<string> P0ReadJournal(string root)
        {
            var path = Path.Combine(root, "journal.log");
            return File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0).ToList() : new List<string>();
        }

        private static string P0Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private static string P0ShaFile(string path) => P0Sha(File.ReadAllBytes(path));

        private static bool P0SamePath(string artistPath, string relative, string fullPath) =>
            Path.GetFullPath(Path.Combine(artistPath, relative)) == Path.GetFullPath(fullPath);

        private static Dictionary<string, string> P0SnapshotFiles(string directory) =>
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetFullPath(f), P0ShaFile);

        private static void P0CopyDirectory(string source, string destination)
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }
        }

        private static void P0ReleaseConnections()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SQLiteConnection.ClearAllPools();
        }

        /// <summary>
        /// Replace THIS process's freshly migrated disposable test database with another process's files.
        /// For a killed process this copies the main file AND its write-ahead log exactly as SIGKILL left them,
        /// so SQLite's own WAL recovery decides which commits survived.
        /// </summary>
        private void P0ImportDatabase(string sourceDb, string evidenceDirectory)
        {
            Assert.That(File.Exists(sourceDb), Is.True, "source database " + sourceDb);
            if (evidenceDirectory != null)
            {
                Directory.CreateDirectory(evidenceDirectory);
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    if (File.Exists(sourceDb + suffix))
                    {
                        File.Copy(sourceDb + suffix, Path.Combine(evidenceDirectory, "lidarr.db" + suffix), overwrite: false);
                        Log($"P0_CRASH_DB_FILE lidarr.db{suffix} bytes={new FileInfo(sourceDb + suffix).Length}");
                    }
                }
            }

            var target = TestFolderInfo.GetDatabase();
            P0ReleaseConnections();
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(target + suffix))
                {
                    File.Delete(target + suffix);
                }
            }

            File.Copy(sourceDb, target, overwrite: true);
            if (File.Exists(sourceDb + "-wal"))
            {
                File.Copy(sourceDb + "-wal", target + "-wal", overwrite: true);
            }

            P0ReleaseConnections();
            using (var connection = Db.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check";
                Assert.That(command.ExecuteScalar() as string, Is.EqualTo("ok"), "reopened SQLite integrity");
            }
        }

        private void P0ExportDatabase(string destination)
        {
            var source = TestFolderInfo.GetDatabase();
            P0ReleaseConnections();
            File.Copy(source, destination, overwrite: false);
            if (File.Exists(source + "-wal"))
            {
                File.Copy(source + "-wal", destination + "-wal", overwrite: false);
            }
        }
    }
}
