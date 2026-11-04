using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Matching;

/// <summary>Who a match applies to. <see cref="Owner"/> is null for a shared match and the Jellyfin user for a personal one.</summary>
/// <param name="SourceKey">See <see cref="SyncSource.SourceKey"/>.</param>
public sealed record MatchScope(ApiName Provider, string SourceKey, Guid? Owner = null);

/// <summary>A tracker entry a match points at. <see cref="Title"/> is for logs, outcomes and the editor.</summary>
public sealed record MatchTarget(EntryRef Entry, string Title);

/// <summary>Source episodes <see cref="SourceStart"/>..<see cref="SourceEnd"/> map to the entry's episodes from <see cref="TargetStart"/>.</summary>
/// <param name="SourceEnd">Null for an open range, which follows an airing entry. Only the last range may be open.</param>
public sealed record MatchRange(int SourceStart, int? SourceEnd, MatchTarget Target, int TargetStart) {
    /// <summary>The entry episode the range ends on; null when it is open.</summary>
    [JsonIgnore]
    public int? TargetEnd => SourceEnd is int end ? TargetFor(end) : null;

    public bool Contains(int episode) => episode >= SourceStart && (SourceEnd is not int end || episode <= end);

    public int TargetFor(int episode) => TargetStart + (episode - SourceStart);
}

/// <summary>One source episode handled apart from the ranges. It wins over any range holding that episode.</summary>
[JsonDerivedType(typeof(TargetException), "target")]
[JsonDerivedType(typeof(IgnoreException), "ignore")]
public abstract record MatchException(int SourceEpisode);

/// <summary>Sends the episode to <see cref="TargetEpisode"/> of <see cref="Target"/>.</summary>
public sealed record TargetException(int SourceEpisode, MatchTarget Target, int TargetEpisode) : MatchException(SourceEpisode);

/// <summary>The episode never syncs.</summary>
public sealed record IgnoreException(int SourceEpisode) : MatchException(SourceEpisode);

/// <summary>
/// A season or movie mapped to one provider's entries. It is complete: an episode it does not name is not in the match,
/// and never falls back to automatic resolution.
/// </summary>
/// <param name="Revision">The stored revision; 0 for a match that was never saved.</param>
/// <param name="BaseSharedRevision">Personal only: the shared revision this override was based on.</param>
public sealed record Match(MatchScope Scope, IReadOnlyList<MatchRange> Ranges, IReadOnlyList<MatchException> Exceptions,
    long Revision = 0, long? BaseSharedRevision = null) {
    public ResolvedBy By => Scope.Owner == null ? ResolvedBy.SharedMatch : ResolvedBy.PersonalMatch;

    /// <summary>The revision to record against a target; null until the match is saved.</summary>
    public long? RecordedRevision => Revision > 0 ? Revision : null;

    /// <summary>Every target the match points at, once each.</summary>
    public IReadOnlyList<MatchTarget> Targets => Ranges.Select(range => range.Target)
        .Concat(Exceptions.OfType<TargetException>().Select(exception => exception.Target))
        .DistinctBy(target => target.Entry)
        .ToList();
}
