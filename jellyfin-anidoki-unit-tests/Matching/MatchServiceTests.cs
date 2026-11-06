using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchApiFixture;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture]
public class MatchServiceTests {
    private MatchApiFixture _fixture = null!;
    private MatchService _service = null!;

    [SetUp]
    public void SetUp() {
        _fixture = new MatchApiFixture();
        _service = _fixture.Service;
        _fixture.Trackers.Link(_fixture.Admin.Id, _fixture.Catalogue);
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    private Task<MatchResult> SaveShared(SaveMatchRequest request) =>
        _service.Save(_fixture.SharedScope, request, _fixture.Admin.Id, null, CancellationToken.None);

    private static T Body<T>(MatchResult result, MatchOutcome outcome = MatchOutcome.Ok) {
        Assert.That(result.Outcome, Is.EqualTo(outcome), () => $"Body: {result.Body}");
        return (T)result.Body!;
    }

    // Save

    [Test]
    public async Task AValidSharedMatchIsSavedAsRevisionOne() {
        var saved = Body<SavedContract>(await SaveShared(FourByTwelve()));

        Assert.That(saved.Revision, Is.EqualTo(1));
        Assert.That(saved.Warnings, Is.Empty);
        StoredMatch stored = _fixture.Store.Get(_fixture.SharedScope)!;
        Assert.That(stored.Match.Ranges.Select(range => range.Target.Entry.Id), Is.EqualTo(new[] { A, B, C, D }));
        Assert.That(stored.Note, Is.EqualTo("split by AniList"));
        Assert.That(stored.UpdatedBy, Is.EqualTo(_fixture.Admin.Id.ToString("D")));
        Assert.That(_fixture.Store.ListSources().Single().Source.Title, Is.EqualTo("Long Show · season 1"));
    }

    [Test]
    public async Task WarningsAreReturnedAndDoNotBlockTheSave() {
        var saved = Body<SavedContract>(await SaveShared(new SaveMatchRequest(0, [Span(1, 12, A), Span(20, 31, B)], [], null)));

        Assert.That(saved.Warnings.Select(issue => issue.Rule), Does.Contain("W1"));
        Assert.That(_fixture.Store.Get(_fixture.SharedScope), Is.Not.Null);
    }

    [Test]
    public async Task AnInvalidMatchIsRefusedWithItsErrorsAndNothingIsWritten() {
        var invalid = Body<InvalidMatchContract>(await SaveShared(new SaveMatchRequest(0, [Span(1, 12, A), Span(10, 21, B)], [], null)), MatchOutcome.Invalid);

        Assert.That(invalid.Errors.Select(issue => issue.Rule), Does.Contain("V3"));
        Assert.That(_fixture.Store.Get(_fixture.SharedScope), Is.Null);
        Assert.That(_fixture.Store.ListSources(), Is.Empty);
    }

    [Test]
    public async Task ATargetTheTrackerCannotReadFailsV6() {
        var invalid = Body<InvalidMatchContract>(await SaveShared(new SaveMatchRequest(0, [Span(1, 12, 999)], [], null)), MatchOutcome.Invalid);

        Assert.That(invalid.Errors.Single().Rule, Is.EqualTo("V6"));
        Assert.That(invalid.Errors.Single().Message, Does.Contain("Entry 999"));
    }

    [Test]
    public async Task WithNoLinkedAccountV6SaysSoOnce() {
        _fixture.Trackers.Linked.Clear();

        var invalid = Body<InvalidMatchContract>(await SaveShared(FourByTwelve()), MatchOutcome.Invalid);

        Assert.That(invalid.Errors, Has.Count.EqualTo(1));
        Assert.That(invalid.Errors[0].Message, Does.Contain("No user on this server has linked AniList"));
    }

    [Test]
    public async Task TheAdminsCatalogueReadsBorrowAnotherUsersLinkWhenTheyHaveNone() {
        _fixture.Trackers.Linked.Clear();
        _fixture.Trackers.Link(_fixture.Bob.Id, _fixture.Catalogue);

        Assert.That((await SaveShared(FourByTwelve())).Outcome, Is.EqualTo(MatchOutcome.Ok));
        Assert.That(_fixture.Catalogue.Reads, Is.EqualTo(4));
    }

    [Test]
    public async Task AUserSavingTheirOwnCorrectionReadsOnlyThroughTheirOwnLink() {
        var invalid = Body<InvalidMatchContract>(await _service.Save(_fixture.PersonalScope(_fixture.Alice), FourByTwelve(), _fixture.Alice.Id, _fixture.Alice.Id,
            CancellationToken.None), MatchOutcome.Invalid);

        Assert.That(invalid.Errors.Single().Message, Is.EqualTo("Link AniList first, so its entries can be checked."));
        Assert.That(_fixture.Catalogue.Reads, Is.EqualTo(0), "the admin's link was not borrowed");
    }

    [Test]
    public async Task AStaleExpectedRevisionIsAConflictWithTheCurrentRevision() {
        await SaveShared(FourByTwelve());

        var conflict = Body<RevisionConflictContract>(await SaveShared(FourByTwelve(expectedRevision: 0)), MatchOutcome.Conflict);

        Assert.That(conflict.CurrentRevision, Is.EqualTo(1));
        Assert.That(_fixture.Store.History(_fixture.SharedScope), Has.Count.EqualTo(1));
    }

    [TestCase("ignore-twice")]
    [TestCase("no-target")]
    [TestCase("no-title")]
    [TestCase("unknown-kind")]
    [TestCase("target-without-episode")]
    public async Task AMalformedRequestIsABadRequest(string problem) {
        SaveMatchRequest request = problem switch {
            "ignore-twice" => new(0, [Span(1, 12, A)], [new(5, "ignore", null, null), new(5, "ignore", null, null)], null),
            "no-target" => new(0, [new RangeContract(1, 12, null, 1)], [], null),
            "no-title" => new(0, [new RangeContract(1, 12, new EntryContract(A, null, null, ""), 1)], [], null),
            "unknown-kind" => new(0, [], [new(5, "skip", null, null)], null),
            _ => new(0, [], [new(5, "target", Entry(A), null)], null)
        };

        Assert.That((await SaveShared(request)).Outcome, Is.EqualTo(MatchOutcome.BadRequest));
        Assert.That(_fixture.Store.ListSources(), Is.Empty);
    }

    [Test]
    public async Task AnUnknownSourceIsNotFound() {
        var result = await _service.Save(new MatchScope(Provider, "season:00000000000000000000000000000009:1"), FourByTwelve(), _fixture.Admin.Id, null,
            CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.NotFound));
    }

