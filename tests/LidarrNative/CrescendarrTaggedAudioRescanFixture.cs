// M14 (issue #38): stock Lidarr DiskScanService with REAL tag reading, identification and
// import decisions over valid, synthetically generated, tagged audio in a disposable folder.
// Source-pinned Lidarr v3.1.6.5079 test project only. No server, network, NAS, downloader,
// user library or Lidarr modification. Synthetic audio is generated here at runtime
// (silent MPEG-1 Layer III frames and a FLAC STREAMINFO stream) and tagged with Lidarr's
// own AudioTag writer; it is never committed as a binary.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FizzWare.NBuilder;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Lyrics;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.MediaFiles.TrackImport.Aggregation;
using NzbDrone.Core.MediaFiles.TrackImport.Aggregation.Aggregators;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.MediaFiles.TrackImport.Specifications;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Mono.Disk;
using NzbDrone.Test.Common;
using NzbDrone.Test.Common.AutoMoq;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrTaggedAudioRescanFixture : DbTest
    {
        private const int Total = 12;
        private const int Upgrades = 11;
        private const int Sixth = 6;
        private const string ArtistName = "Crescendarr Synthetic Artist";
        private const string AlbumTitle = "Crescendarr Synthetic Album";
        private const string ArtistMbid = "b8e9bf54-3424-4691-8657-7b7e3ff902e5";
        private const string ReleaseGroupMbid = "c5b4e91e-40fa-42c1-a25b-4f1220713587";
        private const string ReleaseMbid = "9fd3c58e-91db-4d89-86a7-e57558394c1f";

        public enum Scenario
        {
            /// <summary>Baseline control: all twelve stock tracks and lyrics already registered; no unknown file.</summary>
            CleanFullyLinkedBaseline,

            /// <summary>Unknown track, but protected lyric uses an ordinary "12 - Track Name.lrc" filename.</summary>
            ConventionalProtectedLyricName,

            /// <summary>No import: track six simply has a valid, unregistered file. Proves a real decision is made.</summary>
            ControlUnlinkedValidFile,

            /// <summary>Sixth transfer fails after stock delete; original restored from backup before any fresh row.</summary>
            RestoredOriginalBeforeFreshRow,

            /// <summary>Sixth transfer COMPLETES, then the importer fails before the new row is inserted.</summary>
            IncomingTransferredButUnlinked,

            /// <summary>A real scan runs INSIDE the importer, at track six, after the stock deletion events of tracks 1-6.</summary>
            ScanInterleavedInsideImporter,

            /// <summary>Quarantined original is invisible until copied back to an unlinked final path.</summary>
            QuarantineThenPromoteBeforeRow,
        }

        [TestCase(Scenario.CleanFullyLinkedBaseline)]
        [TestCase(Scenario.ControlUnlinkedValidFile)]
        [TestCase(Scenario.ConventionalProtectedLyricName)]
        [TestCase(Scenario.RestoredOriginalBeforeFreshRow)]
        [TestCase(Scenario.IncomingTransferredButUnlinked)]
        [TestCase(Scenario.ScanInterleavedInsideImporter)]
        public void native_tagged_audio_rescan_characterization(Scenario scenario)
        {
            var w = BuildWorld(sixthUnlinkedFromStart: scenario == Scenario.ControlUnlinkedValidFile || scenario == Scenario.ConventionalProtectedLyricName,
                conventionalProtectedLyric: scenario == Scenario.ConventionalProtectedLyricName);
            WireRealEventsAndStockDisk(w);
            var recorder = WireRealScan(w);

            if (scenario == Scenario.CleanFullyLinkedBaseline)
            {
                // Causal control: same artist, album, tags, physical lyrics, event subscribers,
                // scanner and real decision maker, but no unregistered audio file.
                var originalLinks = Db.All<Track>().ToDictionary(t => t.AbsoluteTrackNumber, t => t.TrackFileId);
                var originalRows = Db.All<TrackFile>().Select(f => f.Id).OrderBy(id => id).ToArray();
                var originalLyrics = Db.All<LyricFile>().ToDictionary(l => l.Id, l => l.TrackFileId);
                var baselineScan = RunRealScan(w, recorder, "fully-linked-baseline");
                var finalLinks = Db.All<Track>().ToDictionary(t => t.AbsoluteTrackNumber, t => t.TrackFileId);
                var finalRows = Db.All<TrackFile>().Select(f => f.Id).OrderBy(id => id).ToArray();
                var finalLyrics = Db.All<LyricFile>().ToDictionary(l => l.Id, l => l.TrackFileId);
                var linksChanged = originalLinks.Any(p => !finalLinks.TryGetValue(p.Key, out var id) || id != p.Value);
                var lyricLinksChanged = originalLyrics.Count != finalLyrics.Count ||
                    originalLyrics.Any(p => !finalLyrics.TryGetValue(p.Key, out var id) || id != p.Value);
                Log($"M14_TAGGED_CLEAN_BASELINE decisions={baselineScan.Decisions.Count} approved={baselineScan.Decisions.Count(d => d.Approved)} " +
                    $"linksChanged={linksChanged} rowIdsChanged={!originalRows.SequenceEqual(finalRows)} " +
                    $"lyricRowsChanged={lyricLinksChanged}");
                CollectInvariants(w, scenario);
                Assert.That(linksChanged, Is.False, "fully linked baseline unexpectedly changed Track links");
                Assert.That(finalRows, Is.EqualTo(originalRows), "fully linked baseline unexpectedly changed TrackFile row IDs");
                Assert.That(lyricLinksChanged, Is.False, "fully linked baseline unexpectedly changed LyricFile links");
                foreach (var i in Enumerable.Range(1, Total))
                    Assert.That(File.ReadAllBytes(w.LyricPaths[i]), Is.EqualTo(w.LyricBytes[i]),
                        $"fully linked baseline unexpectedly modified physical lyric {i}");
                AssertHeldInvariants(w, scenario);
                return;
            }

            if (scenario == Scenario.ConventionalProtectedLyricName)
            {
                var protectedLyric = w.LyricPaths[Total];
                Assert.That(Path.GetFileName(protectedLyric), Is.EqualTo("12 - Synthetic Track 12.lrc"));
                var parsed = NzbDrone.Core.Parser.Parser.ParseMusicPath(protectedLyric);
                var filenameParse = NzbDrone.Core.Parser.Parser.ParseMusicTitle(Path.GetFileName(protectedLyric));
                Log($"M14_TAGGED_CONVENTIONAL_LYRIC pathParsed={(parsed != null)} " +
                    $"pathTrackNumbers=[{string.Join(",", parsed?.TrackNumbers ?? Array.Empty<int>())}] " +
                    $"filenameTrackNumbers=[{string.Join(",", filenameParse?.TrackNumbers ?? Array.Empty<int>())}] " +
                    $"filename={Path.GetFileName(protectedLyric)}");
                var namedLyricScan = RunRealScan(w, recorder, "conventional-protected-lyric");
                AssertStockFullAlbumRescanCascade(w, namedLyricScan, scenario);
                Assert.That(File.Exists(protectedLyric), Is.False,
                    "even a conventionally named registered lyric was physically recycled before rediscovery");
                Assert.That(Db.All<LyricFile>(), Is.Empty,
                    "existing-lyric importer could not recover a lyric already recycled by native delete handlers");
                AssertHeldInvariants(w, scenario);
                return;
            }

            if (scenario == Scenario.ControlUnlinkedValidFile)
            {
                Assert.That(Db.All<Track>().Single(t => t.AbsoluteTrackNumber == Sixth).TrackFileId, Is.EqualTo(0));
                var controlScan = RunRealScan(w, recorder, "control");
                AssertStockFullAlbumRescanCascade(w, controlScan, scenario);
                AssertSixthLinkedAfterScan(w, Quality.MP3_192, w.OwnedBytes[Sixth]);
                AssertHeldInvariants(w, scenario);
                return;
            }

            ScanObservation interleaved = null;
            ScanObservation scan = null;
            w.TransferHook = (source, destination, mode, overwrite) =>
            {
                if (source != w.IncomingPaths[Sixth] || destination != w.OwnedPaths[Sixth])
                {
                    return w.NativeTransfer.TransferFile(source, destination, mode, overwrite);
                }

                switch (scenario)
                {
                    case Scenario.RestoredOriginalBeforeFreshRow:
                        throw new IOException("SYNTHETIC: sixth transfer interrupted after stock delete (M14 tagged)");
                    case Scenario.IncomingTransferredButUnlinked:
                        w.NativeTransfer.TransferFile(source, destination, mode, overwrite);
                        throw new IOException("SYNTHETIC: failure after the sixth transfer completed, before the row insert");
                    case Scenario.ScanInterleavedInsideImporter:
                        interleaved = RunRealScan(w, recorder, "interleaved");
                        return w.NativeTransfer.TransferFile(source, destination, mode, overwrite);
                    default:
                        throw new InvalidOperationException("unexpected scenario");
                }
            };

            List<ImportResult> results = null;
            Exception importFailure = null;
            try
            {
                results = ImportUpgrades(w);
            }
            catch (Exception error) when (scenario == Scenario.ScanInterleavedInsideImporter)
            {
                importFailure = error;
            }

            if (scenario == Scenario.ScanInterleavedInsideImporter)
            {
                CharacterizeInterleavedScan(w, interleaved, importFailure);
                return;
            }

            Log($"M14_TAGGED_IMPORT_RESULTS scenario={scenario} " + string.Join(",", results.Select(r => r.Result)));
            LogDb("after-import", w);

            switch (scenario)
            {
                case Scenario.RestoredOriginalBeforeFreshRow:
                    Assert.That(results.Count(r => r.Result == ImportResultType.Imported), Is.EqualTo(Upgrades - 1));
                    Assert.That(File.Exists(w.OwnedPaths[Sixth]), Is.False, "sixth original recycled by stock upgrader");
                    Assert.That(Db.All<Track>().Single(t => t.AbsoluteTrackNumber == Sixth).TrackFileId, Is.EqualTo(0),
                        "real stock deletion handler detaches track six");

                    // M13's automatic RestoreOriginalBytes step: verified backup back in place, no overwrite,
                    // and deliberately NO fresh row yet (that step needs operator approval in M13).
                    File.Copy(w.BackupPaths[Sixth], w.OwnedPaths[Sixth], overwrite: false);
                    Assert.That(File.ReadAllBytes(w.OwnedPaths[Sixth]), Is.EqualTo(w.OwnedBytes[Sixth]));
                    scan = RunRealScan(w, recorder, "after-restore");
                    AssertStockFullAlbumRescanCascade(w, scan, scenario);
                    AssertSixthLinkedAfterScan(w, Quality.MP3_192, w.OwnedBytes[Sixth]);
                    break;

                case Scenario.IncomingTransferredButUnlinked:
                    Assert.That(File.ReadAllBytes(w.OwnedPaths[Sixth]), Is.EqualTo(w.IncomingBytes[Sixth]),
                        "incoming sixth bytes placed at the destination");
                    Assert.That(Db.All<TrackFile>().Any(f => f.Path == w.OwnedPaths[Sixth]), Is.False,
                        "no row for the placed sixth file before the scan");
                    scan = RunRealScan(w, recorder, "after-unlinked-transfer");
                    AssertStockFullAlbumRescanCascade(w, scan, scenario);
                    AssertSixthLinkedAfterScan(w, Quality.MP3_256, w.IncomingBytes[Sixth]);
                    break;
            }

            AssertHeldInvariants(w, scenario);
        }


        // P0 issue #41: an unknown file with ALL album lyrics DB-detached. We intentionally leave
        // physical sidecars on disk and protect a separate byte-verified copy of each one.
        // This is NOT a safe production protocol: a process crash in the detach/relink window
        // leaves zero registered lyrics until an explicit recovery has rebuilt associations.
        [Test]
        public void all_album_lyric_detach_survives_unknown_file_scan_but_needs_explicit_relink()
        {
            var w = BuildWorld(sixthUnlinkedFromStart: true);
            WireRealEventsAndStockDisk(w);
            var recorder = WireRealScan(w);

            var registered = Db.All<LyricFile>();
            Assert.That(registered.Count, Is.EqualTo(Total - 1), "the unknown sixth has no TrackFile/lyric row");
            var lyricBackups = new Dictionary<int, string>();
            for (var i = 1; i <= Total; i++)
            {
                var backup = Path.Combine(w.Backups, $"lyric-{i:D2}.lrc");
                Assert.That(File.ReadAllBytes(w.LyricPaths[i]), Is.EqualTo(w.LyricBytes[i]));
                File.Copy(w.LyricPaths[i], backup, overwrite: false);
                Assert.That(File.ReadAllBytes(backup), Is.EqualTo(w.LyricBytes[i]), "independent lyric backup verified");
                lyricBackups.Add(i, backup);
            }

            foreach (var lyric in registered) w.Lyrics.Delete(lyric.Id);
            Assert.That(Db.All<LyricFile>(), Is.Empty, "all album lyric DB rows detached before stock scan");

            var scan = RunRealScan(w, recorder, "whole-album-lyric-detach");
            Assert.That(scan.Decisions.Count, Is.EqualTo(Total), "the unknown audio still causes a full tagged scan");
            Assert.That(scan.Decisions.All(d => d.Approved), Is.True);
            Assert.That(Db.All<LyricFile>(), Is.Empty, "no lyric rediscovery in this synthetic name layout");
            for (var i = 1; i <= Total; i++)
            {
                Assert.That(File.ReadAllBytes(w.LyricPaths[i]), Is.EqualTo(w.LyricBytes[i]),
                    $"the physically detached lyric {i} was unexpectedly recycled");
                Assert.That(File.ReadAllBytes(lyricBackups[i]), Is.EqualTo(w.LyricBytes[i]),
                    $"independent lyric backup {i} changed");
                Assert.That(Db.All<Track>().Single(t => t.AbsoluteTrackNumber == i).TrackFileId, Is.GreaterThan(0));
            }

            // The human-approval-only style repair: insert fresh lyric rows against current
            // host IDs. Do NOT resurrect deleted numeric lyric IDs. No production adapter here.
            for (var i = 1; i <= Total; i++)
            {
                var currentTrackFileId = Db.All<Track>().Single(t => t.AbsoluteTrackNumber == i).TrackFileId;
                w.Lyrics.Upsert(new LyricFile
                {
                    ArtistId = w.Artist.Id,
                    AlbumId = w.Album.Id,
                    TrackFileId = currentTrackFileId,
                    RelativePath = Path.GetRelativePath(w.Artist.Path, w.LyricPaths[i]),
                    Extension = ".lrc"
                });
            }

            var freshLyrics = Db.All<LyricFile>();
            Assert.That(freshLyrics.Count, Is.EqualTo(Total), "all album physical lyrics re-registered with fresh IDs");
            Assert.That(freshLyrics.Select(l => l.Id).Intersect(registered.Select(l => l.Id)), Is.Empty,
                "never reuse deleted lyric row IDs");
            foreach (var i in Enumerable.Range(1, Total))
            {
                var t = Db.All<Track>().Single(t => t.AbsoluteTrackNumber == i);
                var l = freshLyrics.Single(l => l.TrackFileId == t.TrackFileId);
                Assert.That(Path.GetFullPath(Path.Combine(w.Artist.Path, l.RelativePath)),
                    Is.EqualTo(w.LyricPaths[i]), $"relinked lyric {i} path is not host-authoritative");
                Assert.That(File.ReadAllBytes(w.LyricPaths[i]), Is.EqualTo(w.LyricBytes[i]));
            }

            AssertHeldInvariants(w, Scenario.ControlUnlinkedValidFile);
            Log("P0_ALBUM_LYRIC_DETACH: full rescan did not recycle any unregistered lyric; all 12 physical bytes and backups survived; operator-style fresh row reattachment verified. This DOES NOT prove crash-safe or concurrent detach.");
        }

        // P0 issue #41: scan-invisible backup/quarantine is a delay mechanism, not an atomic
        // repair. The first scan must leave protected registered lyrics intact; after
        // premature promotion to an unlinked visible path, the same scan must demonstrate
        // the existing album-wide reimport/lyric-recycle hazard.
        [Test]
        public void quarantined_original_avoids_rescan_until_unsafe_visible_promotion()
        {
            var w = BuildWorld(sixthUnlinkedFromStart: false);
            WireRealEventsAndStockDisk(w);
            var recorder = WireRealScan(w);
            w.TransferHook = (source, destination, mode, overwrite) =>
            {
                if (source == w.IncomingPaths[Sixth] && destination == w.OwnedPaths[Sixth])
                    throw new IOException("SYNTHETIC: sixth transfer failed before placement; test quarantine");
                return w.NativeTransfer.TransferFile(source, destination, mode, overwrite);
            };

            var result = ImportUpgrades(w);
            Assert.That(result.Count(r => r.Result == ImportResultType.Imported), Is.EqualTo(Upgrades - 1));
            Assert.That(Db.All<Track>().Single(t => t.AbsoluteTrackNumber == Sixth).TrackFileId, Is.EqualTo(0));
            Assert.That(File.Exists(w.OwnedPaths[Sixth]), Is.False);
            var protectedRow = Db.All<Track>().Single(t => t.AbsoluteTrackNumber == Total).TrackFileId;
            Assert.That(Db.All<LyricFile>().Count, Is.EqualTo(1), "only the unselected protected lyric remains registered");

            var hiddenDirectory = Path.Combine(w.Library, ".crescendarr-recovery");
            Directory.CreateDirectory(hiddenDirectory);
            var quarantined = Path.Combine(hiddenDirectory, "06-original.mp3");
            File.Copy(w.BackupPaths[Sixth], quarantined, overwrite: false);
            Assert.That(File.ReadAllBytes(quarantined), Is.EqualTo(w.OwnedBytes[Sixth]));
            Assert.That(DiskScanService.ExcludedSubFoldersRegex.IsMatch(
                Path.GetRelativePath(w.Artist.Path, quarantined)), Is.True,
                "hidden quarantine must be excluded by the actual stock file-filter grammar");

            // Simulates two ordinary rescans while the plugin is absent. The original is
            // protected in a backup/hidden quarantine but is not yet a usable library file.
            for (var scanNumber = 1; scanNumber <= 2; scanNumber++)
            {
                var scan = RunRealScan(w, recorder, "quarantine-safe-" + scanNumber);
                Assert.That(scan.Decisions, Is.Empty, "quarantined audio unexpectedly reached stock import decisions");
                Assert.That(Db.All<Track>().Single(t => t.AbsoluteTrackNumber == Total).TrackFileId,
                    Is.EqualTo(protectedRow), "stock rescan replaced the protected superior file row");
                Assert.That(Db.All<LyricFile>().Single().TrackFileId, Is.EqualTo(protectedRow),
                    "protected lyric registration changed while the original was quarantined");
                Assert.That(File.ReadAllBytes(w.LyricPaths[Total]), Is.EqualTo(w.LyricBytes[Total]));
                Assert.That(File.ReadAllBytes(quarantined), Is.EqualTo(w.OwnedBytes[Sixth]));
                Assert.That(File.Exists(w.OwnedPaths[Sixth]), Is.False);
            }

            Log("P0_QUARANTINE_SAFE: two real scans ignored the hidden sixth original, retained the protected FLAC row and physical lyric; track six is STILL unlinked and needs recovery.");

            // Counterexample: restoring visibly before an authoritative TrackFile row/link
            // invites stock's album-wide rescan. This is deliberately an unsafe experiment.
            File.Move(quarantined, w.OwnedPaths[Sixth]);
            var premature = RunRealScan(w, recorder, "quarantine-premature-promotion");
            AssertStockFullAlbumRescanCascade(w, premature, Scenario.QuarantineThenPromoteBeforeRow);
            AssertSixthLinkedAfterScan(w, Quality.MP3_192, w.OwnedBytes[Sixth]);
            AssertHeldInvariants(w, Scenario.QuarantineThenPromoteBeforeRow);
            Log("P0_QUARANTINE_UNSAFE_PROMOTION: newly visible unlinked original triggered a fresh full-album reimport and recycled the protected lyric. Quarantine alone is NOT a safe finalization protocol.");
        }

        /// <summary>
        /// CHARACTERIZED STOCK HAZARD (observed in native CI, not a Crescendarr guarantee): one unknown audio
        /// file makes the real scan identify the album with IncludeExisting=true, emit an approved decision for
        /// EVERY existing album file, and re-import each one: its TrackFile row is deleted with ManualOverride
        /// and re-inserted under a new ID. The stock extra-file handler then recycles every lyric that was still
        /// registered to a deleted row; the stock existing-lyric importer does not re-register them.
        /// </summary>
        private void AssertStockFullAlbumRescanCascade(World w, ScanObservation scan, Scenario scenario)
        {
            var tracks = Db.All<Track>().ToDictionary(t => t.AbsoluteTrackNumber);
            var rows = Db.All<TrackFile>().ToDictionary(f => f.Id);
            Assert.That(scan.Decisions.Count, Is.EqualTo(Total), "real decisions for every album file, not just the unknown one");
            Assert.That(scan.Decisions.All(d => d.Approved), Is.True, "every real decision approved");
            Assert.That(scan.Decisions.Count(d => d.Item.AdditionalFile), Is.EqualTo(Total - 1), "all but the unknown file arrived as additional existing files");

            for (var i = 1; i <= Total; i++)
            {
                if (scan.LinksBefore[i] > 0)
                {
                    Assert.That(tracks[i].TrackFileId, Is.Not.EqualTo(scan.LinksBefore[i]), $"track {i} relinked to a NEW row by the scan");
                    Assert.That(rows.ContainsKey(scan.LinksBefore[i]), Is.False, $"track {i} pre-scan row deleted");
                }

                Assert.That(rows.TryGetValue(tracks[i].TrackFileId, out var row) && row.Path == w.OwnedPaths[i], Is.True, $"track {i} linked to a row at its own path");

                var recycledLyric = Directory.Exists(w.Recycle)
                    ? Directory.EnumerateFiles(w.Recycle, Path.GetFileName(w.LyricPaths[i]), SearchOption.AllDirectories).ToList()
                    : new List<string>();
                if (scan.RegisteredLyricTracksBefore.Contains(i))
                {
                    Assert.That(File.Exists(w.LyricPaths[i]), Is.False, $"registered lyric {i} removed from the library by the scan");
                    Assert.That(recycledLyric.Count, Is.EqualTo(1), $"registered lyric {i} moved to the recycle bin");
                    Assert.That(File.ReadAllBytes(recycledLyric[0]), Is.EqualTo(w.LyricBytes[i]), $"recycled lyric {i} bytes intact");
                }
                else
                {
                    Assert.That(File.ReadAllBytes(w.LyricPaths[i]), Is.EqualTo(w.LyricBytes[i]), $"unregistered (detached) lyric {i} untouched by the scan");
                    Assert.That(recycledLyric, Is.Empty, $"unregistered lyric {i} not recycled");
                }
            }

            Assert.That(Db.All<LyricFile>(), Is.Empty, "no lyric row survives or is re-registered after the scan");
            Assert.That(scan.RegisteredLyricTracksBefore.Contains(Total), Is.True, "the protected track's lyric was registered before the scan");
            Log($"M14_TAGGED_CASCADE scenario={scenario} relinked={scan.LinksBefore.Count(kv => kv.Value > 0)} " +
                $"lyricsRecycled={scan.RegisteredLyricTracksBefore.Count} lyricRowsAfter=0 protectedFlacRowReplaced=true");
        }

        private void AssertSixthLinkedAfterScan(World w, Quality expected, byte[] bytes)
        {
            var sixth = Db.All<Track>().Single(t => t.AbsoluteTrackNumber == Sixth);
            var row = Db.All<TrackFile>().Single(f => f.Id == sixth.TrackFileId);
            Assert.That(row.Path, Is.EqualTo(w.OwnedPaths[Sixth]), "stock scan linked track six to the file at its path");
            Assert.That(row.Quality.Quality, Is.EqualTo(expected), "quality read from the actual file");
            Assert.That(File.ReadAllBytes(w.OwnedPaths[Sixth]), Is.EqualTo(bytes), "the scan did not rewrite the sixth file");
        }

        /// <summary>Invariants that HELD in every characterized scenario.</summary>
        private void AssertHeldInvariants(World w, Scenario scenario)
        {
            Assert.That(File.ReadAllBytes(w.OwnedPaths[Total]), Is.EqualTo(w.OwnedBytes[Total]), "protected superior FLAC bytes unchanged");
            Assert.That(File.ReadAllBytes(w.IncomingPaths[Total]), Is.EqualTo(w.IncomingBytes[Total]), "rejected inferior candidate still staged");
            for (var i = 1; i <= Total; i++)
            {
                Assert.That(File.ReadAllBytes(w.BackupPaths[i]), Is.EqualTo(w.OwnedBytes[i]), $"independent backup {i} intact");
            }

            var releases = Db.All<AlbumRelease>();
            Assert.That(releases.Count(r => r.Monitored), Is.EqualTo(1));
            Assert.That(releases.Single(r => r.Monitored).ForeignReleaseId, Is.EqualTo(ReleaseMbid), "monitored MusicBrainz release unchanged");
            Assert.That(Db.All<TrackFile>().GroupBy(f => f.Path).Any(g => g.Count() > 1), Is.False, "stock schema keeps TrackFile paths unique");
            _ = CollectInvariants(w, scenario);
        }

        /// <summary>
        /// A scan forced to run INSIDE the importer (after tracks 1-5 were moved and their old rows deleted, before
        /// the importer's batch insert). Stock Lidarr serializes scans and imports with its disk-access command lock,
        /// so this interleaving is test-forced, not a demonstrated production race.
        /// </summary>
        private void CharacterizeInterleavedScan(World w, ScanObservation interleaved, Exception importFailure)
        {
            Assert.That(interleaved, Is.Not.Null, "the interleaved scan ran inside the importer");
            Assert.That(interleaved.Decisions.Count, Is.EqualTo(Total - 1), "real decisions for the 11 files present at that moment (track six recycled, not yet transferred)");
            Assert.That(importFailure, Is.Not.Null, "the importer did not complete normally after the interleaved scan");
            var sqlite = importFailure as System.Data.SQLite.SQLiteException ?? importFailure.InnerException as System.Data.SQLite.SQLiteException;
            Assert.That(sqlite, Is.Not.Null, "importer failure type: " + importFailure.GetType().Name);
            Assert.That(sqlite.Message, Does.Contain("UNIQUE constraint failed: TrackFiles.Path"), "batch insert collided with rows the scan created");
            LogDb("after-interleaved-import-failure", w);

            // Observed end state: DB and disk disagree, and nothing reports it.
            var tracks = Db.All<Track>().ToDictionary(t => t.AbsoluteTrackNumber);
            var rows = Db.All<TrackFile>().ToDictionary(f => f.Id);
            for (var i = 1; i <= Upgrades; i++)
            {
                Assert.That(File.ReadAllBytes(w.OwnedPaths[i]), Is.EqualTo(w.IncomingBytes[i]), $"incoming bytes in place for track {i}");
            }

            Assert.That(tracks[Sixth].TrackFileId, Is.EqualTo(0), "track six left unlinked although its incoming file is in place");
            for (var i = Sixth + 1; i <= Upgrades; i++)
            {
                Assert.That(rows[tracks[i].TrackFileId].Quality.Quality, Is.EqualTo(Quality.MP3_192),
                    $"track {i} still linked to the scan-created row describing the replaced 192k original");
            }

            Assert.That(Db.All<LyricFile>(), Is.Empty, "every lyric registration lost");
            Log("M14_TAGGED_INTERLEAVED: importer batch insert failed on UNIQUE(TrackFiles.Path); tracks 7-11 rows describe replaced originals; track 6 unlinked; no lyric rows");
            AssertHeldInvariants(w, Scenario.ScanInterleavedInsideImporter);
        }

        // ------------------------------------------------------------------ world

        private sealed class World
        {
            public string Temp;
            public string Library;
            public string Staging;
            public string Recycle;
            public string Backups;
            public Artist Artist;
            public Album Album;
            public AlbumRelease Release;
            public LyricFileService Lyrics;
            public readonly Dictionary<int, Track> Tracks = new Dictionary<int, Track>();
            public readonly Dictionary<int, TrackFile> Previous = new Dictionary<int, TrackFile>();
            public readonly Dictionary<int, string> OwnedPaths = new Dictionary<int, string>();
            public readonly Dictionary<int, string> IncomingPaths = new Dictionary<int, string>();
            public readonly Dictionary<int, string> BackupPaths = new Dictionary<int, string>();
            public readonly Dictionary<int, string> LyricPaths = new Dictionary<int, string>();
            public readonly Dictionary<int, byte[]> OwnedBytes = new Dictionary<int, byte[]>();
            public readonly Dictionary<int, byte[]> IncomingBytes = new Dictionary<int, byte[]>();
            public readonly Dictionary<int, byte[]> LyricBytes = new Dictionary<int, byte[]>();
            public readonly Dictionary<int, int> LyricIds = new Dictionary<int, int>();
            public DiskTransferService NativeTransfer;
            public Func<string, string, TransferMode, bool, TransferMode> TransferHook;
            public EventAggregator Bus;
            public MediaFileService MediaFiles;
            public TrackService TrackService;
        }

        private sealed class ScanObservation
        {
            public string Label;
            public List<ImportDecision<LocalTrack>> Decisions = new List<ImportDecision<LocalTrack>>();
            public Dictionary<int, int> LinksBefore;
            public HashSet<int> RegisteredLyricTracksBefore;
        }

        /// <summary>Records every decision the REAL stock decision maker returns.</summary>
        private sealed class RecordingDecisionMaker : IMakeImportDecision
        {
            private readonly IMakeImportDecision _inner;
            public readonly List<List<ImportDecision<LocalTrack>>> Calls = new List<List<ImportDecision<LocalTrack>>>();

            public RecordingDecisionMaker(IMakeImportDecision inner) => _inner = inner;

            public List<ImportDecision<LocalTrack>> GetImportDecisions(List<System.IO.Abstractions.IFileInfo> musicFiles, IdentificationOverrides idOverrides, ImportDecisionMakerInfo itemInfo, ImportDecisionMakerConfig config)
            {
                var decisions = _inner.GetImportDecisions(musicFiles, idOverrides, itemInfo, config);
                Calls.Add(decisions);
                return decisions;
            }
        }

        private static double TrackSeconds(int i) => 10 + i;

        private static string Uuid(int kind, int i) => $"0000{kind:0000}-0000-4000-8000-{i:000000000000}";

        private static string RecordingMbid(int i) => Uuid(1, i);

        private static string TrackMbid(int i) => Uuid(2, i);

        /// <summary>Silent MPEG-1 Layer III, 44.1 kHz mono, constant bitrate, zero payload frames.</summary>
        private static byte[] SilentMp3(int kbps, double seconds, out int durationMs)
        {
            var index = kbps switch { 128 => 9, 192 => 11, 256 => 13, 320 => 14, _ => throw new ArgumentException("bitrate") };
            var frameLength = 144 * kbps * 1000 / 44100;
            var frames = (int)Math.Ceiling(seconds * 44100 / 1152.0);
            var bytes = new byte[frames * frameLength];
            for (var f = 0; f < frames; f++)
            {
                var o = f * frameLength;
                bytes[o] = 0xFF;
                bytes[o + 1] = 0xFB;                    // MPEG-1, Layer III, no CRC
                bytes[o + 2] = (byte)(index << 4);       // bitrate index, 44.1 kHz, no padding
                bytes[o + 3] = 0xC4;                     // mono, original
            }

            durationMs = (int)Math.Round(bytes.LongLength * 8.0 / kbps);
            return bytes;
        }

        /// <summary>FLAC stream: marker + STREAMINFO (16-bit mono 44.1 kHz, exact sample count) + opaque frame area.</summary>
        private static byte[] SyntheticFlac(double seconds, out int durationMs)
        {
            const int rate = 44100;
            var samples = (long)(seconds * rate);
            var info = new byte[34];
            info[0] = 0x10; info[1] = 0x00;              // min block 4096
            info[2] = 0x10; info[3] = 0x00;              // max block 4096
            // min/max frame size unknown (0): bytes 4..9 stay zero
            var packed = ((ulong)rate << 44) | (0UL << 41) | (15UL << 36) | (ulong)samples;
            for (var b = 0; b < 8; b++) info[10 + b] = (byte)(packed >> (56 - (8 * b)));
            // MD5 (bytes 18..33) zero = unknown
            var header = new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 0x80, 0x00, 0x00, 0x22 }; // last block, STREAMINFO, length 34
            var audio = new byte[(int)(seconds * 40000)];
            durationMs = (int)(samples * 1000 / rate);
            return header.Concat(info).Concat(audio).ToArray();
        }

        private static void Tag(string path, int i)
        {
            new AudioTag
            {
                Title = $"Synthetic Track {i}",
                Performers = new[] { ArtistName },
                AlbumArtists = new[] { ArtistName },
                Album = AlbumTitle,
                Track = (uint)i,
                TrackCount = Total,
                Disc = 1,
                DiscCount = 1,
                Year = 2020,
                MusicBrainzReleaseId = ReleaseMbid,
                MusicBrainzReleaseGroupId = ReleaseGroupMbid,
                MusicBrainzArtistId = ArtistMbid,
                MusicBrainzReleaseArtistId = ArtistMbid,
                MusicBrainzTrackId = RecordingMbid(i),
                MusicBrainzReleaseTrackId = TrackMbid(i)
            }.Write(path);
        }

        private World BuildWorld(bool sixthUnlinkedFromStart, bool conventionalProtectedLyric = false)
        {
            var w = new World { Temp = Path.Combine(TestFolderInfo.AppDataFolder, "crescendarr-m14-tagged") };
            w.Library = Path.Combine(w.Temp, "library", "synthetic-artist", "synthetic-album");
            w.Staging = Path.Combine(w.Temp, "staging");
            w.Recycle = Path.Combine(w.Temp, "synthetic-recycle");
            w.Backups = Path.Combine(w.Temp, "verified-copies");
            foreach (var d in new[] { w.Library, w.Staging, w.Recycle, w.Backups }) Directory.CreateDirectory(d);

            var metadata = Builder<ArtistMetadata>.CreateNew()
                .With(x => x.Id = 0).With(x => x.Name = ArtistName).With(x => x.ForeignArtistId = ArtistMbid).Build();
            Db.Insert(metadata);

            var profile = new QualityProfile { Name = "Crescendarr synthetic profile", Items = Qualities.QualityFixture.GetDefaultQualities(), Cutoff = Quality.FLAC.Id };
            Db.Insert(profile);

            w.Artist = Builder<Artist>.CreateNew()
                .With(x => x.Id = 0).With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.Path = Path.GetDirectoryName(w.Library))
                .With(x => x.QualityProfileId = profile.Id).With(x => x.QualityProfile = profile)
                .With(x => x.Metadata = metadata)
                .Build();
            Db.Insert(w.Artist);

            w.Album = Builder<Album>.CreateNew()
                .With(x => x.Id = 0).With(x => x.ArtistMetadataId = metadata.Id)
                .With(x => x.ForeignAlbumId = ReleaseGroupMbid).With(x => x.Title = AlbumTitle)
                .With(x => x.CleanTitle = "crescendarrsyntheticalbum")
                .With(x => x.ReleaseDate = new DateTime(2020, 1, 1)).With(x => x.AlbumType = "Album")
                .With(x => x.SecondaryTypes = new List<SecondaryAlbumType>()).With(x => x.Genres = new List<string>())
                .With(x => x.Images = new List<MediaCover.MediaCover>()).With(x => x.Links = new List<Links>())
                .With(x => x.OldForeignAlbumIds = new List<string>()).With(x => x.Monitored = true)
                .With(x => x.Artist = w.Artist).With(x => x.ArtistMetadata = metadata)
                .Build();
            Db.Insert(w.Album);

            w.Release = Builder<AlbumRelease>.CreateNew()
                .With(x => x.Id = 0).With(x => x.AlbumId = w.Album.Id).With(x => x.ForeignReleaseId = ReleaseMbid)
                .With(x => x.Title = AlbumTitle).With(x => x.Status = "Official")
                .With(x => x.Label = new List<string>()).With(x => x.Country = new List<string>())
                .With(x => x.Disambiguation = "").With(x => x.OldForeignReleaseIds = new List<string>())
                .With(x => x.ReleaseDate = new DateTime(2020, 1, 1))
                .With(x => x.Media = new List<Medium> { new Medium { Number = 1, Name = "", Format = "Digital Media" } })
                .With(x => x.TrackCount = Total).With(x => x.Monitored = true)
                .Build();
            Db.Insert(w.Release);
            w.Release.Album = w.Album;
            w.Album.AlbumReleases = new List<AlbumRelease> { w.Release };

            var extraRepository = Mocker.Resolve<ExtraFileRepository<LyricFile>>();
            Mocker.SetConstant<IExtraFileRepository<LyricFile>>(extraRepository);
            w.Lyrics = Mocker.Resolve<LyricFileService>();
            Mocker.SetConstant<IExtraFileService<LyricFile>>(w.Lyrics);

            for (var i = 1; i <= Total; i++)
            {
                var stem = i.ToString("D2") + "-synthetic";
                var flac = i == Total;
                w.OwnedPaths[i] = Path.Combine(w.Library, stem + (flac ? ".flac" : ".mp3"));
                w.IncomingPaths[i] = Path.Combine(w.Staging, stem + ".mp3");
                w.LyricPaths[i] = Path.Combine(w.Library,
                    conventionalProtectedLyric && i == Total ? "12 - Synthetic Track 12.lrc" : stem + ".lrc");
                w.BackupPaths[i] = Path.Combine(w.Backups, Path.GetFileName(w.OwnedPaths[i]));

                var ownedAudio = flac ? SyntheticFlac(TrackSeconds(i), out var durationMs) : SilentMp3(192, TrackSeconds(i), out durationMs);
                File.WriteAllBytes(w.OwnedPaths[i], ownedAudio);
                Tag(w.OwnedPaths[i], i);
                File.WriteAllBytes(w.IncomingPaths[i], SilentMp3(256, TrackSeconds(i), out _));
                Tag(w.IncomingPaths[i], i);
                w.OwnedBytes[i] = File.ReadAllBytes(w.OwnedPaths[i]);
                w.IncomingBytes[i] = File.ReadAllBytes(w.IncomingPaths[i]);
                w.LyricBytes[i] = Encoding.UTF8.GetBytes($"[00:00.00] SYNTHETIC LYRIC {i}\n");
                File.WriteAllBytes(w.LyricPaths[i], w.LyricBytes[i]);
                File.Copy(w.OwnedPaths[i], w.BackupPaths[i]);

                // Prove the generated audio is valid for the stock tag reader BEFORE using it.
                var read = new AudioTag(w.OwnedPaths[i]);
                Assert.That(read.IsValid, Is.True, $"stock AudioTag could not read synthetic track {i}");
                Assert.That(read.MusicBrainzReleaseId, Is.EqualTo(ReleaseMbid));
                Assert.That(read.MusicBrainzTrackId, Is.EqualTo(RecordingMbid(i)));
                Assert.That(read.Quality?.Quality, Is.EqualTo(flac ? Quality.FLAC : Quality.MP3_192), $"quality of owned track {i}");
                Assert.That(new AudioTag(w.IncomingPaths[i]).Quality?.Quality, Is.EqualTo(Quality.MP3_256), $"quality of incoming track {i}");

                var unlinked = sixthUnlinkedFromStart && i == Sixth;
                TrackFile old = null;
                if (!unlinked)
                {
                    old = new TrackFile
                    {
                        Path = w.OwnedPaths[i],
                        AlbumId = w.Album.Id,
                        Size = w.OwnedBytes[i].LongLength,
                        Modified = File.GetLastWriteTimeUtc(w.OwnedPaths[i]),
                        DateAdded = DateTime.UtcNow,
                        Quality = new QualityModel(flac ? Quality.FLAC : Quality.MP3_192),
                        MediaInfo = read.MediaInfo,
                        Artist = new LazyLoaded<Artist>(w.Artist)
                    };
                    Db.Insert(old);
                    w.Previous[i] = old;
                }

                var track = Builder<Track>.CreateNew()
                    .With(x => x.Id = 0).With(x => x.ArtistMetadataId = metadata.Id)
                    .With(x => x.AlbumReleaseId = w.Release.Id)
                    .With(x => x.TrackFileId = old?.Id ?? 0)
                    .With(x => x.Title = $"Synthetic Track {i}")
                    .With(x => x.TrackNumber = i.ToString()).With(x => x.AbsoluteTrackNumber = i)
                    .With(x => x.MediumNumber = 1).With(x => x.Duration = durationMs)
                    .With(x => x.ForeignTrackId = TrackMbid(i)).With(x => x.ForeignRecordingId = RecordingMbid(i))
                    .With(x => x.OldForeignTrackIds = new List<string>()).With(x => x.OldForeignRecordingIds = new List<string>())
                    .With(x => x.TrackFile = old == null ? null : new LazyLoaded<TrackFile>(old))
                    .Build();
                Db.Insert(track);
                w.Tracks[i] = track;

                if (old != null)
                {
                    w.Lyrics.Upsert(new LyricFile
                    {
                        ArtistId = w.Artist.Id,
                        AlbumId = w.Album.Id,
                        TrackFileId = old.Id,
                        RelativePath = Path.Combine("synthetic-album", Path.GetFileName(w.LyricPaths[i])),
                        Extension = ".lrc"
                    });
                    var registeredLyric = Db.All<LyricFile>().Single(x => x.TrackFileId == old.Id);
                    // The registered host-relative path must name the actual physical lyric
                    // in EVERY fixture variant, including the conventional-name control.
                    Assert.That(Path.GetFullPath(Path.Combine(w.Artist.Path, registeredLyric.RelativePath)),
                        Is.EqualTo(w.LyricPaths[i]), $"track {i}: test lyric binding points at a decoy path");
                    w.LyricIds[i] = registeredLyric.Id;
                }
            }

            Mocker.GetMock<IArtistService>().Setup(x => x.GetArtists(It.IsAny<IEnumerable<int>>())).Returns(new List<Artist> { w.Artist });
            Mocker.GetMock<IArtistService>().Setup(x => x.GetArtist(w.Artist.Id)).Returns(w.Artist);
            Mocker.GetMock<IAlbumService>().Setup(x => x.GetAlbum(w.Album.Id)).Returns(w.Album);
            Mocker.GetMock<IRootFolderService>().Setup(x => x.GetBestRootFolder(It.IsAny<string>()))
                .Returns(new RootFolder { Path = Path.Combine(w.Temp, "library"), DefaultQualityProfileId = profile.Id });
            return w;
        }

        // ------------------------------------------------------------------ wiring

        private void WireRealEventsAndStockDisk(World w)
        {
            Mocker.SetConstant<IMediaFileRepository>(Mocker.Resolve<MediaFileRepository>());
            Mocker.SetConstant<ITrackRepository>(Mocker.Resolve<TrackRepository>());
            Mocker.SetConstant<IReleaseRepository>(Mocker.Resolve<ReleaseRepository>());
            w.TrackService = Mocker.Resolve<TrackService>();
            Mocker.SetConstant<ITrackService>(w.TrackService);
            Mocker.SetConstant<IReleaseService>(Mocker.Resolve<ReleaseService>());

            // Real disk, mount, symlink and recycle services FIRST, so every stock service built
            // afterwards (including the lyric deletion handler) sees the actual filesystem. An earlier
            // evidence run built LyricFileService before this point; its mocked FileExists returned
            // false and hid whether stock recycles physical lyrics.
            Mocker.SetConstant<IProcMountProvider>(Mocker.Resolve<ProcMountProvider>());
            Mocker.SetConstant<ISymbolicLinkResolver>(Mocker.Resolve<SymbolicLinkResolver>());
            Mocker.SetConstant<IDiskProvider>(Mocker.Resolve<IDiskProvider>(FileSystemType.Actual));
            Mocker.GetMock<IConfigService>().SetupGet(x => x.RecycleBin).Returns(w.Recycle);
            Mocker.SetConstant<IRecycleBinProvider>(Mocker.Resolve<RecycleBinProvider>());
            // AutoMoqer caches resolved concrete types, so Resolve<LyricFileService>() would return the
            // instance built in BuildWorld with a MOCKED disk provider. Construct it explicitly.
            w.Lyrics = new LyricFileService(Mocker.Resolve<IExtraFileRepository<LyricFile>>(), Mocker.Resolve<IArtistService>(),
                Mocker.Resolve<IDiskProvider>(), Mocker.Resolve<IRecycleBinProvider>(), LogManager.GetLogger("CrescendarrM14Lyric"));
            Mocker.SetConstant<IExtraFileService<LyricFile>>(w.Lyrics);
            Assert.That(Db.All<LyricFile>().Count, Is.GreaterThan(0), "lyric rows were registered before wiring");

            // Real synchronous stock deletion subscribers, registered BEFORE the importer runs.
            Mocker.GetMock<IServiceFactory>().Setup(x => x.BuildAll<IHandle<TrackFileDeletedEvent>>())
                .Returns(new List<IHandle<TrackFileDeletedEvent>> { w.TrackService, w.Lyrics });
            w.Bus = Mocker.Resolve<EventAggregator>();
            Mocker.SetConstant<IEventAggregator>(w.Bus);
            w.MediaFiles = new MediaFileService(Mocker.Resolve<IMediaFileRepository>(), w.Bus, LogManager.GetLogger("CrescendarrM14Tagged"));
            Mocker.SetConstant<IMediaFileService>(w.MediaFiles);

            w.NativeTransfer = Mocker.Resolve<DiskTransferService>();
            Mocker.GetMock<IDiskTransferService>()
                .Setup(x => x.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()))
                .Returns((string s, string d, TransferMode m, bool o) =>
                    w.TransferHook != null ? w.TransferHook(s, d, m, o) : w.NativeTransfer.TransferFile(s, d, m, o));

            Mocker.GetMock<IBuildFileNames>()
                .Setup(x => x.BuildTrackFilePath(It.IsAny<List<Track>>(), It.IsAny<Artist>(), It.IsAny<Album>(),
                    It.IsAny<TrackFile>(), It.IsAny<string>(), It.IsAny<NamingConfig>(), It.IsAny<List<CustomFormat>>()))
                .Returns((List<Track> tracks, Artist a, Album al, TrackFile tf, string ext, NamingConfig n, List<CustomFormat> cf) =>
                    w.OwnedPaths[tracks.Single().AbsoluteTrackNumber]);
            Mocker.GetMock<IImportScript>()
                .Setup(x => x.TryImport(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LocalTrack>(), It.IsAny<TrackFile>(), It.IsAny<TransferMode>(), null))
                .Returns(ScriptImportDecision.DeferMove);
            Mocker.SetConstant<IMoveTrackFiles>(Mocker.Resolve<TrackFileMovingService>());
            Mocker.SetConstant<IUpgradeMediaFiles>(Mocker.Resolve<UpgradeMediaFileService>());
        }

        private RecordingDecisionMaker WireRealScan(World w)
        {
            var log = LogManager.GetLogger("CrescendarrM14TaggedScan");
            var tags = Mocker.Resolve<AudioTagService>();
            Mocker.SetConstant<IAudioTagService>(tags);
            var augmenting = new AugmentingService(
                new List<IAggregate<LocalTrack>> { Mocker.Resolve<AggregateQuality>(), Mocker.Resolve<AggregateReleaseGroup>() },
                new List<IAggregate<LocalAlbumRelease>> { Mocker.Resolve<AggregateFilenameInfo>() },
                Mocker.Resolve<IDiskProvider>(), log);
            Mocker.SetConstant<IAugmentingService>(augmenting);
            Mocker.SetConstant<ITrackGroupingService>(Mocker.Resolve<TrackGroupingService>());
            Mocker.SetConstant<ICandidateService>(Mocker.Resolve<CandidateService>());
            var identification = Mocker.Resolve<IdentificationService>();

            var config = Mocker.GetMock<IConfigService>().Object;
            var trackSpecs = new List<IImportDecisionEngineSpecification<LocalTrack>>
            {
                new NzbDrone.Core.MediaFiles.TrackImport.Specifications.FreeSpaceSpecification(Mocker.Resolve<IDiskProvider>(), config, log),
                new NotUnpackingSpecification(Mocker.Resolve<IDiskProvider>(), config, log),
                new SameTracksImportSpecification(new SameTracksSpecification(w.TrackService), log),
                new UpgradeSpecification(config, Mocker.GetMock<ICustomFormatCalculationService>().Object, log),
                new CloseTrackMatchSpecification(log),
            };
            var albumSpecs = new List<IImportDecisionEngineSpecification<LocalAlbumRelease>>
            {
                new AlbumUpgradeSpecification(log),
                new NzbDrone.Core.MediaFiles.TrackImport.Specifications.AlreadyImportedSpecification(Mocker.GetMock<IHistoryService>().Object, log),
                new ArtistPathInRootFolderSpecification(Mocker.GetMock<IRootFolderService>().Object, log),
                new CloseAlbumMatchSpecification(log),
                new MoreTracksSpecification(log),
                new NoMissingOrUnmatchedTracksSpecification(log),
                new ReleaseWantedSpecification(log),
            };
            var maker = new ImportDecisionMaker(trackSpecs, albumSpecs, w.MediaFiles, tags, augmenting, identification,
                Mocker.GetMock<IRootFolderService>().Object, Mocker.GetMock<IQualityProfileService>().Object, log);
            var recorder = new RecordingDecisionMaker(maker);
            Mocker.SetConstant<IMakeImportDecision>(recorder);
            Mocker.SetConstant<IMediaFileTableCleanupService>(Mocker.Resolve<MediaFileTableCleanupService>());
            return recorder;
        }

        private List<ImportResult> ImportUpgrades(World w)
        {
            var nativeQuality = new UpgradeSpecification(Mocker.GetMock<IConfigService>().Object,
                Mocker.GetMock<ICustomFormatCalculationService>().Object, LogManager.GetLogger("CrescendarrM14Quality"));
            var decisions = new List<ImportDecision<LocalTrack>>();
            for (var i = 1; i <= Total; i++)
            {
                var local = new LocalTrack
                {
                    Artist = w.Artist,
                    Album = w.Album,
                    Release = w.Release,
                    Tracks = new List<Track> { Db.All<Track>().Single(t => t.AbsoluteTrackNumber == i) },
                    Path = w.IncomingPaths[i],
                    Size = w.IncomingBytes[i].LongLength,
                    Modified = File.GetLastWriteTimeUtc(w.IncomingPaths[i]),
                    Quality = new QualityModel(Quality.MP3_256),
                    FileTrackInfo = new ParsedTrackInfo { ReleaseGroup = "CRESCENDARR-SYNTHETIC" }
                };
                local.Tracks[0].TrackFile = new LazyLoaded<TrackFile>(w.Previous[i]);
                var accepted = nativeQuality.IsSatisfiedBy(local, null).Accepted;
                Assert.That(accepted, Is.EqualTo(i != Total), "stock quality approves 11 upgrades and rejects the FLAC");
                decisions.Add(accepted ? new ImportDecision<LocalTrack>(local) : new ImportDecision<LocalTrack>(local, new Rejection("Inferior to owned FLAC")));
            }

            // Same lyric protection as M12/M13: remove only the DB association of the eleven targets.
            for (var i = 1; i <= Upgrades; i++) w.Lyrics.Delete(w.LyricIds[i]);

            var importer = Mocker.Resolve<ImportApprovedTracks>();
            ExceptionVerification.IgnoreWarns();
            return importer.Import(decisions, false, importMode: ImportMode.Move);
        }

        private ScanObservation RunRealScan(World w, RecordingDecisionMaker recorder, string label)
        {
            // The scan's own importer must see the real tag service, so resolve it now.
            Mocker.SetConstant<IImportApprovedTracks>(Mocker.Resolve<ImportApprovedTracks>());
            var scanner = Mocker.Resolve<DiskScanService>();
            Mocker.SetConstant<IDiskScanService>(scanner);
            var existingExtras = new ExistingExtraFileService(Mocker.Resolve<IDiskProvider>(), scanner,
                new List<IImportExistingExtraFiles> { new ExistingLyricImporter(w.Lyrics, Mocker.Resolve<IAugmentingService>(), LogManager.GetLogger("CrescendarrM14Lyrics")) },
                LogManager.GetLogger("CrescendarrM14Extras"));
            Mocker.GetMock<IServiceFactory>().Setup(x => x.BuildAll<IHandle<ArtistScannedEvent>>())
                .Returns(new List<IHandle<ArtistScannedEvent>> { existingExtras });

            var before = recorder.Calls.Count;
            var linksBefore = Db.All<Track>().ToDictionary(t => t.AbsoluteTrackNumber, t => t.TrackFileId);
            var registeredBefore = new HashSet<int>(Db.All<LyricFile>()
                .Select(l => Db.All<Track>().FirstOrDefault(t => t.TrackFileId == l.TrackFileId))
                .Where(t => t != null).Select(t => t.AbsoluteTrackNumber));
            ExceptionVerification.IgnoreWarns();
            scanner.Scan(new List<string> { w.Artist.Path }, FilterFilesType.Known, false, new List<int> { w.Artist.Id });
            Assert.That(recorder.Calls.Count, Is.EqualTo(before + 1), "real decision maker invoked once by the scan");

            var observation = new ScanObservation { Label = label, Decisions = recorder.Calls.Last(), LinksBefore = linksBefore, RegisteredLyricTracksBefore = registeredBefore };
            foreach (var d in observation.Decisions)
            {
                var item = d.Item;
                Log($"M14_TAGGED_DECISION scan={label} file={Path.GetFileName(item.Path)} approved={d.Approved} " +
                    $"tracks=[{string.Join(",", item.Tracks?.Select(t => t.AbsoluteTrackNumber) ?? Enumerable.Empty<int>())}] " +
                    $"release={item.Release?.ForeignReleaseId} quality={item.Quality?.Quality} existing={item.ExistingFile} additional={item.AdditionalFile} " +
                    $"rejections=[{string.Join(" | ", d.Rejections.Select(r => r.Reason))}]");
            }

            LogDb("after-scan-" + label, w);
            return observation;
        }

        // ------------------------------------------------------------------ assertions and evidence

        /// <summary>
        /// Collects EVERY Crescendarr invariant outcome (no early exit) and logs it, so a single CI run
        /// yields the complete stock behavior. Returns the violated invariants.
        /// </summary>
        private List<string> CollectInvariants(World w, Scenario scenario)
        {
            var violations = new List<string>();
            void Check(bool ok, string name)
            {
                Log($"M14_TAGGED_INVARIANT scenario={scenario} {(ok ? "held" : "VIOLATED")} {name}");
                if (!ok) violations.Add(name);
            }

            var tracks = Db.All<Track>().ToDictionary(t => t.AbsoluteTrackNumber);
            var rows = Db.All<TrackFile>();
            var lyricRows = Db.All<LyricFile>();
            Check(File.Exists(w.OwnedPaths[Total]) && File.ReadAllBytes(w.OwnedPaths[Total]).SequenceEqual(w.OwnedBytes[Total]), "protected superior FLAC bytes unchanged");
            Check(tracks[Total].TrackFileId == w.Previous[Total].Id, "protected track still linked to its original TrackFile row");
            Check(lyricRows.Any(l => l.Id == w.LyricIds[Total] && l.TrackFileId == w.Previous[Total].Id), "protected lyric row unchanged");
            for (var i = 1; i <= Total; i++)
            {
                Check(File.Exists(w.LyricPaths[i]) && File.ReadAllBytes(w.LyricPaths[i]).SequenceEqual(w.LyricBytes[i]), $"physical lyric {i} present with original bytes");
                Check(File.ReadAllBytes(w.BackupPaths[i]).SequenceEqual(w.OwnedBytes[i]), $"independent backup {i} intact");
            }

            var releases = Db.All<AlbumRelease>();
            Check(releases.Count(r => r.Monitored) == 1 && releases.Single(r => r.Monitored).ForeignReleaseId == ReleaseMbid, "monitored MusicBrainz release unchanged");
            Check(!rows.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1), "no two TrackFile rows share a path");
            Check(rows.All(f => tracks.Values.Any(t => t.TrackFileId == f.Id)), "every TrackFile row is linked to a track");
            Check(tracks.Values.All(t => t.TrackFileId == 0 || rows.Any(f => f.Id == t.TrackFileId)), "no track points at a missing TrackFile row");

            var recycled = Directory.Exists(w.Recycle)
                ? Directory.EnumerateFiles(w.Recycle, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(x => x).ToList()
                : new List<string>();
            Log($"M14_TAGGED_RECYCLE scenario={scenario} count={recycled.Count} lrc={recycled.Count(x => x.EndsWith(".lrc"))} files=[{string.Join(",", recycled)}]");
            Log($"M14_TAGGED_SUMMARY scenario={scenario} trackFiles={rows.Count} lyricRows={lyricRows.Count} violations={violations.Count}");
            return violations;
        }

        private void LogDb(string stage, World w)
        {
            var files = Db.All<TrackFile>().ToDictionary(f => f.Id);
            var lyricsByFile = Db.All<LyricFile>().GroupBy(l => l.TrackFileId).ToDictionary(g => g.Key, g => g.Count());
            foreach (var t in Db.All<Track>().OrderBy(t => t.AbsoluteTrackNumber))
            {
                files.TryGetValue(t.TrackFileId, out var f);
                Log($"M14_TAGGED_STATE stage={stage} track={t.AbsoluteTrackNumber} trackFileId={t.TrackFileId} " +
                    $"row={(f == null ? "none" : Path.GetFileName(f.Path) + "/" + f.Quality?.Quality)} " +
                    $"lyrics={(lyricsByFile.TryGetValue(t.TrackFileId, out var n) ? n : 0)} " +
                    $"diskBytes={(File.Exists(w.OwnedPaths[t.AbsoluteTrackNumber]) ? (File.ReadAllBytes(w.OwnedPaths[t.AbsoluteTrackNumber]).SequenceEqual(w.OwnedBytes[t.AbsoluteTrackNumber]) ? "original" : File.ReadAllBytes(w.OwnedPaths[t.AbsoluteTrackNumber]).SequenceEqual(w.IncomingBytes[t.AbsoluteTrackNumber]) ? "incoming" : "other") : "missing")}");
            }

            foreach (var f in files.Values.Where(f => !Db.All<Track>().Any(t => t.TrackFileId == f.Id)))
            {
                Log($"M14_TAGGED_STATE stage={stage} unlinkedRow id={f.Id} path={Path.GetFileName(f.Path)}");
            }
        }

        private static void Log(string line) => TestContext.Progress.WriteLine(line);
    }
}
