using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Resolution.ResolutionTestSupport;

namespace jellyfin_anidoki_unit_tests.Matching;

/// <summary>
/// The bridge to Save as match: a derived match resolves to exactly what automatic resolution does, or there is none and
/// the season stays automatic. The scenarios repeat FlowCharacterizationTests' seeds and IDs, and a test checks that none is missing.
/// </summary>
[TestFixture, NonParallelizable]
public class DerivedMatchTests {
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>What SourceIdResolver gets from the ARM fixture for an item with an AniList ID.</summary>
    private static readonly SourceIds Arm = IdsFor(SyncHarness.EntryId);

    private static readonly SourceIds NoIds = new(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), null);

    private static readonly Dictionary<string, string> WithAniList = new() { ["AniList"] = SyncHarness.EntryId.ToString() };

    private static readonly ApiName[] Neutral = [ApiName.Annict, ApiName.Shikimori, ApiName.Simkl];

    /// <summary>These routes never walk, so today they write past the entry's end; a match will not, so none is derived.</summary>
    private static readonly HashSet<string> PastEntryEndFlows =
        ["split-season-by-ids", "split-season-unknown-length", "split-season-non-sequel", "length-disagreement-13-vs-12"];

    private sealed record Flow(string Name, Func<LibraryFixture, SyncSource?> Source, SourceIds Ids, Anime[] Seed, int[]? Failing = null, ApiName[]? Providers = null);

    private static Anime Entry(int id, string title, int episodes, MyListStatus? status, params RelatedAnime[] related) =>
        SyncHarness.Entry(id, title, episodes, status, related);

    private static RelatedAnime Sequel(int id) => SyncHarness.Related(RelationType.Sequel, id);

    private static MyListStatus Watching(int progress) => new() { Status = Status.Watching, NumEpisodesWatched = progress };

    private static MyListStatus Completed(int progress) => new() { Status = Status.Completed, NumEpisodesWatched = progress };

    private static Func<LibraryFixture, SyncSource?> Episode(string show, int season, int? episode, Dictionary<string, string>? ids = null, string? name = null) =>
        library => SyncSource.From(library.Episode(show, season, episode, ids, name));

    private static Func<LibraryFixture, SyncSource?> Movie(string name, Dictionary<string, string>? ids = null) =>
        library => SyncSource.From(library.Movie(name, ids));

    private static Anime NamedSpecial() {
        var special = Entry(801, "Fixture OVA", 1, null);
        special.AlternativeTitles.En = "Fixture OVA";
        return special;
    }

    private static readonly Flow[] Flows = [
        new("ids-from-arm-season-1", Episode("Fixture Show", 1, 5, WithAniList), Arm, [Entry(500, "Fixture Show", 12, Watching(3))]),
        new("title-search-season-1", Episode("Fixture Show", 1, 5), NoIds, [Entry(600, "Fixture Show", 12, Watching(3))]),
        new("title-search-season-2-sequel", Episode("Fixture Show", 2, 3), NoIds,
            [Entry(600, "Fixture Show", 12, Completed(12), Sequel(601)), Entry(601, "Fixture Show 2nd Season", 12, Watching(2))]),
        new("sequel-fetch-fails", Episode("Fixture Show", 2, 3), NoIds,
            [Entry(600, "Fixture Show", 12, Completed(12), Sequel(601)), Entry(601, "Fixture Show 2nd Season", 12, Watching(2))], [601]),
        new("multi-cour-walk", Episode("Cour Show", 1, 15), NoIds, [Entry(700, "Cour Show", 12, null, Sequel(701)), Entry(701, "Cour Show Part 2", 12, null)]),
        new("multi-cour-past-last-cour", Episode("Cour Show", 1, 30), NoIds, [Entry(700, "Cour Show", 12, null, Sequel(701)), Entry(701, "Cour Show Part 2", 12, null)]),
        new("special-season-0", Episode("Fixture Show", 0, 1, name: "Fixture OVA"), NoIds,
            [Entry(800, "Fixture Show", 12, Completed(12), SyncHarness.Related(RelationType.Side_Story, 801)), NamedSpecial()]),
        new("movie-with-anilist-id", Movie("Fixture Movie", new Dictionary<string, string> { ["AniList"] = "900" }), Arm,
            [Entry(901, "Fixture Movie", 1, null), Entry(500, "Fixture Movie", 1, null)]),
        new("movie-by-title", Movie("Fixture Movie"), NoIds, [Entry(901, "Fixture Movie", 1, null)]),
        new("unconfigured-shikimori", Episode("Fixture Show", 1, 5, WithAniList), Arm, [Entry(500, "Fixture Show", 12, Watching(3))], Providers: [ApiName.AniList]),
        new("instance-reuse (first: Fixture Show with IDs)", Episode("Fixture Show", 1, 5, WithAniList), Arm, [Entry(500, "Fixture Show", 12, Watching(3))]),
        new("instance-reuse (second: Other Show without IDs, same instance)", Episode("Other Show", 1, 7), NoIds, [Entry(500, "Fixture Show", 12, Watching(3))]),
        new("missing-episode-number", Episode("Fixture Show", 1, null, WithAniList), Arm, [Entry(500, "Fixture Show", 12, Watching(3))]),
        new("split-season-by-ids", Episode("JoJo's Bizarre Adventure", 1, 3, WithAniList), Arm,
            [Entry(500, "JoJo's Bizarre Adventure", 1, null, Sequel(501)), Entry(501, "JoJo's Bizarre Adventure Part 2", 12, null)]),
        new("split-season-unknown-length", Episode("JoJo's Bizarre Adventure", 1, 2, WithAniList), Arm,
            [Entry(500, "JoJo's Bizarre Adventure", 1, Completed(1), Sequel(501)), Entry(501, "JoJo's Bizarre Adventure Part 2", 0, null)]),
        new("split-season-non-sequel", Episode("JoJo's Bizarre Adventure", 1, 3, WithAniList), Arm,
            [Entry(500, "JoJo's Bizarre Adventure", 1, null, SyncHarness.Related(RelationType.Side_Story, 501)), Entry(501, "JoJo's Bizarre Adventure Part 2", 12, null)]),
        new("length-disagreement-13-vs-12", Episode("JoJo's Bizarre Adventure", 1, 13, WithAniList), Arm, [Entry(500, "JoJo's Bizarre Adventure", 12, Watching(12))])
    ];

    private static IEnumerable<TestCaseData> FlowsByProvider() =>
        Flows.SelectMany(flow => (flow.Providers ?? SyncHarness.AllProviders).Select(provider =>
            new TestCaseData(flow.Name, provider).SetName($"{{m}}({flow.Name}, {provider})")));

    private static (ITrackerProvider Tracker, FakeTracker Fake) Seeded(ApiName provider, Flow flow) {
        var (tracker, fake) = ReadOnly(provider);
        foreach (var entry in flow.Seed)
            fake.Add(entry);
        fake.SearchResults.Add(flow.Seed[0]);
        foreach (int id in flow.Failing ?? [])
            fake.FailingIds.Add(id);
        return (tracker, fake);
    }

    /// <summary>What a resolution sends to the planner, leaving out how it was found.</summary>
    private static object[] Targets(SourceResolution resolution) {
        var parts = resolution.EarlierParts.Select(Shape);
        return (resolution is Resolved resolved ? parts.Append(Shape(resolved.Played)) : parts).Cast<object>().ToArray();
    }

    private static (EntryRef, int, TargetRole, string) Shape(ResolvedTarget target) => (target.Entry, target.TargetEpisode, target.Role, target.Title);

    private static async Task<IReadOnlyDictionary<EntryRef, EntryState>> Read(Match match, ITrackerProvider tracker) {
        var entries = new Dictionary<EntryRef, EntryState>();
        foreach (var target in match.Targets) {
            if (await tracker.ReadEntry(target.Entry, None) is { } state)
                entries[target.Entry] = state;
        }

        return entries;
    }

    private static string FlowsFile([CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "..", "Characterization", "Approved", "flows.approved.txt");

    [Test]
    public void EveryFlowScenarioIsCovered() {
        var approved = File.ReadAllLines(FlowsFile()).Where(line => line.StartsWith("## ", StringComparison.Ordinal)).Select(line => line[3..]);

        Assert.That(Flows.Select(flow => flow.Name), Is.EquivalentTo(approved));
    }

    [TestCaseSource(nameof(FlowsByProvider))]
    public async Task DerivedMatchResolvesLikeAutomaticOrThereIsNone(string name, ApiName provider) {
        Flow flow = Flows.Single(candidate => candidate.Name == name);
        var (tracker, _) = Seeded(provider, flow);
        SyncSource? source = flow.Source(new LibraryFixture());
        if (source == null) {
            Assert.That(name, Is.EqualTo("missing-episode-number"));
            return;
        }

        var automatic = await new AutomaticResolver(NullLogger.Instance).Resolve(source, flow.Ids, tracker, None);
        var derived = await new RelationChainSegmenter(tracker, NullLogger.Instance)
            .ToMatch(new MatchScope(provider, source.SourceKey), source, automatic, source.EpisodeNumber, None);

        if (automatic is Unresolved unresolved) {
            Assert.That((derived.Match, derived.FailureReason), Is.EqualTo(((Match?)null, (string?)unresolved.Reason)));
            return;
        }

        if (PastEntryEndFlows.Contains(name) && Neutral.Contains(provider)) {
            Assert.That((derived.Match, derived.FailureReason), Is.EqualTo(((Match?)null, (string?)"past-entry-end")));
            return;
        }

        Assert.That(derived.Match, Is.Not.Null, () => derived.FailureReason ?? "");
        Assert.That(MatchValidator.Validate(derived.Match!, source.Kind, await Read(derived.Match!, tracker)).Errors, Is.Empty);
        var through = RangeResolver.Resolve(derived.Match!, source.EpisodeNumber, source.SearchTitle, await Read(derived.Match!, tracker));
        Assert.That(Targets(through), Is.EqualTo(Targets(automatic)));
    }

    private static (ITrackerProvider Tracker, FakeTracker Fake) Chain(ApiName provider, params int[] lengths) {
        var (tracker, fake) = ReadOnly(provider);
        for (int index = 0; index < lengths.Length; index++) {
            int id = index + 1;
            fake.Add(id < lengths.Length ? Entry(id, $"cour {id}", lengths[index], null, Sequel(id + 1)) : Entry(id, $"cour {id}", lengths[index], null));
        }

        fake.SearchResults.Add(Entry(1, "Show", lengths[0], null));
        return (tracker, fake);
    }

    private static SourceIds IdsFor(int id, int? offset = null) {
        var ids = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
            Anilist = id,
            MyAnimeList = id,
            Kitsu = id,
            AniDb = id
        };
        return new SourceIds(ids, offset);
    }

    private static async Task<DerivedSegmentation> Derive(ITrackerProvider tracker, SyncSource source, SourceIds ids, int lastEpisode) {
        var automatic = await new AutomaticResolver(NullLogger.Instance).Resolve(source, ids, tracker, None);
        return await new RelationChainSegmenter(tracker, NullLogger.Instance).ToMatch(new MatchScope(tracker.Name, source.SourceKey), source, automatic, lastEpisode, None);
    }

    private static (int Start, int? End, int Entry, int Target)[] Ranges(Match match) =>
        match.Ranges.Select(range => (range.SourceStart, range.SourceEnd, range.Target.Entry.Id, range.TargetStart)).ToArray();

    // Save as match renders a whole season from one episode, so every other episode must keep its automatic answer too.
    [Test]
    public async Task AWholeSeasonMatchEqualsAutomaticForEveryEpisode() {
        var library = new LibraryFixture();
        var (tracker, _) = Chain(ApiName.Mal, 12, 12, 12, 12);
        var derived = await Derive(tracker, SyncSource.From(library.Episode("Show", 1, 5))!, IdsFor(1), 48);
        var entries = await Read(derived.Match!, tracker);

        Assert.That(Ranges(derived.Match!), Is.EqualTo(new (int, int?, int, int)[] { (1, 12, 1, 1), (13, 24, 2, 1), (25, 36, 3, 1), (37, 48, 4, 1) }));
        foreach (int episode in Enumerable.Range(1, 48)) {
            var source = SyncSource.From(library.Episode("Show", 1, episode))!;
            var automatic = await new AutomaticResolver(NullLogger.Instance).Resolve(source, IdsFor(1), tracker, None);
            var through = RangeResolver.Resolve(derived.Match!, episode, "Show", entries);
            Assert.That(Targets(through), Is.EqualTo(Targets(automatic)), $"episode {episode}");
        }
    }

    [Test]
    public async Task AChainThatDoesNotCoverTheSeasonHasNoDerivedMatch() {
        var (tracker, _) = Chain(ApiName.Mal, 12, 12);

        var derived = await Derive(tracker, SyncSource.From(new LibraryFixture().Episode("Show", 1, 5))!, IdsFor(1), 48);

        Assert.That((derived.Match, derived.FailureReason), Is.EqualTo(((Match?)null, (string?)"no-cour-covers-episode")));
    }

    [Test]
    public async Task ACourOfUnknownLengthGetsAnOpenRange() {
        var (tracker, _) = Chain(ApiName.AniList, 12, 0);

        var derived = await Derive(tracker, SyncSource.From(new LibraryFixture().Episode("Show", 1, 15))!, IdsFor(1), 15);

        Assert.That(Ranges(derived.Match!), Is.EqualTo(new (int, int?, int, int)[] { (1, 12, 1, 1), (13, null, 2, 1) }));
    }

    // Episode 30 at an AniDB offset of 12 is ID progress 18: past the 12 of the first cour.
    [Test]
    public async Task TheIdPathsOffsetMovesTheRanges() {
        var library = new LibraryFixture();
        var (tracker, _) = Chain(ApiName.AniList, 12, 12);
        var source = SyncSource.From(library.Episode("Show", 1, 30))!;
        var ids = IdsFor(1, offset: 12);

        var derived = await Derive(tracker, source, ids, 30);

        Assert.That(Ranges(derived.Match!), Is.EqualTo(new (int, int?, int, int)[] { (13, 24, 1, 1), (25, 36, 2, 1) }));
        var automatic = await new AutomaticResolver(NullLogger.Instance).Resolve(source, ids, tracker, None);
        Assert.That(Targets(RangeResolver.Resolve(derived.Match!, 30, "Show", await Read(derived.Match!, tracker))), Is.EqualTo(Targets(automatic)));
    }

    // AutomaticResolverTests.IdOffsetHasNoGuard pins this episode as -2; no match can hold that, and none should.
    [Test]
    public async Task AnOffsetPastTheEpisodeHasNoDerivedMatch() {
        var (tracker, _) = Chain(ApiName.AniList, 12);

        var derived = await Derive(tracker, SyncSource.From(new LibraryFixture().Episode("Show", 1, 3))!, IdsFor(1, offset: 5), 12);

        Assert.That((derived.Match, derived.FailureReason), Is.EqualTo(((Match?)null, (string?)"offset-not-representable")));
    }

    // Episode 5 at an offset of 3 is entry episode 2, but episode 3 stays 3 on the title path: no range says that.
    [Test]
    public async Task ATitlePathOffsetHasNoDerivedMatch() {
        var (tracker, _) = Chain(ApiName.AniList, 12);
        var offsetOnly = new SourceIds(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse(), 3);

        var derived = await Derive(tracker, SyncSource.From(new LibraryFixture().Episode("cour 1", 1, 5))!, offsetOnly, 12);

        Assert.That((derived.Match, derived.FailureReason), Is.EqualTo(((Match?)null, (string?)"offset-not-representable")));
    }

    [Test]
    public async Task AMovieIsOneRangeOfOneEpisode() {
        var (tracker, _) = Chain(ApiName.AniList, 1);

        var derived = await Derive(tracker, SyncSource.From(new LibraryFixture().Movie("Film"))!, IdsFor(1), 1);

        Assert.That(Ranges(derived.Match!), Is.EqualTo(new (int, int?, int, int)[] { (1, 1, 1, 1) }));
    }

    [Test]
    public async Task ARouteThatNeverWalksGetsOneOpenRange() {
        var (tracker, _) = Chain(ApiName.Simkl, 12);

        var derived = await Derive(tracker, SyncSource.From(new LibraryFixture().Episode("Show", 1, 5))!, IdsFor(1), 12);

        Assert.That(Ranges(derived.Match!), Is.EqualTo(new (int, int?, int, int)[] { (1, null, 1, 1) }));
    }

    [Test]
    public async Task DerivingNeverWrites() {
        var (tracker, fake) = Chain(ApiName.AniList, 12, 12, 0);

        await Derive(tracker, SyncSource.From(new LibraryFixture().Episode("Show", 1, 5))!, IdsFor(1), 40);

        Assert.That(fake.Writes, Is.Empty);
    }

    private static async Task<bool> FollowsChain(ITrackerProvider tracker, SyncSource source, SourceIds ids) =>
        ((Resolved)await new AutomaticResolver(NullLogger.Instance).Resolve(source, ids, tracker, None)).FollowsChain;

    [Test]
    public async Task IdRoutesFollowTheChainWhetherOrNotTheyWalked() {
        var library = new LibraryFixture();

        Assert.That(await FollowsChain(Chain(ApiName.AniList, 12).Tracker, SyncSource.From(library.Episode("Show", 1, 5))!, IdsFor(1)), Is.True);
        Assert.That(await FollowsChain(Chain(ApiName.AniList, 1, 12).Tracker, SyncSource.From(library.Episode("Show", 1, 3))!, IdsFor(1)), Is.True);
    }

    [Test]
    public async Task SeasonOneTitleSearchFollowsTheChain() =>
        Assert.That(await FollowsChain(Chain(ApiName.AniList, 12).Tracker, SyncSource.From(new LibraryFixture().Episode("cour 1", 1, 5))!, NoIds), Is.True);

    [Test]
    public async Task CrossIdsDoNotFollowTheChain() =>
        Assert.That(await FollowsChain(Chain(ApiName.Simkl, 12).Tracker, SyncSource.From(new LibraryFixture().Episode("Show", 1, 5))!, IdsFor(1)), Is.False);

    [Test]
    public async Task AMovieDoesNotFollowTheChain() =>
        Assert.That(await FollowsChain(Chain(ApiName.AniList, 1).Tracker, SyncSource.From(new LibraryFixture().Movie("Film"))!, IdsFor(1)), Is.False);

    [Test]
    public async Task LaterSeasonTitleSearchDoesNotFollowTheChain() =>
        Assert.That(await FollowsChain(Chain(ApiName.AniList, 12, 12).Tracker, SyncSource.From(new LibraryFixture().Episode("cour 1", 2, 5))!, NoIds), Is.False);

    [Test]
    public async Task MatchingByMalIdDoesNotFollowTheChain() =>
        Assert.That(await FollowsChain(Chain(ApiName.Shikimori, 12).Tracker, SyncSource.From(new LibraryFixture().Episode("Show", 1, 5))!, IdsFor(1)), Is.False);

    private static Anime[] SteelBallRun(RelatedAnime link) => [
        Entry(500, "STEEL BALL RUN 1st STAGE", 1, null, link),
        Entry(501, "STEEL BALL RUN 2nd STAGE", 12, null)
    ];

    private static (ITrackerProvider Tracker, FakeTracker Fake) Seeded(Anime[] entries) {
        var (tracker, fake) = ReadOnly(ApiName.AniList);
        foreach (var entry in entries)
            fake.Add(entry);
        return (tracker, fake);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(13)]
    public async Task SteelBallRunSplitMatchesDerivedChain(int episode) {
        var library = new LibraryFixture();
        var (tracker, _) = Seeded(SteelBallRun(Sequel(501)));
        var source = SyncSource.From(library.Episode("JoJo's Bizarre Adventure", 1, episode))!;

        var derived = await Derive(tracker, source, IdsFor(500), 13);

        Assert.That(Ranges(derived.Match!), Is.EqualTo(new (int, int?, int, int)[] { (1, 1, 500, 1), (2, 13, 501, 1) }));
        var automatic = await new AutomaticResolver(NullLogger.Instance).Resolve(source, IdsFor(500), tracker, None);
        Assert.That(Targets(RangeResolver.Resolve(derived.Match!, episode, "JoJo", await Read(derived.Match!, tracker))), Is.EqualTo(Targets(automatic)));
    }

    [Test]
    public async Task NonSequelSplitResolvesOnlyThroughSavedMatch() {
        var library = new LibraryFixture();
        var (tracker, _) = Seeded(SteelBallRun(SyncHarness.Related(RelationType.Side_Story, 501)));
        var source = SyncSource.From(library.Episode("JoJo's Bizarre Adventure", 1, 3))!;

        var automatic = await new AutomaticResolver(NullLogger.Instance).Resolve(source, IdsFor(500), tracker, None);
        var derived = await Derive(tracker, source, IdsFor(500), 13);
        var scope = new MatchScope(ApiName.AniList, source.SourceKey);
        var saved = new Match(scope, [
            new MatchRange(1, 1, new MatchTarget(new EntryRef(ApiName.AniList, 500, null), "1st STAGE"), 1),
            new MatchRange(2, 13, new MatchTarget(new EntryRef(ApiName.AniList, 501, null), "2nd STAGE"), 1)
        ], []);
        var resolved = (Resolved)RangeResolver.Resolve(saved, 3, "JoJo", await Read(saved, tracker));

        Assert.That(((Unresolved)automatic).Reason, Is.EqualTo("no-cour-covers-episode"));
        Assert.That(derived.Match, Is.Null);
        Assert.That((resolved.Played.Entry.Id, resolved.Played.TargetEpisode), Is.EqualTo((501, 2)));
        Assert.That(resolved.EarlierParts.Select(part => (part.Entry.Id, part.TargetEpisode)), Is.EqualTo(new[] { (500, 1) }));
    }
}