    [Test]
    public async Task APersonalOverrideRecordsTheSharedRevisionAndLinkItWasSavedUnder() {
        await SaveShared(FourByTwelve());
        _fixture.Trackers.Link(_fixture.Alice.Id, _fixture.Catalogue);

        var saved = Body<SavedContract>(await _service.Save(_fixture.PersonalScope(_fixture.Alice), new SaveMatchRequest(0, [Span(1, 12, A)], [], null),
            _fixture.Alice.Id, _fixture.Alice.Id, CancellationToken.None));

        Assert.That(saved.Warnings.Select(issue => issue.Rule), Does.Not.Contain("W4"));
        StoredMatch stored = _fixture.Store.Get(_fixture.PersonalScope(_fixture.Alice))!;
        Assert.That(stored.Match.BaseSharedRevision, Is.EqualTo(1));
        Assert.That(stored.ConnectionGeneration, Is.EqualTo(_fixture.Trackers.Links(_fixture.Alice.Id)[Provider].ToString("D")));
    }

    // Delete

    [Test]
    public async Task DeletingRecordsADeleteRevision() {
        await SaveShared(FourByTwelve());

        var deleted = Body<SavedContract>(_service.Delete(_fixture.SharedScope, 1, _fixture.Admin.Id, null));

        Assert.That(deleted.Revision, Is.EqualTo(2));
        Assert.That(_fixture.Store.Get(_fixture.SharedScope), Is.Null);
        Assert.That(_fixture.Store.History(_fixture.SharedScope).Last().Action, Is.EqualTo(MatchRevisionAction.Delete));
    }

