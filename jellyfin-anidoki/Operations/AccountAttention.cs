using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Storage;

namespace jellyfin_anidoki.Operations;

public enum AttentionKind {
    /// <summary>The tracker refused the sign-in; nothing reaches it until the user signs in again.</summary>
    SignInRejected,
    /// <summary>A retry gave up after its last attempt.</summary>
    UpdatesFailing
}

/// <summary>One linked tracker that needs the user's or the admin's attention.</summary>
/// <param name="Since">When it started: the first of the rejected sign-ins in a row, or when the retries gave up.</param>
/// <param name="Step">Why: unauthorized, or rate-limited or transient for a retry that gave up.</param>
public sealed record Attention(Guid UserId, ApiName Provider, AttentionKind Kind, DateTimeOffset Since, string Step);

/// <summary>
/// D7: which accounts need attention, read from the operations tables, with no table of its own. Only a tracker's newest
/// decisive result counts (a confirmed or unneeded change, a failure, an unconfirmed write), so the next success clears
/// it. Skips and unmatched items say nothing about the account. Throws <see cref="InvalidOperationException"/> when
/// storage is unavailable.
/// </summary>
public sealed class AccountAttention(AniDokiDatabase database) {
    public IReadOnlyList<Attention> For(Guid userId) => Read(userId);

    public IReadOnlyList<Attention> ForAll() => Read(null);

    private IReadOnlyList<Attention> Read(Guid? userId) {
        var results = new List<(Guid User, string Provider, string Outcome, string Step, string Kind, int? Attempt, DateTimeOffset At)>();
        using (var connection = database.Open()) {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT o.user_id, a.provider, a.outcome, a.step, o.kind, o.retry_attempt, COALESCE(o.finished_utc, o.created_utc)
                FROM operation_actions a JOIN operations o ON o.id = a.operation_id
                WHERE a.outcome IN ('Confirmed', 'NoChange', 'Failed', 'Unconfirmed') AND ($user IS NULL OR o.user_id = $user)
                ORDER BY o.user_id, a.provider, o.created_utc DESC, a.seq DESC
                """;
            command.Parameters.AddWithValue("$user", userId is { } user ? user.ToString("D") : DBNull.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                results.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        var attention = new List<Attention>();
        foreach (var account in results.GroupBy(result => (result.User, result.Provider))) {
            if (!Enum.TryParse(account.Key.Provider, out ApiName provider))
                continue;
            var newest = account.First();
            if (newest is { Outcome: "Failed", Step: "unauthorized" }) {
                var run = account.TakeWhile(result => result is { Outcome: "Failed", Step: "unauthorized" });
                attention.Add(new Attention(account.Key.User, provider, AttentionKind.SignInRejected, run.Last().At, newest.Step));
            } else if (newest is { Outcome: "Failed", Step: "rate-limited" or "transient", Kind: nameof(OperationKind.Retry) }
                       && newest.Attempt >= RetryQueue.MaxAttempts) {
                attention.Add(new Attention(account.Key.User, provider, AttentionKind.UpdatesFailing, newest.At, newest.Step));
            }
        }
        return attention;
    }
}
