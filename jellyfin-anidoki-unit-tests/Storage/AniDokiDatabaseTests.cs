using System;
using System.IO;
using jellyfin_anidoki.Storage;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Storage;

[TestFixture]
public class AniDokiDatabaseTests {
    private string _directory = null!;

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-database-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void NewDatabaseIsCreatedAtTheLatestSchema() {
        var database = new AniDokiDatabase(Path.Combine(_directory, "AniDoki", "anidoki.db"));

        Assert.That(database.IsAvailable, Is.True, database.Failure);
        Assert.That(File.Exists(database.FilePath), Is.True);
        Assert.That(SchemaVersion(database), Is.EqualTo(StorageMigrations.Latest));
    }

    [Test]
    public void ReopeningDoesNotRepeatMigrations() {
        string path = Path.Combine(_directory, "anidoki.db");
        _ = new AniDokiDatabase(path);

        var reopened = new AniDokiDatabase(path);

        using var connection = reopened.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_version";
        Assert.That((long)command.ExecuteScalar()!, Is.EqualTo(StorageMigrations.All.Length));
    }

    [Test]
    public void DatabaseFromANewerPluginIsReportedUnavailable() {
        string path = Path.Combine(_directory, "anidoki.db");
        Run(new AniDokiDatabase(path),
            $"INSERT INTO schema_version (version) VALUES ({StorageMigrations.Latest + 1})",
            $"UPDATE schema_compat SET min_reader = {StorageMigrations.Latest + 1}");

        var reopened = new AniDokiDatabase(path);

        Assert.That(reopened.IsAvailable, Is.False);
        Assert.That(reopened.Failure, Does.Contain("newer"));
        Assert.Throws<InvalidOperationException>(() => reopened.Open());
    }

    [Test]
    public void NewerDatabaseWithoutACompatibilityMarkerIsReportedUnavailable() {
        string path = Path.Combine(_directory, "anidoki.db");
        Run(new AniDokiDatabase(path),
            $"INSERT INTO schema_version (version) VALUES ({StorageMigrations.Latest + 1})",
            "DROP TABLE schema_compat");

        var reopened = new AniDokiDatabase(path);

        Assert.That(reopened.IsAvailable, Is.False);
        Assert.That(reopened.Failure, Does.Contain("newer"));
    }

    [Test]
    public void NewerDatabaseThisVersionCanReadOpensUntouched() {
        string path = Path.Combine(_directory, "anidoki.db");
        Run(new AniDokiDatabase(path), $"INSERT INTO schema_version (version) VALUES ({StorageMigrations.Latest + 1})");

        var reopened = new AniDokiDatabase(path);

        Assert.That(reopened.IsAvailable, Is.True, reopened.Failure);
        Assert.That(SchemaVersion(reopened), Is.EqualTo(StorageMigrations.Latest + 1));
    }

    [Test]
    public void VersionThreeDatabaseKeepsItsRowsAndGainsTheColumns() {
        string path = Path.Combine(_directory, "anidoki.db");
        CreateAtVersion(path, 3);
        using (var connection = Connect(path)) {
            Execute(connection, """
                INSERT INTO operation_actions (operation_id, seq, provider, target_id, target_title, outcome, step, meaningful)
                VALUES ('op', 1, 'AniList', '500', 'Fixture Show', 'Confirmed', 'progress', 1)
                """);
        }

        var database = new AniDokiDatabase(path);

        Assert.That(database.IsAvailable, Is.True, database.Failure);
        Assert.That(SchemaVersion(database), Is.EqualTo(StorageMigrations.Latest));
        using var open = database.Open();
        using var command = open.CreateCommand();
        command.CommandText = "SELECT target_title, source_item, source_episode, target_episode, resolved_by, resolution_note, planned_change, planned_against FROM operation_actions";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo("Fixture Show"));
        for (int column = 1; column < reader.FieldCount; column++)
            Assert.That(reader.IsDBNull(column), Is.True, reader.GetName(column));
    }

    [Test]
    public void MigrationFourMarksItsOwnVersionAsTheMinimumReader() {
        var database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT min_reader FROM schema_compat";
        Assert.That((long)command.ExecuteScalar()!, Is.EqualTo(4));
    }

    private static void Run(AniDokiDatabase database, params string[] statements) {
        using var connection = database.Open();
        foreach (string statement in statements)
            Execute(connection, statement);
    }

    private static SqliteConnection Connect(string path) {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql) {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Builds the database a plugin at this schema version left, with released migrations only.</summary>
    private static void CreateAtVersion(string path, int version) {
        using var connection = Connect(path);
        Execute(connection, "CREATE TABLE schema_version (version INTEGER NOT NULL)");
        foreach (var (number, sql) in StorageMigrations.All) {
            if (number > version)
                break;
            Execute(connection, sql);
            Execute(connection, $"INSERT INTO schema_version (version) VALUES ({number})");
        }
    }

    [Test]
    public void UnusablePathIsReportedUnavailable() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");

        var database = new AniDokiDatabase(Path.Combine(file, "anidoki.db"));

        Assert.That(database.IsAvailable, Is.False);
        Assert.That(database.Failure, Does.Contain("could not be opened"));
    }

    [Test]
    public void DatabaseUsesWriteAheadLogging() {
        var database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.That(command.ExecuteScalar(), Is.EqualTo("wal"));
    }

    [Test]
    public void SameDataPathSharesOneInstance() {
        Assert.That(AniDokiDatabase.ForDataPath(_directory), Is.SameAs(AniDokiDatabase.ForDataPath(_directory)));
        Assert.That(AniDokiDatabase.ForDataPath(_directory).FilePath, Is.EqualTo(Path.GetFullPath(Path.Combine(_directory, "AniDoki", "anidoki.db"))));
    }

    private static long SchemaVersion(AniDokiDatabase database) {
        using SqliteConnection connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_version";
        return (long)command.ExecuteScalar()!;
    }
}
