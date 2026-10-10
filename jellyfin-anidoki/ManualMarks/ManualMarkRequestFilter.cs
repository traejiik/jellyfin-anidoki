#nullable enable
using System;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Notifications;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.ManualMarks;

public sealed class ManualMarkRequestFilter(AuthenticatedSessionResolver resolver, ManualMarkCapabilityRegistry registry,
    ManualMarkStore store, ILibraryManager library, IUserManager users, IUserDataManager data,
    TimeProvider clock, ILogger<ManualMarkRequestFilter> logger, IAuthorizationContext authorization) : IAsyncActionFilter
{
    internal static bool IsPlayedAction(string? controller, string method, string verb) =>
        controller == "Jellyfin.Api.Controllers.PlaystateController" &&
        ((method is "MarkPlayedItem" or "MarkPlayedItemLegacy") && verb == "POST" ||
         (method is "MarkUnplayedItem" or "MarkUnplayedItemLegacy") && verb == "DELETE");

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Resolve before action execution, including unknown actions. Unknown enrolled
        // TogglePlayed events are withheld; only verified actions may create prompts.
        AuthenticatedSessionResolution resolved;
        bool apiKey = false;
        try
        {
            resolved = await resolver.Resolve(context.HttpContext);
            if (resolved.Identity == null) apiKey = (await authorization.GetAuthorizationInfo(context.HttpContext.Request))?.IsApiKey == true;
        }
        catch (Exception) { resolved = new(null, 403); logger.LogWarning("Manual request identity unavailable; tracker update withheld"); }
        var identity = resolved.Identity;
        var action = context.ActionDescriptor as ControllerActionDescriptor;
        var known = IsPlayedAction(action?.ControllerTypeInfo.FullName, action?.MethodInfo.Name ?? "", context.HttpContext.Request.Method);
        var itemId = known && context.ActionArguments.TryGetValue("itemId", out var value) && value is Guid id ? id : Guid.Empty;
        var targetUser = context.ActionArguments.TryGetValue("userId", out var target) && target is Guid userId ? userId : identity?.UserId ?? Guid.Empty;
        MediaBrowser.Controller.Entities.BaseItem? item = null;
        var alreadyPlayed = false;
        var withhold = registry.PersistenceUnreadable || (identity == null ? !apiKey : registry.RequiresConfirmation(identity.UserId, identity.AuthenticationKey));
        try
        {
            item = itemId == Guid.Empty ? null : library.GetItemById(itemId);
            var user = identity == null ? null : users.GetUserById(identity.UserId);
            alreadyPlayed = user != null && item != null && data.GetUserData(user, item).Played;
        }
        catch (Exception)
        {
            // A capture failure must not prevent Jellyfin's own action or permit fallback.
            withhold = true;
            item = null;
            logger.LogWarning("Manual request metadata unavailable; tracker update withheld");
        }
        var capture = new ManualMarkRequestContext(identity?.UserId ?? Guid.Empty, identity?.AuthenticationKey ?? "", itemId,
            withhold, alreadyPlayed, known && targetUser == identity?.UserId && item is Episode);
        context.HttpContext.Items[ManualMarkRequestContext.ItemKey] = capture;
        try
        {
            var result = await next();
            try
            {
                if (result.Exception != null && !result.ExceptionHandled || context.HttpContext.Response.StatusCode >= 400 ||
                    result.Result is StatusCodeResult status && status.StatusCode >= 400 ||
                    result.Result is ObjectResult obj && obj.StatusCode >= 400) return;
                if (identity == null || !withhold || !known || Plugin.Instance?.PluginConfiguration.watchedTickboxUpdatesProvider != true ||
                    context.HttpContext.Request.Method != "POST" || item == null || SyncHelper.MediaShouldBeIgnored(item)) return;
                var request = new ManualMarkRequest(Guid.NewGuid(), identity.UserId, identity.AuthenticationKey, itemId,
                    item is Episode episode ? episode.Series?.Name ?? episode.SeriesName ?? episode.Name : item.Name,
                    item is Episode ? item.IndexNumber : null, item is Episode ? item.Name : "",
                    clock.GetUtcNow(), ManualMarkAccounts.Fingerprint(identity.UserId));
                if (capture.ShouldPrompt)
                {
                    if (store.CreateOrGet(request) == null) logger.LogWarning("Manual confirmation capacity reached; tracker update withheld");
                }
                else if (!alreadyPlayed && item is not Episode || targetUser != identity.UserId)
                    store.RecordSuppressed(request, "This mark changes Jellyfin only. Use manual sync for bulk marks, movies or another user's trackers.");
            }
            catch (Exception) { logger.LogWarning("Manual confirmation capture failed; no automatic tracker fallback was scheduled"); }
        }
        finally { context.HttpContext.Items.Remove(ManualMarkRequestContext.ItemKey); }
    }
}
