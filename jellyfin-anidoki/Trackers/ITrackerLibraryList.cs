using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Trackers.Mal;

namespace jellyfin_anidoki.Trackers;

/// <summary>The complete anime list for a linked account, or null if any part of the read failed.</summary>
public interface ITrackerLibraryList {
    Task<IReadOnlyList<TrackerLibraryEntry>?> ReadLibraryList(CancellationToken cancellationToken);
}

public sealed record TrackerLibraryEntry(EntryRef Entry, ListState List, int TotalEpisodes, bool? IsAiring, DateTimeOffset? NextAiringAtUtc);

internal interface ITrackerLibraryClient {
    Task<List<Anime>?> ReadWholeList(CancellationToken cancellationToken);
}
