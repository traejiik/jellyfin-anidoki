using System;
using System.Collections.Generic;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Matching;

/// <summary>
/// Precedence, resolved the way the match resolver (PR 11) will: choose the effective match, and only without one use
/// automatic resolution, which a marker stands in for here.
/// </summary>
[TestFixture]
public class MatchPrecedenceTests {
    private static readonly Guid X = Guid.NewGuid();
    private static readonly Guid Y = Guid.NewGuid();
    private static readonly SourceResolution Automatic = new Unresolved("automatic-marker", null, []);

    private readonly Dictionary<MatchScope, Match> _store = new();

    [SetUp]
    public void SetUp() => _store.Clear();

    private static MatchScope Scope(Guid? owner = null) => new(ApiName.AniList, SourceKey, owner);

    private void Save(Guid? owner, int entry) => _store[Scope(owner)] = new Match(Scope(owner), [Range(1, 12, entry)], [], 1);

    private Match? Effective(Guid user, bool linked = true) =>
        MatchPrecedence.Effective(_store.GetValueOrDefault(Scope(user)), _store.GetValueOrDefault(Scope()), linked);

    private SourceResolution Resolve(Guid user, bool linked = true, params (int Id, int Total)[] readable) =>
        Effective(user, linked) is { } match ? RangeResolver.Resolve(match, 5, "Show", Entries(readable)) : Automatic;

    private static int PlayedEntry(SourceResolution resolution) => ((Resolved)resolution).Played.Entry.Id;

    [Test]
    public void PersonalWinsOverShared() {
        Save(null, A);
        Save(X, B);

        var resolution = Resolve(X, readable: [(A, 12), (B, 12)]);

        Assert.That(PlayedEntry(resolution), Is.EqualTo(B));
        Assert.That(((Resolved)resolution).By, Is.EqualTo(ResolvedBy.PersonalMatch));
    }

    [Test]
    public void SharedWinsOverAutomatic() {
        Save(null, A);

        var resolution = Resolve(X, readable: [(A, 12)]);

        Assert.That(PlayedEntry(resolution), Is.EqualTo(A));
        Assert.That(((Resolved)resolution).By, Is.EqualTo(ResolvedBy.SharedMatch));
    }

    [Test]
    public void AutomaticAppliesWithoutAnyMatch() => Assert.That(Resolve(X), Is.SameAs(Automatic));

    [Test]
    public void OneUsersOverrideDoesNotAffectAnother() {
        Save(null, A);
        Save(X, B);

        Assert.That(PlayedEntry(Resolve(Y, readable: [(A, 12), (B, 12)])), Is.EqualTo(A));
    }

    [Test]
    public void AUsersOverrideWithoutASharedMatchLeavesOthersAutomatic() {
        Save(X, B);

        Assert.That(Resolve(Y), Is.SameAs(Automatic));
    }

    [Test]
    public void AnUnlinkedProviderLeavesAnOverrideDormant() {
        Save(null, A);
        Save(X, B);

        Assert.That(PlayedEntry(Resolve(X, linked: false, readable: [(A, 12), (B, 12)])), Is.EqualTo(A));
        Assert.That(_store, Does.ContainKey(Scope(X)));
        Assert.That(PlayedEntry(Resolve(X, linked: true, readable: [(A, 12), (B, 12)])), Is.EqualTo(B));
    }

    [Test]
    public void AnUnreadablePersonalTargetIsUnresolvedAndDoesNotFallBack() {
        Save(null, A);
        Save(X, B);

        var resolution = Resolve(X, readable: [(A, 12)]);

        Assert.That(((Unresolved)resolution).Reason, Is.EqualTo("match-target-unreadable"));
        Assert.That(resolution, Is.Not.SameAs(Automatic));
    }

    [Test]
    public void AnUnreadableSharedTargetIsUnresolvedAndDoesNotFallBack() {
        Save(null, A);

        Assert.That(((Unresolved)Resolve(X)).Reason, Is.EqualTo("match-target-unreadable"));
    }
}
