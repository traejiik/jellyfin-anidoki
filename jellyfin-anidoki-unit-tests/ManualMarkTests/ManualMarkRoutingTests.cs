using System;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Helpers;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ManualMarkTests;

[TestFixture]
public class ManualMarkRoutingTests {
    [TestCase("MarkPlayedItem", "POST", true), TestCase("MarkPlayedItemLegacy", "POST", true),
     TestCase("MarkUnplayedItem", "DELETE", true), TestCase("MarkUnplayedItemLegacy", "DELETE", true),
     TestCase("ReportPlaybackStopped", "POST", false), TestCase("MarkPlayedItem", "GET", false)]
    public void OnlyVerifiedPlayedActionsCreateProvenance(string method, string verb, bool expected) {
        Assert.That(ManualMarkRequestFilter.IsPlayedAction("Jellyfin.Api.Controllers.PlaystateController", method, verb), Is.EqualTo(expected));
        Assert.That(ManualMarkRequestFilter.IsPlayedAction("Different.Controller", method, verb), Is.False);
    }
    [Test]
    public void EnrolledRequestWithholdsAdditionalUsersAndBulkChildren() {
        var actor = Guid.NewGuid();
        var source = Guid.NewGuid();
        var request = new ManualMarkRequestContext(actor, "identity", source, true, false, true);
        Assert.That(request.Observe(actor, source, true), Is.True);
        Assert.That(request.Observe(Guid.NewGuid(), Guid.NewGuid(), true), Is.True);
        Assert.That(request.SavedOwnSource, Is.True);
    }
    [Test]
    public void UnregisteredRequestPreservesAutomaticFallback() {
        var request = new ManualMarkRequestContext(Guid.NewGuid(), "identity", Guid.NewGuid(), false, false, true);
        Assert.That(request.Observe(Guid.NewGuid(), Guid.NewGuid(), true), Is.False);
    }
    [Test]
    public void DuplicateAlreadyPlayedRequestCannotCreateAnotherPrompt() {
        var actor = Guid.NewGuid();
        var item = Guid.NewGuid();
        var request = new ManualMarkRequestContext(actor, "identity", item, true, true, true);
        request.Observe(actor, item, true);
        Assert.That(request.ShouldPrompt, Is.False);
    }
    [Test]
    public void FailedOrForeignTargetLocalChangeCannotAuthorizeOwnTrackers() {
        var actor = Guid.NewGuid();
        var item = Guid.NewGuid();
        var request = new ManualMarkRequestContext(actor, "identity", item, true, false, false);
        request.Observe(Guid.NewGuid(), item, true);
        Assert.That(request.ShouldPrompt, Is.False);
    }
    [Test]
    public void ConfirmedManualNeverReducesProgressOrRestartsRewatch() {
        var anime = new Anime { MyListStatus = new MyListStatus { NumEpisodesWatched = 5, Status = Status.Watching } };
        Assert.That(ManualMarkProgressRules.Allows(anime, 4, false), Is.False);
        Assert.That(ManualMarkProgressRules.Allows(anime, 6, false), Is.True);
        anime.MyListStatus.Status = Status.Completed;
        Assert.That(ManualMarkProgressRules.Allows(anime, 6, false), Is.False);
        anime.MyListStatus.Status = Status.Rewatching;
        Assert.That(ManualMarkProgressRules.Allows(anime, 6, false), Is.False);
    }
}
