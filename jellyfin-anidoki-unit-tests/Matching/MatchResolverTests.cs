using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;
using static jellyfin_anidoki_unit_tests.Resolution.ResolutionTestSupport;

namespace jellyfin_anidoki_unit_tests.Matching;

internal sealed class FixedWatchState(params int[] watched) : ISeasonWatchState {
    public int Calls { get; private set; }

    public IReadOnlyList<WatchedEpisode> Watched(SyncSource source, Guid userId) {
        Calls++;
        return watched.Select(episode => new WatchedEpisode(episode, null)).ToList();
    }
}

[TestFixture, NonParallelizable]
public class MatchResolverTests {
    private static readonly SourceIds NoIds = new(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), null);

    private sealed class FixedResolver(SourceResolution answer) : ISourceResolver {
        public int Calls { get; private set; }

        public Task<SourceResolution> Resolve(SyncSource source, SourceIds ids, ITrackerProvider tracker, CancellationToken cancellationToken) {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    private static readonly Resolved AutomaticAnswer = new(new ResolvedTarget(Ref(500), 20, TargetRole.Played, "Automatic entry"), ResolvedBy.TitleSearch, []);

    private string _directory = null!;
    private MatchStore _store = null!;
    private SyncSource _source = null!;
    private MatchSource _matchSource = null!;
    private Guid _user;
    private ITrackerProvider _tracker = null!;
    private FakeTracker _fake = null!;

    [SetUp]
    public void SetUp() {
        _directory = OperationTestSupport.NewDirectory();
        _store = new MatchStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        _source = SyncSource.From(new LibraryFixture().Episode("Show", 1, 20))!;
        _matchSource = MatchSource.From(_source);
        _user = Guid.NewGuid();
        (_tracker, _fake) = ReadOnly(ApiName.AniList);
        foreach (int id in new[] { A, B, C, D, 500 })
            _fake.Add(SyncHarness.Entry(id, $"Entry {id}", 12, null));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private MatchResolver Resolver(ISourceResolver automatic, bool apply, ISeasonWatchState? watch = null, MatchProgressScope scope = MatchProgressScope.PlayedEpisode) =>
        new(automatic, _store, _user, apply, watch, scope, NullLogger.Instance);

    private void SaveShared(params MatchRange[] ranges) =>
        _store.Save(_matchSource, new Match(new MatchScope(ApiName.AniList, _source.SourceKey), ranges, []), 0, "admin");

    private Task<SourceResolution> Resolve(ISourceResolver resolver) => resolver.Resolve(_source, NoIds, _tracker, CancellationToken.None);

    [TestCase(false)]
    [TestCase(true)]
    public async Task WithNoMatchTheAutomaticAnswerIsReturnedWithNoReads(bool apply) {
        var automatic = new FixedResolver(AutomaticAnswer);

        Assert.That(await Resolve(Resolver(automatic, apply, new FixedWatchState(1))), Is.SameAs(AutomaticAnswer));

        Assert.That(_fake.Reads, Is.Empty);
    }

    [Test]
    public async Task OffTheAutomaticAnswerDrivesThePlanAndTheSavedOneIsNoted() {
        SaveShared(Range(1, 12, A), Range(13, 24, B));

        var answer = (Resolved)await Resolve(Resolver(new FixedResolver(AutomaticAnswer), false));

        Assert.That(answer.Played, Is.EqualTo(AutomaticAnswer.Played));
        Assert.That(answer.By, Is.EqualTo(ResolvedBy.TitleSearch));
        Assert.That(answer.Note, Is.EqualTo("saved match would resolve to Entry 102 ep 8"));
    }

    [Test]
    public async Task OffAnExistingNoteIsKept() {
        SaveShared(Range(1, 24, A));
        var automatic = new FixedResolver(AutomaticAnswer with { Note = "ambiguous (2 candidates)" });
        _fake.Add(SyncHarness.Entry(A, "Entry 101", 24, null));

        var answer = await Resolve(Resolver(automatic, false));

        Assert.That(answer.Note, Is.EqualTo("ambiguous (2 candidates); saved match would resolve to Entry 101 ep 20"));
    }

    [Test]
    public async Task OffAnUnresolvedAnswerKeepsItsReasonInTheNote() {
        SaveShared(Range(1, 12, A), Range(13, 24, B));
        var automatic = new FixedResolver(new Unresolved("no-title-match", new UnresolvedRecord("", "Show"), []));

        var answer = (Unresolved)await Resolve(Resolver(automatic, false));

        Assert.That(answer.Reason, Is.EqualTo("no-title-match"));
        Assert.That(answer.Note, Is.EqualTo("no-title-match; saved match would resolve to Entry 102 ep 8"));
    }

    [Test]
    public async Task OffANoteSaysWhenTheSavedMatchWouldNotSync() {
        SaveShared(Range(1, 12, A));

        var answer = await Resolve(Resolver(new FixedResolver(AutomaticAnswer), false));

        Assert.That(answer.Note, Is.EqualTo("saved match would not sync this episode: not-in-match"));
    }

    [Test]
    public async Task OffANoteSaysWhenTheSavedMatchIgnoresTheEpisode() {
        _store.Save(_matchSource, new Match(new MatchScope(ApiName.AniList, _source.SourceKey), [], [Skip(20)]), 0, "admin");

        var answer = await Resolve(Resolver(new FixedResolver(AutomaticAnswer), false));

        Assert.That(answer, Is.TypeOf<Resolved>());
        Assert.That(answer.Note, Is.EqualTo("saved match would ignore this episode"));
    }

    [Test]
    public async Task OffATrackerFailureInTheSavedMatchDoesNotStopTheAutomaticAnswer() {
        SaveShared(Range(1, 24, A));
        _fake.ReadFailure = TrackerFailureKind.RateLimited;

        var answer = (Resolved)await Resolve(Resolver(new FixedResolver(AutomaticAnswer), false));

        Assert.That(answer.Played, Is.EqualTo(AutomaticAnswer.Played));
        Assert.That(answer.Note, Is.EqualTo("saved match could not be checked"));
    }

    [Test]
    public async Task OnTheSavedMatchDrivesThePlanAndAutomaticIsNotAsked() {
        SaveShared(Range(1, 12, A), Range(13, 24, B));
        var automatic = new FixedResolver(AutomaticAnswer);

        var answer = (Resolved)await Resolve(Resolver(automatic, true, new FixedWatchState(1, 2, 13)));

        Assert.That(automatic.Calls, Is.Zero);
        Assert.That(answer.By, Is.EqualTo(ResolvedBy.SharedMatch));
        Assert.That(answer.Played.Entry, Is.EqualTo(Ref(B)));
        Assert.That(answer.Played.TargetEpisode, Is.EqualTo(8));
        Assert.That(answer.Played.MatchRevision, Is.EqualTo(1));
    }

    [Test]
    public async Task OnAPersonalMatchWinsOverTheSharedOne() {
        SaveShared(Range(1, 24, A));
        _store.Save(_matchSource, new Match(new MatchScope(ApiName.AniList, _source.SourceKey, _user), [Range(1, 24, C)], []), 0, "user");
        _fake.Add(SyncHarness.Entry(C, "Entry 103", 24, null));

        var answer = (Resolved)await Resolve(Resolver(new FixedResolver(AutomaticAnswer), true));

        Assert.That(answer.By, Is.EqualTo(ResolvedBy.PersonalMatch));
        Assert.That(answer.Played.Entry, Is.EqualTo(Ref(C)));
    }

    [Test]
    public async Task OnProgressIsTheHighestWatchedNotThePlayedEpisode() {
        SaveShared(Range(1, 24, A));
        _fake.Add(SyncHarness.Entry(A, "Entry 101", 24, null));

        var source = SyncSource.From(new LibraryFixture().Episode("Show", 1, 5))!;
        _store.Save(MatchSource.From(source), new Match(new MatchScope(ApiName.AniList, source.SourceKey), [Range(1, 24, A)], []), 0, "admin");

        var answer = (Resolved)await Resolver(new FixedResolver(AutomaticAnswer), true, new FixedWatchState(1, 2, 3, 4, 5, 6, 7, 8))
            .Resolve(source, NoIds, _tracker, CancellationToken.None);

        Assert.That(answer.Played.TargetEpisode, Is.EqualTo(8));
    }

    [Test]
    public async Task OnEarlierEntriesCarryWhatWasWatchedAndOneWithNothingWatchedIsDropped() {
        SaveShared(Range(1, 12, A), Range(13, 24, B));

        var noneWatched = (Resolved)await Resolve(Resolver(new FixedResolver(AutomaticAnswer), true, new FixedWatchState()));
        var someWatched = (Resolved)await Resolve(Resolver(new FixedResolver(AutomaticAnswer), true, new FixedWatchState(1, 2, 3)));

        Assert.That(noneWatched.EarlierParts, Is.Empty);
        ResolvedTarget earlier = someWatched.EarlierParts.Single();
        Assert.That(earlier.Entry, Is.EqualTo(Ref(A)));
        Assert.That(earlier.TargetEpisode, Is.EqualTo(3));
        Assert.That(earlier.Role, Is.EqualTo(TargetRole.EarlierPart));
        Assert.That(earlier.MatchRevision, Is.EqualTo(1));
        Assert.That(earlier.State, Is.Not.Null);
    }

    [TestCase(Status.Completed, 12, false)]
    [TestCase(Status.Rewatching, 3, true)]
    public async Task OnAnEntryNotBehindKeepsThePlayedEpisodeSoARewatchIsNotCounted(Status status, int progress, bool rewatching) {
        var source = SyncSource.From(new LibraryFixture().Episode("Show", 1, 5))!;
        _fake.Add(SyncHarness.Entry(A, "Entry 101", 12, new MyListStatus { Status = status, NumEpisodesWatched = progress, IsRewatching = rewatching, RewatchCount = 1 }));
        _store.Save(MatchSource.From(source), new Match(new MatchScope(ApiName.AniList, source.SourceKey), [Range(1, 12, A)], []), 0, "admin");

        var answer = (Resolved)await Resolver(new FixedResolver(AutomaticAnswer), true, new FixedWatchState(1, 5, 12))
            .Resolve(source, NoIds, _tracker, CancellationToken.None);

        Assert.That(answer.Played.TargetEpisode, Is.EqualTo(5));
    }

    [Test]
    public async Task OnAFinishedEarlierEntryIsLeftAlone() {
        _fake.Add(SyncHarness.Entry(A, "Entry 101", 12, new MyListStatus { Status = Status.Completed, NumEpisodesWatched = 12, RewatchCount = 0 }));
        SaveShared(Range(1, 12, A), Range(13, 24, B));

        var answer = (Resolved)await Resolve(Resolver(new FixedResolver(AutomaticAnswer), true, new FixedWatchState(1, 2, 12)));

        Assert.That(answer.EarlierParts, Is.Empty);
        Assert.That(answer.Played.Entry, Is.EqualTo(Ref(B)));
    }

    [Test]
    public async Task OnAManualSyncCoversEveryEntryOfTheMatch() {
        var match = new Match(new MatchScope(ApiName.AniList, _source.SourceKey), [Range(1, 12, A), Range(13, 24, B)], [SendTo(30, C, 1)]);
        _store.Save(_matchSource, match, 0, "admin");

        var playback = (Resolved)await Resolve(Resolver(new FixedResolver(AutomaticAnswer), true, new FixedWatchState(1, 30)));
        var manual = (Resolved)await Resolve(Resolver(new FixedResolver(AutomaticAnswer), true, new FixedWatchState(1, 30), MatchProgressScope.WholeSeason));

        Assert.That(playback.EarlierParts.Select(part => part.Entry), Is.EqualTo(new[] { Ref(A) }));
        Assert.That(manual.EarlierParts.Select(part => part.Entry), Is.EqualTo(new[] { Ref(A), Ref(C) }));
    }

    [Test]
    public async Task OnAnEpisodeOutsideTheMatchDoesNotFallBack() {
        SaveShared(Range(1, 12, A));
        var automatic = new FixedResolver(AutomaticAnswer);

        var answer = (Unresolved)await Resolve(Resolver(automatic, true));

        Assert.That(answer.Reason, Is.EqualTo("not-in-match"));
        Assert.That(automatic.Calls, Is.Zero);
    }

    [Test]
    public async Task OnAnUnreadableTargetDoesNotFallBackToTheSharedMatch() {
        SaveShared(Range(1, 24, A));
        _store.Save(_matchSource, new Match(new MatchScope(ApiName.AniList, _source.SourceKey, _user), [Range(1, 24, C)], []), 0, "user");
        _fake.FailingIds.Add(C);

        var answer = (Unresolved)await Resolve(Resolver(new FixedResolver(AutomaticAnswer), true));

        Assert.That(answer.Reason, Is.EqualTo("match-target-unreadable"));
    }

    [Test]
    public void OnATrackerFailurePropagatesSoItIsRecordedAsOne() {
        SaveShared(Range(1, 24, A));
        _fake.ReadFailure = TrackerFailureKind.RateLimited;

        Assert.ThrowsAsync<TrackerFailureException>(() => Resolve(Resolver(new FixedResolver(AutomaticAnswer), true)));
    }

    [Test]
    public async Task WatchedStateIsNotReadWhenTheSavedAnswerIsNotAResolution() {
        SaveShared(Range(1, 12, A));
        var watch = new FixedWatchState(1);

        await Resolve(Resolver(new FixedResolver(AutomaticAnswer), true, watch));

        Assert.That(watch.Calls, Is.Zero);
    }
}
