using System;
using System.IO;
using jellyfin_anidoki.ManualMarks;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ManualMarkTests;

[TestFixture]
public class ManualMarkCapabilityTests {
    private string _directory = null!;
    private string PathName => Path.Combine(_directory, "capabilities.json");
    [SetUp] public void Setup() { _directory = Path.Combine(Path.GetTempPath(), "anidoki-capability-" + Guid.NewGuid()); Directory.CreateDirectory(_directory); }
    [TearDown] public void Cleanup() { Directory.Delete(_directory, true); }
    [Test]
    public void EnrollmentSurvivesReloadButNewIdentityNeverInheritsIt() {
        var user = Guid.NewGuid();
        var registry = new ManualMarkCapabilityRegistry(PathName);
        Assert.That(registry.Enroll(user, "fixture-identity"), Is.True);
        var reloaded = new ManualMarkCapabilityRegistry(PathName);
        Assert.That(reloaded.RequiresConfirmation(user, "fixture-identity"), Is.True);
        Assert.That(reloaded.RequiresConfirmation(user, "rotated-identity"), Is.False);
        Assert.That(reloaded.RequiresConfirmation(Guid.NewGuid(), "fixture-identity"), Is.False);
        Assert.That(File.ReadAllText(PathName), Does.Not.Contain("token"));
    }
    [TestCase("not-json"), TestCase("null"), TestCase("{\"Version\":2,\"Identities\":[]}"), TestCase("{\"Version\":1,\"Identities\":null}")]
    public void CorruptPersistenceWithholdsAmbiguousManualWritesAndCannotAcknowledgeEnrollment(string content) {
        File.WriteAllText(PathName, content);
        var registry = new ManualMarkCapabilityRegistry(PathName);
        Assert.That(registry.RequiresConfirmation(Guid.NewGuid(), "unknown"), Is.True);
        Assert.That(registry.Enroll(Guid.NewGuid(), "new"), Is.False);
    }
    [Test]
    public void UnreadablePersistencePathDoesNotPretendThereWasNoEnrollment() {
        Directory.CreateDirectory(PathName);
        var registry = new ManualMarkCapabilityRegistry(PathName);
        Assert.That(registry.RequiresConfirmation(Guid.NewGuid(), "unknown"), Is.True);
        Assert.That(registry.Enroll(Guid.NewGuid(), "new"), Is.False);
    }
    [Test]
    public void FailedPersistenceNeverAcknowledgesEnrollment() {
        var file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        var registry = new ManualMarkCapabilityRegistry(Path.Combine(file, "capabilities.json"));
        Assert.That(registry.Enroll(Guid.NewGuid(), "fixture"), Is.False);
    }
    [Test]
    public void RemovingDeletedIdentitiesPersistsWithoutTouchingOtherUsers() {
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        var registry = new ManualMarkCapabilityRegistry(PathName);
        registry.Enroll(one, "one");
        registry.Enroll(two, "two");
        Assert.That(registry.RemoveUser(one), Is.True);
        var reloaded = new ManualMarkCapabilityRegistry(PathName);
        Assert.That(reloaded.RequiresConfirmation(one, "one"), Is.False);
        Assert.That(reloaded.RequiresConfirmation(two, "two"), Is.True);
    }
    [Test]
    public void AmbiguousEventsForAnEnrolledUserFailClosedButVerifiedOtherIdentitiesStayAutomatic() {
        var user = Guid.NewGuid();
        var registry = new ManualMarkCapabilityRegistry(PathName);
        registry.Enroll(user, "web");
        Assert.That(registry.HasEnrollmentForUser(user), Is.True);
        Assert.That(registry.HasEnrollmentForUser(Guid.NewGuid()), Is.False);
        Assert.That(registry.RequiresConfirmation(user, "native"), Is.False);
    }

}
