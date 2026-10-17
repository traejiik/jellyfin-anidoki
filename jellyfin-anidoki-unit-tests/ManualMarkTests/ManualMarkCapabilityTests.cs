using System;
using System.IO;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Storage;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ManualMarkTests;

[TestFixture]
public class ManualMarkCapabilityTests {
    private string _directory = null!;
    private AniDokiDatabase _database = null!;
    private string LegacyPath => Path.Combine(_directory, "capabilities.json");

    [SetUp]
    public void Setup() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-capability-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _database = new AniDokiDatabase(Path.Combine(_directory, "anidoki.db"));
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_directory, true);

    private ManualMarkCapabilityRegistry Registry() => new(_database, LegacyPath);

    [Test]
    public void EnrollmentSurvivesReloadButNewIdentityNeverInheritsIt() {
        var user = Guid.NewGuid();
        Assert.That(Registry().Enroll(user, "fixture-identity"), Is.True);

        var reloaded = Registry();

        Assert.That(reloaded.RequiresConfirmation(user, "fixture-identity"), Is.True);
        Assert.That(reloaded.RequiresConfirmation(user, "rotated-identity"), Is.False);
        Assert.That(reloaded.RequiresConfirmation(Guid.NewGuid(), "fixture-identity"), Is.False);
        Assert.That(File.Exists(LegacyPath), Is.False);
    }

    [Test]
    public void LegacyEnrollmentsAreImportedOnceAndTheFileIsRemoved() {
        var user = Guid.NewGuid();
        File.WriteAllText(LegacyPath, $$"""{"Version":1,"Identities":[{"UserId":"{{user}}","AuthenticationKey":"web"}]}""");

        var registry = Registry();

        Assert.That(registry.PersistenceUnreadable, Is.False);
        Assert.That(registry.RequiresConfirmation(user, "web"), Is.True);
        Assert.That(File.Exists(LegacyPath), Is.False);
        Assert.That(Registry().RequiresConfirmation(user, "web"), Is.True);
    }

    [TestCase("not-json"), TestCase("null"), TestCase("{\"Version\":2,\"Identities\":[]}"), TestCase("{\"Version\":1,\"Identities\":null}")]
    public void CorruptLegacyFileWithholdsAmbiguousManualWritesAndIsKept(string content) {
        File.WriteAllText(LegacyPath, content);

        var registry = Registry();

        Assert.That(registry.RequiresConfirmation(Guid.NewGuid(), "unknown"), Is.True);
        Assert.That(registry.Enroll(Guid.NewGuid(), "new"), Is.False);
        Assert.That(File.Exists(LegacyPath), Is.True);
    }

    [Test]
    public void UnreadableLegacyPathDoesNotPretendThereWasNoEnrollment() {
        Directory.CreateDirectory(LegacyPath);

        var registry = Registry();

        Assert.That(registry.RequiresConfirmation(Guid.NewGuid(), "unknown"), Is.True);
        Assert.That(registry.Enroll(Guid.NewGuid(), "new"), Is.False);
    }

    [Test]
    public void UnavailableDatabaseNeverAcknowledgesEnrollment() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        var registry = new ManualMarkCapabilityRegistry(new AniDokiDatabase(Path.Combine(file, "anidoki.db")), LegacyPath);

        Assert.That(registry.PersistenceUnreadable, Is.True);
        Assert.That(registry.Enroll(Guid.NewGuid(), "fixture"), Is.False);
    }

    [Test]
    public void RemovingDeletedIdentitiesPersistsWithoutTouchingOtherUsers() {
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        var registry = Registry();
        registry.Enroll(one, "one");
        registry.Enroll(two, "two");

        Assert.That(registry.RemoveUser(one), Is.True);

        var reloaded = Registry();
        Assert.That(reloaded.RequiresConfirmation(one, "one"), Is.False);
        Assert.That(reloaded.RequiresConfirmation(two, "two"), Is.True);
    }

    [Test]
    public void AmbiguousEventsForAnEnrolledUserFailClosedButVerifiedOtherIdentitiesStayAutomatic() {
        var user = Guid.NewGuid();
        var registry = Registry();
        registry.Enroll(user, "web");

        Assert.That(registry.HasEnrollmentForUser(user), Is.True);
        Assert.That(registry.HasEnrollmentForUser(Guid.NewGuid()), Is.False);
        Assert.That(registry.RequiresConfirmation(user, "native"), Is.False);
    }
}
