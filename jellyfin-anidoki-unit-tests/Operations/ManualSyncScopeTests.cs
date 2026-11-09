using System;
using System.IO;
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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Operations;

/// <summary>A manual sync to trackers through the whole pipeline, for every linked tracker or for one.</summary>
[TestFixture, NonParallelizable]
public class ManualSyncScopeTests {
    private SyncHarness _harness = null!;
    private Guid _user;
    private string _directory = null!;

    [SetUp]
    public void SetUp() {
        _harness = new SyncHarness();
        _user = Guid.NewGuid();
        _directory = OperationTestSupport.NewDirectory();
        _harness.ConfigureUser(_user, false, false, ApiName.AniList, ApiName.Kitsu);
        _harness.SeedAll(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12, new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 3 }));
    }

    [TearDown]
    public void TearDown() {
        _harness.Dispose();
        Directory.Delete(_directory, true);
    }

    private async Task ManualSync(Episode episode, ApiName? only) {
        var operations = new OperationStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        Guid id = Guid.NewGuid();
        operations.Create(id, _user, OperationKind.SyncToTrackers, "x");
        var sync = new SyncProviderFromLocal(new Mock<IUserManager>().Object, _harness.Library.Library.Object, NullLoggerFactory.Instance,
            new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(), new Mock<IApplicationPaths>().Object,
            new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, _user.ToString()) {
            ClientOverride = (provider, _) => _harness.Tracker(provider),
            OnlyProvider = only
        };
        await sync.SyncEpisodes([episode], new OperationContext(id, _user, operations, NullLogger.Instance, default));
    }

    [Test]
    public async Task WithoutAChoiceEveryLinkedTrackerIsWritten() {
        await ManualSync(_harness.Library.Episode("Fixture Show", 1, 5), null);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Has.Count.EqualTo(1));
        Assert.That(_harness.Tracker(ApiName.Kitsu).Writes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AChosenTrackerIsTheOnlyOneReadOrWritten() {
        await ManualSync(_harness.Library.Episode("Fixture Show", 1, 5), ApiName.Kitsu);

        Assert.That(_harness.Tracker(ApiName.Kitsu).Writes, Has.Count.EqualTo(1));
        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(_harness.Tracker(ApiName.AniList).Reads, Is.Empty);
    }

    [Test]
    public async Task ChoosingATrackerThatIsNoLongerLinkedWritesNothing() {
        await ManualSync(_harness.Library.Episode("Fixture Show", 1, 5), ApiName.Mal);

        Assert.That(_harness.Tracker(ApiName.AniList).Writes, Is.Empty);
        Assert.That(_harness.Tracker(ApiName.Kitsu).Writes, Is.Empty);
        Assert.That(_harness.Tracker(ApiName.Mal).Writes, Is.Empty);
    }
}
