using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using MediaBrowser.Controller.Entities.TV;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>
/// Records the decision UpdateProviderStatus makes for every combination of provider, list state, episode position,
/// user rules and trigger, through the ID-resolution path (season 1, AniList ID on the series).
/// </summary>
[TestFixture, NonParallelizable]
public class DecisionCharacterizationTests {
    private static readonly (int Episode, int Total)[] Positions = [(1, 12), (5, 12), (12, 12), (1, 1), (5, 0)];

    private static readonly (bool PlanToWatchOnly, bool RewatchCompleted)[] Rules = [(false, false), (false, true), (true, false), (true, true)];

    // LegacyManual follows the same decision path as Playback; only ConfirmedManual changes decisions.
    private static readonly SyncUpdateSource[] Sources = [SyncUpdateSource.Playback, SyncUpdateSource.ConfirmedManual];

    [Test]
    public async Task DecisionsMatchApproved() {
        using var harness = new SyncHarness();
        var user = Guid.NewGuid();
        var episodes = new Dictionary<int, Episode>();
        foreach (int episode in Positions.Select(position => position.Episode).Distinct())
            episodes[episode] = harness.Library.Episode("Fixture Show", 1, episode,
                new Dictionary<string, string> { ["AniList"] = SyncHarness.EntryId.ToString() });

        var output = new StringBuilder();
        foreach (var provider in SyncHarness.AllProviders)
            foreach (var (episode, total) in Positions)
                foreach (var (label, status) in States(provider, total))
                    foreach (var (planToWatchOnly, rewatchCompleted) in Rules)
                        foreach (var source in Sources) {
                            harness.ResetTrackers();
                            harness.ConfigureUser(user, planToWatchOnly, rewatchCompleted, provider);
                            var tracker = harness.Tracker(provider);
                            var entry = tracker.Add(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", total, status));
                            tracker.SearchResults.Add(entry);

                            string result = await harness.RunSingle(harness.NewUpdater(), episodes[episode], user, source, provider);

                            output.Append($"{provider} | {label} | e={episode} n={total} | ptw={OnOff(planToWatchOnly)} rewatch={OnOff(rewatchCompleted)} | {source} => {result}\n");
                        }

        ApprovedFile.Verify("decisions", output.ToString());
    }

    /// <summary>
    /// List states as each provider's conversion presents them to UpdateProviderStatus.
    /// </summary>
    private static IEnumerable<(string Label, MyListStatus? Status)> States(ApiName provider, int total) {
        int completedProgress = total > 0 ? total : 12;
        // ApiCallHelpers.GetConvertedKitsuUserList returns an empty MyListStatus (Watching, progress 0) when the
        // entry is not on the user's list; the other conversions leave it null.
        yield return ("not-on-list", provider == ApiName.Kitsu ? new MyListStatus() : null);
        yield return ("plan-to-watch p=0", new MyListStatus { Status = Status.Plan_to_watch, NumEpisodesWatched = 0 });
        yield return ("watching p=3", new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 3 });
        yield return ("watching p=8", new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 8 });
        yield return ("on-hold p=3", new MyListStatus { Status = Status.On_hold, NumEpisodesWatched = 3 });
        yield return ("dropped p=3", new MyListStatus { Status = Status.Dropped, NumEpisodesWatched = 3 });
        yield return ($"completed p={completedProgress} x1", new MyListStatus { Status = Status.Completed, NumEpisodesWatched = completedProgress, RewatchCount = 1 });
        if (provider is not (ApiName.Simkl or ApiName.Annict))
            yield return ("rewatching p=3 x1", Rewatching(provider));
    }

    private static MyListStatus Rewatching(ApiName provider) => provider switch {
        // MAL keeps the entry completed with is_rewatching set (see UpdateAnimeStatus's MAL rewatch branch).
        ApiName.Mal => new MyListStatus { Status = Status.Completed, IsRewatching = true, NumEpisodesWatched = 3, RewatchCount = 1 },
        // Kitsu reports a reconsuming entry as current (Watching) with Reconsuming set.
        ApiName.Kitsu => new MyListStatus { Status = Status.Watching, IsRewatching = true, NumEpisodesWatched = 3, RewatchCount = 1 },
        // AniList (Repeating) and Shikimori (rewatching) convert to Status.Rewatching.
        _ => new MyListStatus { Status = Status.Rewatching, IsRewatching = true, NumEpisodesWatched = 3, RewatchCount = 1 }
    };

    private static string OnOff(bool value) => value ? "on" : "off";
}
