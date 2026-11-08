using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace jellyfin_anidoki.Matching;

/// <summary>Why the match manager lists a season under a problem chip.</summary>
public enum MatchProblem { NeedsMatch, NeedsReview }

/// <summary>Seasons and movies whose recent syncs say a match would help. The boundary between the match API and operation history.</summary>
public interface IMatchProblems {
    /// <param name="user">Only this user's syncs; null for everyone's.</param>
    /// <returns>Source keys (<see cref="SyncSource.SourceKey"/>).</returns>
    IReadOnlySet<string> SourceKeys(MatchProblem problem, Guid? user);
}

/// <summary>
/// Reads operation history. A source is listed when the latest sync of it, for some user and tracker, needed a match
/// (D6: it found no entry) or a review (D12: title search had several equal candidates). Saving a match and syncing
/// again therefore clears it. No cache: this runs only when an admin or user opens the chip (AGENTS.md: measure first).
/// </summary>
public sealed class RecentMatchProblems(OperationStore operations, ILibraryManager library, TimeProvider clock) : IMatchProblems {
    /// <summary>The whole of operation history, which is kept this long.</summary>
    public static readonly TimeSpan Window = OperationRunner.Retention;

    public IReadOnlySet<string> SourceKeys(MatchProblem problem, Guid? user) {
        var latest = new Dictionary<(Guid User, string SourceKey, string Provider), MatchProblem?>();
        var keys = new Dictionary<Guid, string?>();
        foreach (RecordedResolution row in operations.RecentResolutions(clock.GetUtcNow() - Window, user)) {
            if (!keys.TryGetValue(row.SourceItem, out string? key))
                keys[row.SourceItem] = key = library.GetItemById(row.SourceItem) is Video video ? SyncSource.From(video)?.SourceKey : null;
            if (key != null)
                latest[(row.UserId, key, row.Provider)] = Classify(row.Outcome, row.Note);
        }
        return latest.Where(entry => entry.Value == problem).Select(entry => entry.Key.SourceKey).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// An unresolved sync needs a match, whatever its reason. A note naming several candidates needs review; the note
    /// may be followed by the saved match's shadow note after "; ".
    /// </summary>
    public static MatchProblem? Classify(string outcome, string? note) =>
        outcome == nameof(OutcomeKind.Unresolved) ? MatchProblem.NeedsMatch
        : note?.Split("; ").Any(part => part.StartsWith("ambiguous (", StringComparison.Ordinal)) == true ? MatchProblem.NeedsReview
        : null;
}
