using System;
using System.Net;
using System.Threading;

namespace jellyfin_anidoki.Trackers;

/// <summary>Why a tracker request failed: the pipeline's adapter rule 4.</summary>
public enum TrackerFailureKind { Unauthorized, RateLimited, NotFound, Transient, Rejected }

/// <summary>A tracker request failed, for a reason other than the entry simply not being there on a read.</summary>
public sealed class TrackerFailureException(TrackerFailureKind kind, Exception? inner = null)
    : Exception($"Tracker request failed: {kind}", inner) {
    public TrackerFailureKind Kind { get; } = kind;
}

/// <summary>
/// Collects failure kinds from the provider calls made inside it. The clients turn every failed request into null, and
/// callers outside a scope rely on that; a scope lets the adapter learn why without changing them.
/// </summary>
internal sealed class TrackerCallScope : IDisposable {
    private static readonly AsyncLocal<TrackerCallScope?> Current = new();
    private readonly TrackerCallScope? _outer;

    private TrackerCallScope(TrackerCallScope? outer) => _outer = outer;

    /// <summary>The first definite failure, else NotFound when that is all there was, else null.</summary>
    public TrackerFailureKind? Failure { get; private set; }

    /// <summary>False when the failure is that no response arrived, so a write may still have landed.</summary>
    public bool ResponseReceived { get; private set; } = true;

    public static TrackerCallScope Begin() => Current.Value = new TrackerCallScope(Current.Value);

    public static void Report(TrackerFailureKind kind, bool responseReceived = true) => Current.Value?.Record(kind, responseReceived);

    public static TrackerFailureKind FromStatus(HttpStatusCode status) => status switch {
        HttpStatusCode.Unauthorized => TrackerFailureKind.Unauthorized,
        HttpStatusCode.TooManyRequests => TrackerFailureKind.RateLimited,
        HttpStatusCode.NotFound => TrackerFailureKind.NotFound,
        HttpStatusCode.RequestTimeout => TrackerFailureKind.Transient,
        >= HttpStatusCode.InternalServerError => TrackerFailureKind.Transient,
        _ => TrackerFailureKind.Rejected
    };

    public void Dispose() => Current.Value = _outer;

    private void Record(TrackerFailureKind kind, bool responseReceived) {
        if (Failure is not null and not TrackerFailureKind.NotFound)
            return;
        Failure = kind;
        ResponseReceived = responseReceived;
    }
}
