using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Resolution.ResolutionTestSupport;

namespace jellyfin_anidoki_unit_tests.Resolution;

/// <summary>
/// Resolution alone: IDs are given directly (no ARM), and every test checks that resolving wrote nothing.
/// </summary>
[TestFixture, NonParallelizable]
public class AutomaticResolverTests {
    private const string Show = "JoJo's Bizarre Adventure";
    private LibraryFixture _library = null!;
    private FakeTracker? _fake;

    [SetUp]
    public void SetUp() {
        _library = new LibraryFixture();
        _fake = null;
    }

    [TearDown]
    public void TearDown() => Assert.That(_fake?.Writes, Is.Null.Or.Empty, "resolution must never write");

    private static SourceIds Ids(int? mal = null, int? aniList = null, int? kitsu = null, int? aniDb = null, int? offset = null) =>
        new(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse { MyAnimeList = mal, Anilist = aniList, Kitsu = kitsu, AniDb = aniDb }, offset);

    private static SourceIds AllIds(int id = SyncHarness.EntryId, int? offset = null) => Ids(id, id, id, id, offset);

    private SyncSource Episode(int season, int episode, Dictionary<string, string>? seriesIds = null, string? episodeName = null) =>
        SyncSource.From(_library.Episode(Show, season, episode, seriesIds, episodeName))!;

    private FakeTracker Tracker(ApiName provider, params Anime[] entries) {
        var (_, fake) = ReadOnly(provider);
        foreach (var entry in entries)
            fake.Add(entry);
        return fake;
    }

    private async Task<SourceResolution> Resolve(ApiName provider, SyncSource source, SourceIds ids, params Anime[] entries) {
        _fake ??= Tracker(provider, entries);
        var tracker = new ReadOnlyTracker(new ApiCallHelpersTrackerProvider(provider, _fake, System.TimeProvider.System));
        return await new AutomaticResolver(NullLogger.Instance).Resolve(source, ids, tracker, CancellationToken.None);
    }

    /// <summary>A tracker whose searches return <paramref name="results"/> in order; every result is also an entry.</summary>
    private void Searching(ApiName provider, Anime[] results, params Anime[] others) {
        _fake = Tracker(provider, results.Concat(others).ToArray());
        _fake.SearchResults.AddRange(results);
    }

    private static Anime Entry(int id, string title, int episodes, params RelatedAnime[] related) =>
        SyncHarness.Entry(id, title, episodes, null, related);

    private static RelatedAnime Sequel(int id) => SyncHarness.Related(RelationType.Sequel, id);

    private static void AssertPlayed(SourceResolution resolution, int id, int episode, ResolvedBy by) {
        Assert.That(resolution, Is.TypeOf<Resolved>(), () => resolution.ToString());
        var resolved = (Resolved)resolution;
        Assert.That((resolved.Played.Entry.Id, resolved.Played.TargetEpisode, resolved.Played.Role, resolved.By),
            Is.EqualTo((id, episode, TargetRole.Played, by)));
    }

    private static void AssertUnresolved(SourceResolution resolution, string reason, UnresolvedRecord? record) {
        Assert.That(resolution, Is.TypeOf<Unresolved>(), () => resolution.ToString());
        var unresolved = (Unresolved)resolution;
        Assert.That(unresolved.Reason, Is.EqualTo(reason));
        Assert.That(unresolved.Record, Is.EqualTo(record));
    }

    [TestCase(ApiName.Mal)]
    [TestCase(ApiName.AniList)]
    [TestCase(ApiName.Kitsu)]
    public async Task ResolvedIdReadsTheEntry(ApiName provider) {
        var resolution = await Resolve(provider, Episode(1, 5), AllIds(), Entry(500, Show, 12));

        AssertPlayed(resolution, 500, 5, ResolvedBy.Ids);
        Assert.That(resolution.EarlierParts, Is.Empty);
        Assert.That(((Resolved)resolution).Played.Title, Is.EqualTo(Show));
    }

    [Test]
    public async Task ResolvedIdThatCannotBeReadIsNotFound() {
        _fake = Tracker(ApiName.AniList, Entry(500, Show, 12));
        _fake.FailingIds.Add(500);

        var resolution = await Resolve(ApiName.AniList, Episode(1, 5), AllIds());

        AssertUnresolved(resolution, "not-found", new UnresolvedRecord("500", ""));
    }

    [Test]
    public async Task ResolvedIdPastItsLengthWalks() {
        var resolution = await Resolve(ApiName.AniList, Episode(1, 3), AllIds(), Entry(500, Show, 1, Sequel(501)), Entry(501, Show + " Part 2", 12));

        AssertPlayed(resolution, 501, 2, ResolvedBy.SequelChain);
        Assert.That(resolution.EarlierParts.Select(part => (part.Entry.Id, part.TargetEpisode, part.Role)),
            Is.EqualTo(new[] { (500, 1, TargetRole.EarlierPart) }));
    }

