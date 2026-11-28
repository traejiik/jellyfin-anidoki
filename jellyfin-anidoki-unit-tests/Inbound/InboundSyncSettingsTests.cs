using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Trackers.AniList;
using jellyfin_anidoki_unit_tests.Trackers.Kitsu;
using jellyfin_anidoki_unit_tests.Trackers.Mal;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Inbound;

/// <summary>A sync from trackers before any user has saved settings reads nothing instead of throwing.</summary>
public class InboundSyncSettingsTests {
    private InboundHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new InboundHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [TestCase(ApiName.AniList)]
    [TestCase(ApiName.Kitsu)]
    [TestCase(ApiName.Mal)]
    public async Task NoUserListReadsNothing(ApiName provider) {
        Plugin.Instance!.PluginConfiguration.UserConfig = null;
        Sync sync = _harness.NewSync(provider: provider);
        sync.ListSourceOverride = null;

        Assert.That(await sync.ReadList(_harness.UserId), Is.Null);
    }
}
