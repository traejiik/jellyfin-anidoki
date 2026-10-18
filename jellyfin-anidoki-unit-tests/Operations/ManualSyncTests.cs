using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Enums;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki_unit_tests.Characterization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Operations;

[TestFixture, NonParallelizable]
public class ManualSyncTests {
    private string _directory = null!;
    private OperationStore _store = null!;
    private OperationRunner _runner = null!;
    private User _user = null!;
    private SyncOperationsController _controller = null!;

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        (_store, _runner) = Create(_directory);
        _user = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        var users = new Mock<IUserManager>();
        users.Setup(manager => manager.GetUserById(_user.Id)).Returns(_user);
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem>());
        _controller = new SyncOperationsController(_runner, _store, users.Object, library.Object, NullLoggerFactory.Instance,
            new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(),
            new Mock<IApplicationPaths>().Object, new Mock<IUserDataManager>().Object, new MemoryCache(new MemoryCacheOptions()));
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

    private ActionResult<OperationStarted> Start(SyncAction action = SyncAction.UpdateProvider) =>
        _controller.StartSync(ApiName.AniList, _user.Id, SyncHelper.Status.Both, action);

    private static Guid IdOf(ActionResult<OperationStarted> result) => ((OperationStarted)((ObjectResult)result.Result!).Value!).OperationId;

    /// <summary>
    /// Versions 1.1.0 to 1.2.0 entered the user's gate in SyncFromLocal and again for every item, so the first item
    /// waited forever. Running an item through a SyncToTrackers operation must finish.
    /// </summary>
    [Test]
    public async Task ASyncToTrackersRunFinishesAfterUpdatingAnEpisode() {
        var library = new LibraryFixture();
        var episode = library.Episode("Fixture Show", 1, 5);
        var sync = new SyncProviderFromLocal(new Mock<IUserManager>().Object, library.Library.Object, NullLoggerFactory.Instance,
            new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(),
            new Mock<IApplicationPaths>().Object, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object, _user.Id.ToString());

        var handle = _runner.Enqueue(_user.Id, OperationKind.SyncToTrackers, "test", operation => sync.SyncEpisodes([episode], operation))!;
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var view = _store.Read(handle.OperationId)!;
        Assert.That(OperationStates.IsFinished(view.State), Is.True);
        Assert.That(view.Done, Is.EqualTo(1));
        Assert.That(view.Total, Is.EqualTo(1));
    }

    [Test]
    public async Task StartingASyncAnswersAcceptedAndRunsInTheBackground() {
        var result = Start();

        Assert.That(result.Result, Is.InstanceOf<AcceptedResult>());
        await _runner.Drain();
        var view = _store.Read(IdOf(result))!;
        Assert.That(view.Kind, Is.EqualTo(OperationKind.SyncToTrackers));
        Assert.That(view.State, Is.EqualTo(OperationState.NoChange));
        Assert.That(view.Message, Does.Contain("0 seasons"));
    }

    [Test]
    public async Task ASecondRunWhileOneIsActiveReturnsTheFirst() {
        ActionResult<OperationStarted> first;
        ActionResult<OperationStarted> second;
        using (await SyncExecutionGate.Shared.Enter(_user.Id)) {
            first = Start();
            second = Start();
        }

        Assert.That(second.Result, Is.InstanceOf<ConflictObjectResult>());
        Assert.That(IdOf(second), Is.EqualTo(IdOf(first)));
        await _runner.Drain();
    }

    [Test]
    public async Task TrackerToJellyfinRunsAsItsOwnKind() {
        var result = Start(SyncAction.UpdateJellyfin);

        await _runner.Drain();
        var view = _store.Read(IdOf(result))!;
        Assert.That(view.Kind, Is.EqualTo(OperationKind.SyncFromTrackers));
        Assert.That(OperationStates.IsFinished(view.State), Is.True);
    }

    [Test]
    public void AnUnknownUserIsRejected() =>
        Assert.That(_controller.StartSync(ApiName.AniList, Guid.NewGuid(), SyncHelper.Status.Both, SyncAction.UpdateProvider).Result,
            Is.InstanceOf<BadRequestObjectResult>());

    [Test]
    public async Task OperationsCanBeReadAndUnknownOnesAreNotFound() {
        var result = Start();
        await _runner.Drain();

        Assert.That(((OkObjectResult)_controller.ReadOperation(IdOf(result)).Result!).Value, Is.InstanceOf<OperationView>());
        Assert.That(_controller.ReadOperation(Guid.NewGuid()).Result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task AQueuedRunCanBeCancelledAndAFinishedOneCannot() {
        Guid id;
        using (await SyncExecutionGate.Shared.Enter(_user.Id)) {
            id = IdOf(Start());
            Assert.That(_controller.CancelOperation(id).Result, Is.InstanceOf<AcceptedResult>());
        }
        await _runner.Drain();

        Assert.That(_store.Read(id)!.State, Is.EqualTo(OperationState.Cancelled));
        Assert.That(_controller.CancelOperation(id).Result, Is.InstanceOf<ConflictObjectResult>());
    }
}
