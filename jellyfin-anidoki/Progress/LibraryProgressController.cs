using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Extensions;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace jellyfin_anidoki.Progress;

[ApiController]
[Route("AniDoki/user/progress")]
[Authorize]
[MatchErrors]
public sealed class LibraryProgressController(IUserManager users, LibraryProgress library) : ControllerBase {
    [HttpGet("library")]
    public async Task<ActionResult<LibraryProgressView>> ReadLibrary([FromQuery] ApiName? provider, [FromQuery] bool refresh,
        CancellationToken cancellationToken, [FromQuery] bool afterChange = false) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        if (users.GetUser(User, null)?.Id is not { } userId)
            return Unauthorized();
        if (provider is { } selected && !Enum.IsDefined(selected))
            return BadRequest();
        var view = await library.Read(userId, provider, refresh, SeriesProgressController.ApplySavedMatches, cancellationToken, afterChange);
        return view == null ? StatusCode(StatusCodes.Status503ServiceUnavailable, "Could not read the tracker list. Try refreshing again.") : Ok(view);
    }
}
