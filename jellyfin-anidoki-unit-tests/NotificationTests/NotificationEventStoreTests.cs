using System;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.NotificationTests;

[TestFixture]
public class NotificationEventStoreTests
{
    private DateTimeOffset _now;
    private NotificationEventStore _store = null!;
    private readonly Guid _user = Guid.NewGuid();
    [SetUp] public void Setup() { _now = DateTimeOffset.UtcNow; _store = new(() => _now); }
    private NotificationEvent Event(Guid? operation = null) => new(Guid.NewGuid(), operation ?? Guid.NewGuid(), 0, _now, _user, "session", Guid.NewGuid(), "Title", 3, false,
        [new(ApiName.AniList, "1", "Title", OutcomeKind.Confirmed, 3, Status.Watching, false, "progress", true)]);
    [Test] public void FirstReadStartsAtTailWithoutReplayingOldEvents()
    {
        _store.Publish(Event()); var first = _store.Read(_user, "session", null);
        Assert.That(first.Events, Is.Empty);
        _store.Publish(Event()); Assert.That(_store.Read(_user, "session", first.Cursor).Events, Has.Length.EqualTo(1));
    }
    [Test] public void ReadsAreNonDestructiveAndIsolatedByBothUserAndSession()
    {
        var cursor = _store.Read(_user, "session", null).Cursor; _store.Publish(Event());
        Assert.That(_store.Read(_user, "session", cursor).Events, Has.Length.EqualTo(1));
        Assert.That(_store.Read(_user, "session", cursor).Events, Has.Length.EqualTo(1));
        Assert.That(_store.Read(Guid.NewGuid(), "session", cursor).Events, Is.Empty);
        Assert.That(_store.Read(_user, "elsewhere", cursor).Events, Is.Empty);
    }
    [Test] public void DuplicateOperationPublishesExactlyOnce()
    {
        var cursor = _store.Read(_user, "session", null).Cursor; var item = Event();
        _store.Publish(item); _store.Publish(item with { EventId = Guid.NewGuid() });
        Assert.That(_store.Read(_user, "session", cursor).Events, Has.Length.EqualTo(1));
    }
    [Test] public void ExpiredEventsAreNotReplayed()
    {
        var cursor = _store.Read(_user, "session", null).Cursor; _store.Publish(Event()); _now += TimeSpan.FromMinutes(2);
        Assert.That(_store.Read(_user, "session", cursor).Events, Is.Empty);
    }
    [Test] public void InvalidFutureAndRestartCursorsResetToTail()
    {
        _store.Publish(Event());
        foreach (var cursor in new[] { "bad", Guid.NewGuid() + ":1", _store.Read(_user, "session", null).Cursor.Split(':')[0] + ":99999" })
        { var response = _store.Read(_user, "session", cursor); Assert.That(response.Reset, Is.True); Assert.That(response.Events, Is.Empty); }
    }
    [Test] public void SessionOverflowRetainsOnlyNewestHundredAndResetsTooOldCursor()
    {
        var cursor = _store.Read(_user, "session", null).Cursor;
        for (int i=0;i<110;i++) _store.Publish(Event());
        Assert.That(_store.Count, Is.EqualTo(100)); Assert.That(_store.Read(_user, "session", cursor).Reset, Is.True);
    }
    [Test] public void GlobalAndPartitionLimitsHoldUnderConcurrentPublishing()
    {
        Parallel.For(0, 3000, i => _store.Publish(Event() with { SessionId = "session" + i % 300 }));
        Assert.That(_store.Count, Is.LessThanOrEqualTo(2048)); Assert.That(_store.PartitionCount, Is.LessThanOrEqualTo(256));
    }
    [Test] public void LargeAggregateRetainsLateMeaningfulConfirmationAndUnconfirmedProvider()
    {
        var cursor = _store.Read(_user, "session", null).Cursor; var item = Event();
        var noChanges = Enumerable.Range(0, 30).Select(i => item.ProviderOutcomes[0] with { TargetId = i.ToString(), MeaningfulChange = false }).ToArray();
        var confirmation = item.ProviderOutcomes[0] with { TargetId = "confirmed-last" };
        var failed = confirmation with { Provider = ApiName.Mal, Kind = OutcomeKind.Unconfirmed, MeaningfulChange = false };
        _store.Publish(item with { ProviderOutcomes = [..noChanges, confirmation, failed] });
        var result = _store.Read(_user, "session", cursor).Events.Single();
        Assert.That(result.Outcomes, Has.Length.LessThanOrEqualTo(24));
        Assert.That(result.Outcomes.Any(o => o.Kind == "Confirmed" && o.MeaningfulChange), Is.True);
        Assert.That(result.Outcomes.Any(o => o.Provider == "Mal" && o.Kind == "Unconfirmed"), Is.True);
        Assert.That(result.Summarized, Is.True);
    }
    [Test] public void PublicationCopiesAndBoundsPayloadAndRequiresConfirmedChange()
    {
        var cursor = _store.Read(_user, "session", null).Cursor; var item = Event();
        _store.Publish(item with { ProviderOutcomes = [] }); Assert.That(_store.Count, Is.Zero);
        var outcomes = Enumerable.Repeat(item.ProviderOutcomes[0] with { TargetTitle = new string('x', 400) }, 40).ToArray();
        _store.Publish(item with { SourceTitle = new string('x', 400), ProviderOutcomes = outcomes }); outcomes[0] = outcomes[0] with { Kind = OutcomeKind.Failed };
        var read = _store.Read(_user, "session", cursor).Events.Single();
        Assert.That(read.SourceTitle, Has.Length.EqualTo(256)); Assert.That(read.Outcomes, Has.Length.LessThanOrEqualTo(24)); Assert.That(read.Outcomes[0].Kind, Is.EqualTo("Confirmed"));
        Assert.That(read.Summarized, Is.True);
    }
}
