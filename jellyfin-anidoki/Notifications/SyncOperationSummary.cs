using System;
using System.IO;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
namespace jellyfin_anidoki.Notifications;

public enum SyncNotificationTrigger { Playback, LegacyManual, ConfirmedManual }
public sealed record SyncOperationSummary(Guid OperationId, Guid UserId, Guid SourceItemId, Guid ShowItemId,
    string ShowTitle, int? EpisodeNumber, int? EpisodeEndNumber, string EpisodeTitle, bool IsMovie,
    DateTimeOffset CreatedUtc, SyncNotificationTrigger Trigger, ProviderWriteOutcome[] Outcomes) {
    internal static SyncOperationSummary Capture(BaseItem item, Guid user, Guid operation, SyncNotificationTrigger trigger, PlaybackOutcomeCollector collector) =>
        new(operation, user, item.Id, item is Episode e ? e.SeriesId : item.Id,
            item is Episode episode ? episode.SeriesName ?? "Show title unavailable" : item.Name,
            item is Episode ? item.IndexNumber : null, item is Episode range ? range.IndexNumberEnd : null,
            MetadataEpisodeTitle(item), item is Movie, DateTimeOffset.UtcNow, trigger, collector.Outcomes);
    private static string MetadataEpisodeTitle(BaseItem item) {
        var title = item.Name?.Trim() ?? "";
        if (item is not Episode || string.IsNullOrWhiteSpace(item.Path))
            return title;
        var fileName = Path.GetFileNameWithoutExtension(item.Path);
        // Jellyfin may use an unprocessed file stem before episode metadata is fetched.
        // A simple title such as "Pilot" remains valid even if its file has the same name.
        if (string.Equals(title, fileName, StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(fileName, @"(?:s\d{1,3}e\d{1,4}|\d{1,3}x\d{1,4})", RegexOptions.IgnoreCase))
            return "";
        return title;
    }

}
