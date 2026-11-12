using System;
using System.Collections.Generic;
using System.Globalization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.Preview;

/// <summary>
/// Preview rows, kept and deleted with their operation. Every method throws <see cref="InvalidOperationException"/> when
/// the database is unavailable.
/// </summary>
public sealed class PreviewStore(AniDokiDatabase database, TimeProvider clock) {
    /// <summary>D4: how long after it finished a preview can still be applied.</summary>
    public static readonly TimeSpan ApplicableFor = TimeSpan.FromHours(24);

    /// <summary>The most rows one page returns.</summary>
    public const int MaxPageSize = 200;

    private const string Columns = """
        seq, direction, provider, source_key, source_item, source_title, source_episode, target_id, target_title, target_episode,
        kind, reason, resolved_by, resolution_note, match_revision, apply_saved_matches, watched_fingerprint, planned_change,
        planned_against, selectable, selected_by_default, applied_operation_id
        """;

    private const string Filter = "operation_id = $id AND ($kind IS NULL OR kind = $kind) AND ($provider IS NULL OR provider = $provider)";

    public void Add(Guid operationId, PreviewRow row) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO preview_rows (operation_id, {Columns})
            VALUES ($id, $seq, $direction, $provider, $sourceKey, $sourceItem, $sourceTitle, $sourceEpisode, $targetId, $targetTitle,
                $targetEpisode, $kind, $reason, $resolvedBy, $note, $revision, $applySaved, $fingerprint, $change, $against,
                $selectable, $selected, $applied)
            """;
        Set(command, "$id", Id(operationId));
        Set(command, "$seq", row.Seq);
        Set(command, "$direction", row.Direction.ToString());
        Set(command, "$provider", row.Provider.ToString());
        Set(command, "$sourceKey", row.SourceKey);
        Set(command, "$sourceItem", Id(row.SourceItem));
        Set(command, "$sourceTitle", row.SourceTitle);
        Set(command, "$sourceEpisode", row.SourceEpisode);
        Set(command, "$targetId", row.TargetId);
        Set(command, "$targetTitle", row.TargetTitle);
        Set(command, "$targetEpisode", row.TargetEpisode);
        Set(command, "$kind", row.Kind.ToString());
        Set(command, "$reason", row.Reason);
        Set(command, "$resolvedBy", row.ResolvedBy?.ToString());
        Set(command, "$note", row.ResolutionNote);
        Set(command, "$revision", row.MatchRevision);
        Set(command, "$applySaved", row.ApplySavedMatches ? 1 : 0);
        Set(command, "$fingerprint", row.WatchedFingerprint);
        Set(command, "$change", row.PlannedChange is { } change ? ActionJson.Write(change) : row.PlannedMark is { } mark ? ActionJson.Write(mark) : null);
        Set(command, "$against", row.PlannedAgainst is { } against ? ActionJson.Write(against) : row.TrackerEntry is { } entry ? ActionJson.Write(entry) : null);
        Set(command, "$selectable", row.Selectable ? 1 : 0);
        Set(command, "$selected", row.SelectedByDefault ? 1 : 0);
        Set(command, "$applied", row.AppliedOperationId is { } applied ? Id(applied) : null);
        command.ExecuteNonQuery();
    }

    /// <summary>One page of rows, by season title (series, then season), source, tracker, then the order they were planned in.</summary>
    public PreviewPage Page(Guid operationId, PreviewRowKind? kind, ApiName? provider, int offset, int limit) {
        using var connection = database.Open();
        int total;
        using (var count = connection.CreateCommand()) {
            count.CommandText = $"SELECT COUNT(*) FROM preview_rows WHERE {Filter}";
            SetFilter(count, operationId, kind, provider);
            total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var rows = new List<PreviewRow>();
        using (var select = connection.CreateCommand()) {
            select.CommandText = $"""
                SELECT {Columns} FROM preview_rows WHERE {Filter}
                ORDER BY source_title COLLATE NOCASE, source_key, provider, seq
                LIMIT $limit OFFSET $offset
                """;
            SetFilter(select, operationId, kind, provider);
            Set(select, "$limit", limit);
            Set(select, "$offset", offset);
            using var reader = select.ExecuteReader();
            while (reader.Read())
                rows.Add(ReadRow(reader));
        }
        return new PreviewPage(total, rows);
    }

    public IReadOnlyDictionary<string, int> Counts(Guid operationId) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, COUNT(*) FROM preview_rows WHERE operation_id = $id GROUP BY kind";
        Set(command, "$id", Id(operationId));
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, int>();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    /// <summary>
    /// D4: only the newest finished preview for the user and direction, finished cleanly within <see cref="ApplicableFor"/>
    /// and not yet applied, can be applied. A cancelled, interrupted or partial preview never can.
    /// </summary>
    public bool IsApplicable(OperationView view) {
        if (view.State != OperationState.Succeeded || view.FinishedUtc is not { } finished || clock.GetUtcNow() - finished > ApplicableFor)
            return false;
        using var connection = database.Open();
        using (var newest = connection.CreateCommand()) {
            newest.CommandText = """
                SELECT id FROM operations WHERE user_id = $user AND kind = $kind AND finished_utc IS NOT NULL
                ORDER BY created_utc DESC LIMIT 1
                """;
            Set(newest, "$user", Id(view.UserId));
            Set(newest, "$kind", view.Kind.ToString());
            if (newest.ExecuteScalar() as string != Id(view.OperationId))
                return false;
        }
        using var applied = connection.CreateCommand();
        applied.CommandText = "SELECT COUNT(*) FROM preview_rows WHERE operation_id = $id AND applied_operation_id IS NOT NULL";
        Set(applied, "$id", Id(view.OperationId));
        return (long)applied.ExecuteScalar()! == 0;
    }

    private static void SetFilter(SqliteCommand command, Guid operationId, PreviewRowKind? kind, ApiName? provider) {
        Set(command, "$id", Id(operationId));
        Set(command, "$kind", kind?.ToString());
        Set(command, "$provider", provider?.ToString());
    }

    private static PreviewRow ReadRow(SqliteDataReader reader) {
        var direction = Enum.Parse<PreviewDirection>(reader.GetString(1));
        bool inbound = direction == PreviewDirection.FromTrackers;
        string? change = reader.IsDBNull(17) ? null : reader.GetString(17);
        string? against = reader.IsDBNull(18) ? null : reader.GetString(18);
        return new PreviewRow(
            reader.GetInt32(0),
            direction,
            Enum.Parse<ApiName>(reader.GetString(2)),
            reader.GetString(3),
            Guid.Parse(reader.GetString(4)),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            Enum.Parse<PreviewRowKind>(reader.GetString(10)),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : Enum.Parse<ResolvedBy>(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetInt64(14),
            reader.GetInt64(15) != 0,
            reader.IsDBNull(16) ? null : reader.GetString(16),
            change == null || inbound ? null : ActionJson.Read<EntryChange>(change),
            against == null || inbound ? null : ActionJson.Read<EntryState>(against),
            reader.GetInt64(19) != 0,
            reader.GetInt64(20) != 0,
            reader.IsDBNull(21) ? null : Guid.Parse(reader.GetString(21)),
            change == null || !inbound ? null : ActionJson.Read<InboundChange>(change),
            against == null || !inbound ? null : ActionJson.Read<InboundEntry>(against));
    }

    private static void Set(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Id(Guid id) => id.ToString("D");
}
