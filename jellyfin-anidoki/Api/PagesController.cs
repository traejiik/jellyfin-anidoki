using System.Collections.Generic;
using System.IO;
using System.Linq;
using jellyfin_anidoki.Helpers;
using MediaBrowser.Model;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Api;

/// <summary>Serves the plugin's static assets and the user pages.</summary>
[ApiController]
[Route("AniDoki")]
public sealed class PagesController : ControllerBase {
    private readonly ILogger<PagesController> _logger;

    public PagesController(ILoggerFactory loggerFactory) {
        _logger = loggerFactory.CreateLogger<PagesController>();
    }

    [AllowAnonymous]
    [HttpGet("assets/{asset}")]
    [HttpGet("assets/v/{version}/{asset}")]
    public IActionResult GetAsset([FromRoute] string asset) {
        string? resource = asset switch {
            "styles.css" => "Anidoki.css",
            "notifications.js" => "NotificationsJs.js",
            "notification-state.js" => "NotificationStateJs.js",
            "manual-mark-state.js" => "ManualMarkStateJs.js",
            "plugin-pages-guard.js" => "PluginPagesGuardJs.js",
            "outcome-lines.js" => "OutcomeLinesJs.js",
            "discord-settings.js" => "DiscordSettingsJs.js",
            "anidoki-avatar.png" => "Images.anidoki-avatar.png",
            "anidoki-symbol.svg" => "Images.anidoki-symbol.svg",
            "common.js" => "CommonJs.js",
            "config-state.js" => "ConfigStateJs.js",
            "history.js" => "HistoryJs.js",
            "history-state.js" => "HistoryStateJs.js",
            "user-settings.js" => "ConfigPageUserJs.js",
            "match-editor.js" => "MatchEditorJs.js",
            "match-editor-state.js" => "MatchEditorStateJs.js",
            "preview-state.js" => "PreviewStateJs.js",
            "user-matches.js" => "UserMatchesJs.js",
            "provider-anilist.svg" => "Images.provider-anilist.svg",
            "provider-mal.svg" => "Images.provider-mal.svg",
            "provider-kitsu.svg" => "Images.provider-kitsu.svg",
            "provider-annict.png" => "Images.provider-annict.png",
            "provider-shikimori.svg" => "Images.provider-shikimori.svg",
            "provider-simkl.svg" => "Images.provider-simkl.svg",
            "jellyfin.svg" => "Images.jellyfin.svg",
            "jellyfin-banner.svg" => "Images.jellyfin-banner.svg",
            "noto-sans-latin-400.woff2" => "Fonts.noto-sans-latin-400.woff2",
            "noto-sans-latin-700.woff2" => "Fonts.noto-sans-latin-700.woff2",
            "noto-sans-latin-ext-400.woff2" => "Fonts.noto-sans-latin-ext-400.woff2",
            "noto-sans-latin-ext-700.woff2" => "Fonts.noto-sans-latin-ext-700.woff2",
            _ => null
        };
        if (resource == null)
            return NotFound();
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Configuration.{resource}");
        if (stream == null)
            return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(stream, MimeTypes.GetMimeType(resource));
    }

    [Authorize]
    [HttpGet("{viewName}")]
    public ActionResult GetView([FromRoute] string viewName) {
        if (Plugin.Instance == null) {
            return BadRequest("No plugin instance found");
        }

        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();

        IEnumerable<PluginPageInfo> pages = Plugin.Instance.GetViews();

        if (pages == null) {
            return NotFound("Pages is null or empty");
        }

        PluginPageInfo? view = pages.FirstOrDefault(pageInfo => pageInfo?.Name == viewName, null);

        if (view == null) {
            return NotFound("No matching view found");
        }

        Stream? stream = Plugin.Instance.GetType().Assembly.GetManifestResourceStream(view.EmbeddedResourcePath);

        if (stream == null) {
            _logger.LogError("Could not find the embedded page {Resource}", view.EmbeddedResourcePath);
            return NotFound();
        }

        return File(stream, MimeTypes.GetMimeType(view.EmbeddedResourcePath));
    }
}
