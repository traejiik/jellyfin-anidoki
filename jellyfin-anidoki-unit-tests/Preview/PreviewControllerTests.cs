using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Enums;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Preview;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki_unit_tests.Matching;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Preview;

[TestFixture, NonParallelizable]
public class PreviewControllerTests {
    private string _directory = null!;
    private OperationStore _store = null!;
    private OperationRunner _runner = null!;
    private PreviewStore _rows = null!;
    private User _user = null!;
    private Mock<IUserManager> _users = null!;
    private Mock<ILibraryManager> _library = null!;
    private PreviewController _controller = null!;

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        (_store, _runner) = Create(_directory);
        _rows = new PreviewStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        _user = new User("viewer", "authentication", "password-reset") { Id = Guid.NewGuid() };
        _users = new Mock<IUserManager>();
        _users.Setup(manager => manager.GetUserById(_user.Id)).Returns(_user);
        _library = new Mock<ILibraryManager>();
        _library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem>());
        // The controller reads the user's linked trackers from the plugin configuration.
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        paths.SetupGet(p => p.DataPath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration {
            UserConfig = [new UserConfig {
                UserId = _user.Id,
                LibraryToCheck = [],
                UserApiAuth = [new UserApiAuth { Name = ApiName.AniList, AccessToken = "accessToken", RefreshToken = "refreshToken" }]
            }]
        });
        _ = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
        _controller = Controller(_store, _rows);
    }

    [TearDown]
    public async Task TearDown() {
        try {
            await _runner.Drain().WaitAsync(TimeSpan.FromSeconds(10));
        } catch (TimeoutException) {
        }
        _runner.Dispose();
        Directory.Delete(_directory, true);
    }

    private PreviewController Controller(OperationStore store, PreviewStore rows) =>
        new(_runner, store, rows, new MatchStore(Plugin.Instance!.Database, TimeProvider.System), new FixedWatchState(), _users.Object,
            _library.Object, NullLoggerFactory.Instance, new Mock<IHttpClientFactory>().Object, new Mock<IServerApplicationHost>().Object,
            new HttpContextAccessor(), new Mock<IApplicationPaths>().Object, new MemoryCache(new MemoryCacheOptions()));

    private ActionResult<OperationStarted> Start(string? provider = TrackerChoice.All, SyncAction action = SyncAction.UpdateProvider) =>
        _controller.Start(_user.Id, action, provider);

    private static Guid IdOf(ActionResult<OperationStarted> result) => ((OperationStarted)((ObjectResult)result.Result!).Value!).OperationId;

    private static int? StatusOf<T>(ActionResult<T> result) => (result.Result as ObjectResult)?.StatusCode ?? (result.Result as StatusCodeResult)?.StatusCode;

    [Test]
    public async Task StartingAPreviewRunsItInTheBackgroundAndItCanBeRead() {
        var result = Start();

        Assert.That(result.Result, Is.InstanceOf<AcceptedResult>());
        await _runner.Drain();
        PreviewView view = _controller.Read(IdOf(result)).Value!;
        Assert.That(view.Operation.Kind, Is.EqualTo(OperationKind.PreviewToTrackers));
        Assert.That(view.Operation.State, Is.EqualTo(OperationState.Succeeded));
        Assert.That(view.Operation.Source, Is.EqualTo("All linked trackers"));
        Assert.That(view.Operation.Message, Does.StartWith("Previewed 0 seasons"));
        Assert.That(view.RowCounts, Is.Empty);
        Assert.That(view.Applicable, Is.True);
    }

    [Test]
    public async Task OneLinkedTrackerNamesTheRun() {
        var result = Start("AniList");

        await _runner.Drain();
        Assert.That(_store.Read(IdOf(result))!.Source, Is.EqualTo("AniList"));
    }

    [Test]
    public void AnUnlinkedTrackerIsRejected() =>
        Assert.That(Start("Kitsu").Result, Is.InstanceOf<BadRequestObjectResult>());

    [Test]
    public void TrackerToJellyfinCannotBePreviewedYet() =>
        Assert.That(Start("AniList", SyncAction.UpdateJellyfin).Result, Is.InstanceOf<BadRequestObjectResult>());

    [Test]
    public void AnUnknownUserIsRejected() =>
        Assert.That(_controller.Start(Guid.NewGuid(), SyncAction.UpdateProvider, TrackerChoice.All).Result, Is.InstanceOf<BadRequestObjectResult>());

    [Test]
    public async Task ASecondPreviewWhileOneIsActiveReturnsTheFirst() {
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
    public async Task AnActiveSyncToTrackersAnswersConflictWithItsOperation() {
        ActionResult<OperationStarted> result;
        Guid sync;
        using (await SyncExecutionGate.Shared.Enter(_user.Id)) {
            sync = _runner.Enqueue(_user.Id, OperationKind.SyncToTrackers, "x", _ => Task.CompletedTask)!.OperationId;
            result = Start();
        }

        Assert.That(result.Result, Is.InstanceOf<ConflictObjectResult>());
        Assert.That(IdOf(result), Is.EqualTo(sync));
        await _runner.Drain();
    }

    [Test]
    public void UnavailableStorageAnswers503() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        var broken = new AniDokiDatabase(Path.Combine(file, "anidoki.db"));
        PreviewController controller = Controller(new OperationStore(broken, TimeProvider.System), new PreviewStore(broken, TimeProvider.System));

        Assert.That(StatusOf(controller.Start(_user.Id, SyncAction.UpdateProvider, TrackerChoice.All)), Is.EqualTo(503));
        Assert.That(StatusOf(controller.Read(Guid.NewGuid())), Is.EqualTo(503));
        Assert.That(StatusOf(controller.Rows(Guid.NewGuid(), null, null)), Is.EqualTo(503));
    }

    [Test]
    public void AnotherKindOfOperationIsNotAPreview() {
        Guid id = Guid.NewGuid();
        _store.Create(id, _user.Id, OperationKind.SyncToTrackers, "x");

        Assert.That(_controller.Read(id).Result, Is.InstanceOf<NotFoundResult>());
        Assert.That(_controller.Rows(id, null, null).Result, Is.InstanceOf<NotFoundResult>());
    }

    [TestCase(-1, 50)]
    [TestCase(0, 0)]
    [TestCase(0, 201)]
    public void AnOutOfRangePageIsRejected(int offset, int limit) =>
        Assert.That(_controller.Rows(Guid.NewGuid(), null, null, offset, limit).Result, Is.InstanceOf<BadRequestObjectResult>());

    [Test]
    public async Task RowsArePagedAndFiltered() {
        var result = Start();
        await _runner.Drain();
        Guid id = IdOf(result);
        foreach (var (seq, kind) in new[] { (0, PreviewRowKind.Change), (1, PreviewRowKind.Change), (2, PreviewRowKind.NoChange) })
            _rows.Add(id, new PreviewRow(seq, PreviewDirection.ToTrackers, ApiName.AniList, "season:x:1", Guid.Empty, "Show · season 1", 5,
                "500", "Entry 500", 5, kind, "progress", null, null, null, false, "1-5", null, null, kind == PreviewRowKind.Change,
                kind == PreviewRowKind.Change, null));

        PreviewPage page = _controller.Rows(id, PreviewRowKind.Change, null, 0, 1).Value!;

        Assert.That(page.Total, Is.EqualTo(2));
        Assert.That(page.Rows, Has.Count.EqualTo(1));
        Assert.That(_controller.Read(id).Value!.RowCounts, Is.EqualTo(new Dictionary<string, int> { ["Change"] = 2, ["NoChange"] = 1 }));
    }
}