    [Test]
    public async Task DeletingWithAStaleRevisionIsAConflictAndNothingIsDeleted() {
        await SaveShared(FourByTwelve());

        Assert.That(Body<RevisionConflictContract>(_service.Delete(_fixture.SharedScope, 5, _fixture.Admin.Id, null), MatchOutcome.Conflict).CurrentRevision, Is.EqualTo(1));
        Assert.That(_fixture.Store.Get(_fixture.SharedScope), Is.Not.Null);
    }

    [Test]
    public void DeletingWhatDoesNotExistIsNotFound() =>
        Assert.That(_service.Delete(_fixture.SharedScope, 0, _fixture.Admin.Id, null).Outcome, Is.EqualTo(MatchOutcome.NotFound));

    // Preview

    [Test]
    public async Task PreviewShowsWhereEachEpisodeGoesWithTheSyncReasons() {
        var request = new PreviewRequest([Span(1, 12, A), Span(13, 24, 999)], [new(6, "ignore", null, null), new(7, "target", Entry(B), 13)]);

        var preview = Body<PreviewContract>(await _service.Preview(_fixture.SharedScope, request, _fixture.Admin.Id, null, null, CancellationToken.None));

        Assert.That(preview.Rows, Has.Count.EqualTo(48));
        Assert.That(preview.Rows[0], Is.EqualTo(new PreviewRowContract(1, "mapped", Entry(A), 1)));
        Assert.That(preview.Rows[5].Outcome, Is.EqualTo("ignored"));
        Assert.That(preview.Rows[6], Is.EqualTo(new PreviewRowContract(7, "past-entry-end", Entry(B), 13)));
        Assert.That(preview.Rows[12], Is.EqualTo(new PreviewRowContract(13, "match-target-unreadable", Entry(999), 1)));
        Assert.That(preview.Rows[24], Is.EqualTo(new PreviewRowContract(25, "not-in-match", null, null)));
        Assert.That(preview.Errors.Select(issue => issue.Rule), Is.EquivalentTo(new[] { "V4", "V6" }), "errors are shown, and the table is still drawn");
        Assert.That(preview.Progress, Is.Null);
    }

    [Test]
    public async Task PreviewSavesNothing() {
        await _service.Preview(_fixture.SharedScope, Draft(FourByTwelve()), _fixture.Admin.Id, _fixture.Admin.Id, null, CancellationToken.None);

        Assert.That(_fixture.Store.Get(_fixture.SharedScope), Is.Null);
        Assert.That(_fixture.Store.ListSources(), Is.Empty);
    }

    [Test]
    public async Task PreviewProgressIsHighestWatchedFromTheUsersOwnList() {
        var alicesList = new CatalogueFake(Provider);
        alicesList.Add(A, 12, new ListState(TrackerStatus.Completed, 12, false, 0));
        alicesList.Add(B, 12, new ListState(TrackerStatus.Watching, 3, false, 0));
        alicesList.Add(C, 12);
        alicesList.Add(D, 12);
        _fixture.Trackers.Link(_fixture.Alice.Id, alicesList);
        _fixture.WatchState.ByUser[_fixture.Alice.Id] = [.. Enumerable.Range(1, 14), 20];

        var preview = Body<PreviewContract>(await _service.Preview(_fixture.SharedScope, Draft(FourByTwelve()), _fixture.Admin.Id, _fixture.Alice.Id, null,
            CancellationToken.None));

        Assert.That(preview.Progress, Is.EqualTo(new[] {
            new PreviewProgressContract(Entry(A), 12, TrackerStatus.Completed, 12),
            new PreviewProgressContract(Entry(B), 8, TrackerStatus.Watching, 3)
        }));
        Assert.That(preview.BorrowedLink, Is.False);
    }

    [Test]
    public async Task PreviewShowsNoProgressForAUserWhoHasNotLinkedTheProvider() {
        _fixture.WatchState.ByUser[_fixture.Alice.Id] = [1, 2];

        var preview = Body<PreviewContract>(await _service.Preview(_fixture.SharedScope, Draft(FourByTwelve()), _fixture.Admin.Id, _fixture.Alice.Id, null,
            CancellationToken.None));

        Assert.That(preview.Progress, Is.Null, "list state is only ever read through the owner's own link");
        Assert.That(preview.Rows[0].Outcome, Is.EqualTo("mapped"), "titles and totals still come through the admin's link");
    }

