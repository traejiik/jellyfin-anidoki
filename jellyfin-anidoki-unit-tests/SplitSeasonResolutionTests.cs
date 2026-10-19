using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki_unit_tests.Characterization;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests;

/// <summary>
/// A tracker can split a season the ID databases keep whole: AniDB, TVDB and TMDB list Steel Ball Run as one season,
/// while AniList splits it into a one-episode "1st STAGE" and a sequel. IDs resolve to the first part, so an episode
/// past its length must follow the sequel chain, as the title-search path already does.
/// </summary>
[TestFixture, NonParallelizable]
public class SplitSeasonResolutionTests {
    private const int SequelId = 501;

    private static readonly ApiName[] IdProviders = [ApiName.Mal, ApiName.AniList, ApiName.Kitsu];

    private readonly Guid _user = Guid.NewGuid();
    private SyncHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new SyncHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [TestCaseSource(nameof(IdProviders))]
    public async Task EpisodePastCompletedFirstPartGoesToSequel(ApiName provider) {
        // Episode 1 already completed the first part; the second part is still airing, so its length is unknown.
        SeedSplitSeason(firstPartEpisodes: 1, Completed(1), sequelEpisodes: 0);

        await Sync(provider, episodeNumber: 2);

        var writes = _harness.Tracker(provider).Writes;
        Assert.That(writes, Has.Count.EqualTo(1));
        Assert.That(writes[0].EntryId, Is.EqualTo(SequelId));
        Assert.That(writes[0].Progress, Is.EqualTo(1));
        Assert.That(writes[0].Status, Is.EqualTo(Status.Watching));
    }

    [TestCaseSource(nameof(IdProviders))]
    public async Task EpisodePastUnwatchedFirstPartCompletesItThenWritesSequel(ApiName provider) {
        SeedSplitSeason(firstPartEpisodes: 1, status: null, sequelEpisodes: 12);

        await Sync(provider, episodeNumber: 3);

        var writes = _harness.Tracker(provider).Writes.Select(write => (write.EntryId, write.Progress, write.Status));
        Assert.That(writes, Is.EqualTo(new[] {
            (SyncHarness.EntryId, 1, Status.Completed),
            (SequelId, 2, Status.Watching)
        }));
    }

    [TestCaseSource(nameof(IdProviders))]
    public async Task EpisodeWithinFirstPartStaysOnIt(ApiName provider) {
        SeedSplitSeason(firstPartEpisodes: 12, Watching(3), sequelEpisodes: 12);

        await Sync(provider, episodeNumber: 5);

        var writes = _harness.Tracker(provider).Writes;
        Assert.That(writes, Has.Count.EqualTo(1));
        Assert.That(writes[0].EntryId, Is.EqualTo(SyncHarness.EntryId));
        Assert.That(writes[0].Progress, Is.EqualTo(5));
    }

    [TestCaseSource(nameof(IdProviders))]
    public async Task EpisodePastEntryWithNoSequelIsUnresolved(ApiName provider) {
        _harness.SeedAll(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12, Watching(12)));

        var outcomes = await Sync(provider, episodeNumber: 13);

        Assert.That(_harness.Tracker(provider).Writes, Is.Empty);
        Assert.That(outcomes.Select(outcome => outcome.Kind), Does.Contain(OutcomeKind.Unresolved));
    }

    private void SeedSplitSeason(int firstPartEpisodes, MyListStatus? status, int sequelEpisodes) =>
        _harness.SeedAll(
            SyncHarness.Entry(SyncHarness.EntryId, "STEEL BALL RUN JoJo's Bizarre Adventure 1st STAGE", firstPartEpisodes, status,
                SyncHarness.Related(RelationType.Sequel, SequelId)),
            SyncHarness.Entry(SequelId, "STEEL BALL RUN JoJo's Bizarre Adventure 2nd STAGE", sequelEpisodes, null));

    private async Task<IReadOnlyList<ProviderWriteOutcome>> Sync(ApiName provider, int episodeNumber) {
        // The series AniList ID sends every provider through the ID-resolved path; ARM returns 500 for each.
        var episode = _harness.Library.Episode("JoJo's Bizarre Adventure", 1, episodeNumber,
            new Dictionary<string, string> { ["AniList"] = SyncHarness.EntryId.ToString() });
        _harness.ConfigureUser(_user, false, false, provider);
        var collector = new PlaybackOutcomeCollector();
        await _harness.NewUpdater().UpdateInsideGate(episode, _user, true, collector, SyncUpdateSource.Playback);
        return collector.Outcomes.Where(outcome => outcome.Provider == provider).ToList();
    }

    private static MyListStatus Watching(int progress) => new() { Status = Status.Watching, NumEpisodesWatched = progress };

    private static MyListStatus Completed(int progress) => new() { Status = Status.Completed, NumEpisodesWatched = progress };
}