    [Test]
    public async Task ResolvedIdWalkFailureRecordsTheFirstCour() {
        var resolution = await Resolve(ApiName.AniList, Episode(1, 13), AllIds(), Entry(500, Show, 12));

        AssertUnresolved(resolution, "no-cour-covers-episode", new UnresolvedRecord("500", Show));
    }

    // Today's behaviour, pinned: the ID path subtracts the offset with no guard. PR 13 replaces it.
    [Test]
    public async Task IdOffsetHasNoGuard() {
        var resolution = await Resolve(ApiName.AniList, Episode(1, 3), AllIds(offset: 5), Entry(500, Show, 12));

        AssertPlayed(resolution, 500, -2, ResolvedBy.Ids);
    }

    [Test]
    public async Task AniListFallsBackToTheSeriesId() {
        var source = Episode(1, 5, new Dictionary<string, string> { ["AniList"] = "500" });

        var resolution = await Resolve(ApiName.AniList, source, Ids(), Entry(500, Show, 12));

        AssertPlayed(resolution, 500, 5, ResolvedBy.Ids);
    }

    [Test]
    public async Task SpecialsSkipTheIdPath() {
        var special = Entry(801, "Fixture OVA", 1);
        special.AlternativeTitles.En = "Fixture OVA";
        Searching(ApiName.AniList, [Entry(800, Show, 12, SyncHarness.Related(RelationType.Side_Story, 801))], special, Entry(500, Show, 12));

        var resolution = await Resolve(ApiName.AniList, Episode(0, 1, episodeName: "Fixture OVA"), AllIds());

        AssertPlayed(resolution, 801, 1, ResolvedBy.TitleSearch);
    }

    [Test]
    public async Task SimklResolvesThroughCrossIds() {
        var resolution = await Resolve(ApiName.Simkl, Episode(1, 20), Ids(mal: 500), Entry(500, Show, 12));

        // The ID path's episode, with no length check and no walk, as today.
        AssertPlayed(resolution, 500, 20, ResolvedBy.CrossIds);
        var lookup = ((Resolved)resolution).Played.Entry.Lookup;
        Assert.That(lookup, Is.EqualTo(new CrossReference(new ResolvedIds(null, 500, null, null), Show, MediaKind.Series)));
        Assert.That(resolution.EarlierParts, Is.Empty);
    }

    [Test]
    public async Task SimklWithoutIdsSearchesAndFindsNothing() {
        var resolution = await Resolve(ApiName.Simkl, Episode(1, 5), Ids(), Entry(500, Show, 12));

        AssertUnresolved(resolution, "no-title-match", new UnresolvedRecord("", Show));
    }

    [Test]
    public async Task SimklMoviesStayOffTheIdPath() {
        var source = SyncSource.From(_library.Movie("Fixture Movie"))!;

        var resolution = await Resolve(ApiName.Simkl, source, AllIds(), Entry(500, "Fixture Movie", 1));

        AssertUnresolved(resolution, "no-title-match", new UnresolvedRecord("", "Fixture Movie"));
    }

    [TestCase(ApiName.Annict)]
    [TestCase(ApiName.Shikimori)]
    public async Task MatchesOnMalIdWithoutSeasonHandling(ApiName provider) {
        Searching(provider, [Entry(600, "Other", 12), Entry(500, "Different Title", 12)]);

        var resolution = await Resolve(provider, Episode(2, 3), Ids(mal: 500));

        AssertPlayed(resolution, 500, 3, ResolvedBy.TitleSearch);
        Assert.That(((Resolved)resolution).Played.Entry.AlternativeId, Is.EqualTo("alt-500"));
        Assert.That(_fake!.Reads, Is.Empty, "no sequel is followed");
    }

    [Test]
    public async Task ShikimoriMatchesOnTitlesWithoutMalId() {
        Searching(ApiName.Shikimori, [Entry(600, Show, 12, Sequel(601))], Entry(601, Show + " 2nd Season", 12));

        var resolution = await Resolve(ApiName.Shikimori, Episode(2, 3), Ids());

        AssertPlayed(resolution, 601, 3, ResolvedBy.SequelChain);
        Assert.That(((Resolved)resolution).Played.Entry.AlternativeId, Is.EqualTo("alt-601"));
    }

    [Test]
    public async Task TitleSearchTakesTheFirstMatch() {
        Searching(ApiName.Mal, [Entry(600, "Other", 12), Entry(601, Show, 12), Entry(602, Show, 12)]);

        var resolution = await Resolve(ApiName.Mal, Episode(1, 3), Ids());

        AssertPlayed(resolution, 601, 3, ResolvedBy.TitleSearch);
    }

