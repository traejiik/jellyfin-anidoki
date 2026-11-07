using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using jellyfin_anidoki_unit_tests.Operations;
using MediaBrowser.Controller.Library;
using Moq;

namespace jellyfin_anidoki_unit_tests.Matching;

/// <summary>A catalogue that answers reads from a table and fails the test on any write.</summary>
internal sealed class CatalogueFake(ApiName provider) : ITrackerProvider {
    public Dictionary<int, EntryState> Entries { get; } = new();

    public List<EntryCandidate> Candidates { get; } = new();

    public int Reads { get; private set; }

    public ApiName Name => provider;

    public ProviderCapabilities Capabilities => ProviderCapabilities.For(provider);

    /// <summary>Adds an entry; <paramref name="list"/> is the reading user's list state.</summary>
    public EntryRef Add(int id, int total, ListState? list = null) {
        var entry = new EntryRef(provider, id, null);
        Entries[id] = new EntryState(entry, total, list, DateTimeOffset.UnixEpoch, new EntryTitles($"Entry {id}", null, null, []));
        return entry;
    }

    public Task<EntryState?> ReadEntry(EntryRef entry, CancellationToken cancellationToken) {
        Reads++;
        return Task.FromResult(Entries.GetValueOrDefault(entry.Id));
    }

