using System;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace jellyfin_anidoki.Notifications.Discord;

public sealed record DiscordTestCommand(long ExpectedRevision);
[ApiController, Route("AniDoki/user/discord"), Authorize]
public sealed class DiscordNotificationController(AuthenticatedSessionResolver resolver, DiscordSettingsService settings,
    DiscordNotificationDispatcher dispatcher, DiscordPosterProvider posters, IUserManager users, ILibraryManager library, IServerApplicationHost host) : ControllerBase {
    private async Task<AuthenticatedSessionResolution> Identity() { Response.Headers.CacheControl = "no-store"; return await resolver.Resolve(HttpContext); }
    private ActionResult Rejected(AuthenticatedSessionResolution identity) => identity.FailureStatus == 401 ? Unauthorized() : Forbid();
    [HttpGet("settings")]
    public async Task<IActionResult> Read() { var caller = await Identity(); return caller.Identity == null ? Rejected(caller) : Ok(settings.Read(caller.Identity.UserId)); }
    [HttpPut("settings")]
    public async Task<IActionResult> Save([FromBody] DiscordSettingsCommand command) {
        var caller = await Identity();
        if (caller.Identity == null)
            return Rejected(caller);
        var result = settings.Save(caller.Identity.UserId, command);
        return result.Error == null ? Ok(result.Settings) : StatusCode(result.Status, new { message = result.Error, settings = result.Settings });
    }
    private BaseItem? Example(Guid userId) {
        var user = users.GetUserById(userId);
        if (user == null)
            return null;
        var items = library.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie], Limit = 1, Recursive = true });
        return items.Count > 0 ? items[0] : null;
    }
    [HttpGet("preview")]
    public async Task<IActionResult> Preview() {
        var caller = await Identity();
        if (caller.Identity == null)
            return Rejected(caller);
        var item = Example(caller.Identity.UserId);
        if (item == null)
            return Ok(new { empty = true, message = "Add an accessible show or movie to preview its notification." });
        var summary = SyncOperationSummary.Capture(item, caller.Identity.UserId, Guid.NewGuid(), SyncNotificationTrigger.Playback, new());
        string? link = null;
        if (DiscordSettingsService.AddressReady && summary.ShowItemId != Guid.Empty)
            link = JellyfinShowLink.Build(Plugin.Instance!.PluginConfiguration.callbackUrl, summary.ShowItemId, host.SystemId);
        var example = summary with {
            Outcomes = [new ProviderWriteOutcome(Configuration.ApiName.AniList, "preview", summary.ShowTitle,
            OutcomeKind.Confirmed, summary.IsMovie ? null : summary.EpisodeEndNumber ?? summary.EpisodeNumber ?? 1,
            summary.IsMovie ? Models.Mal.Status.Completed : Models.Mal.Status.Watching, summary.IsMovie, "preview", true)]
        };
        return Ok(new {
            empty = false,
            showTitle = summary.ShowTitle,
            showUrl = link,
            episodeLine = DiscordEmbedBuilder.EpisodeLine(summary),
            posterItemId = summary.ShowItemId,
            fields = DiscordEmbedBuilder.Fields(example),
            serverName = host.FriendlyName,
            footerIconUrl = DiscordEmbedBuilder.JellyfinLogoUrl,
            timestamp = summary.CreatedUtc,
            previewOnly = true
        });
    }
    [HttpGet("preview/poster")]
    public async Task<IActionResult> Poster([FromQuery] Guid itemId) {
        var caller = await Identity();
        if (caller.Identity == null)
            return Rejected(caller);
        var user = users.GetUserById(caller.Identity.UserId);
        var item = library.GetItemById(itemId);
        if (user == null || item == null || !item.IsVisible(user, false))
            return NotFound();
        var bytes = await posters.Read(item, HttpContext.RequestAborted);
        return bytes == null ? NotFound() : File(bytes, "image/jpeg");
    }
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] DiscordTestCommand command) {
        var caller = await Identity();
        if (caller.Identity == null)
            return Rejected(caller);
        var user = caller.Identity.UserId;
        var current = settings.Read(user);
        if (current.Revision != command.ExpectedRevision)
            return StatusCode(409, new { message = "Discord settings changed. Reload before testing." });
        if (!current.HasWebhook || !current.PublicAddressReady)
            return StatusCode(422, new { message = "Save a webhook and configure the server Public address before testing." });
        var item = Example(user);
        var summary = item == null ? null : SyncOperationSummary.Capture(item, user, Guid.NewGuid(), SyncNotificationTrigger.Playback, new());
        if (summary?.ShowItemId == Guid.Empty)
            summary = null;
        var completion = dispatcher.Test(user, current.Revision, summary);
        if (completion == null)
            return StatusCode(429, new { message = "Wait one minute before another test, or try later if the queue is full." });
        var result = await completion.WaitAsync(HttpContext.RequestAborted);
        return Ok(new { confirmed = result.Kind == DiscordDeliveryKind.Delivered, message = result.Message });
    }
}
