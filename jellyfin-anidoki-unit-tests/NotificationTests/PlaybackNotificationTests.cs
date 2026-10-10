using System;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.NotificationTests;

[TestFixture]
public class PlaybackNotificationTests
{
    private sealed class BrokenStore : NotificationEventStore { public override void Publish(NotificationEvent item) => throw new InvalidOperationException("fixture"); }
    private PlaybackOutcomeCollector Collector()
    {
        var c = new PlaybackOutcomeCollector(); c.Record(new(ApiName.AniList,"1","Anime",OutcomeKind.Confirmed,3,Status.Watching,false,"progress",true));
        c.Record(new(ApiName.Mal,"2","Anime",OutcomeKind.Unconfirmed,null,null,false,"counter",false)); return c;
    }
    [Test] public void CapturedSourceRemainsStableAcrossNavigationAndPartialAggregateIsOneEvent()
    {
        var user = Guid.NewGuid(); var store = new NotificationEventStore(); var cursor = store.Read(user,"session",null).Cursor;
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode", SeriesName = "Anime", IndexNumber = 3 };
        var context = PlaybackNotificationContext.Capture(episode,null) with { UserId=user,SessionId="session" };
        episode.IndexNumber=4;episode.SeriesName="Another";
        PlaybackNotificationPublisher.Publish(Collector(),context,user,store,NullLogger.Instance);
        var read=store.Read(user,"session",cursor);Assert.That(read.Events,Has.Length.EqualTo(1));
        Assert.That(read.Events[0].SourceEpisode,Is.EqualTo(3));Assert.That(read.Events[0].SourceTitle,Is.EqualTo("Anime"));Assert.That(read.Events[0].Outcomes,Has.Length.EqualTo(2));
    }
    [Test] public void SharedViewAdditionalUserAndAbsentSessionDoNotGetEvents()
    {
        var store=new NotificationEventStore();var user=Guid.NewGuid();var context=new PlaybackNotificationContext(Guid.NewGuid(),user,"session",Guid.NewGuid(),"Anime",3,false);
        PlaybackNotificationPublisher.Publish(Collector(),context,Guid.NewGuid(),store,NullLogger.Instance);
        PlaybackNotificationPublisher.Publish(Collector(),context with {SessionId=null},user,store,NullLogger.Instance);
        Assert.That(store.Count,Is.Zero);
    }
    [Test] public void DeliveryFailureDoesNotEscapeIntoSynchronization()
    {
        var user=Guid.NewGuid();var context=new PlaybackNotificationContext(Guid.NewGuid(),user,"session",Guid.NewGuid(),"Anime",3,false);
        Assert.DoesNotThrow(()=>PlaybackNotificationPublisher.Publish(Collector(),context,user,new BrokenStore(),NullLogger.Instance));
    }
}
