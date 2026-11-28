using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Simkl;
using jellyfin_anidoki_unit_tests.Characterization;
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

    private Task ManualSync(Episode episode, ApiName? only) => ManualSync([episode], only, new Mock<IAsyncDelayer>());

    private async Task ManualSync(Episode[] episodes, ApiName? only, Mock<IAsyncDelayer> delayer) {
        var operations = new OperationStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        Guid id = Guid.NewGuid();
        operations.Create(id, _user, OperationKind.SyncToTrackers, "x");
        var sync = new SyncProviderFromLocal(new Mock<IUserManager>().Object, _harness.Library.Library.Object, NullLoggerFactory.Instance,
            new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(), IdServices.Arm(), IdServices.AnimeList(),
            new MemoryCache(new MemoryCacheOptions()), delayer.Object, _user.ToString()) {
            ClientOverride = (provider, _) => _harness.Tracker(provider),
            OnlyProvider = only
        };
        await sync.SyncEpisodes(episodes, new OperationContext(id, _user, operations, NullLogger.Instance, default));
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
    public async Task SeasonsThatReachATrackerArePacedTwoSecondsApart() {
        var delayer = new Mock<IAsyncDelayer>();

        await ManualSync([_harness.Library.Episode("Fixture Show", 1, 5), _harness.Library.Episode("Fixture Show", 2, 5)], null, delayer);

        delayer.Verify(d => d.Delay(TimeSpan.FromSeconds(2)), Times.Once);
    }

    [Test]
    public async Task SeasonsOutsideTheAnimeLibrariesAreNotWaitedOn() {
        // An anime library none of the fixture's items is in: every season is left out before a tracker is asked.
        Plugin.Instance!.PluginConfiguration.AnimeLibraries = [new AnimeLibrary { LibraryId = Guid.NewGuid(), Mode = AnimeLibraryMode.Dedicated }];
        var delayer = new Mock<IAsyncDelayer>();

        await ManualSync([_harness.Library.Episode("Fixture Show", 1, 5), _harness.Library.Episode("Fixture Show", 2, 5)], null, delayer);

        delayer.Verify(d => d.Delay(TimeSpan.FromSeconds(2)), Times.Never);
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
