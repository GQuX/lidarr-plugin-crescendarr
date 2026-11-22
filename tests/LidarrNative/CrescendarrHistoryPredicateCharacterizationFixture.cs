// Source-pinned, read-only characterization: tests real Lidarr history predicate
// with synthetic history records. It does not fake the predicate's return value.
using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CrescendarrHistoryPredicateCharacterizationFixture : CoreTest<TrackedDownloadAlreadyImported>
    {
        private TrackedDownload MakeDownload()
        {
            return new TrackedDownload
            {
                DownloadItem = new DownloadClientItem { Title = "Synthetic 12-track album" },
                RemoteAlbum = new RemoteAlbum
                {
                    Artist = new Artist { Id = 4 },
                    Albums = new List<Album>
                    {
                        new Album
                        {
                            Id = 101,
                            AlbumReleases = new List<AlbumRelease>
                            {
                                new AlbumRelease { Monitored = true, TrackCount = 12 }
                            }
                        }
                    }
                }
            };
        }

        [Test]
        public void single_track_imported_history_row_satisfies_twelve_track_album()
        {
            var history = new List<EntityHistory>
            {
                new EntityHistory
                {
                    AlbumId = 101,
                    TrackId = 700,
                    EventType = EntityHistoryEventType.TrackFileImported,
                    Date = DateTime.UtcNow
                }
            };

            // No real 12-track completeness check is performed by this predicate.
            Assert.That(Subject.IsImported(MakeDownload(), history), Is.True);
        }

        [Test]
        public void latest_album_import_incomplete_event_blocks_earlier_track_import_history()
        {
            var now = DateTime.UtcNow;
            // The CompletedDownloadService caller supplies history in newest-first order.
            var history = new List<EntityHistory>
            {
                new EntityHistory { AlbumId = 101, EventType = EntityHistoryEventType.AlbumImportIncomplete, Date = now },
                new EntityHistory { AlbumId = 101, TrackId = 700, EventType = EntityHistoryEventType.TrackFileImported, Date = now.AddSeconds(-1) }
            };

            Assert.That(Subject.IsImported(MakeDownload(), history), Is.False);
        }

        [Test]
        public void empty_history_does_not_claim_album_complete()
        {
            Assert.That(Subject.IsImported(MakeDownload(), new List<EntityHistory>()), Is.False);
        }
    }
}