    // Promote

    private async Task SaveAlicesOverride(params RangeContract[] ranges) {
        _fixture.Trackers.Link(_fixture.Alice.Id, _fixture.Catalogue);
        await _service.Save(_fixture.PersonalScope(_fixture.Alice), new SaveMatchRequest(0, ranges, [], "Alice's fix"), _fixture.Alice.Id, _fixture.Alice.Id,
            CancellationToken.None);
    }

    [Test]
    public async Task PromotingMakesTheOverrideSharedAndCountsOtherOverridesThatNowDiffer() {
        await SaveShared(FourByTwelve());
        await SaveAlicesOverride(Span(1, 12, A), Span(13, 24, B));
        _fixture.Trackers.Link(_fixture.Bob.Id, _fixture.Catalogue);
        await _service.Save(_fixture.PersonalScope(_fixture.Bob), new SaveMatchRequest(0, [Span(1, 12, C)], [], null), _fixture.Bob.Id, _fixture.Bob.Id,
            CancellationToken.None);

        var promoted = Body<PromotedContract>(await _service.Promote(_fixture.Alice.Id, Provider, _fixture.Key, new PromoteRequest(1, 1), _fixture.Admin.Id,
            CancellationToken.None));

        Assert.That((promoted.SharedRevision, promoted.OverrideRevision, promoted.DifferingOverrides), Is.EqualTo((2L, 0L, 1)));
        Assert.That(_fixture.Store.Get(_fixture.SharedScope)!.Match.Ranges.Select(range => range.Target.Entry.Id), Is.EqualTo(new[] { A, B }));
        Assert.That(_fixture.Store.Get(_fixture.PersonalScope(_fixture.Alice)), Is.Null);
    }

    [Test]
    public async Task AnOverrideTheSameAsTheNewSharedMatchIsNotCountedEvenWithOtherTitles() {
        await SaveAlicesOverride(Span(1, 12, A));
        _fixture.Trackers.Link(_fixture.Bob.Id, _fixture.Catalogue);
        await _service.Save(_fixture.PersonalScope(_fixture.Bob), new SaveMatchRequest(0, [new RangeContract(1, 12, new EntryContract(A, null, null, "Renamed"), 1)], [], null),
            _fixture.Bob.Id, _fixture.Bob.Id, CancellationToken.None);

        var promoted = Body<PromotedContract>(await _service.Promote(_fixture.Alice.Id, Provider, _fixture.Key, new PromoteRequest(0, 1), _fixture.Admin.Id,
            CancellationToken.None));

        Assert.That(promoted.DifferingOverrides, Is.EqualTo(0));
    }

    [Test]
    public async Task PromotionIsValidatedAgainAndRefusedWhenATargetCannotBeRead() {
        await SaveAlicesOverride(Span(1, 12, A));
        _fixture.Catalogue.Entries.Remove(A);

        var invalid = Body<InvalidMatchContract>(await _service.Promote(_fixture.Alice.Id, Provider, _fixture.Key, new PromoteRequest(0, 1), _fixture.Admin.Id,
            CancellationToken.None), MatchOutcome.Invalid);

        Assert.That(invalid.Errors.Single().Rule, Is.EqualTo("V6"));
        Assert.That(_fixture.Store.Get(_fixture.SharedScope), Is.Null);
        Assert.That(_fixture.Store.Get(_fixture.PersonalScope(_fixture.Alice)), Is.Not.Null);
    }

    [Test]
    public async Task PromotingAgainstAMovedSharedRevisionIsAConflict() {
        await SaveAlicesOverride(Span(1, 12, A));
        await SaveShared(FourByTwelve());

        var conflict = Body<PromotionConflictContract>(await _service.Promote(_fixture.Alice.Id, Provider, _fixture.Key, new PromoteRequest(0, 1), _fixture.Admin.Id,
            CancellationToken.None), MatchOutcome.Conflict);

        Assert.That(conflict, Is.EqualTo(new PromotionConflictContract(1, 1)));
    }

    [Test]
    public async Task PromotingAMissingOverrideIsNotFound() =>
        Assert.That((await _service.Promote(_fixture.Alice.Id, Provider, _fixture.Key, new PromoteRequest(0, 1), _fixture.Admin.Id, CancellationToken.None)).Outcome,
            Is.EqualTo(MatchOutcome.NotFound));

