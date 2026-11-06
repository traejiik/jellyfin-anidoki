using System;
using System.Text.Json;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;
using IgnoreException = jellyfin_anidoki.Matching.IgnoreException;
using static jellyfin_anidoki_unit_tests.Matching.MatchApiFixture;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture]
public class MatchContractsTests {
    private static readonly MatchScope Shared = new(ApiName.Simkl, "season:00000000000000000000000000000001:1");

    [Test]
    public void ResponsesUseCamelCaseWhateverJellyfinsDefaultIs() {
        string json = JsonSerializer.Serialize(new PreviewContract([new PreviewRowContract(1, "mapped", Entry(5), 1)], [], [], null, true));

        Assert.That(json, Does.Contain("\"rows\":[{\"sourceEpisode\":1,\"outcome\":\"mapped\",\"target\":{\"id\":5,"));
        Assert.That(json, Does.Contain("\"borrowedLink\":true"));
    }

    [Test]
    public void ASimklLookupSurvivesTheRoundTrip() {
        var lookup = new CrossReference(new ResolvedIds(1, 2, 3, 4), "Show", MediaKind.Series);
        var target = new MatchTarget(new EntryRef(ApiName.Simkl, 77, null, lookup), "Show");

        Assert.That(EntryContract.From(target).ToTarget(ApiName.Simkl), Is.EqualTo(target));
    }

    [Test]
    public void ARequestBecomesAMatchUnderTheRoutesProvider() {
        var (match, error) = MatchContracts.ToMatch(Shared, [Span(1, 12, 5)], [new ExceptionContract(3, "ignore", null, null), new ExceptionContract(4, "target", Entry(6), 2)], 9);

        Assert.That(error, Is.Null);
        Assert.That(match!.Ranges[0].Target.Entry, Is.EqualTo(new EntryRef(ApiName.Simkl, 5, null)));
        Assert.That(match.Exceptions, Is.EqualTo(new MatchException[] {
            new IgnoreException(3), new TargetException(4, new MatchTarget(new EntryRef(ApiName.Simkl, 6, null), "Entry 6"), 2)
        }));
        Assert.That(match.BaseSharedRevision, Is.Null, "a shared match has no base revision");
    }

    [Test]
    public void APersonalMatchIsBasedOnTheSharedRevisionGiven() {
        var (match, _) = MatchContracts.ToMatch(Shared with { Owner = Guid.NewGuid() }, [Span(1, 12, 5)], null, 9);

        Assert.That(match!.BaseSharedRevision, Is.EqualTo(9));
        Assert.That(match.Exceptions, Is.Empty);
    }

    [TestCase("movie:0000000000000000000000000000000a", "0000000000000000000000000000000a", null)]
    [TestCase("season:0000000000000000000000000000000a:0", "0000000000000000000000000000000a", 0)]
    [TestCase("season:0000000000000000000000000000000a:12", "0000000000000000000000000000000a", 12)]
    public void SourceKeysParse(string key, string id, int? season) =>
        Assert.That(SourceKeys.Parse(key), Is.EqualTo((Guid.ParseExact(id, "N"), season)));

    [TestCase("")]
    [TestCase("movie:not-a-guid")]
    [TestCase("season:0000000000000000000000000000000a")]
    [TestCase("season:0000000000000000000000000000000a:-1")]
    [TestCase("season:0000000000000000000000000000000a:1:2")]
    [TestCase("show:0000000000000000000000000000000a:1")]
    public void MalformedSourceKeysDoNotParse(string key) => Assert.That(SourceKeys.Parse(key), Is.Null);
}
