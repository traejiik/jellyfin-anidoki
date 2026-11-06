using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.Matching;

/// <summary>
/// Saved matches and their history. Each write is one transaction, checked against the revision the caller last saw:
/// 0 means "none exists". Revision numbers keep rising per scope across delete and recreate. This stores what it is
/// given; <see cref="MatchValidator"/> runs before it. Every method throws <see cref="InvalidOperationException"/> when
/// the database is unavailable.
/// </summary>
public sealed class MatchStore(AniDokiDatabase database, TimeProvider clock) {
    private const string SharedOwner = "";
    private const string SweepAuthor = "sweep";

    public StoredMatch? Get(MatchScope scope) {
        using var connection = database.Open();
        return Read(connection, null, scope);
    }

    /// <summary>The match that applies to this user: see <see cref="MatchPrecedence"/>. Null means automatic resolution.</summary>
    public Match? GetEffective(Guid userId, ApiName provider, string sourceKey, bool providerLinked) {
        using var connection = database.Open();
        Match? personal = providerLinked ? Read(connection, null, new MatchScope(provider, sourceKey, userId))?.Match : null;
        Match? shared = Read(connection, null, new MatchScope(provider, sourceKey))?.Match;
        return MatchPrecedence.Effective(personal, shared, providerLinked);
    }

