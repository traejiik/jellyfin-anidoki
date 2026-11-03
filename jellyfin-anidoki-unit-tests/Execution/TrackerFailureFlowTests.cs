using System;
using System.IO;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Execution;

[TestFixture]
public class TrackerFailureFlowTests {
    private static MyListStatus Watching(int progress) => new() { Status = Status.Watching, NumEpisodesWatched = progress, RewatchCount = 0 };

    [Test]
    public async Task ASyncFillsTheRecordedColumns() {
        string directory = OperationTestSupport.NewDirectory();
        try {
            using var harness = new SyncHarness();
            Guid user = Guid.NewGuid();
            Guid id = Guid.NewGuid();
            harness.ConfigureUser(user, false, false, ApiName.AniList);
            harness.SeedAll(SyncHarness.Entry(500, "Fixture Show", 12, Watching(3)));
            var database = new AniDokiDatabase(Path.Combine(directory, "anidoki.db"));
            var store = new OperationStore(database, TimeProvider.System);
            store.Create(id, user, OperationKind.Playback, "x");
            var context = new OperationContext(id, user, store, NullLogger.Instance, default);
            var episode = harness.Library.Episode("Fixture Show", 1, 5);

            await harness.NewUpdater().UpdateInsideGate(episode, user, true, new PlaybackOutcomeCollector(context.Record), SyncUpdateSource.Playback);

            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_item, source_episode, target_episode, resolved_by, planned_change, planned_against FROM operation_actions";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo(episode.Id.ToString("D")));
            Assert.That(reader.GetInt32(1), Is.EqualTo(5));
            Assert.That(reader.GetInt32(2), Is.EqualTo(5));
            Assert.That(reader.IsDBNull(3), Is.False);
            Assert.That(reader.GetString(4), Does.Contain("\"Progress\":5"));
            Assert.That(reader.GetString(5), Does.Contain("\"Progress\":3"));
        } finally {
            Directory.Delete(directory, true);
        }
    }
}
