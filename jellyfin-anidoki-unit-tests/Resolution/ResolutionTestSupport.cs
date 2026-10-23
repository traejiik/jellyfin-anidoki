using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;

namespace jellyfin_anidoki_unit_tests.Resolution;

/// <summary>Passes every read to the wrapped tracker and fails the test on any write.</summary>
internal sealed class ReadOnlyTracker(ITrackerProvider inner) : ITrackerProvider {
    public ApiName Name => inner.Name;

    public ProviderCapabilities Capabilities => inner.Capabilities;

    public Task<EntryState?> ReadEntry(EntryRef entry, CancellationToken cancellationToken) => inner.ReadEntry(entry, cancellationToken);

    public Task<IReadOnlyList<EntryCandidate>> Search(string title, CancellationToken cancellationToken) => inner.Search(title, cancellationToken);

    public Task<IReadOnlyList<EntryRelation>?> GetRelations(EntryRef entry, CancellationToken cancellationToken) =>
        inner.GetRelations(entry, cancellationToken);

    public Task<EntryRef?> FindByCrossIds(ResolvedIds ids, string title, MediaKind kind, CancellationToken cancellationToken) =>
        inner.FindByCrossIds(ids, title, kind, cancellationToken);

    public Task<WriteReceipt?> Apply(EntryState current, EntryChange change, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Resolution must never write.");
}

internal static class ResolutionTestSupport {
    /// <summary>A fake tracker for <paramref name="provider"/>, seen through the adapter and guarded against writes.</summary>
    public static (ITrackerProvider Tracker, FakeTracker Fake) ReadOnly(ApiName provider) {
        var fake = new FakeTracker(provider);
        return (new ReadOnlyTracker(new ApiCallHelpersTrackerProvider(provider, fake, TimeProvider.System)), fake);
    }

    /// <summary>A ref the provider reads <paramref name="id"/> by: Annict and Shikimori need the alternative ID.</summary>
    public static EntryRef RefFor(ApiName provider, int id) =>
        new(provider, id, provider is ApiName.Annict or ApiName.Shikimori ? $"alt-{id}" : null);
}