    // Reads

    [Test]
    public async Task TheAdminDetailShowsTheSharedMatchHistoryAndStaleOverrides() {
        await SaveAlicesOverride(Span(1, 12, A));
        await SaveShared(FourByTwelve());

        var detail = Body<SourceDetailContract>(_service.Detail(_fixture.Key));

        Assert.That(detail.Episodes, Has.Count.EqualTo(48));
        Assert.That(detail.Providers.Select(provider => provider.Provider), Is.EqualTo(Enum.GetValues<ApiName>()));
        ProviderDetailContract aniList = detail.Providers.Single(provider => provider.Provider == Provider);
        Assert.That(aniList.Shared!.Revision, Is.EqualTo(1));
        Assert.That(aniList.Shared.UpdatedBy, Is.EqualTo("admin"));
        Assert.That(aniList.History.Single().Action, Is.EqualTo(MatchRevisionAction.Save));
        Assert.That(aniList.Overrides, Is.EqualTo(new[] { new OverrideContract(_fixture.Alice.Id, "alice", 1, null, true) }));
    }

    [Test]
    public async Task AUserSeesWhatAppliesToThemForEachLinkedProviderOnly() {
        await SaveShared(FourByTwelve());
        await SaveAlicesOverride(Span(1, 12, A));
        _fixture.Trackers.Link(_fixture.Bob.Id, _fixture.Catalogue);
        _fixture.Trackers.Link(_fixture.Bob.Id, new CatalogueFake(ApiName.Kitsu));

        var alice = Body<UserSourceDetailContract>(_service.UserDetail(_fixture.Key, _fixture.Alice.Id));
        var bob = Body<UserSourceDetailContract>(_service.UserDetail(_fixture.Key, _fixture.Bob.Id));

        Assert.That(alice.Providers.Single().Effective, Is.EqualTo("Personal"));
        Assert.That(alice.Providers.Single().Personal!.Stale, Is.False);
        Assert.That(bob.Providers.Select(provider => (provider.Provider, provider.Effective)),
            Is.EqualTo(new[] { (ApiName.AniList, "Shared"), (ApiName.Kitsu, "Automatic") }));
        Assert.That(bob.Providers[0].Personal, Is.Null);
    }

    [Test]
    public void AUserCannotFindASeasonOutsideTheirLibrary() {
        Assert.That(_service.UserDetail(_fixture.Hidden.Info.SourceKey, _fixture.Alice.Id).Outcome, Is.EqualTo(MatchOutcome.NotFound));
        Assert.That(_service.UserDetail(_fixture.Hidden.Info.SourceKey, _fixture.Bob.Id).Outcome, Is.EqualTo(MatchOutcome.Ok));
    }

    [Test]
    public async Task TheSourceListFiltersSearchesAndPages() {
        await SaveShared(FourByTwelve());

        var all = Body<SourcePageContract>(_service.ListSources(null, null, null, 1));
        var matched = Body<SourcePageContract>(_service.ListSources(null, null, MatchService.MatchedFilter, 1));
        var corrections = Body<SourcePageContract>(_service.ListSources(null, null, MatchService.CorrectionsFilter, 1));
        var searched = Body<SourcePageContract>(_service.ListSources(null, "hidden", null, 1));
        var past = Body<SourcePageContract>(_service.ListSources(null, null, null, 2));

        Assert.That(all.Items.Select(item => item.Title), Is.EqualTo(new[] { "Hidden Show · season 1", "Long Show · season 1" }));
        Assert.That(matched.Items.Single().Providers, Is.EqualTo(new[] { new ProviderStateContract(Provider, 1, 0, null) }));
        Assert.That(corrections.Total, Is.EqualTo(0));
        Assert.That(searched.Items.Single().SourceKey, Is.EqualTo(_fixture.Hidden.Info.SourceKey));
        Assert.That(past.Items, Is.Empty);
        Assert.That(past.Total, Is.EqualTo(2));
        Assert.That(_service.ListSources(null, null, "needs-match", 1).Outcome, Is.EqualTo(MatchOutcome.BadRequest));
    }