    public Task<IReadOnlyList<EntryCandidate>> Search(string title, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EntryCandidate>>(Candidates);

    public Task<IReadOnlyList<EntryRelation>?> GetRelations(EntryRef entry, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EntryRelation>?>([]);

    public Task<EntryRef?> FindByCrossIds(ResolvedIds ids, string title, MediaKind kind, CancellationToken cancellationToken) =>
        Task.FromResult<EntryRef?>(null);

    public Task<WriteReceipt?> Apply(EntryState current, EntryChange change, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The match API must never write to a tracker.");
}

/// <summary>Sources by key; each is visible to everyone unless limited to some users.</summary>
internal sealed class FakeMatchLibrary : IMatchLibrary {
    private readonly List<(LibrarySource Source, HashSet<Guid>? VisibleTo)> _sources = new();

    public LibrarySource Add(LibrarySource source, params Guid[] visibleTo) {
        _sources.Add((source, visibleTo.Length == 0 ? null : visibleTo.ToHashSet()));
        return source;
    }

    public IReadOnlyList<LibrarySourceEntry> List(Guid? viewer) =>
        _sources.Where(entry => Visible(entry.VisibleTo, viewer))
            .Select(entry => new LibrarySourceEntry(entry.Source.Info.SourceKey, entry.Source.Info.Kind, entry.Source.Info.Title))
            .ToList();

    public LibrarySource? Find(string sourceKey, Guid? viewer) =>
        _sources.FirstOrDefault(entry => entry.Source.Info.SourceKey == sourceKey && Visible(entry.VisibleTo, viewer)).Source;

    /// <summary>As a library rebuild does: the source is gone, and its matches orphaned.</summary>
    public void Remove(LibrarySource source) => _sources.RemoveAll(entry => entry.Source == source);

    /// <summary>Every listed source of the same kind; the real library also compares provider IDs (<see cref="MatchSource.SharesId"/>).</summary>
    public IReadOnlyList<LibrarySourceEntry> Candidates(MatchSource orphan, int? seasonNumber) =>
        List(null).Where(entry => entry.Kind == orphan.Kind).ToList();

    private static bool Visible(HashSet<Guid>? visibleTo, Guid? viewer) => viewer == null || visibleTo == null || visibleTo.Contains(viewer.Value);
}

/// <summary>Each user's links, and the tracker each link reads through.</summary>
internal sealed class FakeMatchTrackers : IMatchTrackers {
    public Dictionary<(Guid User, ApiName Provider), (Guid Generation, ITrackerProvider Tracker)> Linked { get; } = new();

    public void Link(Guid user, ITrackerProvider tracker) => Linked[(user, tracker.Name)] = (Guid.NewGuid(), tracker);

    public IReadOnlyDictionary<ApiName, Guid> Links(Guid userId) =>
        Linked.Where(link => link.Key.User == userId).ToDictionary(link => link.Key.Provider, link => link.Value.Generation);

    public CatalogueTracker? ForCatalogue(Guid requester, ApiName provider) {
        if (ForOwner(requester, provider) is { } own)
            return new CatalogueTracker(own, false);
        return Linked.Where(link => link.Key.Provider == provider).Select(link => new CatalogueTracker(link.Value.Tracker, true)).FirstOrDefault();
    }

    public ITrackerProvider? ForOwner(Guid owner, ApiName provider) =>
        Linked.TryGetValue((owner, provider), out var link) ? link.Tracker : null;
}

internal sealed class FakeAutomaticMatches : IAutomaticMatches {
    public DerivedSegmentation Answer { get; set; } = new(null, "no-ids");

    public Task<DerivedSegmentation> Derive(LibrarySource source, ITrackerProvider tracker, CancellationToken cancellationToken) => Task.FromResult(Answer);
}

internal sealed class WatchStateByUser : ISeasonWatchState {
    public Dictionary<Guid, int[]> ByUser { get; } = new();

    public IReadOnlyList<WatchedEpisode> Watched(SyncSource source, Guid userId) =>
        (ByUser.GetValueOrDefault(userId) ?? []).Select(episode => new WatchedEpisode(episode, null)).ToList();
}

/// <summary>A match API over a real store in a temporary directory, with an admin, two users and a 48-episode season.</summary>
internal sealed class MatchApiFixture : IDisposable {
    public const ApiName Provider = ApiName.AniList;

    public string Directory { get; } = OperationTestSupport.NewDirectory();

    public MatchStore Store { get; }

    public FakeMatchLibrary Library { get; } = new();

    public FakeMatchTrackers Trackers { get; } = new();

    public FakeAutomaticMatches Automatic { get; } = new();

    public WatchStateByUser WatchState { get; } = new();

    public Mock<IUserManager> Users { get; } = new();

    public MatchService Service { get; }

    public User Admin { get; } = NewUser("admin");

    public User Alice { get; } = NewUser("alice");

    public User Bob { get; } = NewUser("bob");

    /// <summary>Episodes 1–48 of one season, visible to everyone.</summary>
    public LibrarySource Season { get; }

    /// <summary>A season only the admin and Bob can see.</summary>
    public LibrarySource Hidden { get; }

    /// <summary>The AniList catalogue: entries 101–104 of 12 episodes, read through any link.</summary>
    public CatalogueFake Catalogue { get; } = new(Provider);

    public MatchApiFixture() {
        Store = new MatchStore(new AniDokiDatabase(Path.Combine(Directory, "anidoki.db")), TimeProvider.System);
        foreach (User user in new[] { Admin, Alice, Bob })
            Users.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var fixture = new LibraryFixture();
        Season = Library.Add(Source(fixture.Episode("Long Show", 1, 48), Enumerable.Range(1, 48).ToList()));
        Hidden = Library.Add(Source(fixture.Episode("Hidden Show", 1, 12), Enumerable.Range(1, 12).ToList()), Admin.Id, Bob.Id);
        foreach (int id in new[] { MatchTestSupport.A, MatchTestSupport.B, MatchTestSupport.C, MatchTestSupport.D })
            Catalogue.Add(id, 12);
        Service = new MatchService(Store, Library, Trackers, Automatic, WatchState, Users.Object);
    }

    public string Key => Season.Info.SourceKey;

    public MatchScope SharedScope => new(Provider, Key);

    public MatchScope PersonalScope(User user) => new(Provider, Key, user.Id);

    public static LibrarySource Source(MediaBrowser.Controller.Entities.Video last, IReadOnlyList<int> episodes) {
        SyncSource source = SyncSource.From(last)!;
        return new LibrarySource(MatchSource.From(source), source, episodes);
    }

    public static EntryContract Entry(int id) => new(id, null, null, $"Entry {id}");

    public static RangeContract Span(int start, int? end, int entry, int targetStart = 1) => new(start, end, Entry(entry), targetStart);

    /// <summary>Episodes 1–48 as four 12-episode entries.</summary>
    public static SaveMatchRequest FourByTwelve(long expectedRevision = 0) => new(expectedRevision,
        [Span(1, 12, MatchTestSupport.A), Span(13, 24, MatchTestSupport.B), Span(25, 36, MatchTestSupport.C), Span(37, 48, MatchTestSupport.D)], [], "split by AniList");

    public static PreviewRequest Draft(SaveMatchRequest request) => new(request.Ranges, request.Exceptions);

    public void Dispose() => System.IO.Directory.Delete(Directory, true);

    private static User NewUser(string name) => new(name, "authentication", "password-reset") { Id = Guid.NewGuid() };
}
