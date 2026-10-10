#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace jellyfin_anidoki.Notifications;

// Ephemeral presentation delivery. No provider calls, persistent history or destructive reads.
public class NotificationEventStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private long _sequence;
    private sealed class Partition
    {
        public readonly List<NotificationEvent> Events = [];
        public long Floor;
        public long LastSequence;
    }
    private readonly Dictionary<(Guid, string), Partition> _partitions = [];
    public NotificationEventStore() : this(() => DateTimeOffset.UtcNow) { }
    internal NotificationEventStore(Func<DateTimeOffset> clock) => _clock = clock;
    internal int Count { get { lock (_gate) return _partitions.Values.Sum(p => p.Events.Count); } }
    internal int PartitionCount { get { lock (_gate) return _partitions.Count; } }
    private string Cursor => _epoch + ":" + _sequence.ToString(CultureInfo.InvariantCulture);

    public virtual void Publish(NotificationEvent item)
    {
        if (item.UserId == Guid.Empty || string.IsNullOrWhiteSpace(item.SessionId) ||
            !item.ProviderOutcomes.Any(o => o.Kind == OutcomeKind.Confirmed && o.MeaningfulChange)) return;
        lock (_gate)
        {
            PruneLocked();
            var key = (item.UserId, item.SessionId);
            if (!_partitions.TryGetValue(key, out var partition))
            {
                if (_partitions.Count >= 256) _partitions.Remove(_partitions.MinBy(p => p.Value.LastSequence).Key);
                _partitions[key] = partition = new();
            }
            if (partition.Events.Any(e => e.OperationId == item.OperationId)) return;
            partition.Events.Add(item with { Sequence = ++_sequence, CreatedUtc = _clock(), ProviderOutcomes = item.ProviderOutcomes.ToArray() });
            partition.LastSequence = _sequence;
            while (partition.Events.Count > 100) RemoveOldest(partition);
            while (_partitions.Values.Sum(p => p.Events.Count) > 2048)
                RemoveOldest(_partitions.Values.Where(p => p.Events.Count != 0).MinBy(p => p.Events[0].Sequence)!);
        }
    }
    private static void RemoveOldest(Partition partition)
    {
        partition.Floor = partition.Events[0].Sequence;
        partition.Events.RemoveAt(0);
    }
    public void Prune() { lock (_gate) PruneLocked(); }
    private void PruneLocked()
    {
        var cutoff = _clock() - Lifetime;
        foreach (var pair in _partitions.ToArray())
        {
            var partition = pair.Value;
            while (partition.Events.Count != 0 && partition.Events[0].CreatedUtc <= cutoff) RemoveOldest(partition);
            if (partition.Events.Count == 0) _partitions.Remove(pair.Key);
        }
    }
    public NotificationReadResponse Read(Guid user, string session, string? cursor, bool enabled = true)
    {
        lock (_gate)
        {
            PruneLocked();
            _partitions.TryGetValue((user, session), out var partition);
            var fields = cursor?.Split(':');
            long position = 0;
            bool valid = fields is { Length: 2 } && fields[0] == _epoch &&
                long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out position) && position <= _sequence &&
                (partition == null || position >= partition.Floor);
            var reset = cursor != null && !valid;
            var events = enabled && valid && partition != null
                ? partition.Events.Where(e => e.Sequence > position).Select(PublicNotificationEvent.From).ToArray() : [];
            return new(enabled, Cursor, reset, events);
        }
    }
}