    /// <summary>Every source ever saved, by title, including those whose matches were all deleted.</summary>
    public IReadOnlyList<MatchSourceSummary> ListSources() {
        using var connection = database.Open();
        var summaries = new List<MatchSourceSummary>();
        using (var command = connection.CreateCommand()) {
            command.CommandText = "SELECT source_key, kind, title, fingerprint, updated_utc FROM match_sources ORDER BY title, source_key";
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var fingerprint = ActionJson.Read<Dictionary<string, string>>(reader.GetString(3)) ?? [];
                summaries.Add(new MatchSourceSummary(new MatchSource(reader.GetString(0), Enum.Parse<MatchSourceKind>(reader.GetString(1)), reader.GetString(2), fingerprint),
                    ParseTime(reader.GetString(4)), new Dictionary<ApiName, long>(), new Dictionary<ApiName, int>()));
            }
        }
        var bySource = summaries.ToDictionary(summary => summary.Source.SourceKey);
        using (var command = connection.CreateCommand()) {
            command.CommandText = "SELECT source_key, provider, owner_user_id, revision FROM matches";
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                MatchSourceSummary summary = bySource[reader.GetString(0)];
                ApiName provider = Enum.Parse<ApiName>(reader.GetString(1));
                if (reader.IsDBNull(2)) {
                    ((Dictionary<ApiName, long>)summary.Shared)[provider] = reader.GetInt64(3);
                } else {
                    var overrides = (Dictionary<ApiName, int>)summary.Overrides;
                    overrides[provider] = overrides.GetValueOrDefault(provider) + 1;
                }
            }
        }
        return summaries;
    }

    /// <summary>Personal overrides, of one source, one owner, or both, by source, provider and owner.</summary>
    public IReadOnlyList<OverrideSummary> ListOverrides(string? sourceKey = null, Guid? owner = null) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_key, provider, owner_user_id, revision, base_shared_revision FROM matches
            WHERE owner_user_id IS NOT NULL AND ($source IS NULL OR source_key = $source) AND ($owner IS NULL OR owner_user_id = $owner)
            ORDER BY source_key, provider, owner_user_id
            """;
        command.Parameters.AddWithValue("$source", Nullable(sourceKey));
        command.Parameters.AddWithValue("$owner", owner is { } id ? Id(id) : DBNull.Value);
        using var reader = command.ExecuteReader();
        var overrides = new List<OverrideSummary>();
        while (reader.Read()) {
            // As in the sweep: an owner that is not a user ID was not written by this store.
            if (Guid.TryParse(reader.GetString(2), out Guid user))
                overrides.Add(new OverrideSummary(new MatchScope(Enum.Parse<ApiName>(reader.GetString(1)), reader.GetString(0), user),
                    reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }
        return overrides;
    }

    /// <summary>Refreshes a source's title and fingerprint.</summary>
    public void UpsertSource(MatchSource source) {
        using var connection = database.Open();
        UpsertSource(connection, null, source);
    }

    /// <param name="match">Its <see cref="Match.Revision"/> is ignored; the store assigns the next one.</param>
    public MatchWrite Save(MatchSource source, Match match, long expectedRevision, string author, string? note = null, string? connectionGeneration = null) {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        long current = CurrentRevision(connection, transaction, match.Scope);
        if (current != expectedRevision)
            return new MatchWrite(MatchWriteStatus.Conflict, current);
        UpsertSource(connection, transaction, source);
        long revision = NextRevision(connection, transaction, match.Scope);
        WriteMatch(connection, transaction, match, revision, author, note, connectionGeneration);
        AddRevision(connection, transaction, match.Scope, revision, MatchRevisionAction.Save, author, new MatchSnapshot(match.Ranges, match.Exceptions), null);
        transaction.Commit();
        return new MatchWrite(MatchWriteStatus.Written, revision);
    }

    /// <summary>Deletes the match and records a Delete revision; earlier history stays.</summary>
    public MatchWrite Delete(MatchScope scope, long expectedRevision, string author) {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        long current = CurrentRevision(connection, transaction, scope);
        if (current != expectedRevision)
            return new MatchWrite(MatchWriteStatus.Conflict, current);
        if (current == 0)
            return new MatchWrite(MatchWriteStatus.NotFound, 0);
        long revision = RemoveMatch(connection, transaction, scope, author);
        transaction.Commit();
        return new MatchWrite(MatchWriteStatus.Written, revision);
    }

    /// <summary>
    /// Makes a user's override the shared match, recording where it came from. A kept override now matches the shared
    /// revision it was promoted to, so it is rebased onto it rather than reported as stale.
    /// </summary>
    public MatchPromotion Promote(Guid owner, ApiName provider, string sourceKey, long expectedSharedRevision, long expectedOverrideRevision, bool removeOverride, string author) {
        var overrideScope = new MatchScope(provider, sourceKey, owner);
        var sharedScope = new MatchScope(provider, sourceKey);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        long shared = CurrentRevision(connection, transaction, sharedScope);
        long personal = CurrentRevision(connection, transaction, overrideScope);
        if (shared != expectedSharedRevision || personal != expectedOverrideRevision)
            return new MatchPromotion(MatchWriteStatus.Conflict, shared, personal);
        if (personal == 0)
            return new MatchPromotion(MatchWriteStatus.NotFound, shared, 0);

        StoredMatch stored = Read(connection, transaction, overrideScope)!;
        long promoted = NextRevision(connection, transaction, sharedScope);
        var match = new Match(sharedScope, stored.Match.Ranges, stored.Match.Exceptions);
        WriteMatch(connection, transaction, match, promoted, author, stored.Note, null);
        AddRevision(connection, transaction, sharedScope, promoted, MatchRevisionAction.Promote, author, new MatchSnapshot(match.Ranges, match.Exceptions),
            string.Create(CultureInfo.InvariantCulture, $"{Id(owner)}:{personal}"));
        if (removeOverride) {
            RemoveMatch(connection, transaction, overrideScope, author);
            personal = 0;
        } else {
            Run(connection, transaction, "UPDATE matches SET base_shared_revision = $base WHERE source_key = $source AND provider = $provider AND owner_user_id = $owner",
                ("$base", promoted), ("$source", sourceKey), ("$provider", provider.ToString()), ("$owner", Id(owner)));
        }
        transaction.Commit();
        return new MatchPromotion(MatchWriteStatus.Written, promoted, personal);
    }

    /// <summary>Oldest first. It outlives the match, including a Delete revision for one the sweep removed.</summary>
    public IReadOnlyList<MatchHistoryEntry> History(MatchScope scope) {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision, action, author, created_utc, snapshot, promoted_from FROM match_revisions
            WHERE source_key = $source AND provider = $provider AND owner_user_id = $owner ORDER BY revision
            """;
        AddScope(command, scope);
        using var reader = command.ExecuteReader();
        var history = new List<MatchHistoryEntry>();
        while (reader.Read()) {
            history.Add(new MatchHistoryEntry(reader.GetInt64(0), Enum.Parse<MatchRevisionAction>(reader.GetString(1)), reader.GetString(2), ParseTime(reader.GetString(3)),
                ActionJson.Read<MatchSnapshot>(reader.GetString(4))!, reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return history;
    }

    /// <summary>
    /// Removes the overrides of users Jellyfin no longer has. An empty list removes nothing: a server always has a user,
    /// so an empty list means the users are not loaded yet.
    /// </summary>
    /// <returns>How many overrides were removed.</returns>
    public int SweepDeletedUsers(IReadOnlyCollection<Guid> knownUserIds) {
        if (knownUserIds.Count == 0)
            return 0;
        var known = knownUserIds.Select(Id).ToHashSet();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var orphans = new List<MatchScope>();
        using (var command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = "SELECT provider, source_key, owner_user_id FROM matches WHERE owner_user_id IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                // An owner that is not a user ID was not written by this store, so it is not this sweep's to judge.
                if (Guid.TryParse(reader.GetString(2), out Guid owner) && !known.Contains(Id(owner)))
                    orphans.Add(new MatchScope(Enum.Parse<ApiName>(reader.GetString(0)), reader.GetString(1), owner));
            }
        }
        foreach (MatchScope scope in orphans)
            RemoveMatch(connection, transaction, scope, SweepAuthor);
        transaction.Commit();
        return orphans.Count;
    }

    private void UpsertSource(SqliteConnection connection, SqliteTransaction? transaction, MatchSource source) =>
        Run(connection, transaction, """
            INSERT INTO match_sources (source_key, kind, title, fingerprint, updated_utc) VALUES ($source, $kind, $title, $fingerprint, $now)
            ON CONFLICT (source_key) DO UPDATE SET kind = excluded.kind, title = excluded.title, fingerprint = excluded.fingerprint, updated_utc = excluded.updated_utc
            """,
            ("$source", source.SourceKey), ("$kind", source.Kind.ToString()), ("$title", source.Title),
            ("$fingerprint", ActionJson.Write(source.Fingerprint)), ("$now", Now()));

    private void WriteMatch(SqliteConnection connection, SqliteTransaction transaction, Match match, long revision, string author, string? note, string? connectionGeneration) {
        MatchScope scope = match.Scope;
        // Replacing the row removes its ranges and exceptions through ON DELETE CASCADE.
        DeleteRow(connection, transaction, scope);
        string id = Guid.NewGuid().ToString("D");
        Run(connection, transaction, """
            INSERT INTO matches (id, source_key, provider, owner_user_id, revision, base_shared_revision, connection_generation, note, updated_by, updated_utc)
            VALUES ($id, $source, $provider, $owner, $revision, $base, $generation, $note, $author, $now)
            """,
            ("$id", id), ("$source", scope.SourceKey), ("$provider", scope.Provider.ToString()),
            ("$owner", scope.Owner is { } owner ? Id(owner) : DBNull.Value), ("$revision", revision),
            ("$base", scope.Owner != null && match.BaseSharedRevision is { } baseRevision ? baseRevision : DBNull.Value),
            ("$generation", scope.Owner != null ? Nullable(connectionGeneration) : DBNull.Value),
            ("$note", Nullable(note)), ("$author", author), ("$now", Now()));
        for (int sequence = 0; sequence < match.Ranges.Count; sequence++) {
            MatchRange range = match.Ranges[sequence];
            EntryRef entry = range.Target.Entry;
            Run(connection, transaction, """
                INSERT INTO match_ranges (match_id, seq, source_start, source_end, target_id, target_alternative_id, target_lookup, target_title, target_start)
                VALUES ($id, $seq, $start, $end, $target, $alternative, $lookup, $title, $targetStart)
                """,
                ("$id", id), ("$seq", sequence), ("$start", range.SourceStart), ("$end", range.SourceEnd is { } end ? end : DBNull.Value),
                ("$target", entry.Id), ("$alternative", Nullable(entry.AlternativeId)), ("$lookup", Lookup(entry)),
                ("$title", range.Target.Title), ("$targetStart", range.TargetStart));
        }
        foreach (MatchException exception in match.Exceptions) {
            var target = exception as TargetException;
            Run(connection, transaction, """
                INSERT INTO match_exceptions (match_id, source_episode, kind, target_id, target_alternative_id, target_lookup, target_title, target_episode)
                VALUES ($id, $episode, $kind, $target, $alternative, $lookup, $title, $targetEpisode)
                """,
                ("$id", id), ("$episode", exception.SourceEpisode), ("$kind", target != null ? "Target" : "Ignore"),
                ("$target", target != null ? target.Target.Entry.Id : DBNull.Value),
                ("$alternative", Nullable(target?.Target.Entry.AlternativeId)),
                ("$lookup", target != null ? Lookup(target.Target.Entry) : DBNull.Value),
                ("$title", Nullable(target?.Target.Title)),
                ("$targetEpisode", target != null ? target.TargetEpisode : DBNull.Value));
        }
    }

    private long RemoveMatch(SqliteConnection connection, SqliteTransaction transaction, MatchScope scope, string author) {
        long revision = NextRevision(connection, transaction, scope);
        DeleteRow(connection, transaction, scope);
        AddRevision(connection, transaction, scope, revision, MatchRevisionAction.Delete, author, new MatchSnapshot([], []), null);
        return revision;
    }

    private static void DeleteRow(SqliteConnection connection, SqliteTransaction transaction, MatchScope scope) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM matches WHERE source_key = $source AND provider = $provider AND COALESCE(owner_user_id, '') = $owner";
        AddScope(command, scope);
        command.ExecuteNonQuery();
    }

    private void AddRevision(SqliteConnection connection, SqliteTransaction transaction, MatchScope scope, long revision, MatchRevisionAction action, string author,
        MatchSnapshot snapshot, string? promotedFrom) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO match_revisions (source_key, provider, owner_user_id, revision, action, author, created_utc, snapshot, promoted_from)
            VALUES ($source, $provider, $owner, $revision, $action, $author, $now, $snapshot, $promoted)
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$action", action.ToString());
        command.Parameters.AddWithValue("$author", author);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$snapshot", ActionJson.Write(snapshot));
        command.Parameters.AddWithValue("$promoted", Nullable(promotedFrom));
        command.ExecuteNonQuery();
    }

    private static long CurrentRevision(SqliteConnection connection, SqliteTransaction transaction, MatchScope scope) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM matches WHERE source_key = $source AND provider = $provider AND COALESCE(owner_user_id, '') = $owner";
        AddScope(command, scope);
        return command.ExecuteScalar() is long revision ? revision : 0;
    }

    /// <summary>From history, not the match, so a recreated match does not reuse a deleted one's numbers.</summary>
    private static long NextRevision(SqliteConnection connection, SqliteTransaction transaction, MatchScope scope) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(revision), 0) + 1 FROM match_revisions WHERE source_key = $source AND provider = $provider AND owner_user_id = $owner";
        AddScope(command, scope);
        return (long)command.ExecuteScalar()!;
    }

    private static StoredMatch? Read(SqliteConnection connection, SqliteTransaction? transaction, MatchScope scope) {
        string id;
        long revision;
        long? baseShared;
        string? generation;
        string? note;
        string updatedBy;
        DateTimeOffset updated;
        using (var command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, revision, base_shared_revision, connection_generation, note, updated_by, updated_utc FROM matches
                WHERE source_key = $source AND provider = $provider AND COALESCE(owner_user_id, '') = $owner
                """;
            AddScope(command, scope);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            id = reader.GetString(0);
            revision = reader.GetInt64(1);
            baseShared = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            generation = reader.IsDBNull(3) ? null : reader.GetString(3);
            note = reader.IsDBNull(4) ? null : reader.GetString(4);
            updatedBy = reader.GetString(5);
            updated = ParseTime(reader.GetString(6));
        }

        var ranges = new List<MatchRange>();
        using (var command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT source_start, source_end, target_id, target_alternative_id, target_lookup, target_title, target_start
                FROM match_ranges WHERE match_id = $id ORDER BY seq
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                ranges.Add(new MatchRange(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    Target(scope.Provider, reader, 2, 3, 4, 5), reader.GetInt32(6)));
            }
        }

        var exceptions = new List<MatchException>();
        using (var command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT source_episode, kind, target_id, target_alternative_id, target_lookup, target_title, target_episode
                FROM match_exceptions WHERE match_id = $id ORDER BY source_episode
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                exceptions.Add(reader.GetString(1) == "Target"
                    ? new TargetException(reader.GetInt32(0), Target(scope.Provider, reader, 2, 3, 4, 5), reader.GetInt32(6))
                    : new IgnoreException(reader.GetInt32(0)));
            }
        }
        return new StoredMatch(new Match(scope, ranges, exceptions, revision, baseShared), note, updatedBy, updated, generation);
    }

    private static MatchTarget Target(ApiName provider, SqliteDataReader reader, int id, int alternative, int lookup, int title) =>
        new(new EntryRef(provider, reader.GetInt32(id), reader.IsDBNull(alternative) ? null : reader.GetString(alternative),
            reader.IsDBNull(lookup) ? null : ActionJson.Read<CrossReference>(reader.GetString(lookup))), reader.GetString(title));

    private static object Lookup(EntryRef entry) => entry.Lookup is { } lookup ? ActionJson.Write(lookup) : DBNull.Value;

    private static object Nullable(string? value) => (object?)value ?? DBNull.Value;

    private static void AddScope(SqliteCommand command, MatchScope scope) {
        command.Parameters.AddWithValue("$source", scope.SourceKey);
        command.Parameters.AddWithValue("$provider", scope.Provider.ToString());
        command.Parameters.AddWithValue("$owner", scope.Owner is { } owner ? Id(owner) : SharedOwner);
    }

    private static void Run(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private string Now() => clock.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Id(Guid id) => id.ToString("D");
}
