using System;
using System.Linq;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Extensions;
using jellyfin_anidoki.Settings;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.Operations;

/// <summary>The body of a 503 when storage is unavailable, so the pages can show their storage state.</summary>
public sealed record StorageUnavailable(
    [property: JsonPropertyName("storageUnavailable")] bool Unavailable,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// D7: history and last update for the pages. A user reads their own (an admin anyone's) under <c>AniDoki/user</c>;
/// the admin pages read anyone's under <c>AniDoki/users</c>.
/// </summary>
[ApiController]
[Route("AniDoki")]
public sealed class HistoryController(HistoryReader history, AccountAttention attention, OperationStore store, IUserManager userManager) : ControllerBase {
    public const int DefaultPageSize = 10;

    [Authorize]
    [HttpGet("user/history")]
    public ActionResult<HistoryPage> ReadOwnHistory([FromQuery] Guid user, [FromQuery] int offset = 0, [FromQuery] int limit = DefaultPageSize) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        var jellyfinUser = userManager.GetUser(User, user);
        return jellyfinUser == null ? Forbid() : Storage(() => history.Read(jellyfinUser.Id, offset, limit));
    }

    [Authorize]
    [HttpGet("user/activity")]
    public ActionResult<UserActivity> ReadOwnActivity([FromQuery] Guid user) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        var jellyfinUser = userManager.GetUser(User, user);
        return jellyfinUser == null ? Forbid() : Storage(() => history.Activity(jellyfinUser.Id, attention.For(jellyfinUser.Id)).Single());
    }

    [Authorize]
    [HttpGet("user/operations/{operationId:guid}")]
    public ActionResult<OperationView> ReadOwnOperation(Guid operationId) {
        if (userManager.GetUser(User, null) == null)
            return Forbid();
        OperationView? view;
        try {
            view = store.Read(operationId);
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            return Unavailable(error);
        }
        if (view == null)
            return NotFound();
        return userManager.GetUser(User, view.UserId) == null ? Forbid() : Ok(view);
    }

    /// <summary>The admin's History: every user's updates, or one user's.</summary>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("users/history")]
    public ActionResult<HistoryPage> ReadHistory([FromQuery] Guid? user = null, [FromQuery] int offset = 0, [FromQuery] int limit = DefaultPageSize) =>
        user is { } only && userManager.GetUserById(only) == null ? NotFound() : Storage(() => history.Read(user, offset, limit));

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("users/activity")]
    public ActionResult<UserActivity[]> ReadActivity() => Storage(() => history.Activity(null, attention.ForAll()).ToArray());

    private ActionResult<T> Storage<T>(Func<T> read) {
        Response.Headers.CacheControl = "no-store";
        try {
            return Ok(read());
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            return Unavailable(error);
        }
    }

    private ObjectResult Unavailable(Exception error) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new StorageUnavailable(true, error.Message));
}
