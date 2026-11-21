using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace jellyfin_anidoki.Playback;

/// <summary>
/// Keeps one in-memory watch per (user, item, play session) and feeds it to <see cref="WatchEvaluator"/>. A watch
/// ends at its stop, or after <see cref="IdleLimit"/> without a report, and is never stored; only the completion the
/// listener records from its verdict is.
/// </summary>
public sealed class WatchTracker(TimeProvider clock, IServerConfigurationManager configuration, IUserDataManager userData) {
    /// <summary>A watch with no report for this long is dropped.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(6);

    /// <summary>A stopped watch is remembered this long, so late reports and a repeated stop are recognised.</summary>
    public static readonly TimeSpan StoppedMemory = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<(Guid User, Guid Item, string Cycle), Entry> _watches = new();
    private readonly object _sweepLock = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    /// <summary>How many watches are held, stopped ones included.</summary>
    public int Count => _watches.Count;

    /// <summary>Adds a start or progress report. Reports with no position, and reports after the stop, are ignored.</summary>
    public void Observe(User user, BaseItem item, string cycle, long? positionTicks, bool isPaused, bool isProgress) {
        DateTimeOffset now = clock.GetUtcNow();
        Sweep(now);
        if (positionTicks is not long position)
            return;
        var report = new WatchReport(position, isPaused, now);
        var entry = _watches.GetOrAdd((user.Id, item.Id, cycle), _ => new Entry { LastSeen = now });
        lock (entry) {
            if (entry.Verdict != null)
                return;
            entry.LastSeen = now;
            entry.State = entry.State == null
                ? WatchEvaluator.Begin(report, SavedPosition(user, item), isProgress)
                : isProgress ? WatchEvaluator.Report(entry.State, report, item.RunTimeTicks).State : entry.State;
        }
    }

    /// <summary>Ends the watch and judges its stop. A repeated stop for the same watch gets the first verdict again.</summary>
    /// <param name="cycle">The play session, or null when the client sent none; the stop is then judged on its own.</param>
    public WatchVerdict Stop(User user, BaseItem item, string? cycle, long? positionTicks, bool playedToCompletion) {
        DateTimeOffset now = clock.GetUtcNow();
        Sweep(now);
        if (cycle == null)
            return WatchEvaluator.Stop(null, positionTicks, now, playedToCompletion, Rules(item));
        var entry = _watches.GetOrAdd((user.Id, item.Id, cycle), _ => new Entry { LastSeen = now });
        lock (entry) {
            entry.LastSeen = now;
            entry.Verdict ??= WatchEvaluator.Stop(entry.State, positionTicks, now, playedToCompletion, Rules(item));
            return entry.Verdict;
        }
    }

    private WatchRules Rules(BaseItem item) {
        var server = configuration.Configuration;
        return new WatchRules(item.RunTimeTicks, server.MaxResumePct, server.MinResumeDurationSeconds);
    }

    private long? SavedPosition(User user, BaseItem item) => userData.GetUserData(user, item)?.PlaybackPositionTicks;

    private void Sweep(DateTimeOffset now) {
        lock (_sweepLock) {
            if (now < _nextSweep)
                return;
            _nextSweep = now + SweepInterval;
        }
        foreach (KeyValuePair<(Guid User, Guid Item, string Cycle), Entry> pair in _watches) {
            Entry entry = pair.Value;
            bool expired;
            lock (entry)
                expired = now - entry.LastSeen >= (entry.Verdict != null ? StoppedMemory : IdleLimit);
            if (expired)
                _watches.TryRemove(pair);
        }
    }

    private sealed class Entry {
        public WatchState? State;
        public WatchVerdict? Verdict;
        public DateTimeOffset LastSeen;
    }
}
