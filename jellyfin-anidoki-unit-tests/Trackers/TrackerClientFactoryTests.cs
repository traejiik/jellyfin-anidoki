using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Trackers;

/// <summary>Configuration comes from <c>Plugin.Instance</c>, set up by <see cref="SyncHarness"/> as for the flow tests.</summary>
[TestFixture, NonParallelizable]
public class TrackerClientFactoryTests {
    private SyncHarness _harness = null!;
    private readonly UserConfig _user = new();
    private readonly List<ApiName> _overrideCalls = new();

    [SetUp]
    public void SetUp() {
        _harness = new SyncHarness();
        _overrideCalls.Clear();
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private TrackerClientFactory Factory() => new(new Mock<IHttpClientFactory>().Object, new NullLoggerFactory(),
        new Mock<IServerApplicationHost>().Object, new Mock<IHttpContextAccessor>().Object, new MemoryCache(new MemoryCacheOptions()),
        new Mock<IAsyncDelayer>().Object, NullLogger.Instance) {
        Override = (provider, _) => {
            _overrideCalls.Add(provider);
            return _harness.Tracker(provider);
        }
    };

    [Test]
    public void OverrideBuildsEveryProvider() {
        var factory = Factory();

        foreach (var provider in SyncHarness.AllProviders)
            Assert.That(factory.Create(provider, _user), Is.SameAs(_harness.Tracker(provider)), provider.ToString());
        Assert.That(_overrideCalls, Is.EqualTo(SyncHarness.AllProviders));
    }

    [Test]
    public void UnconfiguredShikimoriGivesNoClientEvenWithOverride() {
        _harness.Configuration.shikimoriAppName = "";

        Assert.That(Factory().Create(ApiName.Shikimori, _user), Is.Null);
        Assert.That(_overrideCalls, Is.Empty);
    }

    [Test]
    public void UnconfiguredSimklGivesNoClientEvenWithOverride() {
        _harness.Configuration.ProviderApiAuth = [new ProviderApiAuth { Name = ApiName.Simkl, ClientId = "" }];

        Assert.That(Factory().Create(ApiName.Simkl, _user), Is.Null);
        Assert.That(_overrideCalls, Is.Empty);
    }

    [Test]
    public async Task AdaptWrapsTheSameClient() {
        var tracker = _harness.Tracker(ApiName.AniList);
        tracker.Add(SyncHarness.Entry(500, "Fixture Show", 12, null));

        var adapted = Factory().Adapt(ApiName.AniList, tracker);
        var state = await adapted.ReadEntry(new EntryRef(ApiName.AniList, 500, null), CancellationToken.None);

        Assert.That(adapted.Name, Is.EqualTo(ApiName.AniList));
        Assert.That(state!.Entry.Id, Is.EqualTo(500));
        Assert.That(tracker.Reads, Is.EqualTo(new[] { (500, false) }));
    }
}
