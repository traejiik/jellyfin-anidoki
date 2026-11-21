using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.ManualMarks;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.LoggingTests;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Operations.OperationTestSupport;

namespace jellyfin_anidoki_unit_tests.Playback;

/// <summary>
/// Pins what Jellyfin's playback and watched-toggle events start today. Each test raises the event on a mocked
/// Jellyfin service through a started hosted service, so rebuilding the entry points changes only the two
/// factories below, never an assertion.
/// </summary>
[TestFixture, NonParallelizable]
public class TriggerCharacterizationTests {
    private string _directory = null!;
    private OperationStore _store = null!;
    private OperationRunner _runner = null!;
    private SyncHarness _harness = null!;
    private Mock<ISessionManager> _sessions = null!;
    private Mock<IUserDataManager> _userData = null!;
    private HttpContextAccessor _http = null!;
    private ManualMarkStore _marks = null!;
    private ManualMarkCapabilityRegistry _capabilities = null!;
    private NotificationEventStore _notifications = null!;
    private CapturedLogs _logs = null!;
    // Null by default: the pipeline's Debug diagnostics read Series.Children, which the mocked library cannot build.
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private IHostedService? _host;

    [SetUp]
    public void SetUp() {
        _directory = NewDirectory();
        _logs = new CapturedLogs();
        _store = new OperationStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        _runner = new OperationRunner(_store, _logs.For<OperationRunner>());
        _harness = new SyncHarness();
        _sessions = new Mock<ISessionManager>();
        _userData = new Mock<IUserDataManager>();
        _http = new HttpContextAccessor();
        _marks = new ManualMarkStore(TimeProvider.System);
        _capabilities = new ManualMarkCapabilityRegistry(AniDokiDatabase.ForDataPath(_directory), Path.Combine(_directory, "manual-capabilities.json"));
        _notifications = new NotificationEventStore();
        _harness.Configuration.watchedTickboxUpdatesProvider = true;
    }

    [TearDown]
    public async Task TearDown() {
        if (_host != null)
            await _host.StopAsync(CancellationToken.None);
        // Bounded, so a test that reintroduces a hang fails instead of hanging the whole run.
        try {
            await _runner.Drain().WaitAsync(TimeSpan.FromSeconds(10));
        } catch (TimeoutException) {
        }
        _runner.Dispose();
        _harness.Dispose();
        Directory.Delete(_directory, true);
    }

    // The only lines that change when the entry points are rebuilt.
    private IHostedService PlaybackHost(OperationRunner runner) => new SessionServerEntry(_sessions.Object, _loggerFactory,
        new Mock<IHttpClientFactory>().Object, _harness.Library.Library.Object, new Mock<IServerApplicationHost>().Object,
        _http, IdServices.Arm(), IdServices.AnimeList(), new MemoryCache(new MemoryCacheOptions()), _notifications, runner, _marks);

    private IHostedService ToggleHost() => new UserDataServerEntry(_userData.Object, _harness.Library.Library.Object, _loggerFactory,
        _http, new Mock<IServerApplicationHost>().Object, new Mock<IHttpClientFactory>().Object, new MemoryCache(new MemoryCacheOptions()),
        IdServices.Arm(), IdServices.AnimeList(), _marks, _capabilities, null!, _runner, null!);

    private async Task StartPlayback(OperationRunner? runner = null) {
        _host = PlaybackHost(runner ?? _runner);
        await _host.StartAsync(CancellationToken.None);
    }

    private async Task StartToggles() {
        _host = ToggleHost();
        await _host.StartAsync(CancellationToken.None);
    }

    private static User NewUser(string name = "viewer") => new(name, "authentication", "password-reset") { Id = Guid.NewGuid() };

    private void Configure(params Guid[] users) => _harness.Configuration.UserConfig =
        users.Select(user => new UserConfig { UserId = user, LibraryToCheck = [], UserApiAuth = [] }).ToArray();

    private void Stop(BaseItem item, bool completed, params User[] users) =>
        _sessions.Raise(s => s.PlaybackStopped += null, new PlaybackStopEventArgs { Users = [.. users], Item = item, PlayedToCompletion = completed });

    private void Toggle(Guid user, BaseItem item, bool played, UserDataSaveReason reason = UserDataSaveReason.TogglePlayed) =>
        _userData.Raise(m => m.UserDataSaved += null, new UserDataSaveEventArgs {
            UserId = user,
            Item = item,
            UserData = new UserItemData { Key = item.Id.ToString("N"), Played = played },
            SaveReason = reason
        });

    private async Task<OperationView[]> Finished(Guid user, int count) {
        await WaitUntil(() => _store.ReadForUser(user).Count(view => OperationStates.IsFinished(view.State)) >= count);
        await _runner.Drain();
        return _store.ReadForUser(user).ToArray();
    }

    // ---- Playback ----

