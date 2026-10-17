using System;
using System.Collections.Generic;
using System.IO;
using jellyfin_anidoki.Storage;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Storage;

[TestFixture]
public class SecretStoreTests {
    private string _directory = null!;
    private SecretStore _store = null!;

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-secrets-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _store = new SecretStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void EmptyStoreReadsNothing() => Assert.That(_store.ReadAll(), Is.Empty);

    [Test]
    public void ReplacedSecretsReadBack() {
        var secrets = new Dictionary<SecretKey, string> {
            [new SecretKey(SecretKey.AccessTokenKind, "user", "AniList")] = "access",
            [new SecretKey(SecretKey.ClientSecretKind, "", "Mal")] = "client"
        };

        _store.ReplaceAll(secrets);

        Assert.That(_store.ReadAll(), Is.EquivalentTo(secrets));
    }

    [Test]
    public void ReplacingRemovesSecretsThatAreNoLongerPresent() {
        _store.ReplaceAll(new Dictionary<SecretKey, string> {
            [new SecretKey(SecretKey.AccessTokenKind, "user", "AniList")] = "access",
            [new SecretKey(SecretKey.RefreshTokenKind, "user", "AniList")] = "refresh"
        });

        _store.ReplaceAll(new Dictionary<SecretKey, string> {
            [new SecretKey(SecretKey.AccessTokenKind, "user", "AniList")] = "rotated"
        });

        Assert.That(_store.ReadAll(), Is.EquivalentTo(new Dictionary<SecretKey, string> {
            [new SecretKey(SecretKey.AccessTokenKind, "user", "AniList")] = "rotated"
        }));
    }

    [Test]
    public void UnavailableDatabaseThrowsInsteadOfReturningNothing() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        var store = new SecretStore(new AniDokiDatabase(Path.Combine(file, "anidoki.db")));

        Assert.Throws<InvalidOperationException>(() => store.ReadAll());
        Assert.Throws<InvalidOperationException>(() => store.ReplaceAll(new Dictionary<SecretKey, string>()));
    }
}
