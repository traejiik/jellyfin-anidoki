using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using jellyfin_anidoki.Storage;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.ManualMarks;

// Sticky enrollment is deliberately separate from the ephemeral operation store.
public sealed class ManualMarkCapabilityRegistry {
    private sealed record Enrollment(Guid UserId, string AuthenticationKey);
    private sealed record Document(int Version, Enrollment[] Identities);
    private readonly object _lock = new();
    private readonly AniDokiDatabase _database;
    private HashSet<Enrollment> _identities = [];

    /// <summary>
    /// True when enrollment cannot be read or written. Every ambiguous manual write is then withheld rather than
    /// guessed, and no new enrollment is acknowledged.
    /// </summary>
    public bool PersistenceUnreadable { get; private set; }

    /// <param name="legacyPath">The JSON file older versions used. It is imported once and deleted after the import commits.</param>
    public ManualMarkCapabilityRegistry(AniDokiDatabase database, string legacyPath) {
        _database = database;
        try {
            ImportLegacy(legacyPath);
            _identities = Load();
        } catch (Exception error) when (error is SqliteException or InvalidOperationException or IOException
                                            or InvalidDataException or UnauthorizedAccessException or JsonException) {
            PersistenceUnreadable = true;
        }
    }

    public bool RequiresConfirmation(Guid userId, string key) {
        lock (_lock)
            return PersistenceUnreadable || _identities.Contains(new(userId, key));
    }

    public bool HasEnrollmentForUser(Guid userId) {
        lock (_lock)
            return PersistenceUnreadable || _identities.Any(e => e.UserId == userId);
    }

    public bool Enroll(Guid userId, string key) {
        lock (_lock) {
            if (PersistenceUnreadable || userId == Guid.Empty || string.IsNullOrWhiteSpace(key))
                return false;
            var identity = new Enrollment(userId, key);
            if (_identities.Contains(identity))
                return true;
            if (_identities.Count >= 10000)
                return false;
            if (!Write("INSERT OR IGNORE INTO manual_mark_enrollments (user_id, authentication_key) VALUES ($user, $key)", userId, key))
                return false;
            _identities = new HashSet<Enrollment>(_identities) { identity };
            return true;
        }
    }

    public bool RemoveUser(Guid userId) {
        lock (_lock) {
            if (PersistenceUnreadable)
                return false;
            if (!Write("DELETE FROM manual_mark_enrollments WHERE user_id = $user", userId, null))
                return false;
            _identities = _identities.Where(e => e.UserId != userId).ToHashSet();
            return true;
        }
    }

    private void ImportLegacy(string path) {
        if (Directory.Exists(path))
            throw new IOException($"{path} is a directory, not an enrollment file");
        if (!File.Exists(path))
            return;
        Enrollment[] identities;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            if (stream.Length > 2 * 1024 * 1024)
                throw new InvalidDataException();
            var document = JsonSerializer.Deserialize<Document>(stream);
            if (document?.Version != 1 || document.Identities == null || document.Identities.Length > 10000 ||
                document.Identities.Any(e => e == null || e.UserId == Guid.Empty || string.IsNullOrWhiteSpace(e.AuthenticationKey)))
                throw new InvalidDataException();
            identities = document.Identities;
        }
        using (var connection = _database.Open()) {
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO manual_mark_enrollments (user_id, authentication_key) VALUES ($user, $key)";
            var user = insert.Parameters.Add("$user", SqliteType.Text);
            var key = insert.Parameters.Add("$key", SqliteType.Text);
            foreach (var identity in identities) {
                user.Value = identity.UserId.ToString("D");
                key.Value = identity.AuthenticationKey;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        File.Delete(path);
    }

    private HashSet<Enrollment> Load() {
        var identities = new HashSet<Enrollment>();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, authentication_key FROM manual_mark_enrollments";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            identities.Add(new Enrollment(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return identities;
    }

    private bool Write(string sql, Guid userId, string? key) {
        try {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$user", userId.ToString("D"));
            if (key != null)
                command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
            return true;
        } catch (Exception error) when (error is SqliteException or InvalidOperationException) {
            return false;
        }
    }
}
