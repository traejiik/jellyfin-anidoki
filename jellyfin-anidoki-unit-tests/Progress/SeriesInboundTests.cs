using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Progress;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Operations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Progress;

/// <summary>Each season's episodes with a played flag; marking sets it and is recorded.</summary>
internal sealed class FakePlayedEpisodes : IPlayedEpisodes {
    private readonly Dictionary<string, List<LibraryEpisode>> _seasons = new();

    public List<(string SourceKey, int[] Episodes, bool MarksSeason)> Marks { get; } = new();

    public void Season(LibrarySource source, int count, int[]? played = null, int[]? ignored = null) =>
        _seasons[source.Info.SourceKey] = Enumerable.Range(1, count)
            .Select(number => new LibraryEpisode(number, new Episode { Id = Guid.NewGuid(), IndexNumber = number }, played?.Contains(number) == true,
                ignored?.Contains(number) == true))
            .ToList();

    public IReadOnlyList<LibraryEpisode> Episodes(LibrarySource source, Guid userId) => _seasons.GetValueOrDefault(source.Info.SourceKey) ?? [];

    public void Mark(LibrarySource source, Guid userId, IReadOnlyList<BaseItem> items, bool marksSeason) {
        List<LibraryEpisode> season = _seasons[source.Info.SourceKey];
        var marked = season.Where(episode => items.Contains(episode.Item)).ToList();
        foreach (LibraryEpisode episode in marked)
            season[season.IndexOf(episode)] = episode with { Played = true };
        Marks.Add((source.Info.SourceKey, marked.Select(episode => episode.Number).ToArray(), marksSeason));
    }
}

public class SeriesInboundTests {
    private ProgressFixture _fixture = null!;
    private FakePlayedEpisodes _played = null!;
    private SeriesInbound _inbound = null!;
    private OperationStore _store = null!;
    private OperationRunner _runner = null!;

    [SetUp]
    public void SetUp() {
        _fixture = new ProgressFixture();
        _played = new FakePlayedEpisodes();
        _inbound = new SeriesInbound(_fixture.Reader, _played, NullLogger<SeriesInbound>.Instance);
        (_store, _runner) = OperationTestSupport.Create(_fixture.Match.Directory);
        _fixture.Shared(_fixture.Match.Season, Range(1, 12, A), Range(13, 24, B), Range(25, 36, C), Range(37, 48, D));
        _played.Season(_fixture.Match.Season, 48, played: [1, 2, 3]);
        _played.Season(_fixture.SeasonTwo, 12);
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    private async Task<IReadOnlyList<RecordedAction>> Run(SeriesItem? item = null) {
        OperationRunner.Handle handle = _runner.Enqueue(_fixture.Alice, OperationKind.SyncFromTrackers, "x",
            operation => _inbound.Run(item ?? _fixture.Series, _fixture.Alice, _fixture.Match.Catalogue, true, operation))!;
        await handle.Completion;
        return _store.ReadActions(handle.OperationId);
    }

    [Test]
    public async Task EpisodesUpToEachEntrysProgressAreMarkedThroughTheMatch() {
        _fixture.Match.Catalogue.Add(A, 12, new ListState(TrackerStatus.Completed, 12, false, 0));
        _fixture.Match.Catalogue.Add(B, 12, new ListState(TrackerStatus.Watching, 5, false, 0));

        IReadOnlyList<RecordedAction> actions = await Run();

        var mark = _played.Marks.Single();
        Assert.That(mark.SourceKey, Is.EqualTo(_fixture.Match.Key));
        Assert.That(mark.Episodes, Is.EqualTo(Enumerable.Range(4, 14).ToArray()), "4–12 for A, then 13–17 for B's first five");
        Assert.That(mark.MarksSeason, Is.False);
        Assert.That(actions[0].Outcome, Is.EqualTo(OutcomeKind.Confirmed));
    }

    [Test]
    public async Task AnIgnoredEpisodeIsNeverMarkedAndKeepsTheSeasonUnmarked() {
        _fixture.Personal(_fixture.Match.Season, [Range(1, 48, A)], [Skip(5)]);
        _fixture.Match.Catalogue.Add(A, 48, new ListState(TrackerStatus.Completed, 48, false, 0));
        _played.Season(_fixture.Match.Season, 48, ignored: [7]);

        await Run();

        var mark = _played.Marks.Single();
        Assert.That(mark.Episodes, Does.Not.Contain(5), "ignored by the match");
        Assert.That(mark.Episodes, Does.Not.Contain(7), "ignored in Jellyfin");
        Assert.That(mark.Episodes, Has.Length.EqualTo(46));
        Assert.That(mark.MarksSeason, Is.False);
    }

    [Test]
    public async Task ASeasonTheTrackerCompletesIsMarkedWhole() {
        _fixture.Shared(_fixture.SeasonTwo, Range(1, null, C));
        _fixture.Match.Catalogue.Add(C, 0, new ListState(TrackerStatus.Completed, 0, false, 0));

        await Run();

        var mark = _played.Marks.Single(entry => entry.SourceKey == _fixture.SeasonTwo.Info.SourceKey);
        Assert.That(mark.Episodes, Is.EqualTo(Enumerable.Range(1, 12).ToArray()), "Completed with an unknown total marks every mapped episode");
        Assert.That(mark.MarksSeason, Is.True);
    }

    [Test]
    public async Task NothingIsMarkedWhenJellyfinIsAlreadyAsFarAsTheTracker() {
        _fixture.Match.Catalogue.Add(A, 12, new ListState(TrackerStatus.Watching, 3, false, 0));

        IReadOnlyList<RecordedAction> actions = await Run();

        Assert.That(_played.Marks.Where(mark => mark.SourceKey == _fixture.Match.Key), Is.Empty);
        Assert.That(actions[0].Outcome, Is.EqualTo(OutcomeKind.NoChange));
    }

    [Test]
    public async Task PlayedEpisodesAheadOfTheTrackerAreNeverUnplayedOrTouched() {
        _played.Season(_fixture.Match.Season, 48, played: Enumerable.Range(1, 20).ToArray());
        _fixture.Match.Catalogue.Add(A, 12, new ListState(TrackerStatus.Watching, 2, false, 0));

        await Run();

        Assert.That(_played.Marks, Is.Empty);
        Assert.That(_played.Episodes(_fixture.Match.Season, _fixture.Alice).Count(episode => episode.Played), Is.EqualTo(20));
    }

    [Test]
    public async Task OnlyTheChosenSeriesIsTouched() {
        _fixture.Shared(_fixture.SeasonTwo, Range(1, 12, D));
        _fixture.Match.Catalogue.Add(D, 12, new ListState(TrackerStatus.Completed, 12, false, 0));
        var onlySeasonTwo = _fixture.Items.Add(_fixture.Series with { Id = Guid.NewGuid(), SourceKeys = [_fixture.SeasonTwo.Info.SourceKey] });

        await Run(onlySeasonTwo);

        Assert.That(_played.Marks.Select(mark => mark.SourceKey), Is.EqualTo(new[] { _fixture.SeasonTwo.Info.SourceKey }));
    }

    [Test]
    public async Task ASeasonWithNoEntryIsReportedUnresolved() {
        IReadOnlyList<RecordedAction> actions = await Run();

        Assert.That(actions.Single(action => action.TargetId == _fixture.SeasonTwo.Info.SourceKey).Outcome, Is.EqualTo(OutcomeKind.Unresolved));
    }
}
