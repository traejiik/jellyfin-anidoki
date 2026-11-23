using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Progress;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Matching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace jellyfin_anidoki_unit_tests.Progress;

/// <summary>Series items by ID, each visible to everyone unless limited to some users.</summary>
internal sealed class FakeSeriesItems : ISeriesItems {
    public Dictionary<Guid, (SeriesItem Item, HashSet<Guid>? VisibleTo)> Items { get; } = new();

    public SeriesItem Add(SeriesItem item, params Guid[] visibleTo) {
        Items[item.Id] = (item, visibleTo.Length == 0 ? null : visibleTo.ToHashSet());
        return item;
    }

    public SeriesItem? Find(Guid itemId, Guid userId) =>
        Items.TryGetValue(itemId, out var found) && (found.VisibleTo == null || found.VisibleTo.Contains(userId)) ? found.Item : null;
}

/// <summary>A tracker whose every read fails the way a tracker can.</summary>
internal sealed class FailingTracker(ApiName provider, TrackerFailureKind kind) : ITrackerProvider {
    public ApiName Name => provider;

    public ProviderCapabilities Capabilities => ProviderCapabilities.For(provider);

    public Task<EntryState?> ReadEntry(EntryRef entry, CancellationToken cancellationToken) => throw new TrackerFailureException(kind, null);

    public Task<ScoreFormat?> ReadScoreFormat(CancellationToken cancellationToken) => throw new TrackerFailureException(kind, null);

    public Task<IReadOnlyList<EntryCandidate>> Search(string title, CancellationToken cancellationToken) => throw new TrackerFailureException(kind, null);

    public Task<IReadOnlyList<EntryRelation>?> GetRelations(EntryRef entry, CancellationToken cancellationToken) => throw new TrackerFailureException(kind, null);

    public Task<EntryRef?> FindByCrossIds(ResolvedIds ids, string title, MediaKind kind1, CancellationToken cancellationToken) => throw new TrackerFailureException(kind, null);

    public Task<WriteReceipt?> Apply(EntryState current, EntryChange change, CancellationToken cancellationToken) => throw new TrackerFailureException(kind, null);
}

/// <summary>A tracker that reads from a catalogue and writes back to it, recording each write; it can fail or not confirm.</summary>
internal sealed class WritableTracker(CatalogueFake catalogue) : ITrackerProvider {
    public List<(EntryRef Entry, EntryChange Change)> Writes { get; } = new();

    public TrackerFailureKind? WriteFailure { get; set; }

    public bool Unconfirmed { get; set; }

    public ApiName Name => catalogue.Name;

    public ProviderCapabilities Capabilities => catalogue.Capabilities;

    public Task<EntryState?> ReadEntry(EntryRef entry, CancellationToken cancellationToken) => catalogue.ReadEntry(entry, cancellationToken);

    public Task<ScoreFormat?> ReadScoreFormat(CancellationToken cancellationToken) => catalogue.ReadScoreFormat(cancellationToken);

    public Task<IReadOnlyList<EntryCandidate>> Search(string title, CancellationToken cancellationToken) => catalogue.Search(title, cancellationToken);

    public Task<IReadOnlyList<EntryRelation>?> GetRelations(EntryRef entry, CancellationToken cancellationToken) => catalogue.GetRelations(entry, cancellationToken);

    public Task<EntryRef?> FindByCrossIds(ResolvedIds ids, string title, MediaKind kind, CancellationToken cancellationToken) =>
        catalogue.FindByCrossIds(ids, title, kind, cancellationToken);

    public Task<WriteReceipt?> Apply(EntryState current, EntryChange change, CancellationToken cancellationToken) {
        if (WriteFailure is { } failure)
            throw new TrackerFailureException(failure, null);
        Writes.Add((current.Entry, change));
        if (Unconfirmed)
            return Task.FromResult<WriteReceipt?>(null);
        ListState before = current.List ?? new ListState(change.Status, 0, false, 0);
        int? score = change.ClearScore ? null : change.Score ?? before.Score;
        catalogue.Entries[current.Entry.Id] = current with { List = before with { Status = change.Status, Progress = change.Progress, Score = score } };
        return Task.FromResult<WriteReceipt?>(new WriteReceipt(change.Progress, change.Status, false, 0, true, score));
    }
}

/// <summary>
/// A series of two seasons over the match API's fakes: season 1 is 48 episodes (<see cref="MatchApiFixture.Season"/>), season
/// 2 is 12. Alice is linked to AniList, whose catalogue holds entries 101–104 of 12 episodes.
/// </summary>
internal sealed class ProgressFixture : IDisposable {
    public MatchApiFixture Match { get; } = new();

    public FakeSeriesItems Items { get; } = new();

    public SeriesProgressReader Reader { get; }

    public LibrarySource SeasonTwo { get; }

    public SeriesItem Series { get; }

    public ProgressFixture() {
        SeasonTwo = Match.Library.Add(MatchApiFixture.Source(new LibraryFixture().Episode("Long Show", 2, 12), Enumerable.Range(1, 12).ToList()));
        Series = Items.Add(new SeriesItem(Guid.NewGuid(), "Long Show", false, 2020, 2021, 8.5f, "TV-14", "Ended", ["Primary", "Logo"],
            [Match.Season.Info.SourceKey, SeasonTwo.Info.SourceKey], null));
        Match.Trackers.Link(Match.Alice.Id, Match.Catalogue);
        Reader = new SeriesProgressReader(Items, Match.Library, Match.Store, Match.Trackers, Match.Automatic, Match.WatchState,
            new ScoreFormatCache(new MemoryCache(new MemoryCacheOptions())), NullLogger<SeriesProgressReader>.Instance);
    }

    public Guid Alice => Match.Alice.Id;

    /// <summary>Saves a shared match for <paramref name="source"/>.</summary>
    public void Shared(LibrarySource source, params MatchRange[] ranges) =>
        Match.Store.Save(source.Info, new jellyfin_anidoki.Matching.Match(new MatchScope(ApiName.AniList, source.Info.SourceKey), ranges, []), 0, "admin");

    /// <summary>Saves Alice's own match for <paramref name="source"/>.</summary>
    public void Personal(LibrarySource source, IReadOnlyList<MatchRange> ranges, IReadOnlyList<MatchException>? exceptions = null) =>
        Match.Store.Save(source.Info, new jellyfin_anidoki.Matching.Match(new MatchScope(ApiName.AniList, source.Info.SourceKey, Alice), ranges, exceptions ?? []), 0, "alice");

    public Task<SeriesViewContract?> Read(Guid? itemId = null, Guid? user = null) =>
        Reader.Read(itemId ?? Series.Id, user ?? Alice, true, true, CancellationToken.None);

    public void Dispose() => Match.Dispose();
}