    [Test]
    public async Task ACompletedStopRunsOnePlaybackOperation() {
        var user = NewUser();
        Configure(user.Id);
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        await StartPlayback();

        Stop(episode, true, user);

        var operation = (await Finished(user.Id, 1)).Single();
        Assert.That(operation.Kind, Is.EqualTo(OperationKind.Playback));
        Assert.That(operation.Source, Is.EqualTo("Fixture Show · episode 5"));
        Assert.That(operation.State, Is.EqualTo(OperationState.NoChange));
    }

    [Test]
    public async Task AnUnfinishedStopStartsNothing() {
        var user = NewUser();
        Configure(user.Id);
        await StartPlayback();

        Stop(_harness.Library.Episode("Fixture Show", 1, 5), false, user);
        await _runner.Drain();

        Assert.That(_store.ReadForUser(user.Id), Is.Empty);
    }

    [Test]
    public async Task AnIgnoredItemStartsNothing() {
        var user = NewUser();
        Configure(user.Id);
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        episode.Tags = ["jas-ignore"];
        await StartPlayback();

        Stop(episode, true, user);
        await _runner.Drain();

        Assert.That(_store.ReadForUser(user.Id), Is.Empty);
    }

    [Test]
    public async Task EveryUserOnTheStopGetsTheirOwnOperation() {
        var first = NewUser("first");
        var second = NewUser("second");
        Configure(first.Id, second.Id);
        await StartPlayback();

        Stop(_harness.Library.Episode("Fixture Show", 1, 5), true, first, second);

        Assert.That((await Finished(first.Id, 1)).Single().Kind, Is.EqualTo(OperationKind.Playback));
        Assert.That((await Finished(second.Id, 1)).Single().Kind, Is.EqualTo(OperationKind.Playback));
    }

    [Test]
    public async Task UnavailableStorageStartsNothingAndWarns() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        using var broken = new OperationRunner(new OperationStore(new AniDokiDatabase(Path.Combine(file, "anidoki.db")), TimeProvider.System),
            NullLogger<OperationRunner>.Instance);
        var user = NewUser();
        Configure(user.Id);
        _loggerFactory = _logs;
        await StartPlayback(broken);

        Assert.DoesNotThrow(() => Stop(_harness.Library.Episode("Fixture Show", 1, 5), true, user));

