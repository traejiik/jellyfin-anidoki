using System.Collections.Generic;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Resolution;

/// <summary>How the played entry was found. PR 11 adds SharedMatch and PersonalMatch.</summary>
public enum ResolvedBy { Ids, CrossIds, TitleSearch, SequelChain }

/// <summary>
/// <see cref="Played"/> is the entry the watched episode belongs to; <see cref="EarlierPart"/> is a part of the same
/// Jellyfin season the episode is past, which the sync completes.
/// </summary>
public enum TargetRole { Played, EarlierPart }

/// <param name="TargetEpisode">The episode number on this entry (for an earlier part, its length).</param>
/// <param name="Title">The tracker's display title, for logs and outcomes.</param>
/// <param name="State">The entry as resolution read it, list state included; null when resolution saw only a search
/// result or a cross-ID match, and the executor reads it.</param>
public sealed record ResolvedTarget(EntryRef Entry, int TargetEpisode, TargetRole Role, string Title, EntryState? State = null);

/// <summary>What to record when resolution fails. Null on <see cref="Unresolved"/> means record nothing, as today.</summary>
public sealed record UnresolvedRecord(string TargetId, string Title);

/// <summary>
/// One provider's answer to "which entry, which episode". It reads only; nothing here writes.
/// <see cref="EarlierParts"/> are listed in chain order and are completed whatever the outcome, as the walk does today.
/// </summary>
public abstract record SourceResolution(IReadOnlyList<ResolvedTarget> EarlierParts, string? Note);

public sealed record Resolved(ResolvedTarget Played, ResolvedBy By, IReadOnlyList<ResolvedTarget> EarlierParts, string? Note = null)
    : SourceResolution(EarlierParts, Note);

/// <param name="Reason">not-found, no-title-match, sequel-fetch-failed, no-cour-covers-episode, or no-special-match.</param>
public sealed record Unresolved(string Reason, UnresolvedRecord? Record, IReadOnlyList<ResolvedTarget> EarlierParts, string? Note = null)
    : SourceResolution(EarlierParts, Note);
