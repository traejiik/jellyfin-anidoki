using System;
using System.Collections.Concurrent;

namespace jellyfin_anidoki.Playback;

/// <summary>
/// Picks which playback progress reports get a Debug line. Clients report every second or so, which buried everything
/// else in the AniDōki log, so a playing session gets a line when it first reports, on a pause or resume, on a seek, and
/// once a minute in between.
/// </summary>
internal sealed class ProgressLogGate {
    internal const long EverySeconds = 60;

    /// <summary>A jump of more than this between two reports is a seek, not playback.</summary>
    internal const long SeekSeconds = 30;

    /// <summary>A stop that never arrived leaves its session behind; past this many, the gate starts over.</summary>
    private const int MaxSessions = 1000;

    private readonly ConcurrentDictionary<string, Seen> _sessions = new();

    public bool ShouldLog(string session, long seconds, bool paused) {
        if (!_sessions.TryGetValue(session, out Seen previous)) {
            if (_sessions.Count >= MaxSessions)
                _sessions.Clear();
            _sessions[session] = new Seen(seconds, seconds, paused);
            return true;
        }
        bool log = paused != previous.Paused
            || Math.Abs(seconds - previous.Last) > SeekSeconds
            || Math.Abs(seconds - previous.Logged) >= EverySeconds;
        _sessions[session] = new Seen(log ? seconds : previous.Logged, seconds, paused);
        return log;
    }

    public void Forget(string session) => _sessions.TryRemove(session, out _);

    /// <param name="Logged">The position of the last report that got a line.</param>
    /// <param name="Last">The position of the last report.</param>
    private readonly record struct Seen(long Logged, long Last, bool Paused);
}
