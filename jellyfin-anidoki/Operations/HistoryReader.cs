using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Storage;

namespace jellyfin_anidoki.Operations;

/// <summary>What one tracker's line in a history entry says; the pages word it.</summary>
public enum HistoryResultKind { Updated, Retrying, GaveUp, NeedsSignIn, Unconfirmed, Skipped, Unresolved, Failed }

/// <param name="Try">The try that updated the tracker, when a retry did: 2 for the first retry.</param>
/// <param name="Step">The planner's reason for a skip.</param>
public sealed record HistoryResult(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("try")] int? Try = null,
    [property: JsonPropertyName("step")] string? Step = null);

/// <summary>One update from playback, a watched mark, a confirmed mark or an orphaned retry, with one line per tracker that did something.</summary>
/// <param name="Results">Empty when no tracker needed a change.</param>
public sealed record HistoryEntry(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("whenUtc")] DateTimeOffset WhenUtc,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("episode")] int? Episode,
    [property: JsonPropertyName("results")] IReadOnlyList<HistoryResult> Results);

public sealed record HistoryPage(
    [property: JsonPropertyName("entries")] IReadOnlyList<HistoryEntry> Entries,
    [property: JsonPropertyName("more")] bool More);

public sealed record AttentionView(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sinceUtc")] DateTimeOffset SinceUtc,
    [property: JsonPropertyName("step")] string Step);

/// <param name="LastUpdateUtc">The newest change this tracker confirmed in retention; null when none.</param>
public sealed record TrackerActivity(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("lastUpdateUtc")] DateTimeOffset? LastUpdateUtc,
    [property: JsonPropertyName("attention")] AttentionView? Attention);

/// <param name="LastUpdateUtc">The newest change on any tracker in retention; null when none.</param>
public sealed record UserActivity(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("lastUpdateUtc")] DateTimeOffset? LastUpdateUtc,
    [property: JsonPropertyName("trackers")] IReadOnlyList<TrackerActivity> Trackers);

/// <summary>
/// D7: a user's recent updates and each tracker's last update, read from the operations tables. A retry is folded into the
/// update it followed (<c>operations.retry_of</c>), so its tracker's line shows the newest attempt. Every method throws
/// <see cref="InvalidOperationException"/> when storage is unavailable.
/// </summary>
public sealed class HistoryReader(AniDokiDatabase database, TimeProvider clock) {
    /// <summary>The most entries a page can reach, newest first.</summary>
    public const int MaxEntries = 50;

    private static readonly string[] Listed = [nameof(OperationKind.Playback), nameof(OperationKind.WatchedToggle), nameof(OperationKind.ConfirmedManual), nameof(OperationKind.Retry)];

    // The line that wins when one tracker has several entries in one update.
    private static readonly HistoryResultKind[] Priority = [HistoryResultKind.NeedsSignIn, HistoryResultKind.GaveUp, HistoryResultKind.Retrying,
        HistoryResultKind.Failed, HistoryResultKind.Unconfirmed, HistoryResultKind.Updated, HistoryResultKind.Unresolved, HistoryResultKind.Skipped];

    private const string EpisodeMarker = " · episode ";

    private sealed record Action(string Provider, string Outcome, string Step, bool Meaningful);

    private sealed record Operation(string Id, OperationKind Kind, string State, string Source, DateTimeOffset Created, DateTimeOffset? Finished,
        int? Attempt, string? RetryOf, List<Action> Actions) {
        public bool IsFinished => State is not (nameof(OperationState.Queued) or nameof(OperationState.Running));
    }

