using System;
using System.Text;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>
/// Records what a Tracker → Jellyfin manual sync saves as played: finish dates, partial progress, anime-list offsets,
/// two seasons of one series, the ignore tag at each level, entries with no AniDB ID, and seasons already played.
/// </summary>
[TestFixture, NonParallelizable]
public class InboundCharacterizationTests {
    private readonly StringBuilder _output = new();

    [Test]
    public async Task InboundMatchesApproved() {
        await CompletedWithFinishDate();
        await WatchingWithoutFinishDate();
        await EpisodeOffset();
        await TwoSeasons();
        await IgnoredSeries();
        await IgnoredSeason();
        await IgnoredEpisode();
        await NoProgressMarksEverything();
        await NoAniDbId();
        await AlreadyPlayed();

        ApprovedFile.Verify("inbound", _output.ToString());
    }

    private async Task CompletedWithFinishDate() {
        using var harness = new InboundHarness();
        harness.Map(7001, 9001, "1");
        harness.Ids("anilist", 1001, 7001);
        harness.Series("Alpha", 1001, (1, 4));
        harness.Entry(1001, "Alpha", Status.Completed, 4, "2026-01-02");
        await Record("completed-with-finish-date", harness);
    }

    private async Task WatchingWithoutFinishDate() {
        using var harness = new InboundHarness();
        harness.Map(7001, 9001, "1");
        harness.Ids("anilist", 1001, 7001);
        harness.Series("Alpha", 1001, (1, 4));
        harness.Entry(1001, "Alpha", Status.Watching, 2);
        await Record("watching-without-finish-date", harness);
    }

    private async Task EpisodeOffset() {
        using var harness = new InboundHarness();
        harness.Map(7002, 9002, "1", offset: 2);
        harness.Ids("anilist", 1002, 7002);
        harness.Series("Beta", 1002, (1, 6));
        harness.Entry(1002, "Beta", Status.Watching, 3);
        await Record("episode-offset", harness);
    }

    private async Task TwoSeasons() {
        using var harness = new InboundHarness();
        harness.Map(7003, 9003, "1");
        harness.Map(7004, 9003, "2");
        harness.Ids("anilist", 1003, 7003);
        harness.Ids("anilist", 1004, 7004);
        harness.Series("Gamma", 1003, (1, 2), (2, 2));
        harness.Entry(1003, "Gamma", Status.Completed, 2, "2026-02-01");
        harness.Entry(1004, "Gamma 2", Status.Watching, 1);
        await Record("two-seasons", harness);
    }

    private async Task IgnoredSeries() {
        using var harness = new InboundHarness();
        harness.Map(7005, 9005, "1");
        harness.Ids("anilist", 1005, 7005);
        InboundHarness.Ignore(harness.Series("Delta", 1005, (1, 2)));
        harness.Entry(1005, "Delta", Status.Completed, 2, "2026-02-01");
        await Record("ignored-series", harness);
    }

    private async Task IgnoredSeason() {
        using var harness = new InboundHarness();
        harness.Map(7006, 9006, "1");
        harness.Ids("anilist", 1006, 7006);
        InboundHarness.Ignore(InboundHarness.SeasonOf(harness.Series("Epsilon", 1006, (1, 2)), 1));
        harness.Entry(1006, "Epsilon", Status.Completed, 2, "2026-02-01");
        await Record("ignored-season", harness);
    }

    private async Task IgnoredEpisode() {
        using var harness = new InboundHarness();
        harness.Map(7007, 9007, "1");
        harness.Ids("anilist", 1007, 7007);
        InboundHarness.Ignore(InboundHarness.EpisodeOf(harness.Series("Zeta", 1007, (1, 3)), 1, 2));
        harness.Entry(1007, "Zeta", Status.Completed, 3, "2026-03-01");
        await Record("ignored-episode", harness);
    }

    private async Task NoProgressMarksEverything() {
        using var harness = new InboundHarness();
        harness.Map(7008, 9008, "1");
        harness.Ids("anilist", 1008, 7008);
        harness.Series("Theta", 1008, (1, 3));
        harness.Entry(1008, "Theta", Status.Completed, 0, "2026-04-01");
        await Record("no-progress-marks-everything", harness);
    }

    private async Task NoAniDbId() {
        using var harness = new InboundHarness();
        harness.Map(7009, 9009, "1");
        harness.Series("Eta", 1009, (1, 2));
        harness.Entry(1009, "Eta", Status.Completed, 2, "2026-04-01");
        await Record("no-anidb-id", harness);
    }

    private async Task AlreadyPlayed() {
        using var harness = new InboundHarness();
        harness.Map(7010, 9010, "1");
        harness.Ids("anilist", 1010, 7010);
        var series = harness.Series("Iota", 1010, (1, 2));
        harness.Played(InboundHarness.EpisodeOf(series, 1, 1));
        harness.Played(InboundHarness.EpisodeOf(series, 1, 2));
        harness.Played(InboundHarness.SeasonOf(series, 1));
        harness.Entry(1010, "Iota", Status.Completed, 2, "2026-05-01");
        await Record("already-played", harness);
    }

    private async Task Record(string name, InboundHarness harness) {
        var operations = new OperationStore(Plugin.Instance!.Database, TimeProvider.System);
        Guid id = Guid.NewGuid();
        operations.Create(id, harness.UserId, OperationKind.SyncFromTrackers, "x");
        var context = new OperationContext(id, harness.UserId, operations, NullLogger.Instance, default);

        await harness.NewSync().SyncFromProvider(harness.UserId.ToString(), context);

        _output.Append("## ").Append(name).Append('\n');
        if (harness.Saves.Count == 0)
            _output.Append("(no saves)\n");
        foreach (string save in harness.Saves)
            _output.Append(save).Append('\n');
        _output.Append("result: ").Append(context.Result).Append(" | ").Append(context.Message).Append('\n');
    }
}