        Assert.That(_logs.At(LogLevel.Warning), Has.Length.EqualTo(1).And.Some.Contains("storage is unavailable"));
    }

    [Test]
    public async Task APlaybackWithNoConfirmedChangeKeepsThePromptAndPublishesNoToast() {
        var user = NewUser();
        Configure(user.Id);
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        var pending = _marks.CreateOrGet(new ManualMarkRequest(Guid.NewGuid(), user.Id, "key", episode.Id, "Fixture Show", 5, "Episode 5",
            DateTimeOffset.UtcNow, "fingerprint"))!;
        await StartPlayback();

        Stop(episode, true, user);
        await Finished(user.Id, 1);

        Assert.That(_marks.Read(user.Id, "key", pending.OperationId)!.State, Is.EqualTo(ManualMarkState.Pending));
        Assert.That(_notifications.Count, Is.Zero);
    }

    // ---- Watched toggle ----

    [Test]
    public async Task MarksInOneSeasonWithinASecondRunOnceForTheHighestEpisode() {
        Guid user = Guid.NewGuid();
        Configure(user);
        var three = _harness.Library.Episode("Fixture Show", 1, 3);
        var five = new Episode { Id = Guid.NewGuid(), Name = "Episode 5", IndexNumber = 5, ParentId = three.ParentId, SeasonId = three.SeasonId, SeriesId = three.SeriesId, SeriesName = "Fixture Show", ProviderIds = new Dictionary<string, string>() };
        var four = new Episode { Id = Guid.NewGuid(), Name = "Episode 4", IndexNumber = 4, ParentId = three.ParentId, SeasonId = three.SeasonId, SeriesId = three.SeriesId, SeriesName = "Fixture Show", ProviderIds = new Dictionary<string, string>() };
        await StartToggles();

        Toggle(user, three, true);
        Toggle(user, five, true);
        Toggle(user, four, true);

        var operation = (await Finished(user, 1)).Single();
        Assert.That(operation.Kind, Is.EqualTo(OperationKind.WatchedToggle));
        Assert.That(operation.Source, Is.EqualTo("Fixture Show · episode 5"));
    }

    [Test]
    public async Task MarksDoNothingWhenTheSettingIsOff() {
        _harness.Configuration.watchedTickboxUpdatesProvider = false;
        Guid user = Guid.NewGuid();
        Configure(user);
        await StartToggles();

        Toggle(user, _harness.Library.Movie("Fixture Movie"), true);
        await Task.Delay(1500);
        await _runner.Drain();

        Assert.That(_store.ReadForUser(user), Is.Empty);
    }

    [Test]
    public async Task UnmarkingCancelsThePendingPromptAndStartsNothing() {
        Guid user = Guid.NewGuid();
        Configure(user);
        var unmarked = _harness.Library.Movie("Unmarked Movie");
        var pending = _marks.CreateOrGet(new ManualMarkRequest(Guid.NewGuid(), user, "key", unmarked.Id, "Unmarked Movie", null, "Unmarked Movie",
            DateTimeOffset.UtcNow, "fingerprint"))!;
        await StartToggles();

        Toggle(user, unmarked, false);
        Toggle(user, _harness.Library.Movie("Control Movie"), true);

        var operation = (await Finished(user, 1)).Single();
        Assert.That(operation.Source, Is.EqualTo("Control Movie"));
        Assert.That(_marks.Read(user, "key", pending.OperationId)!.State, Is.EqualTo(ManualMarkState.Cancelled));
    }

    [Test]
    public async Task AnEnrolledUserWithoutARequestCaptureDefersToManualConfirmation() {
        Guid enrolled = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Configure(enrolled, other);
        Assert.That(_capabilities.Enroll(enrolled, "key"), Is.True);
        await StartToggles();

        Toggle(enrolled, _harness.Library.Movie("Deferred Movie"), true);
        Toggle(other, _harness.Library.Movie("Control Movie"), true);

        Assert.That((await Finished(other, 1)).Single().Source, Is.EqualTo("Control Movie"));
        Assert.That(_store.ReadForUser(enrolled), Is.Empty);
    }

    [Test]
    public async Task UnreadableEnrollmentDefersEveryMark() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        _capabilities = new ManualMarkCapabilityRegistry(new AniDokiDatabase(Path.Combine(file, "anidoki.db")), Path.Combine(_directory, "missing.json"));
        Assert.That(_capabilities.PersistenceUnreadable, Is.True);
        Guid user = Guid.NewGuid();
        Configure(user);
        await StartToggles();

        Toggle(user, _harness.Library.Movie("Fixture Movie"), true);
        await Task.Delay(1500);
        await _runner.Drain();

        Assert.That(_store.ReadForUser(user), Is.Empty);
    }

    [Test]
    public async Task AWithholdingRequestCaptureDefersTheMark() {
        Guid user = Guid.NewGuid();
        Configure(user);
        var deferred = _harness.Library.Movie("Deferred Movie");
        var context = new DefaultHttpContext();
        context.Items["AniDoki.ManualMarkRequest"] = new ManualMarkRequestContext(user, "key", deferred.Id, true, false, true);
        await StartToggles();

        _http.HttpContext = context;
        Toggle(user, deferred, true);
        _http.HttpContext = null;
        Toggle(user, _harness.Library.Movie("Control Movie"), true);

        Assert.That((await Finished(user, 1)).Single().Source, Is.EqualTo("Control Movie"));
    }

    [Test]
    public async Task SeasonsAndUsersRunAsSeparateBatches() {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Configure(first, second);
        await StartToggles();

        Toggle(first, _harness.Library.Episode("Show A", 1, 2), true);
        Toggle(first, _harness.Library.Episode("Show B", 1, 7), true);
        Toggle(second, _harness.Library.Episode("Show A", 1, 4), true);

        Assert.That((await Finished(first, 2)).Select(view => view.Source), Is.EquivalentTo(new[] { "Show A · episode 2", "Show B · episode 7" }));
        Assert.That((await Finished(second, 1)).Single().Source, Is.EqualTo("Show A · episode 4"));
    }

    /// <summary>
    /// The legacy queue used to stop at a failing item, leaving it and every item behind it unprocessed until the
    /// next mark restarted the loop (audit #7).
    /// </summary>
    [Test]
    public async Task AFailingWatchedMarkDoesNotHoldUpTheNextOne() {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Configure(first, second);
        Guid brokenParent = Guid.NewGuid();
        _harness.Library.Library.Setup(manager => manager.GetItemById(brokenParent)).Throws(new InvalidOperationException("library unavailable"));
        _harness.Library.Library.Setup(manager => manager.GetItemById<BaseItem>(brokenParent)).Throws(new InvalidOperationException("library unavailable"));
        // A movie, so the event handler itself never looks up a season: the batch fails when it reads the parent.
        var broken = new Movie { Id = Guid.NewGuid(), Name = "Broken", ParentId = brokenParent, Tags = [], ProviderIds = new Dictionary<string, string>() };
        await StartToggles();

        Toggle(first, broken, true);
        Toggle(second, _harness.Library.Movie("Fixture Movie"), true);

        Assert.That((await Finished(second, 1)).Single().Kind, Is.EqualTo(OperationKind.WatchedToggle));
        Assert.That(_store.ReadForUser(first), Is.Empty);
    }

    [Test]
    public async Task OnlyAToggledVideoStartsAnything() {
        Guid user = Guid.NewGuid();
        Configure(user);
        await StartToggles();

        Toggle(user, new Series { Id = Guid.NewGuid(), Name = "Not A Video" }, true);
        Toggle(user, _harness.Library.Movie("Resumed Movie"), true, UserDataSaveReason.PlaybackProgress);
        Toggle(user, _harness.Library.Movie("Control Movie"), true);

        Assert.That((await Finished(user, 1)).Single().Source, Is.EqualTo("Control Movie"));
    }
}
