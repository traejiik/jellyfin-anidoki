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
        var database = new AniDokiDatabase(path);
        using (var connection = database.Open()) {
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO schema_version (version) VALUES ({StorageMigrations.Latest + 1})";
            command.ExecuteNonQuery();
        }

        var reopened = new AniDokiDatabase(path);

        Assert.That(reopened.IsAvailable, Is.False);
        Assert.That(reopened.Failure, Does.Contain("newer"));
        Assert.Throws<InvalidOperationException>(() => reopened.Open());
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
