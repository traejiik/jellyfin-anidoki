using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Matching;

public sealed record CheckTarget(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("alternativeId")] string? AlternativeId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("episode")] int Episode,
    [property: JsonPropertyName("role")] TargetRole Role);

/// <summary>A resolution as the check stores it: where it goes, without any list state.</summary>
/// <param name="Kind">Resolved, Ignored, Unresolved, or Failed when the tracker could not answer.</param>
/// <param name="Reason">Why it is Unresolved, or the failure kind.</param>
public sealed record CheckResolution(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("by")] ResolvedBy? By,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("targets")] IReadOnlyList<CheckTarget> Targets) {
    public static CheckResolution From(SourceResolution resolution) => resolution switch {
        Resolved resolved => new CheckResolution(nameof(Resolved), resolved.By, null,
            resolved.EarlierParts.Append(resolved.Played).Select(Target).ToList()),
        Ignored ignored => new CheckResolution(nameof(Ignored), ignored.By, null, []),
        Unresolved unresolved => new CheckResolution(nameof(Unresolved), null, unresolved.Reason, unresolved.EarlierParts.Select(Target).ToList()),
        _ => throw new ArgumentOutOfRangeException(nameof(resolution))
    };

    public static CheckResolution Failed(TrackerFailureKind kind) => new("Failed", null, kind.ToString(), []);

    /// <summary>Whether both send the episode to the same entries and episodes, or both decline to.</summary>
    public bool SameAs(CheckResolution other) => Kind == other.Kind && Targets.SequenceEqual(other.Targets, TargetComparer.Instance);

    private static CheckTarget Target(ResolvedTarget target) =>
        new(target.Entry.Id, target.Entry.AlternativeId, target.Title, target.TargetEpisode, target.Role);

    /// <summary>Titles are left out: the match stores one from when it was saved, and the tracker may have renamed the entry.</summary>
    private sealed class TargetComparer : IEqualityComparer<CheckTarget> {
        public static readonly TargetComparer Instance = new();

        public bool Equals(CheckTarget? first, CheckTarget? second) =>
            first?.Id == second?.Id && first?.AlternativeId == second?.AlternativeId && first?.Episode == second?.Episode && first?.Role == second?.Role;

        public int GetHashCode(CheckTarget target) => HashCode.Combine(target.Id, target.AlternativeId, target.Episode, target.Role);
    }
}

/// <summary>One provider's answer for one source.</summary>
/// <param name="Saved">Through a saved match, when one applies.</param>
/// <param name="PlayedRuleProgress">The episode the match or automatic answer sends the played episode to.</param>
/// <param name="HighestWatchedProgress">What highest-watched would give the same entry; null when it cannot be worked out.</param>
/// <param name="Differs">The saved answer goes somewhere the automatic one does not.</param>
public sealed record ResolutionCheckRow(
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("provider")] ApiName Provider,
    [property: JsonPropertyName("automatic")] CheckResolution Automatic,
    [property: JsonPropertyName("saved")] CheckResolution? Saved,
    [property: JsonPropertyName("playedRuleProgress")] int? PlayedRuleProgress,
    [property: JsonPropertyName("highestWatchedProgress")] int? HighestWatchedProgress,
    [property: JsonPropertyName("differs")] bool Differs);

/// <summary>The check's report. Retention removes it with its operation. Throws <see cref="InvalidOperationException"/> when the database is unavailable.</summary>
public sealed class ResolutionCheckStore(AniDokiDatabase database) {
    public void Add(Guid operationId, int sequence, ResolutionCheckRow row) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO resolution_check_rows (operation_id, seq, source_key, provider, automatic, saved, played_rule_progress, highest_watched_progress, differs)
            VALUES ($id, $seq, $source, $provider, $automatic, $saved, $played, $highest, $differs)
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$seq", sequence);
        command.Parameters.AddWithValue("$source", row.SourceKey);
        command.Parameters.AddWithValue("$provider", row.Provider.ToString());
        command.Parameters.AddWithValue("$automatic", ActionJson.Write(row.Automatic));
        command.Parameters.AddWithValue("$saved", row.Saved is { } saved ? ActionJson.Write(saved) : DBNull.Value);
        command.Parameters.AddWithValue("$played", (object?)row.PlayedRuleProgress ?? DBNull.Value);
        command.Parameters.AddWithValue("$highest", (object?)row.HighestWatchedProgress ?? DBNull.Value);
        command.Parameters.AddWithValue("$differs", row.Differs ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ResolutionCheckRow> Read(Guid operationId) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_key, provider, automatic, saved, played_rule_progress, highest_watched_progress, differs
            FROM resolution_check_rows WHERE operation_id = $id ORDER BY seq
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        var rows = new List<ResolutionCheckRow>();
        while (reader.Read()) {
            rows.Add(new ResolutionCheckRow(reader.GetString(0), Enum.Parse<ApiName>(reader.GetString(1)),
                ActionJson.Read<CheckResolution>(reader.GetString(2))!,
                reader.IsDBNull(3) ? null : ActionJson.Read<CheckResolution>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt64(6) != 0));
        }
        return rows;
    }
}
