using System;
using System.IO;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Operations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture, NonParallelizable]
public class MatchProblemsTests {
    private string _directory = null!;
    private MutableTime _clock = null!;
    private OperationStore _store = null!;
    private LibraryFixture _library = null!;

    [SetUp]
    public void SetUp() {
        _directory = OperationTestSupport.NewDirectory();
        _clock = new MutableTime(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        (_store, _) = OperationTestSupport.Create(_directory, _clock);
        _library = new LibraryFixture();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [TestCase("Unresolved", "no-cour-covers-episode", MatchProblem.NeedsMatch)]
    [TestCase("Unresolved", "not-in-match", MatchProblem.NeedsMatch)]
    [TestCase("Unresolved", "no-title-match; saved match would resolve to Entry 101 ep 5", MatchProblem.NeedsMatch)]
    [TestCase("Unresolved", null, MatchProblem.NeedsMatch)]
    [TestCase("Confirmed", "ambiguous (2 candidates)", MatchProblem.NeedsReview)]
    [TestCase("NoChange", "ambiguous (3 candidates); saved match would ignore this episode", MatchProblem.NeedsReview)]
    [TestCase("Confirmed", "saved match would resolve to Entry 101 ep 5", null)]
    [TestCase("Confirmed", null, null)]
    [TestCase("Failed", null, null)]
    public void ClassifiesWhatASyncRecorded(string outcome, string? note, MatchProblem? expected) =>
        Assert.That(RecentMatchProblems.Classify(outcome, note), Is.EqualTo(expected));

    private Episode Registered(Episode episode) {
        _library.Library.Setup(manager => manager.GetItemById(episode.Id)).Returns(episode);
        return episode;
    }

    private void Record(Guid user, Guid item, OutcomeKind kind, string? note) {
        Guid id = Guid.NewGuid();
        _store.Create(id, user, OperationKind.Playback, "Fixture");
        _store.AddAction(id, 0, OperationTestSupport.Outcome(kind), new OutcomeDetail(item, 1, null, null, note, null, null));
        _clock.Now += TimeSpan.FromMinutes(1);
    }

    private RecentMatchProblems Problems() => new(_store, _library.Library.Object, _clock);

    [Test]
    public void ASourceIsFlaggedByItsLatestSyncOnEachTracker() {
        Guid user = Guid.NewGuid();
        Episode fixedLater = Registered(_library.Episode("Fixed Show", 1, 5));
        Episode broken = Registered(_library.Episode("Broken Show", 2, 3));
        Movie movie = _library.Movie("Fixture Movie");
        _library.Library.Setup(manager => manager.GetItemById(movie.Id)).Returns(movie);
        Record(user, fixedLater.Id, OutcomeKind.Unresolved, "no-title-match");
        Record(user, fixedLater.Id, OutcomeKind.Confirmed, null);
        Record(user, broken.Id, OutcomeKind.Unresolved, "no-cour-covers-episode");
        Record(user, movie.Id, OutcomeKind.Confirmed, "ambiguous (2 candidates)");
        Record(user, Guid.NewGuid(), OutcomeKind.Unresolved, "no-title-match");

        Assert.That(Problems().SourceKeys(MatchProblem.NeedsMatch, null), Is.EquivalentTo(new[] { SyncSource.From(broken)!.SourceKey }),
            "a later sync that resolved clears a season, and an item the library no longer has is skipped");
        Assert.That(Problems().SourceKeys(MatchProblem.NeedsReview, user), Is.EquivalentTo(new[] { $"movie:{movie.Id:N}" }));
    }

    [Test]
    public void OnlyTheLastThirtyDaysAndTheUsersOwnSyncsCount() {
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid();
        Episode old = Registered(_library.Episode("Old Show", 1, 5));
        Episode bobs = Registered(_library.Episode("Bob's Show", 1, 5));
        _clock.Now -= TimeSpan.FromDays(31);
        Record(alice, old.Id, OutcomeKind.Unresolved, "no-title-match");
        _clock.Now += TimeSpan.FromDays(31);
        Record(bob, bobs.Id, OutcomeKind.Unresolved, "no-title-match");

        Assert.That(Problems().SourceKeys(MatchProblem.NeedsMatch, alice), Is.Empty);
        Assert.That(Problems().SourceKeys(MatchProblem.NeedsMatch, null), Is.EquivalentTo(new[] { SyncSource.From(bobs)!.SourceKey }));
    }

    [Test]
    public void ASeasonFlaggedOnOneTrackerStaysFlaggedWhenAnotherResolves() {
        Guid user = Guid.NewGuid();
        Episode episode = Registered(_library.Episode("Split Show", 1, 20));
        Guid id = Guid.NewGuid();
        _store.Create(id, user, OperationKind.Playback, "Split Show");
        _store.AddAction(id, 0, OperationTestSupport.Outcome(OutcomeKind.Unresolved), new OutcomeDetail(episode.Id, 20, null, null, "no-cour-covers-episode", null, null));
        _store.AddAction(id, 1, new ProviderWriteOutcome(jellyfin_anidoki.Configuration.ApiName.Kitsu, "7", "Split Show", OutcomeKind.Confirmed, 20, null, false, "progress", true),
            new OutcomeDetail(episode.Id, 20, 20, ResolvedBy.Ids, null, null, null));

        Assert.That(Problems().SourceKeys(MatchProblem.NeedsMatch, user), Has.Count.EqualTo(1));
    }
}
