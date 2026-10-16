using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jellyfin_anidoki.Notifications;

[ApiController, Route("AniDoki/user/events"), Authorize]
public sealed class NotificationController(NotificationEventStore store, AuthenticatedSessionResolver resolver) : ControllerBase {
    [HttpGet]
    public async Task<ActionResult<NotificationReadResponse>> Read([FromQuery] string? cursor = null) {
        Response.Headers.CacheControl = "no-store";
        var resolved = await resolver.Resolve(HttpContext);
        if (resolved.Identity == null)
            return resolved.FailureStatus == 401 ? Unauthorized() : Forbid();
        var identity = resolved.Identity;
        var enabled = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(c => c.UserId == identity.UserId)?.ShowLogNotifications ?? true;
        return store.Read(identity.UserId, identity.SessionId, cursor, enabled);
    }
}
