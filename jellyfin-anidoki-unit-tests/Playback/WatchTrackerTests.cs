using System;
using jellyfin_anidoki.Playback;
using jellyfin_anidoki_unit_tests.Operations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Playback;

[TestFixture]
public class WatchTrackerTests {
    private static readonly long Second = TimeSpan.TicksPerSecond;
    private MutableTime _clock = null!;
    private Mock<IUserDataManager> _userData = null!;
    private WatchTracker _tracker = null!;
    private User _user = null!;
    private Episode _episode = null!;

    [SetUp]
    public void SetUp() {
        _clock = new MutableTime(new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero));
        _userData = new Mock<IUserDataManager>();
        _tracker = Tracker(_clock, _userData);
        _user = NewUser();
        _episode = new Episode { Id = Guid.NewGuid(), Name = "Episode 5", RunTimeTicks = 1440 * Second };
    }

    /// <summary>A tracker on a server with Jellyfin's default resume settings (90%, 300 s).</summary>
    internal static WatchTracker Tracker(TimeProvider clock, Mock<IUserDataManager> userData) {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(manager => manager.Configuration).Returns(new ServerConfiguration { MaxResumePct = 90, MinResumeDurationSeconds = 300 });
        return new WatchTracker(clock, configuration.Object, userData.Object);
    }

    private static User NewUser(string name = "viewer") => new(name, "authentication", "password-reset") { Id = Guid.NewGuid() };

    /// <summary>Starts at <paramref name="from"/> and reports every 10 s up to <paramref name="to"/>.</summary>
    private void Play(User user, string cycle, long from, long to) {
        _tracker.Observe(user, _episode, cycle, from * Second, false, false);
        for (long position = from + 10; position <= to; position += 10) {
            _clock.Now = _clock.Now.AddSeconds(10);
            _tracker.Observe(user, _episode, cycle, position * Second, false, true);
        }
    }

    [Test]
    public void ACompletionPercentageIsReportedOnceWhenTheWatchPassesIt() {
        _tracker.Observe(_user, _episode, "s", 0, false, false, 60);
        var crossings = new System.Collections.Generic.List<long>();
        for (long position = 10; position <= 1400; position += 10) {
            _clock.Now = _clock.Now.AddSeconds(10);
            if (_tracker.Observe(_user, _episode, "s", position * Second, false, true, 60))
                crossings.Add(position);
        }

        Assert.That(crossings, Is.EqualTo(new[] { 870L }), "60% of 1440 s is 864 s, first passed by the report at 870 s");
    }

    [Test]
    public void NoPercentageOrNoRuntimeNeverCrosses() {
        var noRuntime = new Episode { Id = Guid.NewGuid(), Name = "Episode 6" };
        bool crossed = false;
        _tracker.Observe(_user, _episode, "none", 0, false, false);
        _tracker.Observe(_user, noRuntime, "runtime", 0, false, false, 60);
        for (long position = 10; position <= 1400; position += 10) {
            _clock.Now = _clock.Now.AddSeconds(10);
            crossed |= _tracker.Observe(_user, _episode, "none", position * Second, false, true);
            crossed |= _tracker.Observe(_user, noRuntime, "runtime", position * Second, false, true, 60);
        }

        Assert.That(crossed, Is.False);
    }

    [Test]
    public void APercentageAboveTheServersOwnIsIgnored() {
        _tracker.Observe(_user, _episode, "s", 0, false, false, 95);
        bool crossed = false;
        for (long position = 10; position <= 1440; position += 10) {
            _clock.Now = _clock.Now.AddSeconds(10);
            crossed |= _tracker.Observe(_user, _episode, "s", position * Second, false, true, 95);
        }

        Assert.That(crossed, Is.False);
    }

    [Test]
    public void AWatchedSessionCountsAndASkippedOneDoesNot() {
        Play(_user, "watched", 0, 1400);
        Play(_user, "skipped", 0, 100);
        _tracker.Observe(_user, _episode, "skipped", 1380 * Second, false, true);

        Assert.That(_tracker.Stop(_user, _episode, "watched", 1400 * Second, true).Counts, Is.True);
        Assert.That(_tracker.Stop(_user, _episode, "skipped", 1400 * Second, true).Counts, Is.False);
    }

    [Test]
    public void TwoUsersOnOneSessionHaveTheirOwnWatch() {
        var other = NewUser("other");
        Play(_user, "shared", 0, 1400);
        _tracker.Observe(other, _episode, "shared", 0, false, false);
        _tracker.Observe(other, _episode, "shared", 10 * Second, false, true);
        _tracker.Observe(other, _episode, "shared", 1400 * Second, false, true);

        Assert.That(_tracker.Stop(_user, _episode, "shared", 1400 * Second, true).Counts, Is.True);
        Assert.That(_tracker.Stop(other, _episode, "shared", 1400 * Second, true).Counts, Is.False);
    }

    [Test]
    public void ReportsAfterTheStopAreIgnoredAndARepeatedStopGetsTheSameVerdict() {
        Play(_user, "s", 0, 100);
        var first = _tracker.Stop(_user, _episode, "s", 1400 * Second, true);

        Play(_user, "s", 100, 400);
        var second = _tracker.Stop(_user, _episode, "s", 1400 * Second, true);

        Assert.That(first.Counts, Is.False);
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void TheSavedPositionMakesAStartAResume() {
        _userData.Setup(manager => manager.GetUserData(_user, _episode)).Returns(new UserItemData { Key = "key", PlaybackPositionTicks = 576 * Second });

        Play(_user, "resume", 578, 1400);

        Assert.That(_tracker.Stop(_user, _episode, "resume", 1400 * Second, true).Counts, Is.True);
    }

    [Test]
    public void AStopWithoutAPlaySessionIsJudgedOnItsOwn() {
        var verdict = _tracker.Stop(_user, _episode, null, 1430 * Second, true);

        Assert.That(verdict.Rule, Is.EqualTo(WatchRule.StopPosition));
        Assert.That(verdict.Counts, Is.True);
        Assert.That(_tracker.Count, Is.Zero);
    }

    [Test]
    public void AnIdleWatchIsDroppedAfterSixHours() {
        Play(_user, "idle", 0, 100);
        Assert.That(_tracker.Count, Is.EqualTo(1));

        _clock.Now = _clock.Now + WatchTracker.IdleLimit - TimeSpan.FromSeconds(1);
        _tracker.Observe(_user, _episode, "other", 0, false, false);
        Assert.That(_tracker.Count, Is.EqualTo(2));

        _clock.Now = _clock.Now.AddMinutes(2);
        _tracker.Observe(_user, _episode, "another", 0, false, false);
        Assert.That(_tracker.Count, Is.EqualTo(2), "the idle watch is gone, the two recent ones stay");
    }

    [Test]
    public void AStoppedWatchIsForgottenAfterTenMinutes() {
        Play(_user, "s", 0, 1400);
        _tracker.Stop(_user, _episode, "s", 1400 * Second, true);

        _clock.Now = _clock.Now + WatchTracker.StoppedMemory + TimeSpan.FromSeconds(1);
        _tracker.Observe(_user, _episode, "next", 0, false, false);

        Assert.That(_tracker.Count, Is.EqualTo(1));
    }
}
