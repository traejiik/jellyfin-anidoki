using System;
using System.Collections.Generic;
using System.Globalization;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Storage;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.Operations;

/// <summary>
/// Persists operations and the outcome of each action in the plugin database. Every method throws
/// <see cref="InvalidOperationException"/> when the database is unavailable.
/// </summary>
public sealed class OperationStore(AniDokiDatabase database, TimeProvider clock) {
    public void Create(Guid id, Guid userId, OperationKind kind, string source) =>
        Execute("INSERT INTO operations (id, user_id, kind, state, source, created_utc) VALUES ($id, $user, $kind, $state, $source, $now)",
            ("$id", Id(id)), ("$user", Id(userId)), ("$kind", kind.ToString()), ("$state", nameof(OperationState.Queued)), ("$source", source), ("$now", Now()));

    public void MarkRunning(Guid id) =>
        Execute("UPDATE operations SET state = $state, started_utc = $now WHERE id = $id",
            ("$id", Id(id)), ("$state", nameof(OperationState.Running)), ("$now", Now()));

    public void SetProgress(Guid id, int done, int? total) =>
        Execute("UPDATE operations SET done = $done, total = $total WHERE id = $id",
            ("$id", Id(id)), ("$done", done), ("$total", (object?)total ?? DBNull.Value));

    public void AddAction(Guid id, int sequence, ProviderWriteOutcome outcome) =>
        Execute("""
            INSERT INTO operation_actions (operation_id, seq, provider, target_id, target_title, outcome, step, progress, status, meaningful)
            VALUES ($id, $seq, $provider, $target, $title, $outcome, $step, $progress, $status, $meaningful)
            """,
            ("$id", Id(id)), ("$seq", sequence), ("$provider", outcome.Provider.ToString()), ("$target", outcome.TargetId),
            ("$title", outcome.TargetTitle), ("$outcome", outcome.Kind.ToString()), ("$step", outcome.Step),
            ("$progress", (object?)outcome.ConfirmedProgress ?? DBNull.Value),
            ("$status", (object?)outcome.ConfirmedStatus?.ToString() ?? DBNull.Value),
            ("$meaningful", outcome.MeaningfulChange ? 1 : 0));

    public void Finish(Guid id, OperationState state, string? message) =>
        Execute("UPDATE operations SET state = $state, finished_utc = $now, message = $message WHERE id = $id",
            ("$id", Id(id)), ("$state", state.ToString()), ("$now", Now()), ("$message", (object?)message ?? DBNull.Value));

    public OperationView? Read(Guid id) {
        using var connection = database.Open();
        return Read(connection, Id(id));
    }

    public IReadOnlyList<OperationView> ReadForUser(Guid userId) {
        using var connection = database.Open();
        var ids = new List<string>();
        using (var command = connection.CreateCommand()) {
            command.CommandText = "SELECT id FROM operations WHERE user_id = $user ORDER BY created_utc DESC";
            command.Parameters.AddWithValue("$user", Id(userId));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetString(0));
        }
        var views = new List<OperationView>();
        foreach (string id in ids) {
            if (Read(connection, id) is { } view)
                views.Add(view);
        }
        return views;
    }

    public Guid? FindActive(Guid userId, OperationKind kind) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM operations WHERE user_id = $user AND kind = $kind AND state IN ('Queued', 'Running') ORDER BY created_utc LIMIT 1";
        command.Parameters.AddWithValue("$user", Id(userId));
        command.Parameters.AddWithValue("$kind", kind.ToString());
        return command.ExecuteScalar() is string id ? Guid.Parse(id) : null;
    }

    /// <summary>Finishes operations a previous run left queued or running. They are not resumed.</summary>
    public int MarkInterrupted() =>
        Execute("UPDATE operations SET state = $state, finished_utc = $now, message = $message WHERE state IN ('Queued', 'Running')",
            ("$state", nameof(OperationState.Interrupted)), ("$now", Now()),
            ("$message", "Jellyfin stopped before this finished. Updates already sent were kept."));

    public int DeleteFinishedBefore(TimeSpan age) {
        string cutoff = Format(clock.GetUtcNow() - age);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var actions = connection.CreateCommand()) {
            actions.Transaction = transaction;
            actions.CommandText = "DELETE FROM operation_actions WHERE operation_id IN (SELECT id FROM operations WHERE finished_utc IS NOT NULL AND finished_utc < $cutoff)";
            actions.Parameters.AddWithValue("$cutoff", cutoff);
            actions.ExecuteNonQuery();
        }
        int removed;
        using (var operations = connection.CreateCommand()) {
            operations.Transaction = transaction;
            operations.CommandText = "DELETE FROM operations WHERE finished_utc IS NOT NULL AND finished_utc < $cutoff";
            operations.Parameters.AddWithValue("$cutoff", cutoff);
            removed = operations.ExecuteNonQuery();
        }
        transaction.Commit();
        return removed;
    }

    private static OperationView? Read(SqliteConnection connection, string id) {
        OperationView? view;
        using (var command = connection.CreateCommand()) {
            command.CommandText = "SELECT id, user_id, kind, state, source, created_utc, started_utc, finished_utc, message, done, total FROM operations WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            view = new OperationView(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Enum.Parse<OperationKind>(reader.GetString(2)),
                Enum.Parse<OperationState>(reader.GetString(3)),
                reader.GetString(4),
                Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                new Dictionary<string, int>());
        }
        var counts = new Dictionary<string, int>();
        using (var command = connection.CreateCommand()) {
            command.CommandText = "SELECT outcome, COUNT(*) FROM operation_actions WHERE operation_id = $id GROUP BY outcome";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                counts[reader.GetString(0)] = reader.GetInt32(1);
        }
        return view with { Counts = counts };
    }

    private int Execute(string sql, params (string Name, object Value)[] parameters) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private string Now() => Format(clock.GetUtcNow());

    private static string Format(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Id(Guid id) => id.ToString("D");
}
