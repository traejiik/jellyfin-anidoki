using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Extensions;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace jellyfin_anidoki.Progress;

/// <summary>
/// Phase 6: the progress popup's backend. Always the signed-in user's own series and entries; there is no user ID to pass.
/// Answers 404 while the admin has the user pages off, like the other user routes.
/// </summary>
[ApiController]
[Route("AniDoki/user/progress")]
[Authorize]
[MatchErrors]
public sealed class SeriesProgressController(SeriesProgressReader reader, EntryEditor editor, SeriesOutbound outbound, SeriesInbound inbound,
    ISeriesItems items, IMatchTrackers trackers, OperationRunner runner, OperationStore store, IUserManager users) : ControllerBase {
    /// <summary>A series, season or episode ID opens its series; a movie ID opens the movie.</summary>
    [HttpGet("series/{itemId:guid}")]
    public async Task<ActionResult<SeriesViewContract>> ReadSeries(Guid itemId, CancellationToken cancellationToken) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        if (Me() is not { } user)
            return Unauthorized();
        SeriesViewContract? view = await reader.Read(itemId, user, ApplySavedMatches, ConfigHelper.UserPagesEnabled(), cancellationToken);
        return view == null ? NotFound() : Ok(view);
    }

    /// <summary>
    /// Whether an item page may offer the progress popup: the item is an anime series, season, episode or movie the user can
    /// see in an anime library AniDōki syncs, and the user has a linked tracker. It never reads a tracker, so item pages can ask on
    /// every visit.
    /// </summary>
    [HttpGet("series/{itemId:guid}/available")]
    public ActionResult Available(Guid itemId) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        if (Me() is not { } user)
            return Unauthorized();
        // Sources holds only seasons in the anime libraries that AniDōki syncs; it reads Jellyfin, never a tracker.
        return items.Find(itemId, user) is { } item && trackers.Links(user).Count > 0 && reader.Sources(item, user).Count > 0 ? NoContent() : NotFound();
    }

    /// <summary>
    /// D4: saves the popup's pending edits as one Edit operation. Every edit is checked first; nothing runs if any fails.
    /// Read the results with <see cref="ReadEdit"/>.
    /// </summary>
    [HttpPost("edit")]
    public async Task<ActionResult<OperationStarted>> Edit([FromBody] EditRequest request, CancellationToken cancellationToken) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        if (Me() is not { } user)
            return Unauthorized();
        switch (await editor.Check(user, request, ApplySavedMatches, cancellationToken)) {
            case EditCheck.NotFound:
                return NotFound();
            case EditCheck.Forbidden:
                return Forbid();
            case EditCheck.Invalid invalid:
                return UnprocessableEntity(invalid.Message);
            case EditCheck.Ready ready:
                OperationRunner.Handle? handle = runner.Enqueue(user, OperationKind.Edit, ready.SeriesName, operation => editor.Run(ready.Edits, operation));
                return handle == null ? StatusCode(StatusCodes.Status503ServiceUnavailable, "Nothing was saved.") : Accepted(new OperationStarted(handle.OperationId));
            default:
                return NotFound();
        }
    }

    /// <summary>An edit operation's results, one per entry. Finished is false while it is still queued or running.</summary>
    [HttpGet("edit/{operationId:guid}")]
    public ActionResult<EditOutcomeContract> ReadEdit(Guid operationId) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        if (Me() is not { } user)
            return Unauthorized();
        if (store.Read(operationId) is not { Kind: OperationKind.Edit } view)
            return NotFound();
        return view.UserId != user ? Forbid() : Ok(EntryEditor.Results(view, store.ReadActions(operationId)));
    }

    /// <summary>D5: Sync to a tracker for one series, when Jellyfin is ahead. Never lowers the tracker's progress.</summary>
    [HttpPost("sync-to-trackers")]
    public ActionResult<OperationStarted> SyncToTrackers([FromBody] SeriesSyncRequest request) =>
        StartSync(request, OperationKind.SyncToTrackers, item => item.IsMovie ? "A movie updates its trackers when you watch it." : null,
            (user, item, _, operation) => outbound.Run(user, item.Id, request.Provider, operation));

    /// <summary>D5: Sync to Jellyfin for one series, when the tracker is ahead. Only ever marks episodes played.</summary>
    [HttpPost("sync-to-jellyfin")]
    public ActionResult<OperationStarted> SyncToJellyfin([FromBody] SeriesSyncRequest request) =>
        StartSync(request, OperationKind.SyncFromTrackers, _ => null,
            (user, item, tracker, operation) => inbound.Run(item, user, tracker, ApplySavedMatches, operation));

    private ActionResult<OperationStarted> StartSync(SeriesSyncRequest request, OperationKind kind,
        Func<SeriesItem, string?> refuse, Func<Guid, SeriesItem, ITrackerProvider, OperationContext, Task> run) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        if (Me() is not { } user)
            return Unauthorized();
        if (items.Find(request.ItemId, user) is not { } item)
            return NotFound();
        if (trackers.ForOwner(user, request.Provider) is not { } tracker)
            return UnprocessableEntity($"{request.Provider} is not linked.");
        if (refuse(item) is { } problem)
            return UnprocessableEntity(problem);
        if (store.FindActive(user, kind) is { } active)
            return Conflict(new OperationStarted(active));
        OperationRunner.Handle? handle = runner.Enqueue(user, kind, $"{item.Name} ({request.Provider})", operation => run(user, item, tracker, operation));
        return handle == null ? StatusCode(StatusCodes.Status503ServiceUnavailable, "Nothing was synced.") : Accepted(new OperationStarted(handle.OperationId));
    }

    internal static bool ApplySavedMatches => Plugin.Instance?.PluginConfiguration.applySavedMatches ?? true;

    private Guid? Me() => users.GetUser(User, null)?.Id;
}