    public HistoryPage Read(Guid userId, int offset, int limit) {
        offset = Math.Clamp(offset, 0, MaxEntries);
        limit = Math.Clamp(limit, 1, MaxEntries);
        var (operations, pending) = Load(userId);
        var byId = operations.ToDictionary(operation => operation.Id);
        var chains = new Dictionary<string, List<Operation>>();
        foreach (var operation in operations) {
            Operation root = operation;
            var seen = new HashSet<string> { root.Id };
            while (root.Kind == OperationKind.Retry && root.RetryOf is { } previous && byId.TryGetValue(previous, out var earlier) && seen.Add(earlier.Id))
                root = earlier;
            if (!chains.TryGetValue(root.Id, out var chain))
                chains[root.Id] = chain = [];
            if (operation != root)
                chain.Add(operation);
        }
        var entries = chains
            .Select(chain => (Root: byId[chain.Key], Retries: chain.Value))
            .Where(chain => chain.Root.IsFinished)
            .OrderByDescending(chain => chain.Root.Created)
            .Take(MaxEntries)
            .ToList();
        var page = entries.Skip(offset).Take(limit).Select(chain => Entry(chain.Root, chain.Retries, pending)).ToList();
        return new HistoryPage(page, offset + limit < entries.Count);
    }

    /// <summary>Each user's last update and attention per tracker; one user's, or everyone's when <paramref name="userId"/> is null.</summary>
    public IReadOnlyList<UserActivity> Activity(Guid? userId, IReadOnlyList<Attention> attention) {
        var updates = new List<(Guid User, string Provider, DateTimeOffset At)>();
        using (var connection = database.Open()) {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT o.user_id, a.provider, MAX(COALESCE(o.finished_utc, o.created_utc))
                FROM operation_actions a JOIN operations o ON o.id = a.operation_id
                WHERE a.outcome = 'Confirmed' AND a.meaningful = 1 AND ($user IS NULL OR o.user_id = $user)
                GROUP BY o.user_id, a.provider
                """;
            command.Parameters.AddWithValue("$user", userId is { } user ? user.ToString("D") : DBNull.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                updates.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), Parse(reader.GetString(2))));
        }
        var users = updates.Select(update => update.User).Concat(attention.Select(item => item.UserId)).Distinct();
        if (userId is { } only)
            users = users.Append(only).Distinct();
        return users.Select(user => {
            var trackers = updates.Where(update => update.User == user).Select(update => update.Provider)
                .Concat(attention.Where(item => item.UserId == user).Select(item => item.Provider.ToString()))
                .Distinct()
                .Select(provider => new TrackerActivity(provider,
                    updates.Where(update => update.User == user && update.Provider == provider).Select(update => (DateTimeOffset?)update.At).FirstOrDefault(),
                    attention.Where(item => item.UserId == user && item.Provider.ToString() == provider)
                        .Select(item => new AttentionView(item.Kind.ToString(), item.Since, item.Step)).FirstOrDefault()))
                .OrderBy(tracker => Enum.TryParse(tracker.Provider, out ApiName api) ? (int)api : int.MaxValue)
                .ToList();
            DateTimeOffset? newest = trackers.Max(tracker => tracker.LastUpdateUtc);
            return new UserActivity(user, newest, trackers);
        }).ToList();
    }

    private (List<Operation> Operations, List<(string Provider, string Operation)> Pending) Load(Guid userId) {
        string user = userId.ToString("D");
        var operations = new Dictionary<string, Operation>();
        var pending = new List<(string, string)>();
        using var connection = database.Open();
        using (var command = connection.CreateCommand()) {
            command.CommandText = $"""
                SELECT id, kind, state, source, created_utc, finished_utc, retry_attempt, retry_of FROM operations
                WHERE user_id = $user AND created_utc >= $since AND kind IN ({string.Join(", ", Listed.Select(kind => $"'{kind}'"))})
                """;
            command.Parameters.AddWithValue("$user", user);
            command.Parameters.AddWithValue("$since", (clock.GetUtcNow() - OperationRunner.Retention).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                operations[reader.GetString(0)] = new Operation(reader.GetString(0), Enum.Parse<OperationKind>(reader.GetString(1)), reader.GetString(2),
                    reader.GetString(3), Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), []);
        }
        using (var command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT a.operation_id, a.provider, a.outcome, a.step, a.meaningful
                FROM operation_actions a JOIN operations o ON o.id = a.operation_id
                WHERE o.user_id = $user ORDER BY a.operation_id, a.seq
                """;
            command.Parameters.AddWithValue("$user", user);
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                if (operations.TryGetValue(reader.GetString(0), out var operation))
                    operation.Actions.Add(new Action(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4) == 1));
            }
        }
        using (var command = connection.CreateCommand()) {
            command.CommandText = "SELECT provider, last_operation_id FROM retries WHERE user_id = $user";
            command.Parameters.AddWithValue("$user", user);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                pending.Add((reader.GetString(0), reader.GetString(1)));
        }
        return (operations.Values.ToList(), pending);
    }

    private static HistoryEntry Entry(Operation root, List<Operation> retries, List<(string Provider, string Operation)> pending) {
        var chainIds = retries.Select(retry => retry.Id).Append(root.Id).ToHashSet();
        var results = new List<HistoryResult>();
        foreach (string provider in root.Actions.Concat(retries.SelectMany(retry => retry.Actions)).Select(action => action.Provider).Distinct()) {
            // The newest finished attempt that reached this tracker decides its line.
            Operation decisive = retries.Where(retry => retry.IsFinished && retry.Actions.Any(action => action.Provider == provider))
                .OrderBy(retry => retry.Created).LastOrDefault() ?? root;
            bool retrying = pending.Any(row => row.Provider == provider && chainIds.Contains(row.Operation));
            int? tryNumber = decisive.Kind == OperationKind.Retry && decisive.Attempt is { } attempt ? attempt + 1 : null;
            var candidates = decisive.Actions.Where(action => action.Provider == provider)
                .Select(action => Classify(action, decisive, retrying, tryNumber))
                .OfType<HistoryResult>()
                .ToList();
            if (candidates.Count > 0)
                results.Add(candidates.OrderBy(candidate => Array.IndexOf(Priority, Enum.Parse<HistoryResultKind>(candidate.Result))).First());
        }
        results.Sort((left, right) => Order(left.Provider).CompareTo(Order(right.Provider)));
        var (title, episode) = Describe(root.Source);
        return new HistoryEntry(Guid.Parse(root.Id), root.Kind.ToString(), root.Finished ?? root.Created, title, episode, results);
    }

    private static HistoryResult? Classify(Action action, Operation source, bool retrying, int? tryNumber) {
        HistoryResultKind? kind = action.Outcome switch {
            "Confirmed" when action.Meaningful => HistoryResultKind.Updated,
            "Skipped" => HistoryResultKind.Skipped,
            "Unresolved" => HistoryResultKind.Unresolved,
            "Unconfirmed" => HistoryResultKind.Unconfirmed,
            "Failed" when action.Step == "unauthorized" => HistoryResultKind.NeedsSignIn,
            "Failed" when action.Step is "rate-limited" or "transient" => retrying ? HistoryResultKind.Retrying
                : source.Kind == OperationKind.Retry && source.Attempt >= RetryQueue.MaxAttempts ? HistoryResultKind.GaveUp
                : HistoryResultKind.Failed,
            "Failed" => HistoryResultKind.Failed,
            _ => null
        };
        return kind switch {
            null => null,
            HistoryResultKind.Updated => new HistoryResult(action.Provider, kind.Value.ToString(), tryNumber),
            HistoryResultKind.Skipped => new HistoryResult(action.Provider, kind.Value.ToString(), Step: action.Step),
            HistoryResultKind.GaveUp => new HistoryResult(action.Provider, kind.Value.ToString(), Step: action.Step),
            _ => new HistoryResult(action.Provider, kind.Value.ToString())
        };
    }

    /// <summary>Title and episode from <see cref="OperationSources.Describe"/>'s label: "Series · episode N", or a movie's name.</summary>
    internal static (string Title, int? Episode) Describe(string source) {
        int marker = source.LastIndexOf(EpisodeMarker, StringComparison.Ordinal);
        if (marker < 0)
            return (source, null);
        string number = source[(marker + EpisodeMarker.Length)..];
        return (source[..marker], int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int episode) ? episode : null);
    }

    private static int Order(string provider) => Enum.TryParse(provider, out ApiName api) ? (int)api : int.MaxValue;

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