    [Test]
    public async Task AUsersSourceListShowsTheirOwnOverridesButNotOtherUsers() {
        await SaveAlicesOverride(Span(1, 12, A));
        _fixture.Trackers.Link(_fixture.Bob.Id, _fixture.Catalogue);

        var alice = Body<SourcePageContract>(_service.ListSources(_fixture.Alice.Id, null, MatchService.CorrectionsFilter, 1));
        var bob = Body<SourcePageContract>(_service.ListSources(_fixture.Bob.Id, null, null, 1));

        Assert.That(alice.Items.Single().Providers, Is.EqualTo(new[] { new ProviderStateContract(Provider, null, null, 1) }));
        Assert.That(bob.Items.SelectMany(item => item.Providers), Is.Empty, "Bob cannot tell that Alice has a correction");
        Assert.That(bob.Items, Has.Count.EqualTo(2));
        Assert.That(Body<SourcePageContract>(_service.ListSources(_fixture.Alice.Id, null, null, 1)).Items, Has.Count.EqualTo(1));
    }

    // Picker and automatic

    [Test]
    public async Task EntrySearchSaysWhenItBorrowedALink() {
        _fixture.Catalogue.Candidates.Add(new EntryCandidate(Ref(A), new EntryTitles("Entry 101", "Entry One", null, []), 12));

        var own = Body<EntrySearchContract>(await _service.SearchEntries(_fixture.Admin.Id, null, Provider, " entry ", CancellationToken.None));
        var borrowed = Body<EntrySearchContract>(await _service.SearchEntries(_fixture.Alice.Id, null, Provider, "entry", CancellationToken.None));
        var ownLinkOnly = Body<EntrySearchContract>(await _service.SearchEntries(_fixture.Alice.Id, _fixture.Alice.Id, Provider, "entry", CancellationToken.None));

        Assert.That(own.Entries.Single(), Is.EqualTo(new EntryCandidateContract(Entry(A), "Entry One", null, 12)));
        Assert.That(own.BorrowedLink, Is.False);
        Assert.That(borrowed.BorrowedLink, Is.True);
        Assert.That(ownLinkOnly.Entries, Is.Empty, "a user searching for their own correction never borrows a link");
        Assert.That((await _service.SearchEntries(_fixture.Admin.Id, null, Provider, " ", CancellationToken.None)).Outcome, Is.EqualTo(MatchOutcome.BadRequest));
    }

    [Test]
    public async Task ReadingAnEntryLeavesOutListState() {
        _fixture.Catalogue.Add(A, 12, new ListState(TrackerStatus.Watching, 4, false, 0));

        var entry = Body<EntryCandidateContract>(await _service.ReadEntry(_fixture.Alice.Id, Provider, A, null, CancellationToken.None));

        Assert.That(entry, Is.EqualTo(new EntryCandidateContract(Entry(A), null, null, 12)));
        Assert.That((await _service.ReadEntry(_fixture.Alice.Id, Provider, 999, null, CancellationToken.None)).Outcome, Is.EqualTo(MatchOutcome.NotFound));
    }

    [Test]
    public async Task TheAutomaticAnswerComesBackAsAMappingOrAReason() {
        _fixture.Automatic.Answer = new DerivedSegmentation(new Match(_fixture.SharedScope, [MatchTestSupport.Range(1, 48, A)], []), null);
        var derived = Body<AutomaticContract>(await _service.Automatic(_fixture.Key, Provider, _fixture.Admin.Id, CancellationToken.None));
        _fixture.Automatic.Answer = new DerivedSegmentation(null, "offset-not-representable");
        var none = Body<AutomaticContract>(await _service.Automatic(_fixture.Key, Provider, _fixture.Admin.Id, CancellationToken.None));
        _fixture.Trackers.Linked.Clear();
        var unlinked = Body<AutomaticContract>(await _service.Automatic(_fixture.Key, Provider, _fixture.Admin.Id, CancellationToken.None));

        Assert.That(derived.Mapping!.Ranges.Single(), Is.EqualTo(Span(1, 48, A)));
        Assert.That(none, Is.EqualTo(new AutomaticContract(null, "offset-not-representable", false)));
        Assert.That(unlinked.Reason, Is.EqualTo("no-linked-account"));
    }
}
