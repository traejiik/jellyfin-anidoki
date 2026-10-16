using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Notifications;

namespace jellyfin_anidoki.ManualMarks;

// Only transitions hold this lock. Provider I/O belongs to the worker after TryBegin.
public sealed class ManualMarkStore(TimeProvider clock) {
    private sealed class Entry(ManualMarkRequest request) {
        public ManualMarkRequest Request { get; } = request;
        public ManualMarkState State = ManualMarkState.Pending;
        public long Revision = 1;
        public DateTimeOffset? TerminalUtc;
        public string Reason = "Confirm within two minutes to update trackers.";
        public PublicOutcome[] Outcomes = [];
        public DateTimeOffset ExpiresUtc => Request.CreatedUtc.AddMinutes(2);
        public PublicManualMarkOperation Public() => new(Request.OperationId, Revision, Request.SourceItemId,
            PublicNotificationEvent.Bounded(Request.SourceTitle), Request.SourceEpisode,
            PublicNotificationEvent.Bounded(Request.EpisodeTitle), State, ExpiresUtc, Outcomes.ToArray(), Reason);
    }
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private static bool BeforeSending(Entry entry) => entry.State is ManualMarkState.Pending or ManualMarkState.Queued;
    private static bool Owns(Entry entry, Guid userId, string key) => entry.Request.UserId == userId && entry.Request.AuthenticationKey == key;
    private void Transition(Entry entry, ManualMarkState state, string reason) {
        entry.State = state;
        entry.Revision++;
        entry.Reason = reason;
        if (state is not (ManualMarkState.Pending or ManualMarkState.Queued or ManualMarkState.Sending))
            entry.TerminalUtc = clock.GetUtcNow();
    }
    private void Prune() {
        var now = clock.GetUtcNow();
        foreach (var entry in _entries.Values)
            if (BeforeSending(entry) && now >= entry.ExpiresUtc)
                Transition(entry, ManualMarkState.Expired, "Confirmation expired. Trackers were not updated.");
        foreach (var key in _entries.Where(pair => pair.Value.TerminalUtc is { } time && now - time >= TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray())
            _entries.Remove(key);
    }
    internal PublicManualMarkOperation? CreateOrGet(ManualMarkRequest request) {
        lock (_lock) {
            Prune();
            var existing = _entries.Values.FirstOrDefault(e => Owns(e, request.UserId, request.AuthenticationKey) &&
                e.Request.SourceItemId == request.SourceItemId && e.State is ManualMarkState.Pending or ManualMarkState.Queued or ManualMarkState.Sending);
            if (existing != null)
                return existing.Public();
            // Terminal recovery is bounded too; overflow never evicts actionable work.
            if (_entries.Count >= 1000 || _entries.Values.Count(e => Owns(e, request.UserId, request.AuthenticationKey) && BeforeSending(e)) >= 10)
                return null;
            if (_entries.ContainsKey(request.OperationId))
                return null;
            var entry = new Entry(request);
            _entries.Add(request.OperationId, entry);
            return entry.Public();
        }
    }
    public PublicManualMarkOperation[] Read(Guid userId, string key) {
        lock (_lock) { Prune(); return _entries.Values.Where(e => Owns(e, userId, key)).OrderBy(e => e.Request.CreatedUtc).Select(e => e.Public()).ToArray(); }
    }
    public PublicManualMarkOperation? Read(Guid userId, string key, Guid operationId) {
        lock (_lock) { Prune(); return _entries.TryGetValue(operationId, out var entry) && Owns(entry, userId, key) ? entry.Public() : null; }
    }
    public bool TryQueue(Guid userId, string key, Guid operationId, long revision) {
        lock (_lock) {
            Prune();
            if (!_entries.TryGetValue(operationId, out var entry) || !Owns(entry, userId, key) || entry.State != ManualMarkState.Pending || entry.Revision != revision)
                return false;
            Transition(entry, ManualMarkState.Queued, "Waiting to update trackers.");
            return true;
        }
    }
    internal bool TryBegin(Guid operationId, out ManualMarkRequest? request) {
        lock (_lock) {
            Prune();
            request = null;
            if (!_entries.TryGetValue(operationId, out var entry) || entry.State != ManualMarkState.Queued)
                return false;
            Transition(entry, ManualMarkState.Sending, "Updating trackers…");
            request = entry.Request;
            return true;
        }
    }
    public bool Dismiss(Guid userId, string key, Guid operationId, long revision) {
        lock (_lock) {
            Prune();
            if (!_entries.TryGetValue(operationId, out var entry) || !Owns(entry, userId, key) || !BeforeSending(entry) || entry.Revision != revision)
                return false;
            Transition(entry, ManualMarkState.Dismissed, "Dismissed. Trackers were not updated.");
            return true;
        }
    }
    public void CancelForSource(Guid userId, Guid sourceItemId) {
        lock (_lock) {
            Prune();
            foreach (var entry in _entries.Values.Where(e => e.Request.UserId == userId && e.Request.SourceItemId == sourceItemId)) {
                if (BeforeSending(entry))
                    Transition(entry, ManualMarkState.Cancelled, "Marked unwatched. Trackers were not updated.");
                else if (entry.State == ManualMarkState.Sending && !entry.Reason.Contains("cannot undo", StringComparison.Ordinal)) { entry.Revision++; entry.Reason = "Marked unwatched after updating started; AniDoki cannot undo a transmitted tracker update."; }
            }
        }
    }
    public void CancelPending(string reason) {
        lock (_lock) { Prune(); foreach (var entry in _entries.Values.Where(BeforeSending)) Transition(entry, ManualMarkState.Cancelled, reason); }
    }
    internal PublicManualMarkOperation? RecordSuppressed(ManualMarkRequest request, string reason) {
        lock (_lock) {
            var created = CreateOrGet(request);
            if (created == null)
                return null;
            var entry = _entries[created.OperationId];
            if (entry.State == ManualMarkState.Pending)
                Transition(entry, ManualMarkState.Skipped, reason);
            return entry.Public();
        }
    }
    internal ManualMarkRequest? QueuedRequest(Guid operationId) {
        lock (_lock) { Prune(); return _entries.TryGetValue(operationId, out var entry) && entry.State == ManualMarkState.Queued ? entry.Request : null; }
    }
    internal void RestorePending(Guid operationId) {
        lock (_lock) {
            Prune();
            if (_entries.TryGetValue(operationId, out var entry) && entry.State == ManualMarkState.Queued)
                Transition(entry, ManualMarkState.Pending, "Busy. Confirm again to queue this update.");
        }
    }
    internal void Supersede(Guid userId, Guid sourceItemId) {
        lock (_lock) {
            Prune();
            foreach (var entry in _entries.Values.Where(e => e.Request.UserId == userId && e.Request.SourceItemId == sourceItemId && BeforeSending(e)))
                Transition(entry, ManualMarkState.Superseded, "Completed playback updated trackers. No further confirmation is needed.");
        }
    }
    internal void Complete(Guid operationId, ManualMarkState state, PublicOutcome[] outcomes, string reason) {
        if (state is ManualMarkState.Pending or ManualMarkState.Queued or ManualMarkState.Sending)
            throw new ArgumentException("Completion must be terminal.", nameof(state));
        lock (_lock) {
            if (!_entries.TryGetValue(operationId, out var entry) || entry.State != ManualMarkState.Sending)
                return;
            entry.Outcomes = outcomes.ToArray();
            var unmarked = entry.Reason.Contains("cannot undo", StringComparison.Ordinal) ? " " + entry.Reason : "";
            Transition(entry, state, reason + unmarked);
        }
    }
}
