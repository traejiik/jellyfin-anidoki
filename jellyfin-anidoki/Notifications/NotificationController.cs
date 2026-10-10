#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jellyfin_anidoki.Notifications;

[ApiController, Route("AniDoki/user/events"), Authorize]
public sealed class NotificationController(NotificationEventStore store, IAuthorizationContext authorization,
    ISessionManager sessions, IUserManager users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationReadResponse>> Read([FromQuery] string? cursor = null)
    {
        Response.Headers.CacheControl = "no-store";
        var auth = await authorization.GetAuthorizationInfo(Request);
        if (auth == null || auth.IsApiKey || auth.UserId == Guid.Empty || string.IsNullOrWhiteSpace(auth.Token)) return Unauthorized();
        if (users.GetUserById(auth.UserId) == null) return Unauthorized();
        // Null selects the token's stored device. Passing the request header here
        // would let it influence Jellyfin's session lookup/creation.
        var session = await sessions.GetSessionByAuthenticationToken(auth.Token, null!, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
        if (session == null || session.UserId != auth.UserId || string.IsNullOrWhiteSpace(session.Id) ||
            string.IsNullOrWhiteSpace(auth.DeviceId) || !string.Equals(session.DeviceId, auth.DeviceId, StringComparison.OrdinalIgnoreCase)) return Forbid();
        var enabled = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(c => c.UserId == auth.UserId)?.ShowLogNotifications ?? true;
        return store.Read(auth.UserId, session.Id, cursor, enabled);
    }
}
