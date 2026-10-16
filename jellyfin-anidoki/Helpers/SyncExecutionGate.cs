#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace jellyfin_anidoki.Helpers;

public sealed class SyncExecutionGate {
    public static SyncExecutionGate Shared { get; } = new();
    private sealed class Entry { public readonly SemaphoreSlim Semaphore = new(1, 1); public int References; }
    private sealed class Lease(Action release) : IDisposable {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    public async Task<IDisposable> Enter(Guid userId, CancellationToken cancellation = default) {
        Entry entry;
        lock (_lock) {
            if (!_entries.TryGetValue(userId, out entry!))
                _entries.Add(userId, entry = new());
            entry.References++;
        }
        try { await entry.Semaphore.WaitAsync(cancellation); } catch { Release(userId, entry, acquired: false); throw; }
        return new Lease(() => Release(userId, entry, acquired: true));
    }
    private void Release(Guid userId, Entry entry, bool acquired) {
        lock (_lock) {
            if (acquired)
                entry.Semaphore.Release();
            if (--entry.References == 0) { _entries.Remove(userId); entry.Semaphore.Dispose(); }
        }
    }
}