    [Test]
    public async Task TitleSearchMatchesOriginalTitle() {
        var candidate = Entry(600, "Shou", 12);
        candidate.AlternativeTitles.Ja = "ショー";
        Searching(ApiName.Mal, [candidate]);
        var episode = _library.Episode("Show", 1, 3);
        episode.Series.OriginalTitle = "ショー";

        var resolution = await Resolve(ApiName.Mal, SyncSource.From(episode)!, Ids());

        AssertPlayed(resolution, 600, 3, ResolvedBy.TitleSearch);
    }

    [Test]
    public async Task NoTitleMatchRecordsTheSearchTitle() {
        Searching(ApiName.Mal, [Entry(600, "Other", 12)]);

        var resolution = await Resolve(ApiName.Mal, Episode(1, 3), Ids());

        AssertUnresolved(resolution, "no-title-match", new UnresolvedRecord("", Show));
    }

    [Test]
    public async Task LaterSeasonFollowsSequels() {
        Searching(ApiName.Mal, [Entry(600, Show, 12, Sequel(601))], Entry(601, "S2", 12, Sequel(602)), Entry(602, "S3", 12));

        var resolution = await Resolve(ApiName.Mal, Episode(3, 7), Ids(offset: 2));

        AssertPlayed(resolution, 602, 5, ResolvedBy.SequelChain);
    }

    // Today a later season that cannot be reached records nothing.
    [Test]
    public async Task LaterSeasonFailureIsSilent() {
        Searching(ApiName.Mal, [Entry(600, Show, 12)]);

        var resolution = await Resolve(ApiName.Mal, Episode(2, 3), Ids());

        AssertUnresolved(resolution, "sequel-fetch-failed", null);
    }

    private Anime SeedSpecial(string? english) {
        var special = Entry(801, "Fixture OVA", 1);
        special.AlternativeTitles.En = english!;
        Searching(ApiName.Mal, [Entry(800, Show, 12, SyncHarness.Related(RelationType.Side_Story, 801))], special);
        return special;
    }

    [Test]
    public async Task SpecialMatchesARelatedEntryByEpisodeName() {
        SeedSpecial("Fixture OVA");

        var resolution = await Resolve(ApiName.Mal, Episode(0, 4, episodeName: "fixture ova"), Ids(offset: 1));

        AssertPlayed(resolution, 801, 3, ResolvedBy.TitleSearch);
    }

    [Test]
    public async Task SpecialNeedsAnEnglishTitle() {
        SeedSpecial(null);

        var resolution = await Resolve(ApiName.Mal, Episode(0, 1, episodeName: "Fixture OVA"), Ids());

        AssertUnresolved(resolution, "no-special-match", null);
    }

    // Today a special without a name throws once an eligible related entry is read (decision 9).
    [Test]
    public async Task SpecialWithoutEpisodeNameIsSilent() {
        SeedSpecial("Fixture OVA");
        var episode = _library.Episode(Show, 0, 1);
        episode.Name = null;

        SourceResolution? resolution = null;
        Assert.DoesNotThrowAsync(async () => resolution = await Resolve(ApiName.Mal, SyncSource.From(episode)!, Ids()));

        AssertUnresolved(resolution!, "no-special-match", null);
    }

    // Today's quirk, pinned: the rough match drops every character outside ASCII letters and digits, so a name with
    // none left (here, Japanese only) matches the first eligible related entry.
    [Test]
    public async Task SpecialNameWithoutLatinCharactersMatchesTheFirstEligibleEntry() {
        SeedSpecial("Fixture OVA");

        var resolution = await Resolve(ApiName.Mal, Episode(0, 1, episodeName: "特別編"), Ids());

        AssertPlayed(resolution, 801, 1, ResolvedBy.TitleSearch);
    }

    [Test]
    public async Task TitleWalkUsesTheRawEpisodeNumber() {
        Searching(ApiName.Mal, [Entry(700, Show, 12, Sequel(701))], Entry(701, "Part 2", 12));

        var resolution = await Resolve(ApiName.Mal, Episode(1, 15), Ids(offset: 2));

        // 12 < 15 is compared on the raw number, and the target is 15 - 12, with the offset ignored, as today.
        AssertPlayed(resolution, 701, 3, ResolvedBy.SequelChain);
        Assert.That(resolution.EarlierParts.Select(part => (part.Entry.Id, part.TargetEpisode)), Is.EqualTo(new[] { (700, 12) }));
    }

