using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>
/// Records how UpdateProviderStatus resolves the target entry across the paths the decision matrix does not reach:
/// title search, later seasons, multi-cour walks, specials, movies, and the regressions fixed in #14.
/// All six providers run in their configured order, with PlanToWatchOnly and RewatchCompleted off.
/// </summary>
[TestFixture, NonParallelizable]
public class FlowCharacterizationTests {
    private readonly StringBuilder _output = new();
    private readonly Guid _user = Guid.NewGuid();

    [Test]
    public async Task FlowsMatchApproved() {
        await IdsFromArmSeasonOne();
        await TitleSearchSeasonOne();
        await TitleSearchSeasonTwoFollowsSequel();
        await SequelFetchFailure();
        await MultiCourWalk();
        await MultiCourPastLastCour();
        await SpecialResolvesSideStory();
        await MovieWithAniListId();
        await MovieByTitle();
        await UnconfiguredShikimoriBeforeAniList();
        await InstanceReuseAcrossSeries();
        await MissingEpisodeNumber();

        ApprovedFile.Verify("flows", _output.ToString());
    }

    private async Task IdsFromArmSeasonOne() {
        using var harness = NewHarness();
        harness.SeedAll(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12, Watching(3)));
        var episode = harness.Library.Episode("Fixture Show", 1, 5, new Dictionary<string, string> { ["AniList"] = SyncHarness.EntryId.ToString() });
        await Record("ids-from-arm-season-1", harness, episode);
    }

    private async Task TitleSearchSeasonOne() {
        using var harness = NewHarness();
        harness.SeedAll(SyncHarness.Entry(600, "Fixture Show", 12, Watching(3)));
        var episode = harness.Library.Episode("Fixture Show", 1, 5);
        await Record("title-search-season-1", harness, episode);
    }

    private async Task TitleSearchSeasonTwoFollowsSequel() {
        using var harness = NewHarness();
        harness.SeedAll(
            SyncHarness.Entry(600, "Fixture Show", 12, Completed(12), SyncHarness.Related(RelationType.Sequel, 601)),
            SyncHarness.Entry(601, "Fixture Show 2nd Season", 12, Watching(2)));
        var episode = harness.Library.Episode("Fixture Show", 2, 3);
        await Record("title-search-season-2-sequel", harness, episode);
    }

    private async Task SequelFetchFailure() {
        using var harness = NewHarness();
        harness.SeedAll(
            SyncHarness.Entry(600, "Fixture Show", 12, Completed(12), SyncHarness.Related(RelationType.Sequel, 601)),
            SyncHarness.Entry(601, "Fixture Show 2nd Season", 12, Watching(2)));
        foreach (var provider in SyncHarness.AllProviders)
            harness.Tracker(provider).FailingIds.Add(601);
        var episode = harness.Library.Episode("Fixture Show", 2, 3);
        await Record("sequel-fetch-fails", harness, episode);
    }

    private async Task MultiCourWalk() {
        using var harness = NewHarness();
        harness.SeedAll(
            SyncHarness.Entry(700, "Cour Show", 12, null, SyncHarness.Related(RelationType.Sequel, 701)),
            SyncHarness.Entry(701, "Cour Show Part 2", 12, null));
        var episode = harness.Library.Episode("Cour Show", 1, 15);
        await Record("multi-cour-walk", harness, episode);
    }

    private async Task MultiCourPastLastCour() {
        using var harness = NewHarness();
        harness.SeedAll(
            SyncHarness.Entry(700, "Cour Show", 12, null, SyncHarness.Related(RelationType.Sequel, 701)),
            SyncHarness.Entry(701, "Cour Show Part 2", 12, null));
        var episode = harness.Library.Episode("Cour Show", 1, 30);
        await Record("multi-cour-past-last-cour", harness, episode);
    }

    private async Task SpecialResolvesSideStory() {
        using var harness = NewHarness();
        var special = SyncHarness.Entry(801, "Fixture OVA", 1, null);
        special.AlternativeTitles.En = "Fixture OVA";
        harness.SeedAll(SyncHarness.Entry(800, "Fixture Show", 12, Completed(12), SyncHarness.Related(RelationType.Side_Story, 801)), special);
        var episode = harness.Library.Episode("Fixture Show", 0, 1, episodeName: "Fixture OVA");
        await Record("special-season-0", harness, episode);
    }

    private async Task MovieWithAniListId() {
        using var harness = NewHarness();
        // Title search finds 901; the IDs ARM resolves point at a same-named entry, 500 (a remake, say).
        harness.SeedAll(SyncHarness.Entry(901, "Fixture Movie", 1, null), SyncHarness.Entry(SyncHarness.EntryId, "Fixture Movie", 1, null));
        var movie = harness.Library.Movie("Fixture Movie", new Dictionary<string, string> { ["AniList"] = "900" });
        await Record("movie-with-anilist-id", harness, movie);
        _output.Append($"movie IndexNumber after sync: {movie.IndexNumber?.ToString() ?? "null"}\n");
    }

    private async Task MovieByTitle() {
        using var harness = NewHarness();
        harness.SeedAll(SyncHarness.Entry(901, "Fixture Movie", 1, null));
        var movie = harness.Library.Movie("Fixture Movie");
        await Record("movie-by-title", harness, movie);
        _output.Append($"movie IndexNumber after sync: {movie.IndexNumber?.ToString() ?? "null"}\n");
    }

    private async Task UnconfiguredShikimoriBeforeAniList() {
        using var harness = NewHarness();
        harness.Configuration.shikimoriAppName = "";
        harness.SeedAll(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12, Watching(3)));
        var episode = harness.Library.Episode("Fixture Show", 1, 5, new Dictionary<string, string> { ["AniList"] = SyncHarness.EntryId.ToString() });
        await Record("unconfigured-shikimori", harness, episode, ApiName.Shikimori, ApiName.AniList);
    }

    private async Task InstanceReuseAcrossSeries() {
        using var harness = NewHarness();
        harness.SeedAll(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12, Watching(3)));
        var updater = harness.NewUpdater();
        var first = harness.Library.Episode("Fixture Show", 1, 5, new Dictionary<string, string> { ["AniList"] = SyncHarness.EntryId.ToString() });
        var second = harness.Library.Episode("Other Show", 1, 7);
        harness.ConfigureUser(_user, false, false, SyncHarness.AllProviders);
        _output.Append("## instance-reuse (first: Fixture Show with IDs)\n");
        _output.Append(await harness.Run(updater, first, _user, SyncUpdateSource.Playback, SyncHarness.AllProviders)).Append('\n');
        _output.Append("## instance-reuse (second: Other Show without IDs, same instance)\n");
        _output.Append(await harness.Run(updater, second, _user, SyncUpdateSource.Playback, SyncHarness.AllProviders)).Append('\n');
    }

    private async Task MissingEpisodeNumber() {
        using var harness = NewHarness();
        harness.SeedAll(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12, Watching(3)));
        var episode = harness.Library.Episode("Fixture Show", 1, null, new Dictionary<string, string> { ["AniList"] = SyncHarness.EntryId.ToString() });
        await Record("missing-episode-number", harness, episode);
    }

    private SyncHarness NewHarness() => new();

    private async Task Record(string name, SyncHarness harness, MediaBrowser.Controller.Entities.BaseItem item, params ApiName[] providers) {
        if (providers.Length == 0)
            providers = SyncHarness.AllProviders;
        harness.ConfigureUser(_user, false, false, providers);
        _output.Append($"## {name}\n");
        _output.Append(await harness.Run(harness.NewUpdater(), item, _user, SyncUpdateSource.Playback, providers)).Append('\n');
    }

    private static MyListStatus Watching(int progress) => new() { Status = Status.Watching, NumEpisodesWatched = progress };

    private static MyListStatus Completed(int progress) => new() { Status = Status.Completed, NumEpisodesWatched = progress };
}
