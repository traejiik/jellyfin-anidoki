using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.Storage;

/// <summary>
/// The plugin-owned SQLite database. It uses the server's SQLite, applies <see cref="StorageMigrations"/> on open,
/// and reports <see cref="Failure"/> instead of throwing when it cannot be used.
/// </summary>
public sealed class AniDokiDatabase {
    private static readonly ConcurrentDictionary<string, AniDokiDatabase> Instances = new(StringComparer.Ordinal);
    private readonly string _connectionString;

    public AniDokiDatabase(string filePath) {
        FilePath = Path.GetFullPath(filePath);
        _connectionString = new SqliteConnectionStringBuilder {
            DataSource = FilePath,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var connection = OpenCore();
            Migrate(connection);
        } catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException
                                            or InvalidOperationException or TypeInitializationException or DllNotFoundException) {
            Failure = $"The AniDoki database at {FilePath} could not be opened: {error.Message}";
        }
    }

    public string FilePath { get; }

    /// <summary>Why the database cannot be used, or null when it is available.</summary>
    public string? Failure { get; }

    public bool IsAvailable => Failure == null;

    /// <summary>The database for a Jellyfin data folder, shared by the plugin and its services.</summary>
    public static AniDokiDatabase ForDataPath(string dataPath) =>
        Instances.GetOrAdd(Path.GetFullPath(Path.Combine(dataPath, "AniDoki", "anidoki.db")), path => new AniDokiDatabase(path));

    /// <exception cref="InvalidOperationException">The database is unavailable.</exception>
    public SqliteConnection Open() {
        if (Failure != null)
            throw new InvalidOperationException(Failure);
        return OpenCore();
    }

    private SqliteConnection OpenCore() {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void Migrate(SqliteConnection connection) {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");
        long current;
        using (var read = connection.CreateCommand()) {
            read.Transaction = transaction;
            read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
            current = (long)read.ExecuteScalar()!;
        }
        if (current > StorageMigrations.Latest)
            throw new InvalidOperationException($"its schema version {current} is newer than this plugin supports ({StorageMigrations.Latest})");
        foreach (var (version, sql) in StorageMigrations.All.Where(migration => migration.Version > current)) {
            Execute(connection, transaction, sql);
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_version (version) VALUES ($version)";
            record.Parameters.AddWithValue("$version", version);
            record.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
