#nullable enable
using System;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Notifications;

internal sealed record PlaybackNotificationContext(Guid OperationId, Guid UserId, string? SessionId,
    Guid SourceItemId, string SourceTitle, int? SourceEpisode, bool IsMovie)
{
    internal static PlaybackNotificationContext Capture(BaseItem item, SessionInfo? session) => new(Guid.NewGuid(),
        session?.UserId ?? Guid.Empty, session?.Id, item.Id,
        item is Episode episode ? episode.SeriesName ?? item.Name : item.Name,
        item is Episode e ? e.IndexNumber : null, item is Movie);
}

internal static class PlaybackNotificationPublisher
{
    internal static void Publish(PlaybackOutcomeCollector collector, PlaybackNotificationContext context, Guid user,
        NotificationEventStore store, ILogger logger)
    {
        if (context.UserId != user || user == Guid.Empty || string.IsNullOrWhiteSpace(context.SessionId) || !collector.HasConfirmedChange) return;
        try
        {
            if (Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(c => c.UserId == user)?.ShowLogNotifications == false) return;
            store.Publish(new(Guid.NewGuid(), context.OperationId, 0, DateTimeOffset.UtcNow, user, context.SessionId,
                context.SourceItemId, context.SourceTitle, context.SourceEpisode, context.IsMovie, collector.Outcomes));
        }
        catch (Exception) { logger.LogWarning("AniDoki could not deliver a playback update notification; tracker writes are unchanged."); }
    }
}
