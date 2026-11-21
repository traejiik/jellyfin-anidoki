using System;
using System.Collections.Generic;
using System.Globalization;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.Playback;

/// <summary>What produced a completion. <see cref="Threshold"/> is used once the per-user threshold lands.</summary>
public enum CompletionTrigger { Threshold, Stop, Toggle, ConfirmedManual }

/// <summary>One recorded completion: this user finished this item, by this trigger.</summary>
public sealed record WatchCompletion(Guid Id, Guid UserId, Guid ItemId, string CycleKey, CompletionTrigger Trigger,
    DateTimeOffset CompletedUtc, Guid? OperationId);

/// <summary>
/// The durable record of finished watches that every trigger asks before it enqueues an operation, so one watch
/// updates trackers once. Every method throws <see cref="InvalidOperationException"/> when the database is unavailable.
/// </summary>
public sealed class CompletionLedger(AniDokiDatabase database, TimeProvider clock) {
    /// <summary>How long a completion keeps a later watched toggle or manual mark of the same item from writing again.</summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(6);

    /// <summary>
    /// Records a completion unless its watch cycle already has one, or, with <paramref name="window"/>, the user has a
    /// completion for the item within it. Checked and written in one transaction, so two reports milliseconds apart
    /// cannot both pass.
    /// </summary>
    /// <returns>True when recorded and the operation should be enqueued; false for a duplicate.</returns>
    public bool TryRecord(Guid userId, Guid itemId, string cycleKey, CompletionTrigger trigger, Guid operationId, TimeSpan? window = null) {
        DateTimeOffset now = clock.GetUtcNow();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var check = connection.CreateCommand()) {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT COUNT(*) FROM watch_completions
                WHERE user_id = $user AND item_id = $item AND (cycle_key = $cycle OR ($since IS NOT NULL AND completed_utc >= $since))
                """;
            check.Parameters.AddWithValue("$user", Id(userId));
            check.Parameters.AddWithValue("$item", Id(itemId));
            check.Parameters.AddWithValue("$cycle", cycleKey);
            check.Parameters.AddWithValue("$since", window is { } span ? Format(now - span) : DBNull.Value);
            if ((long)check.ExecuteScalar()! > 0)
                return false;
        }
        using (var insert = connection.CreateCommand()) {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO watch_completions (id, user_id, item_id, cycle_key, trigger, completed_utc, operation_id)
                VALUES ($id, $user, $item, $cycle, $trigger, $now, $operation)
                """;
            insert.Parameters.AddWithValue("$id", Id(Guid.NewGuid()));
            insert.Parameters.AddWithValue("$user", Id(userId));
            insert.Parameters.AddWithValue("$item", Id(itemId));
            insert.Parameters.AddWithValue("$cycle", cycleKey);
            insert.Parameters.AddWithValue("$trigger", trigger.ToString());
            insert.Parameters.AddWithValue("$now", Format(now));
            insert.Parameters.AddWithValue("$operation", Id(operationId));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    /// <summary>The user's completions of the item within <paramref name="window"/>, newest first.</summary>
    public IReadOnlyList<WatchCompletion> RecentFor(Guid userId, Guid itemId, TimeSpan window) =>
        Query("""
            SELECT id, user_id, item_id, cycle_key, trigger, completed_utc, operation_id FROM watch_completions
            WHERE user_id = $user AND item_id = $item AND completed_utc >= $since ORDER BY completed_utc DESC
            """, ("$user", Id(userId)), ("$item", Id(itemId)), ("$since", Format(clock.GetUtcNow() - window)));

    /// <summary>Forgets the user's completions of the item after it is marked unwatched, so the next watch counts.</summary>
    /// <returns>How many completions were removed.</returns>
    public int EndWindow(Guid userId, Guid itemId) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM watch_completions WHERE user_id = $user AND item_id = $item";
        command.Parameters.AddWithValue("$user", Id(userId));
        command.Parameters.AddWithValue("$item", Id(itemId));
        return command.ExecuteNonQuery();
    }

    /// <summary>Completions whose operation a restart interrupted and that were not enqueued again yet, oldest first.</summary>
    public IReadOnlyList<WatchCompletion> Interrupted() =>
        Query("""
            SELECT c.id, c.user_id, c.item_id, c.cycle_key, c.trigger, c.completed_utc, c.operation_id
            FROM watch_completions c JOIN operations o ON o.id = c.operation_id
            WHERE c.requeued = 0 AND o.state = $state ORDER BY c.completed_utc
            """, ("$state", nameof(OperationState.Interrupted)));

    /// <summary>Points the completion at the operation that runs it again, and makes sure it is never enqueued a third time.</summary>
    public void MarkRequeued(Guid completionId, Guid? operationId) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE watch_completions SET requeued = 1, operation_id = $operation WHERE id = $id";
        command.Parameters.AddWithValue("$id", Id(completionId));
        command.Parameters.AddWithValue("$operation", operationId is { } id ? Id(id) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<WatchCompletion> Query(string sql, params (string Name, object Value)[] parameters) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<WatchCompletion>();
        while (reader.Read())
            rows.Add(new WatchCompletion(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.GetString(3), Enum.Parse<CompletionTrigger>(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6))));
        return rows;
    }

    private static string Format(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Id(Guid id) => id.ToString("D");
}
