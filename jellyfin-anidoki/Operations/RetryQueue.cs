using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Storage;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.Operations;

/// <summary>
/// What an operation updated, for retrying it. The original update has attempt 0; a <see cref="OperationKind.Retry"/>
/// carries its attempt and the one tracker it retries.
/// </summary>
/// <param name="Trigger">Playback, WatchedToggle or ConfirmedManual: the update rules the retry runs with.</param>
/// <param name="RetryOf">The operation a retry follows (the original update or the previous retry), so history can fold them.</param>
public sealed record RetrySubject(Guid SourceItem, OperationKind Trigger, int Attempt = 0, ApiName? Provider = null, Guid? RetryOf = null);

/// <summary>A pending retry of one tracker's update for one user and season.</summary>
/// <param name="Attempt">The attempt this retry will run, 1 to <see cref="RetryQueue.MaxAttempts"/>.</param>
/// <param name="LastStep">Why the last try failed: rate-limited or transient.</param>
public sealed record RetryRow(Guid Id, Guid UserId, ApiName Provider, Guid SourceItem, string SeriesKey, OperationKind Trigger, int Attempt,
    DateTimeOffset DueUtc, Guid LastOperationId, string LastStep);

public enum RetryDecisionKind { Scheduled, Cleared, Dropped }

/// <summary>What happened to one tracker's retry after an operation; the runner logs it.</summary>
/// <param name="Attempt">The attempt scheduled, or the attempts made when dropped.</param>
public sealed record RetryDecision(ApiName Provider, RetryDecisionKind Kind, int Attempt, DateTimeOffset? DueUtc, string Title);

