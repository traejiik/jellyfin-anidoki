using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Trackers;

/// <summary>
/// One tracker, seen through neutral types. Reads have no side effects; <see cref="Apply"/> is the only write and owns
/// any provider-specific call sequence.
/// </summary>
public interface ITrackerProvider {
    ApiName Name { get; }

    ProviderCapabilities Capabilities { get; }

    /// <returns>The entry, or null when it cannot be read.</returns>
    Task<EntryState?> ReadEntry(EntryRef entry, CancellationToken cancellationToken);

    /// <returns>Candidates in the provider's order; empty when the provider cannot search or finds nothing.</returns>
    Task<IReadOnlyList<EntryCandidate>> Search(string title, CancellationToken cancellationToken);

    /// <returns>Related entries, or null when the entry cannot be read (distinct from having no relations).</returns>
    Task<IReadOnlyList<EntryRelation>?> GetRelations(EntryRef entry, CancellationToken cancellationToken);

    /// <returns>The entry the IDs identify on this provider, or null.</returns>
    Task<EntryRef?> FindByCrossIds(ResolvedIds ids, string title, MediaKind kind, CancellationToken cancellationToken);

    /// <param name="current">The state the change was planned against.</param>
    /// <returns>The acknowledged result, or null when nothing was confirmed.</returns>
    /// <exception cref="System.ArgumentException">The change is a rewatch on a provider without rewatch support, or a rewatch not targeting Completed.</exception>
    Task<WriteReceipt?> Apply(EntryState current, EntryChange change, CancellationToken cancellationToken);
}