    [Test]
    public async Task TitleWalkFailureRecordsTheCandidate() {
        Searching(ApiName.Shikimori, [Entry(700, "Cour Show", 12)]);
        var source = SyncSource.From(_library.Episode("Cour Show", 1, 30))!;

        var resolution = await Resolve(ApiName.Shikimori, source, Ids());

        AssertUnresolved(resolution, "no-cour-covers-episode", new UnresolvedRecord("alt-700", "Cour Show"));
    }

    [Test]
    public async Task MovieByTitleIsEpisodeOne() {
        Searching(ApiName.Mal, [Entry(901, "Fixture Movie", 1)]);
        var source = SyncSource.From(_library.Movie("Fixture Movie"))!;

        var resolution = await Resolve(ApiName.Mal, source, Ids());

        AssertPlayed(resolution, 901, 1, ResolvedBy.TitleSearch);
    }

    // Today a candidate with no alternative titles throws during the title check (decision 9).
    [TestCase("Other", false)]
    [TestCase(Show, true)]
    public async Task CandidateWithoutAlternativeTitlesDoesNotThrow(string title, bool matches) {
        var candidate = Entry(600, title, 12);
        candidate.AlternativeTitles = null!;
        Searching(ApiName.Mal, [candidate]);

        var resolution = await Resolve(ApiName.Mal, Episode(1, 3), Ids());

        if (matches)
            AssertPlayed(resolution, 600, 3, ResolvedBy.TitleSearch);
        else
            AssertUnresolved(resolution, "no-title-match", new UnresolvedRecord("", Show));
    }

    [Test]
    public async Task ResolvedIdCarriesTheReadState() {
        var resolution = await Resolve(ApiName.AniList, Episode(1, 5), AllIds(),
            SyncHarness.Entry(500, Show, 12, new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 3, RewatchCount = 0 }));

        var state = ((Resolved)resolution).Played.State;
        Assert.That((state?.Entry.Id, state?.TotalEpisodes, state?.List), Is.EqualTo((500, 12, new ListState(TrackerStatus.Watching, 3, false, 0))));
    }

    [Test]
    public async Task ResolvedIdWalkCarriesEveryCourState() {
        var resolution = await Resolve(ApiName.AniList, Episode(1, 3), AllIds(), Entry(500, Show, 1, Sequel(501)), Entry(501, Show + " Part 2", 12));

        Assert.That(resolution.EarlierParts.Single().State?.Entry.Id, Is.EqualTo(500));
        Assert.That(((Resolved)resolution).Played.State?.Entry.Id, Is.EqualTo(501));
    }

    [Test]
    public async Task TitleMatchLeavesTheReadToTheExecutor() {
        Searching(ApiName.Mal, [Entry(600, Show, 12)]);
        var played = await Resolve(ApiName.Mal, Episode(1, 3), Ids());
        Assert.That(((Resolved)played).Played.State, Is.Null);

        _fake = null;
        Searching(ApiName.Mal, [Entry(900, "Fixture Movie", 1)]);
        var movie = await Resolve(ApiName.Mal, SyncSource.From(_library.Movie("Fixture Movie"))!, Ids());
        Assert.That(((Resolved)movie).Played.State, Is.Null);
    }

    [Test]
    public async Task TitleWalkCarriesStatesAfterTheFirstCour() {
        Searching(ApiName.Mal, [Entry(700, Show, 12, Sequel(701))], Entry(701, "Part 2", 12));

        var resolution = await Resolve(ApiName.Mal, Episode(1, 15), Ids());

        Assert.That(resolution.EarlierParts.Single().State, Is.Null);
        Assert.That(((Resolved)resolution).Played.State?.Entry.Id, Is.EqualTo(701));
    }

    [Test]
    public async Task LaterSeasonAndSpecialCarryTheirState() {
        Searching(ApiName.Mal, [Entry(600, Show, 12, Sequel(601))], Entry(601, "S2", 12, Sequel(602)), Entry(602, "S3", 12));
        var season = await Resolve(ApiName.Mal, Episode(3, 7), Ids(offset: 2));
        Assert.That(((Resolved)season).Played.State?.Entry.Id, Is.EqualTo(602));

        _fake = null;
        SeedSpecial("Fixture OVA");
        var special = await Resolve(ApiName.Mal, Episode(0, 4, episodeName: "fixture ova"), Ids(offset: 1));
        Assert.That(((Resolved)special).Played.State?.Entry.Id, Is.EqualTo(801));
    }

    [Test]
    public async Task CrossIdsLeaveTheReadToTheExecutor() {
        var resolution = await Resolve(ApiName.Simkl, Episode(1, 5), Ids(mal: 500), Entry(500, Show, 12));

        Assert.That(((Resolved)resolution).Played.State, Is.Null);
    }
}