/// <summary>
/// D6: a durable queue of tracker updates that failed for a reason that may pass (rate limited, unreachable), one row per
/// user, tracker and season. Each retry re-plans against the tracker's current state, so a stale one ends as no change.
/// Every method throws <see cref="InvalidOperationException"/> or <see cref="SqliteException"/> when storage is unavailable.
/// </summary>
/// <param name="seriesKeyOf">The season an item belongs to (a movie is its own): a newer completion in it replaces the pending retry.</param>
/// <param name="rateLimitedUntil">When a tracker accepts calls again after a rate limit; a later time than the back-off wins.</param>
public sealed class RetryQueue(AniDokiDatabase database, TimeProvider clock, Func<Guid, string> seriesKeyOf,
    Func<ApiName, DateTimeOffset?>? rateLimitedUntil = null) {
    public const int MaxAttempts = 4;

    /// <summary>The wait before attempts 1 to 4: about 7 hours in all.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    /// <summary>How long a running retry is held back; one cut off by a restart runs again after it, at the same attempt.</summary>
    public static readonly TimeSpan LeaseTime = TimeSpan.FromMinutes(30);

    private static readonly string[] RetryableSteps = ["rate-limited", "transient"];

    /// <summary>
    /// Schedules, moves on, or clears each tracker's retry after an operation finished. An original update always takes
    /// the season's row; a retry only touches the row it leased, found by its operation rather than by the season, so an
    /// older retry finishing late never undoes a newer one and a row whose season changed is still cleared.
    /// </summary>
    public IReadOnlyList<RetryDecision> AfterOperation(Guid operationId, Guid userId, RetrySubject subject, IReadOnlyCollection<ProviderWriteOutcome> outcomes) {
        var byProvider = outcomes.GroupBy(outcome => outcome.Provider).ToDictionary(group => group.Key, group => group.ToArray());
        if (subject.Provider is { } only && !byProvider.ContainsKey(only))
            byProvider[only] = [];
        if (byProvider.Count == 0)
            return [];
        string seriesKey = seriesKeyOf(subject.SourceItem);
        bool isRetry = subject.Attempt > 0;
        var decisions = new List<RetryDecision>();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (provider, results) in byProvider) {
            ProviderWriteOutcome? failure = results.FirstOrDefault(outcome => outcome.Kind == OutcomeKind.Failed && RetryableSteps.Contains(outcome.Step));
            string title = failure?.TargetTitle ?? results.FirstOrDefault()?.TargetTitle ?? string.Empty;
            int next = subject.Attempt + 1;
            if (failure == null || next > MaxAttempts) {
                if (Delete(connection, transaction, userId, provider, seriesKey, isRetry ? operationId : null) > 0 || failure != null)
                    decisions.Add(new RetryDecision(provider, failure == null ? RetryDecisionKind.Cleared : RetryDecisionKind.Dropped, subject.Attempt, null, title));
                continue;
            }
            DateTimeOffset due = clock.GetUtcNow() + Backoff[next - 1];
            if (failure.Step == "rate-limited" && rateLimitedUntil?.Invoke(provider) is { } until && until > due)
                due = until;
            int written = isRetry
                ? Advance(connection, transaction, userId, provider, seriesKey, operationId, next, due, failure.Step)
                : Upsert(connection, transaction, userId, provider, seriesKey, subject, operationId, next, due, failure.Step);
            if (written > 0)
                decisions.Add(new RetryDecision(provider, RetryDecisionKind.Scheduled, next, due, title));
        }
        transaction.Commit();
        return decisions;
    }

    /// <summary>Retries whose time has come, oldest first.</summary>
    public IReadOnlyList<RetryRow> Due() => Query("WHERE due_utc <= $now ORDER BY due_utc", ("$now", Format(clock.GetUtcNow())));

    public IReadOnlyList<RetryRow> ForUser(Guid userId) => Query("WHERE user_id = $user ORDER BY due_utc", ("$user", Id(userId)));

    /// <summary>Holds a retry back while <paramref name="operationId"/> runs it, and ties the row to that operation.</summary>
    public void Lease(Guid id, Guid operationId) =>
        Execute("UPDATE retries SET due_utc = $due, last_operation_id = $operation WHERE id = $id",
            ("$id", Id(id)), ("$due", Format(clock.GetUtcNow() + LeaseTime)), ("$operation", Id(operationId)));

    public void Remove(Guid id) => Execute("DELETE FROM retries WHERE id = $id", ("$id", Id(id)));

    private static int Upsert(SqliteConnection connection, SqliteTransaction transaction, Guid userId, ApiName provider, string seriesKey,
        RetrySubject subject, Guid operationId, int attempt, DateTimeOffset due, string step) =>
        Run(connection, transaction, """
            INSERT INTO retries (id, user_id, provider, source_item, series_key, trigger, attempt, due_utc, last_operation_id, last_step)
            VALUES ($id, $user, $provider, $item, $series, $trigger, $attempt, $due, $operation, $step)
            ON CONFLICT (user_id, provider, series_key) DO UPDATE SET source_item = excluded.source_item, trigger = excluded.trigger,
                attempt = excluded.attempt, due_utc = excluded.due_utc, last_operation_id = excluded.last_operation_id, last_step = excluded.last_step
            """,
            ("$id", Id(Guid.NewGuid())), ("$user", Id(userId)), ("$provider", provider.ToString()), ("$item", Id(subject.SourceItem)),
            ("$series", seriesKey), ("$trigger", subject.Trigger.ToString()), ("$attempt", attempt), ("$due", Format(due)),
            ("$operation", Id(operationId)), ("$step", step));

    private static int Advance(SqliteConnection connection, SqliteTransaction transaction, Guid userId, ApiName provider, string seriesKey,
        Guid operationId, int attempt, DateTimeOffset due, string step) =>
        Run(connection, transaction, """
            UPDATE retries SET attempt = $attempt, due_utc = $due, last_step = $step
            WHERE user_id = $user AND provider = $provider AND last_operation_id = $operation
            """,
            ("$user", Id(userId)), ("$provider", provider.ToString()), ("$series", seriesKey), ("$operation", Id(operationId)),
            ("$attempt", attempt), ("$due", Format(due)), ("$step", step));

    private static int Delete(SqliteConnection connection, SqliteTransaction transaction, Guid userId, ApiName provider, string seriesKey, Guid? onlyFor) =>
        Run(connection, transaction, """
            DELETE FROM retries WHERE user_id = $user AND provider = $provider
                AND (($operation IS NULL AND series_key = $series) OR last_operation_id = $operation)
            """,
            ("$user", Id(userId)), ("$provider", provider.ToString()), ("$series", seriesKey),
            ("$operation", onlyFor is { } operation ? Id(operation) : DBNull.Value));

    private IReadOnlyList<RetryRow> Query(string where, params (string Name, object Value)[] parameters) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, user_id, provider, source_item, series_key, trigger, attempt, due_utc, last_operation_id, last_step FROM retries {where}";
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var rows = new List<RetryRow>();
        while (reader.Read()) {
            // A row naming a tracker or trigger this version does not know is skipped, not fatal.
            if (!Enum.TryParse(reader.GetString(2), out ApiName provider) || !Enum.TryParse(reader.GetString(5), out OperationKind trigger))
                continue;
            rows.Add(new RetryRow(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), provider, Guid.Parse(reader.GetString(3)),
                reader.GetString(4), trigger, reader.GetInt32(6), Parse(reader.GetString(7)), Guid.Parse(reader.GetString(8)), reader.GetString(9)));
        }
        return rows;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters) {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        Run(connection, transaction, sql, parameters);
        transaction.Commit();
    }

    private static int Run(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static string Format(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Id(Guid id) => id.ToString("D");
}
