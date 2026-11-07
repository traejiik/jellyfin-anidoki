using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Operations;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture]
public class MatchStoreTests {
    private const string Author = "admin";
    private static readonly MatchSource Source = new(SourceKey, MatchSourceKind.Season, "Show · season 1", new Dictionary<string, string> { ["series:AniDB"] = "42" });

    private string _directory = null!;
    private AniDokiDatabase _database = null!;
    private MutableTime _clock = null!;
    private MatchStore _store = null!;
    private Guid _user;
    private MatchScope _personalScope = null!;

    [SetUp]
    public void SetUp() {
        _directory = OperationTestSupport.NewDirectory();
        _database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));
        _clock = new MutableTime(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero));
        _store = new MatchStore(_database, _clock);
        _user = Guid.NewGuid();
        _personalScope = new MatchScope(ApiName.AniList, SourceKey, _user);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private Match Personal(params MatchRange[] ranges) => new(_personalScope, ranges, []);

    private MatchWrite SaveShared(long expected, params MatchRange[] ranges) => _store.Save(Source, Shared(ranges), expected, Author);

    private long Count(string table) {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    [Test]
    public void NothingSavedReadsAsNull() {
        Assert.That(_store.Get(SharedScope), Is.Null);
        Assert.That(_store.History(SharedScope), Is.Empty);
    }

    [Test]
    public void ASavedMatchReadsBackWithEveryField() {
        var lookup = new CrossReference(new ResolvedIds(1, null, null, 4), "Simkl title", MediaKind.Series);
        var scope = new MatchScope(ApiName.Simkl, SourceKey);
        var simkl = new MatchTarget(new EntryRef(ApiName.Simkl, 77, "alt-77", lookup), "Simkl entry");
        var match = new Match(scope,
            [new MatchRange(1, 12, simkl, 1), new MatchRange(13, null, simkl, 13)],
            [new TargetException(25, simkl, 3), Skip(26)]);

        _store.Save(Source, match, 0, Author, "a note");

        StoredMatch stored = _store.Get(scope)!;
        Assert.That(stored.Match.Scope, Is.EqualTo(scope));
        Assert.That(stored.Match.Revision, Is.EqualTo(1));
        Assert.That(stored.Match.Ranges, Is.EqualTo(match.Ranges));
        Assert.That(stored.Match.Exceptions, Is.EqualTo(match.Exceptions));
        Assert.That(stored.Note, Is.EqualTo("a note"));
        Assert.That(stored.UpdatedBy, Is.EqualTo(Author));
        Assert.That(stored.UpdatedUtc, Is.EqualTo(_clock.Now));
    }

    [Test]
    public void TheFirstSaveIsRevisionOneAndEachSaveRaisesIt() {
        Assert.That(SaveShared(0, Range(1, 12, A)), Is.EqualTo(new MatchWrite(MatchWriteStatus.Written, 1)));
        Assert.That(SaveShared(1, Range(1, 13, A)), Is.EqualTo(new MatchWrite(MatchWriteStatus.Written, 2)));
    }

    [Test]
    public void AStaleSaveConflictsWithTheCurrentRevisionAndChangesNothing() {
        SaveShared(0, Range(1, 12, A));
        SaveShared(1, Range(1, 13, A));

        MatchWrite write = SaveShared(1, Range(1, 14, B));

        Assert.That(write, Is.EqualTo(new MatchWrite(MatchWriteStatus.Conflict, 2)));
        Assert.That(_store.Get(SharedScope)!.Match.Ranges.Single().SourceEnd, Is.EqualTo(13));
        Assert.That(_store.History(SharedScope), Has.Count.EqualTo(2));
    }

    [Test]
    public void ExpectingARevisionWhenNoneExistsConflictsWithZero() {
        Assert.That(SaveShared(3, Range(1, 12, A)), Is.EqualTo(new MatchWrite(MatchWriteStatus.Conflict, 0)));
        Assert.That(Count("match_sources"), Is.Zero);
    }

    [Test]
    public void ExpectingNoneWhenOneExistsConflicts() {
        SaveShared(0, Range(1, 12, A));

        Assert.That(SaveShared(0, Range(1, 12, B)), Is.EqualTo(new MatchWrite(MatchWriteStatus.Conflict, 1)));
    }

    [Test]
    public void SavingReplacesTheEarlierRangesAndExceptions() {
        _store.Save(Source, Shared([Range(1, 12, A), Range(13, 24, B)], Skip(25), SendTo(26, C, 1)), 0, Author);

        _store.Save(Source, Shared(Range(1, 24, A)), 1, Author);

        Assert.That(_store.Get(SharedScope)!.Match.Ranges, Has.Count.EqualTo(1));
        Assert.That(_store.Get(SharedScope)!.Match.Exceptions, Is.Empty);
        Assert.That(Count("match_ranges"), Is.EqualTo(1));
        Assert.That(Count("match_exceptions"), Is.Zero);
        Assert.That(Count("matches"), Is.EqualTo(1));
    }

    [Test]
    public void OnlyOneOfManySavesFromTheSameRevisionWins() {
        SaveShared(0, Range(1, 12, A));
        var stores = Enumerable.Range(0, 8).Select(_ => new MatchStore(_database, _clock)).ToList();

        var writes = stores.AsParallel().Select((store, index) => store.Save(Source, Shared(Range(1, 12 + index, A)), 1, Author)).ToList();

        Assert.That(writes.Count(write => write.Status == MatchWriteStatus.Written), Is.EqualTo(1));
        Assert.That(writes.Where(write => write.Status != MatchWriteStatus.Written).Select(write => write.Revision), Is.All.EqualTo(2));
        Assert.That(_store.History(SharedScope).Select(entry => entry.Revision), Is.EqualTo(new long[] { 1, 2 }));
    }

    [Test]
    public void DeletingRemovesTheRowsAndRecordsARevision() {
        _store.Save(Source, Shared([Range(1, 12, A)], Skip(13)), 0, Author);

        Assert.That(_store.Delete(SharedScope, 1, "other"), Is.EqualTo(new MatchWrite(MatchWriteStatus.Written, 2)));

        Assert.That(_store.Get(SharedScope), Is.Null);
        Assert.That(Count("matches"), Is.Zero);
        Assert.That(Count("match_ranges"), Is.Zero);
        Assert.That(Count("match_exceptions"), Is.Zero);
        MatchHistoryEntry deleted = _store.History(SharedScope)[^1];
        Assert.That(deleted.Action, Is.EqualTo(MatchRevisionAction.Delete));
        Assert.That(deleted.Author, Is.EqualTo("other"));
        Assert.That(deleted.Snapshot.Ranges, Is.Empty);
        Assert.That(deleted.Snapshot.Exceptions, Is.Empty);
    }

    [Test]
    public void AStaleDeleteConflicts() {
        SaveShared(0, Range(1, 12, A));
        SaveShared(1, Range(1, 13, A));

        Assert.That(_store.Delete(SharedScope, 1, Author), Is.EqualTo(new MatchWrite(MatchWriteStatus.Conflict, 2)));
        Assert.That(_store.Get(SharedScope), Is.Not.Null);
    }

    [Test]
    public void DeletingWhatDoesNotExistIsNotFound() {
        Assert.That(_store.Delete(SharedScope, 0, Author), Is.EqualTo(new MatchWrite(MatchWriteStatus.NotFound, 0)));
        Assert.That(_store.History(SharedScope), Is.Empty);
    }

    [Test]
    public void RevisionsKeepRisingAcrossDeleteAndRecreate() {
        SaveShared(0, Range(1, 12, A));
        _store.Delete(SharedScope, 1, Author);

        Assert.That(SaveShared(0, Range(1, 12, B)), Is.EqualTo(new MatchWrite(MatchWriteStatus.Written, 3)));
        Assert.That(_store.Get(SharedScope)!.Match.Revision, Is.EqualTo(3));
    }

    [Test]
    public void AnOldRevisionConflictsAfterTheMatchWasDeleted() {
        SaveShared(0, Range(1, 12, A));
        _store.Delete(SharedScope, 1, Author);

        Assert.That(SaveShared(1, Range(1, 12, B)), Is.EqualTo(new MatchWrite(MatchWriteStatus.Conflict, 0)));
    }

    [Test]
    public void HistoryKeepsEachRevisionsSnapshotInOrder() {
        SaveShared(0, Range(1, 12, A));
        _clock.Now = _clock.Now.AddDays(1);
        _store.Save(Source, Shared([Range(1, 12, B)], Skip(13), SendTo(14, C, 2)), 1, "other");

        var history = _store.History(SharedScope);

        Assert.That(history.Select(entry => entry.Revision), Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(history[0].Action, Is.EqualTo(MatchRevisionAction.Save));
        Assert.That(history[0].Author, Is.EqualTo(Author));
        Assert.That(history[0].Snapshot.Ranges, Is.EqualTo(new[] { Range(1, 12, A) }));
        Assert.That(history[1].CreatedUtc, Is.EqualTo(_clock.Now));
        Assert.That(history[1].Snapshot.Ranges, Is.EqualTo(new[] { Range(1, 12, B) }));
        Assert.That(history[1].Snapshot.Exceptions, Is.EqualTo(new MatchException[] { Skip(13), SendTo(14, C, 2) }));
    }

    [Test]
    public void ASnapshotKeepsASimklLookup() {
        var lookup = new CrossReference(new ResolvedIds(1, 2, 3, 4), "Simkl title", MediaKind.Movie);
        var scope = new MatchScope(ApiName.Simkl, SourceKey);
        var range = new MatchRange(1, 1, new MatchTarget(new EntryRef(ApiName.Simkl, 77, null, lookup), "Simkl entry"), 1);

        _store.Save(Source, new Match(scope, [range], []), 0, Author);

        Assert.That(_store.History(scope).Single().Snapshot.Ranges.Single(), Is.EqualTo(range));
    }

    [Test]
    public void ScopesDoNotAffectEachOther() {
        var otherProvider = new MatchScope(ApiName.Kitsu, SourceKey);
        var otherUser = new MatchScope(ApiName.AniList, SourceKey, Guid.NewGuid());
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, new Match(otherProvider, [Range(1, 12, B)], []), 0, Author);
        _store.Save(Source, Personal(Range(1, 12, C)), 0, Author);
        _store.Save(Source, new Match(otherUser, [Range(1, 12, D)], []), 0, Author);

        Assert.That(_store.Get(SharedScope)!.Match.Ranges.Single().Target, Is.EqualTo(Target(A)));
        Assert.That(_store.Get(_personalScope)!.Match.Ranges.Single().Target, Is.EqualTo(Target(C)));
        Assert.That(_store.Get(otherUser)!.Match.Ranges.Single().Target, Is.EqualTo(Target(D)));

        _store.Delete(_personalScope, 1, Author);

        Assert.That(_store.Get(SharedScope), Is.Not.Null);
        Assert.That(_store.Get(otherUser), Is.Not.Null);
        Assert.That(_store.Get(otherProvider), Is.Not.Null);
    }

    [Test]
    public void APersonalMatchKeepsItsBaseRevisionAndConnectionGeneration() {
        var match = Personal(Range(1, 12, A)) with { BaseSharedRevision = 7 };

        _store.Save(Source, match, 0, Author, connectionGeneration: "generation");

        StoredMatch stored = _store.Get(_personalScope)!;
        Assert.That(stored.Match.BaseSharedRevision, Is.EqualTo(7));
        Assert.That(stored.ConnectionGeneration, Is.EqualTo("generation"));
    }

    [Test]
    public void ASharedMatchHasNoBaseRevisionOrConnectionGeneration() {
        var match = Shared(Range(1, 12, A)) with { BaseSharedRevision = 7 };

        _store.Save(Source, match, 0, Author, connectionGeneration: "generation");

        StoredMatch stored = _store.Get(SharedScope)!;
        Assert.That(stored.Match.BaseSharedRevision, Is.Null);
        Assert.That(stored.ConnectionGeneration, Is.Null);
    }

    [Test]
    public void TheEffectiveMatchIsTheOverrideWhileTheProviderIsLinked() {
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, Personal(Range(1, 12, B)), 0, Author);

        Assert.That(_store.GetEffective(_user, ApiName.AniList, SourceKey, true)!.By, Is.EqualTo(ResolvedBy.PersonalMatch));
        Assert.That(_store.GetEffective(_user, ApiName.AniList, SourceKey, false)!.By, Is.EqualTo(ResolvedBy.SharedMatch));
    }

    [Test]
    public void AnotherUsersOverrideIsNotEffective() {
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, Personal(Range(1, 12, B)), 0, Author);

        Assert.That(_store.GetEffective(Guid.NewGuid(), ApiName.AniList, SourceKey, true)!.Scope.Owner, Is.Null);
    }

    [Test]
    public void WithNoMatchTheEffectiveMatchIsNull() {
        Assert.That(_store.GetEffective(_user, ApiName.AniList, SourceKey, true), Is.Null);
    }

    [Test]
    public void PromotingWritesAPromoteRevisionAndRemovesTheOverrideWhenAsked() {
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, new Match(_personalScope, [Range(1, 12, B)], [Skip(13)]), 0, "user", "from the user");

        MatchPromotion result = _store.Promote(_user, ApiName.AniList, SourceKey, 1, 1, true, Author);

        Assert.That(result, Is.EqualTo(new MatchPromotion(MatchWriteStatus.Written, 2, 0)));
        StoredMatch shared = _store.Get(SharedScope)!;
        Assert.That(shared.Match.Ranges, Is.EqualTo(new[] { Range(1, 12, B) }));
        Assert.That(shared.Match.Exceptions, Is.EqualTo(new MatchException[] { Skip(13) }));
        Assert.That(shared.Match.Scope.Owner, Is.Null);
        Assert.That(shared.Match.BaseSharedRevision, Is.Null);
        Assert.That(shared.Note, Is.EqualTo("from the user"));
        Assert.That(shared.UpdatedBy, Is.EqualTo(Author));
        MatchHistoryEntry promoted = _store.History(SharedScope)[^1];
        Assert.That(promoted.Action, Is.EqualTo(MatchRevisionAction.Promote));
        Assert.That(promoted.PromotedFrom, Is.EqualTo($"{_user:D}:1"));
        Assert.That(promoted.Snapshot.Ranges, Is.EqualTo(new[] { Range(1, 12, B) }));
        Assert.That(_store.Get(_personalScope), Is.Null);
        Assert.That(_store.History(_personalScope)[^1].Action, Is.EqualTo(MatchRevisionAction.Delete));
    }

    [Test]
    public void PromotingWithNoSharedMatchStartsFromRevisionOne() {
        _store.Save(Source, Personal(Range(1, 12, B)), 0, "user");

        Assert.That(_store.Promote(_user, ApiName.AniList, SourceKey, 0, 1, true, Author), Is.EqualTo(new MatchPromotion(MatchWriteStatus.Written, 1, 0)));
    }

    [Test]
    public void AKeptOverrideIsRebasedOntoThePromotedRevision() {
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, Personal(Range(1, 12, B)) with { BaseSharedRevision = 1 }, 0, "user");

        MatchPromotion result = _store.Promote(_user, ApiName.AniList, SourceKey, 1, 1, false, Author);

        Assert.That(result, Is.EqualTo(new MatchPromotion(MatchWriteStatus.Written, 2, 1)));
        StoredMatch kept = _store.Get(_personalScope)!;
        Assert.That(kept.Match.Revision, Is.EqualTo(1));
        Assert.That(kept.Match.BaseSharedRevision, Is.EqualTo(2));
    }

    [Test]
    public void PromotingAfterTheSharedMatchMovedConflicts() {
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, Personal(Range(1, 12, B)), 0, "user");
        SaveShared(1, Range(1, 12, C));

        Assert.That(_store.Promote(_user, ApiName.AniList, SourceKey, 1, 1, true, Author), Is.EqualTo(new MatchPromotion(MatchWriteStatus.Conflict, 2, 1)));
        Assert.That(_store.Get(SharedScope)!.Match.Ranges.Single().Target, Is.EqualTo(Target(C)));
        Assert.That(_store.Get(_personalScope), Is.Not.Null);
    }

    [Test]
    public void PromotingAfterTheOverrideMovedConflicts() {
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, Personal(Range(1, 12, B)), 0, "user");
        _store.Save(Source, Personal(Range(1, 12, C)), 1, "user");

        Assert.That(_store.Promote(_user, ApiName.AniList, SourceKey, 1, 1, true, Author), Is.EqualTo(new MatchPromotion(MatchWriteStatus.Conflict, 1, 2)));
    }

    [Test]
    public void PromotingAMissingOverrideIsNotFound() {
        SaveShared(0, Range(1, 12, A));

        Assert.That(_store.Promote(_user, ApiName.AniList, SourceKey, 1, 0, true, Author), Is.EqualTo(new MatchPromotion(MatchWriteStatus.NotFound, 1, 0)));
    }

    [Test]
    public void TheSweepRemovesOnlyOverridesOfUnknownUsers() {
        var gone = new MatchScope(ApiName.AniList, SourceKey, Guid.NewGuid());
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, Personal(Range(1, 12, B)), 0, "user");
        _store.Save(Source, new Match(gone, [Range(1, 12, C)], [Skip(13)]), 0, "user");

        Assert.That(_store.SweepDeletedUsers([_user]), Is.EqualTo(1));

        Assert.That(_store.Get(gone), Is.Null);
        Assert.That(_store.Get(_personalScope), Is.Not.Null);
        Assert.That(_store.Get(SharedScope), Is.Not.Null);
        Assert.That(Count("match_exceptions"), Is.Zero);
        MatchHistoryEntry removed = _store.History(gone)[^1];
        Assert.That(removed.Action, Is.EqualTo(MatchRevisionAction.Delete));
        Assert.That(removed.Author, Is.EqualTo("sweep"));
    }

    [Test]
    public void TheSweepLeavesAnOwnerItCannotReadAndStillRemovesTheRest() {
        var gone = new MatchScope(ApiName.AniList, SourceKey, Guid.NewGuid());
        _store.Save(Source, new Match(gone, [Range(1, 12, C)], []), 0, "user");
        using (var connection = _database.Open()) {
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO matches (id, source_key, provider, owner_user_id, revision, updated_by, updated_utc) VALUES ('bad', '{SourceKey}', 'Kitsu', 'not-a-guid', 1, 'x', 'x')";
            command.ExecuteNonQuery();
        }

        Assert.That(_store.SweepDeletedUsers([_user]), Is.EqualTo(1));

        Assert.That(_store.Get(gone), Is.Null);
        Assert.That(Count("matches"), Is.EqualTo(1));
    }

    [Test]
    public void AnEmptyUserListSweepsNothing() {
        _store.Save(Source, Personal(Range(1, 12, B)), 0, "user");

        Assert.That(_store.SweepDeletedUsers([]), Is.Zero);
        Assert.That(_store.Get(_personalScope), Is.Not.Null);
    }

    [Test]
    public void SourcesListTheirSharedRevisionsAndOverrideCounts() {
        var other = new MatchSource("movie:abc", MatchSourceKind.Movie, "A film", new Dictionary<string, string>());
        SaveShared(0, Range(1, 12, A));
        SaveShared(1, Range(1, 13, A));
        _store.Save(Source, Personal(Range(1, 12, B)), 0, "user");
        _store.Save(Source, new Match(new MatchScope(ApiName.AniList, SourceKey, Guid.NewGuid()), [Range(1, 12, C)], []), 0, "user");
        _store.Save(Source, new Match(new MatchScope(ApiName.Kitsu, SourceKey, Guid.NewGuid()), [Range(1, 12, D)], []), 0, "user");
        _store.Save(other, new Match(new MatchScope(ApiName.Mal, "movie:abc"), [Range(1, 1, A)], []), 0, Author);

        var sources = _store.ListSources();

        Assert.That(sources.Select(source => source.Source.Title), Is.EqualTo(new[] { "A film", "Show · season 1" }));
        Assert.That(sources[0].Source.Kind, Is.EqualTo(MatchSourceKind.Movie));
        Assert.That(sources[0].Shared[ApiName.Mal], Is.EqualTo(1));
        Assert.That(sources[1].Source.Fingerprint["series:AniDB"], Is.EqualTo("42"));
        Assert.That(sources[1].Shared[ApiName.AniList], Is.EqualTo(2));
        Assert.That(sources[1].Overrides[ApiName.AniList], Is.EqualTo(2));
        Assert.That(sources[1].Overrides[ApiName.Kitsu], Is.EqualTo(1));
        Assert.That(sources[1].Shared.ContainsKey(ApiName.Kitsu), Is.False);
    }

    [Test]
    public void ASourceStaysListedAfterItsMatchesAreDeleted() {
        SaveShared(0, Range(1, 12, A));
        _store.Delete(SharedScope, 1, Author);

        MatchSourceSummary source = _store.ListSources().Single();

        Assert.That(source.Shared, Is.Empty);
        Assert.That(source.Overrides, Is.Empty);
    }

    [Test]
    public void UpsertRefreshesTheTitleAndFingerprint() {
        SaveShared(0, Range(1, 12, A));
        _clock.Now = _clock.Now.AddDays(1);

        _store.UpsertSource(Source with { Title = "New title", Fingerprint = new Dictionary<string, string> { ["series:Tvdb"] = "9" } });

        MatchSourceSummary source = _store.ListSources().Single();
        Assert.That(source.Source.Title, Is.EqualTo("New title"));
        Assert.That(source.Source.Fingerprint, Is.EqualTo(new Dictionary<string, string> { ["series:Tvdb"] = "9" }));
        Assert.That(source.UpdatedUtc, Is.EqualTo(_clock.Now));
        Assert.That(_store.Get(SharedScope), Is.Not.Null);
    }

    [Test]
    public void AnUnavailableDatabaseThrows() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        var store = new MatchStore(new AniDokiDatabase(Path.Combine(file, "anidoki.db")), _clock);

        Assert.Throws<InvalidOperationException>(() => store.Get(SharedScope));
        Assert.Throws<InvalidOperationException>(() => store.SweepDeletedUsers([_user]));
    }

    [Test]
    public void OverridesAreListedBySourceOrOwnerWithoutTheSharedMatch() {
        var other = Guid.NewGuid();
        const string otherKey = "season:00000000000000000000000000000002:1";
        var otherSource = Source with { SourceKey = otherKey };
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, Personal(Range(1, 12, B)) with { BaseSharedRevision = 1 }, 0, Author);
        _store.Save(Source, new Match(new MatchScope(ApiName.Kitsu, SourceKey, other), [Range(1, 12, C)], []), 0, Author);
        _store.Save(otherSource, new Match(new MatchScope(ApiName.AniList, otherKey, _user), [Range(1, 12, D)], []), 0, Author);

        Assert.That(_store.ListOverrides(SourceKey), Is.EqualTo(new[] {
            new OverrideSummary(_personalScope, 1, 1),
            new OverrideSummary(new MatchScope(ApiName.Kitsu, SourceKey, other), 1, null)
        }));
        Assert.That(_store.ListOverrides(owner: _user).Select(entry => entry.Scope.SourceKey), Is.EqualTo(new[] { SourceKey, otherKey }));
        Assert.That(_store.ListOverrides(SourceKey, other).Single().Scope.Provider, Is.EqualTo(ApiName.Kitsu));
        Assert.That(_store.ListOverrides("movie:00000000000000000000000000000003"), Is.Empty);
    }

    [Test]
    public void ADeletedOverrideIsNoLongerListed() {
        _store.Save(Source, Personal(Range(1, 12, B)), 0, Author);
        _store.Delete(_personalScope, 1, Author);

        Assert.That(_store.ListOverrides(SourceKey), Is.Empty);
    }

    [Test]
    public void MovingASourceCarriesItsMatchesOverridesAndHistory() {
        var moved = Source with { SourceKey = "season:00000000000000000000000000000009:1", Title = "Show · season 1 (rebuilt)" };
        SaveShared(0, Range(1, 12, A));
        SaveShared(1, Range(1, 12, B));
        _store.Save(Source, Personal(Range(1, 12, C)), 0, Author);

        Assert.That(_store.MoveSource(SourceKey, moved), Is.EqualTo(MatchWriteStatus.Written));

        var shared = new MatchScope(ApiName.AniList, moved.SourceKey);
        Assert.That(_store.Get(shared)!.Match.Revision, Is.EqualTo(2));
        Assert.That(_store.History(shared).Select(entry => entry.Revision), Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(_store.Get(_personalScope with { SourceKey = moved.SourceKey }), Is.Not.Null);
        Assert.That(_store.Get(SharedScope), Is.Null);
        Assert.That(_store.History(SharedScope), Is.Empty);
        Assert.That(_store.ListSources().Single().Source, Is.EqualTo(moved).Using<MatchSource>((x, y) => x.SourceKey == y.SourceKey && x.Title == y.Title));
    }

    [Test]
    public void MovingOntoASourceWithMatchesOrHistoryIsAConflictAndChangesNothing() {
        var other = Source with { SourceKey = "season:00000000000000000000000000000009:1" };
        SaveShared(0, Range(1, 12, A));
        _store.Save(other, new Match(new MatchScope(ApiName.Kitsu, other.SourceKey), [Range(1, 12, B)], []), 0, Author);
        _store.Delete(new MatchScope(ApiName.Kitsu, other.SourceKey), 1, Author);

        Assert.That(_store.MoveSource(SourceKey, other), Is.EqualTo(MatchWriteStatus.Conflict), "history alone is enough to refuse");
        Assert.That(_store.Get(SharedScope), Is.Not.Null);
        Assert.That(_store.MoveSource("season:00000000000000000000000000000008:1", Source with { SourceKey = "movie:00000000000000000000000000000007" }),
            Is.EqualTo(MatchWriteStatus.NotFound));
    }

    [Test]
    public void DeletingASourceRemovesItsMatchesHistoryAndRow() {
        SaveShared(0, Range(1, 12, A));
        _store.Save(Source, Personal(Range(1, 12, B)), 0, Author);

        Assert.That(_store.DeleteSource(SourceKey), Is.EqualTo(MatchWriteStatus.Written));

        Assert.That(_store.ListSources(), Is.Empty);
        Assert.That(_store.History(SharedScope), Is.Empty);
        Assert.That(Count("matches") + Count("match_ranges") + Count("match_revisions"), Is.EqualTo(0));
        Assert.That(_store.DeleteSource(SourceKey), Is.EqualTo(MatchWriteStatus.NotFound));
    }
}
