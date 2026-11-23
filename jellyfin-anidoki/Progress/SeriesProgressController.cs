using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Extensions;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Matching;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
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
public sealed class SeriesProgressController(SeriesProgressReader reader, IUserManager users) : ControllerBase {
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

    internal static bool ApplySavedMatches => Plugin.Instance?.PluginConfiguration.applySavedMatches ?? true;

    private Guid? Me() => users.GetUser(User, null)?.Id;
}
