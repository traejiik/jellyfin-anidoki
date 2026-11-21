using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Playback;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Playback;

/// <summary>
/// The watch evaluator against timelines of player reports (Phase 5 design, Worked timelines). Unless a case says
/// otherwise the item is a 24-minute episode (1440 s), the server's <c>MaxResumePct</c> is 90 (1296 s needed) and
/// the client reports every 10 seconds.
/// </summary>
[TestFixture]
public class WatchEvaluatorTests {
    private const long Episode = 1440;

    public static IEnumerable<TestCaseData> Timelines() {
        yield return Case("Straight play to 92%", true, WatchRule.Measured, t => t.Start(0).Play(1325).Stop(1325));
        yield return Case("Seek from 10% to 95%", false, WatchRule.Measured, t => t.Start(0).Play(144).Seek(1368).Play(1380).Stop(1380));
        yield return Case("Skip a 90-second opening", true, WatchRule.Measured, t => t.Start(0).Play(10).Seek(100).Play(1440).Stop(1440));
        yield return Case("Pause for an hour, then play on", true, WatchRule.Measured, t => t.Start(0).Play(400).Pause(3600).Play(1440).Stop(1440));
        yield return Case("Pause for an hour and seek to 95%", false, WatchRule.Measured, t => t.Start(0).Play(400).Pause(3600).Seek(1368).Play(1380).Stop(1380));
        yield return Case("Resume from a saved 40%", true, WatchRule.Measured, t => t.Start(576, saved: 576).Play(1400).Stop(1400));
        yield return Case("Start at 40% with nothing saved", false, WatchRule.Measured, t => t.Start(576, saved: 0).Play(1400).Stop(1400));
        yield return Case("Seek back and replay", true, WatchRule.Measured, t => t.Start(0).Play(1000).Seek(500).Play(1440).Stop(1440));
        yield return Case("Unknown runtime follows Jellyfin's completion", true, WatchRule.JellyfinUnknownRuntime,
            t => t.Start(0).Play(30).Seek(1368).Play(1380).Stop(1380), runtime: null);
        yield return Case("Unknown runtime, not completed", false, WatchRule.NotPlayedToCompletion,
            t => t.Start(0).Play(30).Stop(30, completed: false), runtime: null);
        yield return Case("A 4-minute item follows Jellyfin's completion, even after a seek", true, WatchRule.JellyfinShortItem,
            t => t.Start(0).Play(20).Seek(230).Stop(235), runtime: 240);
        yield return Case("Reports out of order or doubled", true, WatchRule.Measured, t => t.Start(0).PlayUnevenly(1325).Stop(1325));
        yield return Case("A full play at 2× speed", true, WatchRule.Measured, t => t.Start(0).Play(1440, speed: 2).Stop(1440));
        yield return Case("A full play at 2× speed between Jellyfin's own reports", true, WatchRule.Measured,
            t => t.Start(0).PlayWithServerTicks(1440, speed: 2).Stop(1440));
        yield return Case("Seek from 10% to 95% between Jellyfin's own reports", false, WatchRule.Measured,
            t => t.Start(0).PlayWithServerTicks(144).Seek(1368).PlayWithServerTicks(1380).Stop(1380));
        yield return Case("Fewer than 2 progress reports, stopped at the end", true, WatchRule.StopPosition, t => t.Start(0).Play(10).Stop(1430));
        yield return Case("Fewer than 2 progress reports, stopped early", false, WatchRule.StopPosition, t => t.Start(0).Play(10).Stop(600));
        yield return Case("No reports and no stop position", true, WatchRule.JellyfinNoPosition, t => t.Stop(null));
        yield return Case("Jellyfin did not complete it", false, WatchRule.NotPlayedToCompletion, t => t.Start(0).Play(1300).Stop(1300, completed: false));
    }

    [TestCaseSource(nameof(Timelines))]
    public void ATimelineCountsOnlyWhenItWasPlayed(Func<Timeline, WatchVerdict> run, long? runtime, bool counts, WatchRule rule) {
        WatchVerdict verdict = run(new Timeline(runtime));

        Assert.That(verdict.Counts, Is.EqualTo(counts));
        Assert.That(verdict.Rule, Is.EqualTo(rule));
    }

    [Test]
    public void ASkipToTheCreditsIsCreditedTheAllowanceOnly() {
        // Worked timeline E: 164 s played and 180 s of the skip forgiven. The seek may also use the 30 s of playable
        // time carried over and the 2.5 s of the second before its report.
        WatchVerdict verdict = new Timeline(Episode).Start(0).Play(144).Seek(1380).Play(1400).Stop(1400);

        Assert.That(verdict.CreditedTicks, Is.EqualTo(Seconds(344) + TimeSpan.FromSeconds(32.5).Ticks));
        Assert.That(verdict.RequiredTicks, Is.EqualTo(Seconds(1296)));
    }

    [Test]
    public void ASeekBackNeverLowersTheCreditedPosition() {
        var timeline = new Timeline(Episode).Start(0).Play(1000);
        long before = timeline.Credited;

        timeline.Seek(500);

        Assert.That(timeline.Credited, Is.EqualTo(before));
    }

    [Test]
    public void ReportsOutOfOrderOrDoubledCreditTheSameAsStraightPlay() {
        long straight = new Timeline(Episode).Start(0).Play(1200).Credited;
        long uneven = new Timeline(Episode).Start(0).PlayUnevenly(1200).Credited;

        Assert.That(uneven, Is.EqualTo(straight));
    }

