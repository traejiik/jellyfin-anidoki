using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Enums;
using Jellyfin.Database.Implementations.Entities;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Progress;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Operations;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Progress;

public class SeriesProgressControllerTests {
    private ProgressFixture _fixture = null!;
    private Plugin _plugin = null!;
    private OperationStore _store = null!;
    private OperationRunner _runner = null!;
    private EntryEditor _editor = null!;

    [SetUp]
    public void SetUp() {
        _fixture = new ProgressFixture();
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(item => item.PluginsPath).Returns(_fixture.Match.Directory);
        paths.SetupGet(item => item.PluginConfigurationsPath).Returns(_fixture.Match.Directory);
        paths.SetupGet(item => item.DataPath).Returns(_fixture.Match.Directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(serializer => serializer.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        _plugin = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
        _plugin.Configuration.enableUserPages = true;
        (_store, _runner) = OperationTestSupport.Create(_fixture.Match.Directory);
        _editor = new EntryEditor(_fixture.Items, _fixture.Reader, _fixture.Match.Trackers, NullLogger<EntryEditor>.Instance);
        _fixture.Shared(_fixture.Match.Season, Range(1, 48, A));
        _fixture.Match.Catalogue.Add(A, 48, new ListState(TrackerStatus.Watching, 4, false, 0));
        _fixture.Match.Trackers.Link(_fixture.Alice, new WritableTracker(_fixture.Match.Catalogue));
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    private SeriesProgressController As(User user) => new(_fixture.Reader, _editor, _runner, _store, _fixture.Match.Users.Object) {
        ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimValues.UserId, user.Id.ToString())])) }
        }
    };

    private EditRequest Request(int entry, int progress = 5) =>
        new(_fixture.Series.Id, [new EntryEditContract(ApiName.AniList, entry, null, new ShownStateContract(TrackerStatus.Watching, 4, null),
            TrackerStatus.Watching, progress, null, false)]);

    [Test]
    public async Task TheSeriesViewIsTheSignedInUsersOwn() {
        var result = await As(_fixture.Match.Alice).ReadSeries(_fixture.Series.Id, CancellationToken.None);

        Assert.That(((OkObjectResult)result.Result!).Value, Is.TypeOf<SeriesViewContract>());
    }

    [Test]
    public async Task EveryRouteIsNotFoundWhileTheUserPagesAreOff() {
        _plugin.Configuration.enableUserPages = false;
        var controller = As(_fixture.Match.Alice);

        Assert.That((await controller.ReadSeries(_fixture.Series.Id, CancellationToken.None)).Result, Is.TypeOf<NotFoundResult>());
        Assert.That((await controller.Edit(Request(A), CancellationToken.None)).Result, Is.TypeOf<NotFoundResult>());
        Assert.That(controller.ReadEdit(Guid.NewGuid()).Result, Is.TypeOf<NotFoundResult>());
    }

    [Test]
    public async Task EditingAnEntryTheUsersMatchesDoNotPointAtIsForbidden() {
        _fixture.Match.Catalogue.Add(999, 12);

        Assert.That((await As(_fixture.Match.Alice).Edit(Request(999), CancellationToken.None)).Result, Is.TypeOf<ForbidResult>());
    }

    [Test]
    public async Task AnInvalidEditIsUnprocessable() {
        Assert.That((await As(_fixture.Match.Alice).Edit(Request(A, progress: 49), CancellationToken.None)).Result, Is.TypeOf<UnprocessableEntityObjectResult>());
    }

    [Test]
    public void AnotherUsersEditIsForbiddenAndOtherOperationsAreNotFound() {
        Guid bobs = Guid.NewGuid();
        _store.Create(bobs, _fixture.Match.Bob.Id, OperationKind.Edit, "Long Show");
        Guid sync = Guid.NewGuid();
        _store.Create(sync, _fixture.Match.Alice.Id, OperationKind.SyncToTrackers, "Long Show");

        Assert.That(As(_fixture.Match.Alice).ReadEdit(bobs).Result, Is.TypeOf<ForbidResult>());
        Assert.That(As(_fixture.Match.Alice).ReadEdit(sync).Result, Is.TypeOf<NotFoundResult>());
    }

    [Test]
    public async Task ASavedEditIsAcceptedAndItsResultCanBeRead() {
        var controller = As(_fixture.Match.Alice);
        var accepted = (AcceptedResult)(await controller.Edit(Request(A), CancellationToken.None)).Result!;
        Guid id = ((OperationStarted)accepted.Value!).OperationId;
        await OperationTestSupport.WaitUntil(() => _store.Read(id) is { } view && OperationStates.IsFinished(view.State));

        var outcome = (EditOutcomeContract)((OkObjectResult)controller.ReadEdit(id).Result!).Value!;
        Assert.That(outcome.Finished, Is.True);
        Assert.That(outcome.Results.Single().Result, Is.EqualTo(EditResultKind.Saved));
    }
}
