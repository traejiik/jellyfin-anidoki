using System;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Notifications;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.NotificationTests;

public class PlaybackOutcomeCollectorTests {
    private static Anime Target(string title = "Mapped season") => new() { Id = 10, Title = title,
        MyListStatus = new MyListStatus { NumEpisodesWatched = 0, Status = Status.Watching } };
    private static UpdateAnimeStatusResponse Receipt(int progress = 1, Status status = Status.Watching) =>
        new() { NumEpisodesWatched = progress, Status = status };

    [Test]
    public async Task ObserveWriteCallsDelegateOnceAndReturnsOriginalReceipt() {
        var collector = new PlaybackOutcomeCollector();
        int calls = 0;
        var receipt = Receipt();
        var result = await collector.ObserveWrite(ApiName.Mal, Target(), 1, Status.Watching, "progress", () => {
            calls++;
            return Task.FromResult<UpdateAnimeStatusResponse?>(receipt);
        });
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(result, Is.SameAs(receipt));
        Assert.That(collector.Outcomes[0].Kind, Is.EqualTo(OutcomeKind.Confirmed));
        Assert.That(collector.HasConfirmedChange, Is.True);
    }

    [Test]
    public async Task EarlierConfirmationSurvivesAnExceptionAndExceptionPropagates() {
        var collector = new PlaybackOutcomeCollector();
        await collector.ObserveWrite(ApiName.Mal, Target(), 1, Status.Watching, "progress", () => Task.FromResult<UpdateAnimeStatusResponse?>(Receipt()));
        var error = new InvalidOperationException("sensitive diagnostic");
        var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => collector.ObserveWrite(ApiName.Mal, Target(), 1,
            Status.Watching, "rewatch-count", () => Task.FromException<UpdateAnimeStatusResponse?>(error)));
        Assert.That(thrown, Is.SameAs(error));
        Assert.That(collector.Outcomes, Has.Length.EqualTo(2));
        Assert.That(collector.Outcomes[1].Kind, Is.EqualTo(OutcomeKind.Unconfirmed));
        Assert.That(collector.HasConfirmedChange, Is.True);
    }

    [Test]
    public async Task AllMissingReceiptsAndNoChangeDoNotPublishSuccess() {
        var collector = new PlaybackOutcomeCollector();
        await collector.ObserveWrite(ApiName.Mal, Target(), 1, Status.Watching, "progress", () => Task.FromResult<UpdateAnimeStatusResponse?>(null));
        await collector.ObserveWrite(ApiName.Mal, Target(), 0, Status.Watching, "progress", () => Task.FromResult<UpdateAnimeStatusResponse?>(Receipt(0)), meaningfulChange: false);
        Assert.That(collector.HasConfirmedChange, Is.False);
        Assert.That(collector.Outcomes[1].Kind, Is.EqualTo(OutcomeKind.NoChange));
    }

    [Test]
    public async Task AnnictConfirmationContainsOnlyStatus() {
        var collector = new PlaybackOutcomeCollector();
        await collector.ObserveWrite(ApiName.Annict, Target(), 1, Status.Completed, "status", () => Task.FromResult<UpdateAnimeStatusResponse?>(Receipt(1, Status.Completed)));
        var outcome = collector.Outcomes[0];
        Assert.That(outcome.StatusOnly, Is.True);
        Assert.That(outcome.ConfirmedProgress, Is.Null);
        Assert.That(outcome.ConfirmedStatus, Is.EqualTo(Status.Completed));
    }

    [Test]
    public async Task AggregateRetainsBothProvidersAndMappedTargetsWithImmutableSnapshot() {
        var collector = new PlaybackOutcomeCollector();
        await collector.ObserveWrite(ApiName.Mal, Target("Season 1"), 13, Status.Watching, "progress", () => Task.FromResult<UpdateAnimeStatusResponse?>(Receipt(13)));
        await collector.ObserveWrite(ApiName.AniList, Target("Season 2"), 1, Status.Watching, "progress", () => Task.FromResult<UpdateAnimeStatusResponse?>(Receipt()));
        var snapshot = collector.Outcomes;
        snapshot[0] = snapshot[1];
        Assert.That(collector.Outcomes[0].TargetTitle, Is.EqualTo("Season 1"));
        Assert.That(collector.Outcomes[0].ConfirmedProgress, Is.EqualTo(13));
        Assert.That(collector.Outcomes[1].ConfirmedProgress, Is.EqualTo(1));
    }
}
