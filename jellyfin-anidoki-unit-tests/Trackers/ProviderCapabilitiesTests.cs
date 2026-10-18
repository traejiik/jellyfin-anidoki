using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Trackers;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Trackers;

[TestFixture]
public class ProviderCapabilitiesTests {
    // Values come from what each client does today: which fields its conversion fills and which arguments its
    // update call sends.
    [TestCase(ApiName.Mal, true, true, false, true, false)]
    [TestCase(ApiName.AniList, true, true, true, true, false)]
    [TestCase(ApiName.Kitsu, true, true, true, true, false)]
    [TestCase(ApiName.Annict, false, false, false, false, true)]
    [TestCase(ApiName.Shikimori, true, true, true, false, true)]
    [TestCase(ApiName.Simkl, true, false, false, false, true)]
    public void CapabilitiesMatchTheClients(ApiName provider, bool tracksProgress, bool supportsRewatch,
        bool reportsRewatchCount, bool supportsDates, bool resolvesByCrossIds) {
        Assert.That(ProviderCapabilities.For(provider),
            Is.EqualTo(new ProviderCapabilities(tracksProgress, supportsRewatch, reportsRewatchCount, supportsDates, resolvesByCrossIds)));
    }
}
