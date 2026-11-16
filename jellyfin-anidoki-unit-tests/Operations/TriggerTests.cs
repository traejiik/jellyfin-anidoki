using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki_unit_tests.Characterization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Operations;

[TestFixture, NonParallelizable]
public class TriggerTests {
    private string _directory = null!;
    private OperationStore _store = null!;
    private OperationRunner _runner = null!;

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        (_store, _runner) = Create(_directory);
    }

    [TearDown]
    public async Task TearDown() {
        // Bounded, so a test that reintroduces a hang fails instead of hanging the whole run.
        try {
            await _runner.Drain().WaitAsync(TimeSpan.FromSeconds(10));
        } catch (TimeoutException) {
        }
        _runner.Dispose();
        Directory.Delete(_directory, true);
    }

    private SessionServerEntry Sessions(SyncHarness harness) => new(new Mock<ISessionManager>().Object, NullLoggerFactory.Instance,
        new Mock<IHttpClientFactory>().Object, harness.Library.Library.Object, new Mock<IServerApplicationHost>().Object,
        new HttpContextAccessor(), IdServices.Arm(), IdServices.AnimeList(), new MemoryCache(new MemoryCacheOptions()),
        new NotificationEventStore(), _runner);

    [Test]
    public async Task FinishedPlaybackRunsAsAnOperation() {
        using var harness = new SyncHarness();
        var user = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        harness.ConfigureUser(user.Id, false, false);
        // No provider IDs and no linked trackers, so the sync completes without any network call.
        var episode = harness.Library.Episode("Fixture Show", 1, 5);

        Sessions(harness).PlaybackStopped(null, new PlaybackStopEventArgs { Users = [user], Item = episode, PlayedToCompletion = true });
        await _runner.Drain();

        var operation = _store.ReadForUser(user.Id).Single();
        Assert.That(operation.Kind, Is.EqualTo(OperationKind.Playback));
        Assert.That(operation.Source, Is.EqualTo("Fixture Show · episode 5"));
        Assert.That(operation.State, Is.EqualTo(OperationState.NoChange));
    }

    [Test]
    public async Task UnfinishedPlaybackStartsNothing() {
        using var harness = new SyncHarness();
        var user = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        var episode = harness.Library.Episode("Fixture Show", 1, 5);

        Sessions(harness).PlaybackStopped(null, new PlaybackStopEventArgs { Users = [user], Item = episode, PlayedToCompletion = false });
        await _runner.Drain();

        Assert.That(_store.ReadForUser(user.Id), Is.Empty);
    }

    /// <summary>
    /// The legacy queue used to stop at a failing item, leaving it and every item behind it unprocessed until the
    /// next mark restarted the loop (audit #7).
    /// </summary>
    [Test]
    public async Task AFailingWatchedMarkDoesNotHoldUpTheNextOne() {
        using var harness = new SyncHarness();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        harness.Configuration.UserConfig = [
            new UserConfig { UserId = first, LibraryToCheck = [], UserApiAuth = [] },
            new UserConfig { UserId = second, LibraryToCheck = [], UserApiAuth = [] }
        ];
        Guid brokenSeason = Guid.NewGuid();
        harness.Library.Library.Setup(manager => manager.GetItemById(brokenSeason)).Throws(new InvalidOperationException("library unavailable"));
        harness.Library.Library.Setup(manager => manager.GetItemById<BaseItem>(brokenSeason)).Throws(new InvalidOperationException("library unavailable"));
        var broken = new Episode { Id = Guid.NewGuid(), Name = "Broken", IndexNumber = 1, ParentId = brokenSeason, Tags = [] };
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Fixture Movie", Tags = [], ProviderIds = new Dictionary<string, string>() };
        var marks = new TaskProcessMarkedMedia(NullLoggerFactory.Instance, harness.Library.Library.Object, new MemoryCache(new MemoryCacheOptions()),
            new HttpContextAccessor(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpClientFactory>().Object,
            IdServices.Arm(), IdServices.AnimeList(), new Mock<IAsyncDelayer>().Object, _runner);
        marks.AddToUpdateList((first, brokenSeason, broken));
        marks.AddToUpdateList((second, null, movie));

        await marks.RunUpdate();

        Assert.That(_store.ReadForUser(first), Is.Empty);
        Assert.That(_store.ReadForUser(second).Single().Kind, Is.EqualTo(OperationKind.WatchedToggle));
    }

    [Test]
    public async Task AConfirmedMarkRunsAsAnOperationWithItsOwnId() {
        var marks = new ManualMarkStore(TimeProvider.System);
        var queue = new ManualMarkQueue();
        var service = new ManualMarkService(marks, new Mock<IUserManager>().Object, new Mock<ILibraryManager>().Object,
            new Mock<IUserDataManager>().Object, NullLoggerFactory.Instance, new HttpContextAccessor(), new Mock<IServerApplicationHost>().Object,
            new Mock<IHttpClientFactory>().Object, IdServices.Arm(), IdServices.AnimeList(), new MemoryCache(new MemoryCacheOptions()), null!);
        using var worker = new ManualMarkWorker(queue, marks, service, _runner, NullLogger<ManualMarkWorker>.Instance);
        Guid user = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        var created = marks.CreateOrGet(new ManualMarkRequest(id, user, "key", Guid.NewGuid(), "Fixture Show", 5, "Episode 5",
            DateTimeOffset.UtcNow, "fingerprint"))!;
        Assert.That(marks.TryQueue(user, "key", id, created.Revision), Is.True);

        await worker.StartAsync(CancellationToken.None);
        Assert.That(queue.TryWrite(id), Is.True);
        await WaitUntil(() => _store.Read(id) is { } view && OperationStates.IsFinished(view.State));
        await worker.StopAsync(CancellationToken.None);

        // The user no longer exists, so the mark is cancelled before anything is sent.
        var operation = _store.Read(id)!;
        Assert.That(operation.Kind, Is.EqualTo(OperationKind.ConfirmedManual));
        Assert.That(operation.State, Is.EqualTo(OperationState.Cancelled));
        Assert.That(operation.Source, Is.EqualTo("Fixture Show · episode 5"));
        Assert.That(marks.Read(user, "key", id)!.State, Is.EqualTo(ManualMarkState.Cancelled));
    }

    [Test]
    public async Task AMarkWithoutUserDataIsCancelledAsNotPlayed() {
        var marks = new ManualMarkStore(TimeProvider.System);
        var queue = new ManualMarkQueue();
        var jellyfinUser = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode 5", IndexNumber = 5, Tags = [] };
        var users = new Mock<IUserManager>();
        users.Setup(manager => manager.GetUserById(jellyfinUser.Id)).Returns(jellyfinUser);
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetItemById(episode.Id)).Returns(episode);
        // A loose mock returns no user data, which Jellyfin itself never does: it creates an unplayed row on read.
        var userData = new Mock<IUserDataManager>();
        var service = new ManualMarkService(marks, users.Object, library.Object, userData.Object, NullLoggerFactory.Instance,
            new HttpContextAccessor(), new Mock<IServerApplicationHost>().Object, new Mock<IHttpClientFactory>().Object,
            IdServices.Arm(), IdServices.AnimeList(), new MemoryCache(new MemoryCacheOptions()), null!);
        using var worker = new ManualMarkWorker(queue, marks, service, _runner, NullLogger<ManualMarkWorker>.Instance);
        Guid id = Guid.NewGuid();
        var created = marks.CreateOrGet(new ManualMarkRequest(id, jellyfinUser.Id, "key", episode.Id, "Fixture Show", 5, "Episode 5",
            DateTimeOffset.UtcNow, "fingerprint"))!;
        Assert.That(marks.TryQueue(jellyfinUser.Id, "key", id, created.Revision), Is.True);

        await worker.StartAsync(CancellationToken.None);
        Assert.That(queue.TryWrite(id), Is.True);
        await WaitUntil(() => _store.Read(id) is { } view && OperationStates.IsFinished(view.State));
        await worker.StopAsync(CancellationToken.None);

        // Missing user data reads as not played, so nothing was sent and the mark says so.
        userData.Verify(manager => manager.GetUserData(jellyfinUser, episode), Times.Once);
        Assert.That(_store.Read(id)!.State, Is.EqualTo(OperationState.Cancelled));
        var mark = marks.Read(jellyfinUser.Id, "key", id)!;
        Assert.That(mark.State, Is.EqualTo(ManualMarkState.Cancelled));
        Assert.That(mark.Reason, Is.EqualTo("The item, access or tracking settings changed. Trackers were not updated."));
    }
}
