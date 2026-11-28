using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Extensions;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jellyfin_anidoki.Progress;

[ApiController]
[Route("AniDoki/user/progress/series/{itemId:guid}")]
[Authorize]
[MatchErrors]
public sealed class SeriesDetailsController(IUserManager users, SeriesDetails details) : ControllerBase {
    [HttpGet("related")]
    public async Task<ActionResult<RelatedView>> ReadRelated(Guid itemId, [FromQuery] ApiName provider, [FromQuery] string? sourceKey,
        [FromQuery] int? entryId, CancellationToken cancellationToken) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        if (users.GetUser(User, null)?.Id is not { } userId)
            return Unauthorized();
        if (details.Select(itemId, userId, sourceKey) is not { } source)
            return NotFound();
        if (!Enum.IsDefined(provider) || !details.Linked(userId, provider))
            return UnprocessableEntity("The tracker is not linked.");
        var view = await details.Related(source, userId, provider, entryId, SeriesProgressController.ApplySavedMatches, cancellationToken);
        return view == null ? NotFound() : Ok(view);
    }

    [HttpGet("airing")]
    public async Task<ActionResult<AiringView>> ReadAiring(Guid itemId, [FromQuery] string? sourceKey, [FromQuery] int? aniListEntryId,
        CancellationToken cancellationToken) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        if (users.GetUser(User, null)?.Id is not { } userId)
            return Unauthorized();
        if (details.Select(itemId, userId, sourceKey) is not { } source)
            return NotFound();
        var view = await details.Airing(source, userId, aniListEntryId, SeriesProgressController.ApplySavedMatches, cancellationToken);
        return view == null ? NotFound() : Ok(view);
    }
}