    [Test]
    public void AtMostThirtySecondsOfUnusedPlayableTimeCarriesOverToASeek() {
        // Ten minutes at normal speed leave far more than 30 s unused; a 60-s seek reported a second later may use
        // only the 30 s carried over and the 2.5 s of its own second.
        var timeline = new Timeline(Episode).Start(0).Play(600);
        long before = timeline.Played;

        timeline.Seek(660);

        Assert.That(timeline.Played - before, Is.EqualTo(TimeSpan.FromSeconds(32.5).Ticks));
    }

    [Test]
    public void TheAllowanceIsTenPercentOrThreeMinutes() {
        Assert.That(WatchEvaluator.Allowance(Seconds(1440)), Is.EqualTo(Seconds(180)));
        Assert.That(WatchEvaluator.Allowance(Seconds(7200)), Is.EqualTo(Seconds(720)));
    }

    [Test]
    public void AThresholdIsCrossedOnce() {
        var at = new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
        WatchState state = WatchEvaluator.Begin(new WatchReport(0, false, at), 0);
        var crossings = new List<long>();
        for (int second = 10; second <= 1440; second += 10) {
            (state, bool crossed) = WatchEvaluator.Report(state, new WatchReport(Seconds(second), false, at.AddSeconds(second)), Seconds(Episode), 60);
            if (crossed)
                crossings.Add(second);
        }

        Assert.That(crossings, Is.EqualTo(new long[] { 870 }));
    }

    [Test]
    public void NoThresholdIsNeverCrossed() {
        var at = new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
        WatchState state = WatchEvaluator.Begin(new WatchReport(0, false, at), 0);

        var (_, crossed) = WatchEvaluator.Report(state, new WatchReport(Seconds(10), false, at.AddSeconds(10)), Seconds(Episode));

        Assert.That(crossed, Is.False);
    }

    private static TestCaseData Case(string name, bool counts, WatchRule rule, Func<Timeline, WatchVerdict> run, long? runtime = Episode) =>
        new TestCaseData(run, runtime, counts, rule).SetName(name);

    private static long Seconds(long seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    /// <summary>Plays a timeline of reports into the evaluator, the way a client reports to Jellyfin.</summary>
    public sealed class Timeline(long? runtimeSeconds) {
        private readonly long? _runtime = runtimeSeconds is long seconds ? Seconds(seconds) : null;
        private DateTimeOffset _now = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
        private WatchState? _state;
        private long _position;

        public long Played => _state!.PlayedTicks;

        public long Credited => WatchEvaluator.Credited(_state!, _runtime ?? Seconds(Episode));

        public Timeline Start(long position, long saved = 0) {
            _position = position;
            _state = WatchEvaluator.Begin(new WatchReport(Seconds(position), false, _now), Seconds(saved));
            return this;
        }

        /// <summary>Reports every 10 s of wall-clock time until <paramref name="to"/>.</summary>
        public Timeline Play(long to, double speed = 1) {
            while (_position < to) {
                _now = _now.AddSeconds(10);
                _position = Math.Min(to, _position + (long)(10 * speed));
                Report(_position, false);
            }
            return this;
        }

        /// <summary>
        /// Plays to <paramref name="to"/> the way Jellyfin 12.4 reports it: every second Jellyfin itself reports a
        /// position estimated at normal speed, and every 10 s the client reports where it really is.
        /// </summary>
        public Timeline PlayWithServerTicks(long to, double speed = 1) {
            double actual = _position;
            long estimate = _position;
            for (int second = 1; actual < to; second++) {
                _now = _now.AddSeconds(1);
                actual = Math.Min(to, actual + speed);
                estimate = second % 10 == 0 || actual >= to ? (long)actual : estimate + 1;
                _position = estimate;
                Report(estimate, false);
            }
            return this;
        }

        /// <summary>Plays to <paramref name="to"/>, sending every report twice and swapping every third pair.</summary>
        public Timeline PlayUnevenly(long to) {
            var reports = new List<(long Position, DateTimeOffset At)>();
            while (_position < to) {
                _now = _now.AddSeconds(10);
                _position = Math.Min(to, _position + 10);
                reports.Add((_position, _now));
            }
            for (int i = 0; i < reports.Count; i++) {
                var current = reports[i];
                if (i % 3 == 2 && i + 1 < reports.Count) {
                    var next = reports[i + 1];
                    Deliver(next.Position, next.At.AddMilliseconds(-500));
                    Deliver(current.Position, next.At.AddMilliseconds(-300));
                    Deliver(next.Position, next.At);
                    i++;
                    continue;
                }
                Deliver(current.Position, current.At);
                Deliver(current.Position, current.At.AddMilliseconds(200));
            }
            return this;
        }

        /// <summary>Jumps to <paramref name="to"/>; the player reports the new position a second later.</summary>
        public Timeline Seek(long to) {
            _now = _now.AddSeconds(1);
            _position = to;
            Report(to, false);
            return this;
        }

        /// <summary>Stays paused, reporting every 10 s.</summary>
        public Timeline Pause(long seconds) {
            for (long elapsed = 0; elapsed < seconds; elapsed += 10) {
                _now = _now.AddSeconds(10);
                Report(_position, true);
            }
            return this;
        }

        public WatchVerdict Stop(long? position, bool completed = true) {
            _now = _now.AddSeconds(1);
            return WatchEvaluator.Stop(_state, position is long stop ? Seconds(stop) : null, _now, completed,
                new WatchRules(_runtime, 90, 300));
        }

        private void Report(long position, bool paused) =>
            _state = WatchEvaluator.Report(_state!, new WatchReport(Seconds(position), paused, _now), _runtime).State;

        private void Deliver(long position, DateTimeOffset at) =>
            _state = WatchEvaluator.Report(_state!, new WatchReport(Seconds(position), false, at), _runtime).State;
    }
}
