using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Resolution.ResolutionTestSupport;

namespace jellyfin_anidoki_unit_tests.Resolution;

[TestFixture]
public class RelationChainSegmenterTests {
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Entries 1..n, each a sequel of the one before, with the given lengths.</summary>
    private static void SeedCours(FakeTracker fake, params int[] lengths) {
        for (int index = 0; index < lengths.Length; index++) {
            int id = index + 1;
            var related = id < lengths.Length ? new[] { SyncHarness.Related(RelationType.Sequel, id + 1) } : [];
            fake.Add(SyncHarness.Entry(id, $"cour {id}", lengths[index], null, related));
        }
    }

    private static async Task<(RelationChainSegmenter Segmenter, ChainLink FirstCour, FakeTracker Fake)> Cours(ApiName provider, params int[] lengths) {
        var (tracker, fake) = ReadOnly(provider);
        SeedCours(fake, lengths);
        var first = ChainLink.Of((await tracker.ReadEntry(RefFor(provider, 1), None))!);
        fake.Reads.Clear();
        return (new RelationChainSegmenter(tracker, NullLogger.Instance), first, fake);
    }

    [Test]
    public async Task FollowSequelsReachesTheRequestedSeason() {
        var (tracker, fake) = ReadOnly(ApiName.Mal);
        SeedCours(fake, 12, 12, 12);

        var season = await new RelationChainSegmenter(tracker, NullLogger.Instance).FollowSequels(RefFor(ApiName.Mal, 1), 2, None);

        Assert.That(season!.Entry.Id, Is.EqualTo(3));
        Assert.That(season.Titles?.Main, Is.EqualTo("cour 3"));
        // Three reads, as the season lookup this replaced made: the start's relations, the middle's relations, the season.
        Assert.That(fake.Reads, Is.EqualTo(new[] { (1, true), (2, true), (3, false) }));
    }

    [Test]
    public async Task FollowSequelsFailsWithoutASequel() {
        var (tracker, fake) = ReadOnly(ApiName.Mal);
        fake.Add(SyncHarness.Entry(1, "cour 1", 12, null));

        var season = await new RelationChainSegmenter(tracker, NullLogger.Instance).FollowSequels(RefFor(ApiName.Mal, 1), 2, None);

        Assert.That(season, Is.Null);
    }

    // The previous season must not be returned in place of a sequel that cannot be read.
    [Test]
    public async Task FollowSequelsFailsWhenASequelCannotBeRead() {
        var (tracker, fake) = ReadOnly(ApiName.Mal);
        SeedCours(fake, 12, 12);
        fake.FailingIds.Add(2);

        var segmenter = new RelationChainSegmenter(tracker, NullLogger.Instance);

        Assert.That(await segmenter.FollowSequels(RefFor(ApiName.Mal, 1), 1, None), Is.Null);
        Assert.That(await segmenter.FollowSequel(RefFor(ApiName.Mal, 1), None), Is.Null);
    }

    // Episode 30 of four 12-episode cours belongs to cour 3; overshooting to cour 4 writes progress to the wrong entry.
    [Test]
    public async Task WalkAdvancesOneSequelPerCour() {
        var (segmenter, first, fake) = await Cours(ApiName.Mal, 12, 12, 12, 12);

        var walk = await segmenter.Walk(first, 30, None);

        Assert.That(walk.Target!.Entry.Id, Is.EqualTo(3));
        Assert.That(walk.EpisodesBefore, Is.EqualTo(24));
        Assert.That(walk.FailureReason, Is.Null);
        Assert.That(walk.EarlierParts.Select(part => (part.Entry.Id, part.TargetEpisode, part.Role)),
            Is.EqualTo(new[] { (1, 12, TargetRole.EarlierPart), (2, 12, TargetRole.EarlierPart) }));
        Assert.That(walk.EarlierParts.Select(part => part.Title), Is.EqualTo(new[] { "cour 1", "cour 2" }));
        Assert.That(fake.Reads.Select(read => read.Id), Has.No.Member(4));
    }

    // The episode lies past the last cour: no target, but the cour passed is still completed, as the walk did.
    [Test]
    public async Task WalkFailsPastTheLastCourButKeepsPassedCours() {
        var (segmenter, first, _) = await Cours(ApiName.Mal, 12, 12);

        var walk = await segmenter.Walk(first, 30, None);

        Assert.That(walk.Target, Is.Null);
        Assert.That(walk.EpisodesBefore, Is.EqualTo(12));
        Assert.That(walk.EarlierParts.Select(part => (part.Entry.Id, part.TargetEpisode)), Is.EqualTo(new[] { (1, 12) }));
        Assert.That(walk.FailureReason, Is.EqualTo("no-cour-covers-episode"));
    }

    // A cour still airing reports 0 episodes; the walk can't measure past it, so it is the target.
    [Test]
    public async Task WalkStopsAtCourOfUnknownLength() {
        var (segmenter, first, fake) = await Cours(ApiName.Mal, 12, 0, 12);

        var walk = await segmenter.Walk(first, 20, None);

        Assert.That(walk.Target!.Entry.Id, Is.EqualTo(2));
        Assert.That(walk.EpisodesBefore, Is.EqualTo(12));
        Assert.That(fake.Reads.Select(read => read.Id), Has.No.Member(3));
    }

    // Each passed cour must be written by its own alternative ID; reusing the first cour's finds nothing for later ones.
    [Test]
    public async Task EarlierPartsCarryTheirOwnAlternativeId() {
        var (segmenter, first, _) = await Cours(ApiName.Shikimori, 12, 12, 12);

        var walk = await segmenter.Walk(first, 30, None);

        Assert.That(walk.EarlierParts.Select(part => part.Entry.AlternativeId), Is.EqualTo(new[] { "alt-1", "alt-2" }));
        Assert.That(walk.Target!.Entry.AlternativeId, Is.EqualTo("alt-3"));
    }

    [Test]
    public async Task WalkFailsWhenRelationsCannotBeRead() {
        var (segmenter, first, fake) = await Cours(ApiName.Mal, 12, 12);
        fake.FailingIds.Add(1);

        var walk = await segmenter.Walk(first, 15, None);

        Assert.That(walk.Target, Is.Null);
        Assert.That(walk.FailureReason, Is.EqualTo("sequel-fetch-failed"));
        Assert.That(walk.EarlierParts, Is.Empty);
    }

    [Test]
    public async Task WalkIgnoresNonSequelRelations() {
        var (tracker, fake) = ReadOnly(ApiName.Mal);
        fake.Add(SyncHarness.Entry(1, "cour 1", 12, null, SyncHarness.Related(RelationType.Side_Story, 2)));
        fake.Add(SyncHarness.Entry(2, "side story", 12, null));
        var first = ChainLink.Of((await tracker.ReadEntry(RefFor(ApiName.Mal, 1), None))!);

        var walk = await new RelationChainSegmenter(tracker, NullLogger.Instance).Walk(first, 15, None);

        Assert.That(walk.Target, Is.Null);
        Assert.That(walk.FailureReason, Is.EqualTo("no-cour-covers-episode"));
    }

    [Test]
    public async Task SegmenterNeverWrites() {
        var (segmenter, first, fake) = await Cours(ApiName.AniList, 12, 12, 0);

        await segmenter.FollowSequel(first.Entry, None);
        await segmenter.FollowSequels(first.Entry, 2, None);
        await segmenter.Walk(first, 15, None);
        await segmenter.Walk(first, 40, None);

        Assert.That(fake.Writes, Is.Empty);
    }
}
