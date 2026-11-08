using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Library;

namespace jellyfin_anidoki.Matching;

public enum MatchOutcome { Ok, NotFound, BadRequest, Invalid, Conflict }

/// <summary>What a match request came to; the controllers turn it into a status code.</summary>
public sealed record MatchResult(MatchOutcome Outcome, object? Body = null) {
    public static MatchResult Ok(object body) => new(MatchOutcome.Ok, body);

    public static readonly MatchResult NotFound = new(MatchOutcome.NotFound);
}

/// <summary>
/// Reads, checks and saves matches for both controllers, which decide who may ask (D12). Every tracker call here is a
/// read; nothing plans or applies a change. A <paramref name="viewer"/> argument is the user whose library limits what
/// can be found, or null for an admin.
/// </summary>
public sealed class MatchService(MatchStore store, IMatchLibrary library, IMatchTrackers trackers, IAutomaticMatches automatic,
    ISeasonWatchState watchState, IMatchProblems problems, IUserManager users) {
    public const int PageSize = 50;

    public const string MatchedFilter = "matched";
    public const string CorrectionsFilter = "corrections";
    public const string NeedsMatchFilter = "needs-match";
    public const string NeedsReviewFilter = "needs-review";

    public MatchResult ListSources(Guid? viewer, string? search, string? filter, int page) {
        if (filter is not (null or "" or MatchedFilter or CorrectionsFilter or NeedsMatchFilter or NeedsReviewFilter))
            return new MatchResult(MatchOutcome.BadRequest, $"Unknown filter \"{filter}\".");
        // A user's problems come from their own syncs only, so the chip says nothing about anyone else's.
        IReadOnlySet<string>? flagged = filter switch {
            NeedsMatchFilter => problems.SourceKeys(MatchProblem.NeedsMatch, viewer),
            NeedsReviewFilter => problems.SourceKeys(MatchProblem.NeedsReview, viewer),
            _ => null
        };
        Dictionary<string, MatchSourceSummary> saved = store.ListSources().ToDictionary(summary => summary.Source.SourceKey);
        ILookup<string, OverrideSummary>? own = viewer is Guid user ? store.ListOverrides(owner: user).ToLookup(entry => entry.Scope.SourceKey) : null;
        IReadOnlyCollection<ApiName>? linked = viewer is Guid id ? trackers.Links(id).Keys.ToList() : null;

        var items = library.List(viewer)
            .Where(entry => flagged == null || flagged.Contains(entry.SourceKey))
            .Where(entry => string.IsNullOrWhiteSpace(search) || entry.Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(entry => new SourceSummaryContract(entry.SourceKey, entry.Kind, entry.Title,
                own == null ? AdminStates(saved.GetValueOrDefault(entry.SourceKey)) : UserStates(saved.GetValueOrDefault(entry.SourceKey), own[entry.SourceKey], linked!)))
            .Where(item => filter switch {
                MatchedFilter => item.Providers.Any(state => state.SharedRevision != null || state.PersonalRevision != null),
                CorrectionsFilter => item.Providers.Any(state => state.Corrections > 0 || state.PersonalRevision != null),
                _ => true
            })
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            .ToList();
        int current = Math.Max(page, 1);
        return MatchResult.Ok(new SourcePageContract(items.Skip((current - 1) * PageSize).Take(PageSize).ToList(), items.Count, current, PageSize));
    }

    /// <summary>Admin: every provider's shared match and history, and who has an override.</summary>
    public MatchResult Detail(string sourceKey) {
        if (library.Find(sourceKey, null) is not { } source)
            return MatchResult.NotFound;
        ILookup<ApiName, OverrideSummary> overrides = store.ListOverrides(sourceKey).ToLookup(entry => entry.Scope.Provider);
        var providers = Enum.GetValues<ApiName>().Select(provider => {
            var scope = new MatchScope(provider, sourceKey);
            StoredMatch? shared = store.Get(scope);
            return new ProviderDetailContract(provider, shared == null ? null : View(shared, null),
                store.History(scope).Select(History).ToList(),
                overrides[provider].Select(entry => new OverrideContract(entry.Scope.Owner!.Value, UserName(entry.Scope.Owner.Value), entry.Revision,
                    entry.BaseSharedRevision, entry.BaseSharedRevision != shared?.Match.Revision)).ToList());
        }).ToList();
        return MatchResult.Ok(new SourceDetailContract(sourceKey, source.Info.Kind, source.Info.Title, source.Episodes, providers));
    }

    /// <summary>A user: what applies to them for each linked provider, and their own override. Nothing about other users.</summary>
    public MatchResult UserDetail(string sourceKey, Guid user) {
        if (library.Find(sourceKey, user) is not { } source)
            return MatchResult.NotFound;
        var providers = trackers.Links(user).Keys.Order().Select(provider => {
            StoredMatch? shared = store.Get(new MatchScope(provider, sourceKey));
            StoredMatch? personal = store.Get(new MatchScope(provider, sourceKey, user));
            string effective = personal != null ? "Personal" : shared != null ? "Shared" : "Automatic";
            return new UserProviderContract(provider, effective, shared == null ? null : View(shared, null),
                personal == null ? null : View(personal, shared?.Match.Revision));
        }).ToList();
        return MatchResult.Ok(new UserSourceDetailContract(sourceKey, source.Info.Kind, source.Info.Title, source.Episodes, providers));
    }

    /// <summary>Admin: one user's override.</summary>
    public MatchResult ReadOverride(MatchScope scope) =>
        store.Get(scope) is { } personal ? MatchResult.Ok(View(personal, store.Get(scope with { Owner = null })?.Match.Revision)) : MatchResult.NotFound;

    /// <summary>Validates the match, reading its targets (V6), and saves it if it passes.</summary>
    public async Task<MatchResult> Save(MatchScope scope, SaveMatchRequest request, Guid requester, Guid? viewer, CancellationToken cancellationToken) {
        if (library.Find(scope.SourceKey, viewer) is not { } source)
            return MatchResult.NotFound;
        long? currentShared = scope.Owner == null ? null : store.Get(scope with { Owner = null })?.Match.Revision;
        var (match, error) = MatchContracts.ToMatch(scope, request.Ranges, request.Exceptions, currentShared);
        if (match == null)
            return new MatchResult(MatchOutcome.BadRequest, error);

        MatchValidation validation = await Validate(match, source, requester, viewer, currentShared, cancellationToken);
        if (!validation.IsValid)
            return Invalid(validation);
        string? generation = scope.Owner is Guid owner && trackers.Links(owner).TryGetValue(scope.Provider, out Guid link) ? link.ToString("D") : null;
        MatchWrite write = store.Save(source.Info, match, request.ExpectedRevision, Author(requester), request.Note, generation);
        return write.Status == MatchWriteStatus.Conflict
            ? new MatchResult(MatchOutcome.Conflict, new RevisionConflictContract(write.Revision))
            : MatchResult.Ok(new SavedContract(write.Revision, IssueContract.From(validation.Warnings)));
    }

    /// <summary>An admin may delete a match whose season has left the library; a user only one they can still see.</summary>
    public MatchResult Delete(MatchScope scope, long expectedRevision, Guid requester, Guid? viewer) {
        if (viewer != null && library.Find(scope.SourceKey, viewer) == null)
            return MatchResult.NotFound;
        MatchWrite write = store.Delete(scope, expectedRevision, Author(requester));
        return write.Status switch {
            MatchWriteStatus.Conflict => new MatchResult(MatchOutcome.Conflict, new RevisionConflictContract(write.Revision)),
            MatchWriteStatus.NotFound => MatchResult.NotFound,
            _ => MatchResult.Ok(new SavedContract(write.Revision, []))
        };
    }

    /// <summary>
    /// What sync would do with the draft for each episode the library holds, through the same resolver. Read-only:
    /// nothing is saved. Progress is shown for <paramref name="progressFor"/>, read through that user's own link only.
    /// </summary>
    public async Task<MatchResult> Preview(MatchScope scope, PreviewRequest request, Guid requester, Guid? progressFor, Guid? viewer,
        CancellationToken cancellationToken) {
        if (library.Find(scope.SourceKey, viewer) is not { } source)
            return MatchResult.NotFound;
        long? currentShared = scope.Owner == null ? null : store.Get(scope with { Owner = null })?.Match.Revision;
        var (match, error) = MatchContracts.ToMatch(scope, request.Ranges, request.Exceptions, currentShared);
        if (match == null)
            return new MatchResult(MatchOutcome.BadRequest, error);

        CatalogueTracker? catalogue = Catalogue(requester, scope.Provider, viewer);
        Dictionary<EntryRef, EntryState> entries = catalogue == null ? [] : await MatchResolver.ReadTargets(match, catalogue.Tracker, cancellationToken);
        MatchValidation validation = WithoutReader(MatchValidator.Validate(match, source.Last.Kind, entries, currentShared), catalogue, scope.Provider, requester == viewer);
        var rows = source.Episodes.Select(episode => Row(match, episode, source.Info.Title, entries)).ToList();

        IReadOnlyList<PreviewProgressContract>? progress = null;
        if (progressFor is Guid user && trackers.ForOwner(user, scope.Provider) is { } own) {
            Dictionary<EntryRef, EntryState> list = await MatchResolver.ReadTargets(match, own, cancellationToken);
            IEnumerable<int> watched = watchState.Watched(source.Last, user).Select(episode => episode.Number);
            progress = ProgressAggregator.ForWholeSeason(match, watched, list)
                .Select(entry => new PreviewProgressContract(EntryContract.From(entry.Target), entry.Progress,
                    list.GetValueOrDefault(entry.Target.Entry)?.List?.Status, list.GetValueOrDefault(entry.Target.Entry)?.List?.Progress))
                .ToList();
        }
        return MatchResult.Ok(new PreviewContract(rows, IssueContract.From(validation.Errors), IssueContract.From(validation.Warnings), progress,
            catalogue?.Borrowed == true));
    }

    /// <summary>
    /// Makes a user's override the shared match. It is validated again as a shared match first, since it was only ever
    /// checked as one user's.
    /// </summary>
    public async Task<MatchResult> Promote(Guid owner, ApiName provider, string sourceKey, PromoteRequest request, Guid requester, CancellationToken cancellationToken) {
        if (library.Find(sourceKey, null) is not { } source || store.Get(new MatchScope(provider, sourceKey, owner)) is not { } personal)
            return MatchResult.NotFound;
        var shared = new Match(new MatchScope(provider, sourceKey), personal.Match.Ranges, personal.Match.Exceptions);
        MatchValidation validation = await Validate(shared, source, requester, null, null, cancellationToken);
        if (!validation.IsValid)
            return Invalid(validation);

        MatchPromotion promotion = store.Promote(owner, provider, sourceKey, request.ExpectedSharedRevision, request.ExpectedOverrideRevision,
            request.RemoveOverride, Author(requester));
        if (promotion.Status == MatchWriteStatus.Conflict)
            return new MatchResult(MatchOutcome.Conflict, new PromotionConflictContract(promotion.SharedRevision, promotion.OverrideRevision));
        if (promotion.Status == MatchWriteStatus.NotFound)
            return MatchResult.NotFound;
        int differing = store.ListOverrides(sourceKey)
            .Where(entry => entry.Scope.Provider == provider && entry.Scope.Owner != owner)
            .Count(entry => store.Get(entry.Scope) is { } other && !SameMapping(other.Match, shared));
        return MatchResult.Ok(new PromotedContract(promotion.SharedRevision, promotion.OverrideRevision, differing, IssueContract.From(validation.Warnings)));
    }

    /// <param name="viewer">The requester when a user searches for their own correction, who reads only through their own link.</param>
    public async Task<MatchResult> SearchEntries(Guid requester, Guid? viewer, ApiName provider, string? query, CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(query))
            return new MatchResult(MatchOutcome.BadRequest, "Enter a title to search for.");
        if (Catalogue(requester, provider, viewer) is not { } catalogue)
            return MatchResult.Ok(new EntrySearchContract([], false));
        IReadOnlyList<EntryCandidate> candidates = await catalogue.Tracker.Search(query.Trim(), cancellationToken);
        return MatchResult.Ok(new EntrySearchContract(
            candidates.Select(candidate => EntryCandidateContract.From(candidate.Entry, candidate.Titles, candidate.TotalEpisodes)).ToList(), catalogue.Borrowed));
    }

    /// <summary>One entry by ID, without list state, which may be another user's.</summary>
    public async Task<MatchResult> ReadEntry(Guid requester, ApiName provider, int id, string? alternativeId, CancellationToken cancellationToken) {
        if (trackers.ForCatalogue(requester, provider) is not { } catalogue
            || await catalogue.Tracker.ReadEntry(new EntryRef(provider, id, alternativeId), cancellationToken) is not { } state)
            return MatchResult.NotFound;
        return MatchResult.Ok(EntryCandidateContract.From(state.Entry, state.Titles, state.TotalEpisodes));
    }

    /// <summary>Automatic resolution's answer for the season, as a match the editor can save unchanged.</summary>
    /// <param name="viewer">A user looking at their own season, who reads only through their own link; null for an admin, who may borrow one (D12).</param>
    public async Task<MatchResult> Automatic(string sourceKey, ApiName provider, Guid requester, Guid? viewer, CancellationToken cancellationToken) {
        if (library.Find(sourceKey, viewer) is not { } source)
            return MatchResult.NotFound;
        if (Catalogue(requester, provider, viewer) is not { } catalogue)
            return MatchResult.Ok(new AutomaticContract(null, "no-linked-account", false));
        DerivedSegmentation derived = await automatic.Derive(source, catalogue.Tracker, cancellationToken);
        return MatchResult.Ok(new AutomaticContract(derived.Match == null ? null : MappingContract.From(derived.Match.Ranges, derived.Match.Exceptions),
            derived.FailureReason, catalogue.Borrowed));
    }

    /// <summary>Saved sources the library no longer finds, with where each may belong.</summary>
    public MatchResult ListOrphans() => MatchResult.Ok(store.ListSources()
        .Where(saved => library.Find(saved.Source.SourceKey, null) == null)
        .Select(saved => new OrphanContract(saved.Source.SourceKey, saved.Source.Kind, saved.Source.Title, saved.UpdatedUtc, AdminStates(saved),
            saved.Source.Fingerprint,
            library.Candidates(saved.Source, SourceKeys.Parse(saved.Source.SourceKey)?.SeasonNumber)
                .Select(candidate => new SourceRefContract(candidate.SourceKey, candidate.Title)).ToList()))
        .ToList());

    /// <summary>
    /// Moves an orphan's matches and history to a library source the admin chose. Never automatic (D3), and only
    /// onto a source of the same kind with nothing saved, so no two histories mix.
    /// </summary>
    public MatchResult Reattach(string sourceKey, ReattachRequest request) {
        if (library.Find(sourceKey, null) != null)
            return new MatchResult(MatchOutcome.BadRequest, "This season or movie is still in the library, so its matches are not orphaned.");
        if (store.ListSources().FirstOrDefault(saved => saved.Source.SourceKey == sourceKey) is not { } orphan)
            return MatchResult.NotFound;
        if (string.IsNullOrEmpty(request.TargetSourceKey) || library.Find(request.TargetSourceKey, null) is not { } target)
            return new MatchResult(MatchOutcome.BadRequest, "Choose a season or movie in the library.");
        if (target.Info.Kind != orphan.Source.Kind)
            return new MatchResult(MatchOutcome.BadRequest, "A season's matches can only move to a season, and a movie's to a movie.");
        return store.MoveSource(sourceKey, target.Info) switch {
            MatchWriteStatus.Conflict => new MatchResult(MatchOutcome.Conflict, $"{target.Info.Title} already has saved matches or their history, and the two cannot be merged. Save the match again on {target.Info.Title} and delete this one instead."),
            MatchWriteStatus.NotFound => MatchResult.NotFound,
            _ => MatchResult.Ok(new SourceRefContract(target.Info.SourceKey, target.Info.Title))
        };
    }

    /// <summary>Deletes an orphan with its matches and history. A source still in the library keeps its history.</summary>
    public MatchResult DeleteOrphan(string sourceKey) {
        if (library.Find(sourceKey, null) != null)
            return new MatchResult(MatchOutcome.BadRequest, "This season or movie is still in the library. Delete its matches one tracker at a time instead.");
        return store.DeleteSource(sourceKey) == MatchWriteStatus.NotFound ? MatchResult.NotFound : MatchResult.Ok(new SourceRefContract(sourceKey, ""));
    }

    private async Task<MatchValidation> Validate(Match match, LibrarySource source, Guid requester, Guid? viewer, long? currentShared, CancellationToken cancellationToken) {
        CatalogueTracker? catalogue = Catalogue(requester, match.Scope.Provider, viewer);
        Dictionary<EntryRef, EntryState> entries = catalogue == null ? [] : await MatchResolver.ReadTargets(match, catalogue.Tracker, cancellationToken);
        return WithoutReader(MatchValidator.Validate(match, source.Last.Kind, entries, currentShared), catalogue, match.Scope.Provider, requester == viewer);
    }

    /// <summary>
    /// D12: catalogue reads may borrow another user's link, except for a user working in their own library, who reads
    /// only through their own; their override only ever applies while they are linked anyway (D1).
    /// </summary>
    private CatalogueTracker? Catalogue(Guid requester, ApiName provider, Guid? viewer) =>
        requester == viewer
            ? trackers.ForOwner(requester, provider) is { } own ? new CatalogueTracker(own, false) : null
            : trackers.ForCatalogue(requester, provider);

    /// <summary>With no account to read through, every target fails V6; say why once, rather than blaming each entry.</summary>
    private static MatchValidation WithoutReader(MatchValidation validation, CatalogueTracker? catalogue, ApiName provider, bool ownLinkOnly) =>
        catalogue != null || validation.Errors.All(issue => issue.Rule != "V6")
            ? validation
            : validation with {
                Errors = [
                    new MatchIssue("V6", ownLinkOnly ? $"Link {provider} first, so its entries can be checked." : $"No user on this server has linked {provider}, so its entries cannot be checked."),
                    .. validation.Errors.Where(issue => issue.Rule != "V6")
                ]
            };

    private static MatchResult Invalid(MatchValidation validation) =>
        new(MatchOutcome.Invalid, new InvalidMatchContract(IssueContract.From(validation.Errors), IssueContract.From(validation.Warnings)));

    private static PreviewRowContract Row(Match match, int episode, string title, IReadOnlyDictionary<EntryRef, EntryState> entries) {
        var mapped = RangeResolver.Map(match, episode) as MappedEpisode;
        EntryContract? target = mapped == null ? null : EntryContract.From(mapped.Target);
        return RangeResolver.Resolve(match, episode, title, entries) switch {
            Resolved => new PreviewRowContract(episode, "mapped", target, mapped!.TargetEpisode),
            Ignored => new PreviewRowContract(episode, "ignored", null, null),
            Unresolved unresolved => new PreviewRowContract(episode, unresolved.Reason, target, mapped?.TargetEpisode),
            _ => throw new InvalidOperationException("Unknown resolution.")
        };
    }

    /// <summary>The same entries and numbers; titles are left out, since each was saved when its match was.</summary>
    internal static bool SameMapping(Match first, Match second) =>
        first.Ranges.OrderBy(range => range.SourceStart).Select(range => (range.SourceStart, range.SourceEnd, range.Target.Entry, range.TargetStart))
            .SequenceEqual(second.Ranges.OrderBy(range => range.SourceStart).Select(range => (range.SourceStart, range.SourceEnd, range.Target.Entry, range.TargetStart)))
        && first.Exceptions.OrderBy(exception => exception.SourceEpisode).Select(Key)
            .SequenceEqual(second.Exceptions.OrderBy(exception => exception.SourceEpisode).Select(Key));

    private static (int, EntryRef?, int?) Key(MatchException exception) =>
        exception is TargetException target ? (target.SourceEpisode, target.Target.Entry, target.TargetEpisode) : (exception.SourceEpisode, null, null);

    private static IReadOnlyList<ProviderStateContract> AdminStates(MatchSourceSummary? saved) =>
        saved == null ? [] : saved.Shared.Keys.Union(saved.Overrides.Keys).Order()
            .Select(provider => new ProviderStateContract(provider, saved.Shared.TryGetValue(provider, out long revision) ? revision : null,
                saved.Overrides.GetValueOrDefault(provider), null))
            .ToList();

    /// <summary>Only the user's linked providers, and never other users' overrides.</summary>
    private static IReadOnlyList<ProviderStateContract> UserStates(MatchSourceSummary? saved, IEnumerable<OverrideSummary> own, IReadOnlyCollection<ApiName> linked) {
        var personal = own.ToDictionary(entry => entry.Scope.Provider, entry => entry.Revision);
        return linked.Order()
            .Where(provider => saved?.Shared.ContainsKey(provider) == true || personal.ContainsKey(provider))
            .Select(provider => new ProviderStateContract(provider, saved != null && saved.Shared.TryGetValue(provider, out long revision) ? revision : null,
                null, personal.TryGetValue(provider, out long mine) ? mine : null))
            .ToList();
    }

    private MatchView View(StoredMatch stored, long? currentShared) => new(stored.Match.Revision, stored.Match.BaseSharedRevision,
        stored.Match.Scope.Owner != null && stored.Match.BaseSharedRevision != currentShared, stored.Note, AuthorName(stored.UpdatedBy), stored.UpdatedUtc,
        MappingContract.From(stored.Match.Ranges, stored.Match.Exceptions));

    private HistoryContract History(MatchHistoryEntry entry) => new(entry.Revision, entry.Action, AuthorName(entry.Author), entry.CreatedUtc,
        MappingContract.From(entry.Snapshot.Ranges, entry.Snapshot.Exceptions), entry.PromotedFrom);

    /// <summary>Authors are stored as user IDs, so a renamed user shows their current name.</summary>
    private static string Author(Guid userId) => userId.ToString("D");

    private string AuthorName(string author) => Guid.TryParse(author, out Guid id) ? UserName(id) : author;

    private string UserName(Guid id) => users.GetUserById(id)?.Username ?? "Deleted user";
}
