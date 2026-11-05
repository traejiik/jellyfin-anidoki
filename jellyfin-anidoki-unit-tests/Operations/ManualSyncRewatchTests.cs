using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki_unit_tests.Characterization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Operations;

/// <summary>A manual sync through the whole pipeline, against a season that is already finished on the tracker.</summary>
[TestFixture, NonParallelizable]
public class ManualSyncRewatchTests {
    private SyncHarness _harness = null!;
    private Guid _user;
    private string _directory = null!;

    [SetUp]
    public void SetUp() {
        _harness = new SyncHarness();
        _user = Guid.NewGuid();
        _directory = OperationTestSupport.NewDirectory();
        _harness.ConfigureUser(_user, false, true, ApiName.AniList);
        _harness.SeedAll(SyncHarness.Entry(500, "Fixture Show", 12, new MyListStatus { Status = Status.Completed, NumEpisodesWatched = 12, RewatchCount = 3 }));
    }

    [TearDown]
    public void TearDown() {
        _harness.Dispose();
        Directory.Delete(_directory, true);
    }

    private async Task ManualSync(MediaBrowser.Controller.Entities.TV.Episode episode) {
        var operations = new OperationStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        Guid id = Guid.NewGuid();
        operations.Create(id, _user, OperationKind.SyncToTrackers, "x");
        var sync = new SyncProviderFromLocal(new Mock<IUserManager>().Object, _harness.Library.Library.Object, NullLoggerFactory.Instance,
            new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(), new Mock<IApplicationPaths>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, _user.ToString()) {
            ClientOverride = (provider, _) => _harness.Tracker(provider)
        };
        await sync.SyncEpisodes([episode], new OperationContext(id, _user, operations, NullLogger.Instance, default));
    }

    [Test]
    public async Task RunningItTwiceLeavesAFinishedSeasonsRewatchCountAlone() {
        var episode = _harness.Library.Episode("Fixture Show", 1, 12);

        await ManualSync(episode);
        await ManualSync(episode);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
    }

    [Test]
    public async Task PlayingTheLastEpisodeOfACompletedEntryStillCountsARewatch() {
        var episode = _harness.Library.Episode("Fixture Show", 1, 12);

        await _harness.NewUpdater().Run(episode, _user, true, null, SyncUpdateSource.Playback);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes.Select(write => write.RewatchCount), Is.EqualTo(new int?[] { 4 }));
    }
}
