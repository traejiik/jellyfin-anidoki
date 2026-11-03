using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Notifications.Discord;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
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

    [Test]
    public async Task AProviderThatFailsDoesNotStopTheOthers() {
        using var harness = new SyncHarness();
        Guid user = Guid.NewGuid();
        harness.ConfigureUser(user, false, false, ApiName.AniList, ApiName.Mal);
        harness.SeedAll(SyncHarness.Entry(500, "Fixture Show", 12, Watching(3)));
        harness.Tracker(ApiName.AniList).ReadFailure = TrackerFailureKind.Unauthorized;
        var episode = harness.Library.Episode("Fixture Show", 1, 5);

        string result = await harness.Run(harness.NewUpdater(), episode, user, SyncUpdateSource.Playback, ApiName.AniList, ApiName.Mal);

        Assert.That(result, Does.Contain("AniList: no writes | Failed:unauthorized@?"));
        Assert.That(result, Does.Contain("Confirmed:progress@500"));
        Assert.That(harness.Tracker(ApiName.Mal).Writes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AFailureDuringResolutionRecordsTheSeries() {
        using var harness = new SyncHarness();
        Guid user = Guid.NewGuid();
        harness.ConfigureUser(user, false, false, ApiName.AniList);
        harness.SeedAll(SyncHarness.Entry(500, "Fixture Show", 12, Watching(3)));
        harness.Tracker(ApiName.AniList).ReadFailure = TrackerFailureKind.Transient;
        var collector = new PlaybackOutcomeCollector();

        await harness.NewUpdater().UpdateInsideGate(harness.Library.Episode("Fixture Show", 1, 5), user, true, collector, SyncUpdateSource.Playback);

        var outcome = collector.Outcomes.Single();
        Assert.That((outcome.Kind, outcome.Step, outcome.TargetId, outcome.TargetTitle),
            Is.EqualTo((OutcomeKind.Failed, "transient", "", "Fixture Show")));
    }

    [TestCase("unauthorized")]
    [TestCase("rate-limited")]
    [TestCase("transient")]
    [TestCase("rejected")]
    [TestCase("stale")]
    public void EveryFailureStepIsWordedAsAFailedUpdate(string step) =>
        Assert.That(DiscordOutcomeText.Result(new ProviderWriteOutcome(ApiName.AniList, "500", "Fixture Show", OutcomeKind.Failed, null, null, false, step, false), false),
            Is.EqualTo("Update failed"));
}
