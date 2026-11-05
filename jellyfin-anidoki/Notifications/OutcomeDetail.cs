using System;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Notifications;

/// <summary>
/// How an outcome came about, for operation history only. It stays off <see cref="ProviderWriteOutcome"/> so nothing
/// here can reach a toast, Discord or a manual card. Every field is null when it does not apply.
/// </summary>
/// <param name="SourceItem">The Jellyfin item that was played.</param>
/// <param name="SourceEpisode">Its episode number in Jellyfin; a movie is 1.</param>
/// <param name="TargetEpisode">The episode number on the tracker entry.</param>
/// <param name="By">How the entry was found.</param>
/// <param name="Note">Why it could not be resolved, or a note from resolution.</param>
/// <param name="PlannedChange">The change that was written; null when nothing was planned.</param>
/// <param name="PlannedAgainst">The entry as read, before planner quirks; Phase 7's Undo restores from it.</param>
/// <param name="MatchRevision">The revision of the saved match behind this action; null otherwise.</param>
public sealed record OutcomeDetail(Guid? SourceItem, int? SourceEpisode, int? TargetEpisode, ResolvedBy? By, string? Note,
    EntryChange? PlannedChange, EntryState? PlannedAgainst, long? MatchRevision = null);
