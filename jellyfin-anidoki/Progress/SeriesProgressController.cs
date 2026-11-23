using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Extensions;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Operations;
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
public sealed class SeriesProgressController(SeriesProgressReader reader, EntryEditor editor, OperationRunner runner, OperationStore store,
    IUserManager users) : ControllerBase {
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

    internal static bool ApplySavedMatches => Plugin.Instance?.PluginConfiguration.applySavedMatches ?? true;

    private Guid? Me() => users.GetUser(User, null)?.Id;
}
