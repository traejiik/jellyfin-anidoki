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
using jellyfin_anidoki.Playback;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.LoggingTests;
using jellyfin_anidoki_unit_tests.Operations;
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
    private IHostedService? _second;
    private MutableTime _clock = null!;
    private CompletionLedger _ledger = null!;

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
        _clock = new MutableTime(DateTimeOffset.UtcNow);
        _ledger = Ledger(_directory, _clock);
    }

    [TearDown]
    public async Task TearDown() {
        if (_host != null)
            await _host.StopAsync(CancellationToken.None);
        if (_second != null)
            await _second.StopAsync(CancellationToken.None);
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
    private IHostedService PlaybackHost(OperationRunner runner) => new PlaybackListener(_sessions.Object, _loggerFactory,
        new Mock<IHttpClientFactory>().Object, _harness.Library.Library.Object, new Mock<IServerApplicationHost>().Object,
        _http, IdServices.Arm(), IdServices.AnimeList(), new MemoryCache(new MemoryCacheOptions()), _notifications, runner, _ledger,
        WatchTrackerTests.Tracker(_clock, _userData), _marks);

    private IHostedService ToggleHost() => new UserDataServerEntry(_userData.Object, _harness.Library.Library.Object, _loggerFactory,
        _http, new Mock<IServerApplicationHost>().Object, new Mock<IHttpClientFactory>().Object, new MemoryCache(new MemoryCacheOptions()),
        IdServices.Arm(), IdServices.AnimeList(), _marks, _capabilities, null!, _runner, _ledger, null!);

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

    private void StopSession(string playSessionId, BaseItem item, params User[] users) =>
        _sessions.Raise(s => s.PlaybackStopped += null,
            new PlaybackStopEventArgs { Users = [.. users], Item = item, PlayedToCompletion = true, PlaySessionId = playSessionId });

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

    // ---- Watch evaluation (D2–D4) ----

    private const long Second = TimeSpan.TicksPerSecond;

    /// <summary>A 24-minute episode, so the measured rule applies (the fixture's own episodes have no runtime).</summary>
    private BaseItem TimedEpisode() {
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        episode.RunTimeTicks = 1440 * Second;
        return episode;
    }

    private void Report(string playSessionId, BaseItem item, long seconds, bool start, params User[] users) {
        var report = new PlaybackProgressEventArgs { Users = [.. users], Item = item, PlaybackPositionTicks = seconds * Second, PlaySessionId = playSessionId };
        if (start)
            _sessions.Raise(s => s.PlaybackStart += null, report);
        else
            _sessions.Raise(s => s.PlaybackProgress += null, report);
    }

    /// <summary>Starts at <paramref name="from"/> and reports every 10 s of the clock up to <paramref name="to"/>.</summary>
    private void Play(string playSessionId, BaseItem item, long from, long to, User user) {
        Report(playSessionId, item, from, true, user);
        for (long position = from + 10; position <= to; position += 10) {
            _clock.Now = _clock.Now.AddSeconds(10);
            Report(playSessionId, item, position, false, user);
        }
    }

    private void StopAt(string playSessionId, BaseItem item, long seconds, User user) =>
        _sessions.Raise(s => s.PlaybackStopped += null, new PlaybackStopEventArgs {
            Users = [user],
            Item = item,
            PlayedToCompletion = true,
            PlaySessionId = playSessionId,
            PlaybackPositionTicks = seconds * Second
        });

    [Test]
    public async Task AMeasuredPlayToTheEndRunsOnePlaybackOperation() {
        var user = NewUser();
        Configure(user.Id);
        var episode = TimedEpisode();
        await StartPlayback();

        Play("watched", episode, 0, 1400, user);
        StopAt("watched", episode, 1400, user);

        Assert.That((await Finished(user.Id, 1)).Single().Kind, Is.EqualTo(OperationKind.Playback));
    }

    [Test]
    public async Task ASeekToTheCreditsStartsNothing() {
        var user = NewUser();
        Configure(user.Id);
        _loggerFactory = _logs;
        var episode = TimedEpisode();
        await StartPlayback();

        Play("skipped", episode, 0, 140, user);
        _clock.Now = _clock.Now.AddSeconds(1);
        Report("skipped", episode, 1380, false, user);
        StopAt("skipped", episode, 1400, user);
        await _runner.Drain();

        Assert.That(_store.ReadForUser(user.Id), Is.Empty);
        Assert.That(_logs.At(LogLevel.Information), Has.Some.Contains("Did not count Episode 5 as watched"));
    }

    [Test]
    public async Task AReplayCountsOnlyWhenItWasPlayedThrough() {
        var user = NewUser();
        Configure(user.Id);
        var episode = TimedEpisode();
        await StartPlayback();

        Play("first", episode, 0, 1400, user);
        StopAt("first", episode, 1400, user);
        await Finished(user.Id, 1);
        Play("short replay", episode, 0, 60, user);
        StopAt("short replay", episode, 1400, user);
        Play("full replay", episode, 0, 1400, user);
        StopAt("full replay", episode, 1400, user);

        Assert.That(await Finished(user.Id, 2), Has.Length.EqualTo(2));
        await Task.Delay(200);
        Assert.That(_store.ReadForUser(user.Id), Has.Count.EqualTo(2));
    }

    // ---- Completion percentage (D3) ----

    private void Threshold(User user, int? percent) =>
        _harness.Configuration.UserConfig!.Single(config => config.UserId == user.Id).TrackerThresholdPercent = percent;

    [Test]
    public async Task PassingTheCompletionPercentageUpdatesTrackersWhilePlayingAndTheStopAddsNothing() {
        var user = NewUser();
        Configure(user.Id);
        Threshold(user, 60);
        _loggerFactory = _logs;
        var episode = TimedEpisode();
        await StartPlayback();

        Play("early", episode, 0, 880, user);
        var during = await Finished(user.Id, 1);
        Assert.That(during.Single().Kind, Is.EqualTo(OperationKind.Playback), "the update ran before the stop");

        Play("early", episode, 890, 1380, user);
        StopAt("early", episode, 1380, user);
        await _runner.Drain();

        Assert.That(_store.ReadForUser(user.Id), Has.Count.EqualTo(1));
        Assert.That(_logs.At(LogLevel.Debug), Has.Some.Contains("Dropped a duplicate stop for Episode 5"));
    }

    [Test]
    public async Task ThePercentageNeverMarksTheItemPlayedInJellyfin() {
        var user = NewUser();
        Configure(user.Id);
        Threshold(user, 60);
        var episode = TimedEpisode();
        await StartPlayback();

        Play("early", episode, 0, 880, user);
        await Finished(user.Id, 1);

        // Reading the saved position is the only call AniDōki makes; it never saves user data, so Played is untouched.
        _userData.Verify(manager => manager.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()), Times.AtLeastOnce);
        _userData.VerifyNoOtherCalls();
    }

    [Test]
    public async Task TwoUsersOnOneSessionFollowTheirOwnPercentage() {
        var early = NewUser("early");
        var late = NewUser("late");
        Configure(early.Id, late.Id);
        Threshold(early, 60);
        var episode = TimedEpisode();
        await StartPlayback();

        Report("shared", episode, 0, true, early, late);
        for (long position = 10; position <= 880; position += 10) {
            _clock.Now = _clock.Now.AddSeconds(10);
            Report("shared", episode, position, false, early, late);
        }
        await Finished(early.Id, 1);
        Assert.That(_store.ReadForUser(late.Id), Is.Empty, "the other user has no percentage, so waits for the stop");

        for (long position = 890; position <= 1400; position += 10) {
            _clock.Now = _clock.Now.AddSeconds(10);
            Report("shared", episode, position, false, early, late);
        }
        _sessions.Raise(s => s.PlaybackStopped += null, new PlaybackStopEventArgs {
            Users = [early, late],
            Item = episode,
            PlayedToCompletion = true,
            PlaySessionId = "shared",
            PlaybackPositionTicks = 1400 * Second
        });

        Assert.That(await Finished(late.Id, 1), Has.Length.EqualTo(1));
        await _runner.Drain();
        Assert.That(_store.ReadForUser(early.Id), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task StoppingBeforeJellyfinsOwnPercentageKeepsTheEarlierUpdate() {
        var user = NewUser();
        Configure(user.Id);
        Threshold(user, 60);
        var episode = TimedEpisode();
        await StartPlayback();

        Play("early", episode, 0, 1000, user);
        await Finished(user.Id, 1);
        _sessions.Raise(s => s.PlaybackStopped += null, new PlaybackStopEventArgs {
            Users = [user],
            Item = episode,
            PlayedToCompletion = false,
            PlaySessionId = "early",
            PlaybackPositionTicks = 1000 * Second
        });
        await _runner.Drain();

        Assert.That(_store.ReadForUser(user.Id), Has.Count.EqualTo(1));
    }

    // ---- Completion ledger (D5) ----

    [Test]
    public async Task TwoStopsForOnePlaySessionRunOneOperation() {
        var user = NewUser();
        Configure(user.Id);
        _loggerFactory = _logs;
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        await StartPlayback();

        StopSession("session-1", episode, user);
        await Task.Delay(3);
        StopSession("session-1", episode, user);

        Assert.That((await Finished(user.Id, 1)).Single().Kind, Is.EqualTo(OperationKind.Playback));
        await Task.Delay(200);
        Assert.That(_store.ReadForUser(user.Id), Has.Count.EqualTo(1));
        Assert.That(_logs.At(LogLevel.Debug), Has.Some.Contains("Dropped a duplicate stop for Episode 5"));
    }

    [Test]
    public async Task TwoUsersOnOnePlaySessionEachGetOneOperation() {
        var first = NewUser("first");
        var second = NewUser("second");
        Configure(first.Id, second.Id);
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        await StartPlayback();

        StopSession("shared", episode, first, second);
        StopSession("shared", episode, first, second);

        Assert.That(await Finished(first.Id, 1), Has.Length.EqualTo(1));
        Assert.That(await Finished(second.Id, 1), Has.Length.EqualTo(1));
        await Task.Delay(200);
        Assert.That(_store.ReadForUser(first.Id), Has.Count.EqualTo(1));
        Assert.That(_store.ReadForUser(second.Id), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AToggleWithinSixHoursOfAPlaybackIsDroppedAndOneAfterRuns() {
        var user = NewUser();
        Configure(user.Id);
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        await StartPlayback();
        _second = ToggleHost();
        await _second.StartAsync(CancellationToken.None);

        StopSession("session-1", episode, user);
        await Finished(user.Id, 1);
        _clock.Now += TimeSpan.FromMinutes(10);
        Toggle(user.Id, episode, true);
        await Task.Delay(1500);
        await _runner.Drain();
        Assert.That(_store.ReadForUser(user.Id).Select(view => view.Kind), Is.EqualTo(new[] { OperationKind.Playback }));

        _clock.Now += TimeSpan.FromHours(7);
        Toggle(user.Id, episode, true);

        var operations = await Finished(user.Id, 2);
        Assert.That(operations.Select(view => view.Kind), Is.EquivalentTo(new[] { OperationKind.Playback, OperationKind.WatchedToggle }));
    }

    [Test]
    public async Task UnmarkingReopensTheWindowForTheNextToggle() {
        var user = NewUser();
        Configure(user.Id);
        var episode = _harness.Library.Episode("Fixture Show", 1, 5);
        await StartPlayback();
        _second = ToggleHost();
        await _second.StartAsync(CancellationToken.None);

        StopSession("session-1", episode, user);
        await Finished(user.Id, 1);
        Toggle(user.Id, episode, false);
        Toggle(user.Id, episode, true);

        var operations = await Finished(user.Id, 2);
        Assert.That(operations.Select(view => view.Kind), Is.EquivalentTo(new[] { OperationKind.Playback, OperationKind.WatchedToggle }));
    }
}
