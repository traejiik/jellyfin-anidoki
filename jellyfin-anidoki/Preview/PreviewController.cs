using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Enums;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Operations;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Preview;

/// <summary>Starts and reads read-only previews of a manual sync. Admin only, like manual sync (D9).</summary>
[ApiController]
[Route("AniDoki/preview")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class PreviewController(OperationRunner runner, OperationStore store, PreviewStore rows, MatchStore matches,
    ISeasonWatchState watchState, IUserManager users, ILibraryManager library, IUserDataManager userData, ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor,
    IApplicationPaths applicationPaths, IMemoryCache memoryCache) : ControllerBase {
    private const string Unavailable = "AniDoki storage is unavailable, so the preview could not be started or read.";

    [HttpPost]
    public ActionResult<OperationStarted> Start([FromQuery] Guid userId, [FromQuery] SyncAction syncAction, [FromQuery] string? provider,
        [FromQuery] SyncHelper.Status status = SyncHelper.Status.Both) {
        if (userId == Guid.Empty || users.GetUserById(userId) == null)
            return BadRequest("Select an existing Jellyfin user.");
        bool inbound = syncAction == SyncAction.UpdateJellyfin;
        ApiName? only;
        if (inbound) {
            // Sync from trackers reads lists from these three only.
            only = TrackerChoice.One(provider);
            if (only is not (ApiName.Mal or ApiName.AniList or ApiName.Kitsu) || !TrackerChoice.Linked(userId).Contains(only.Value))
                return BadRequest("Choose one linked tracker to bring progress from: MyAnimeList, AniList or Kitsu.");
        } else if (TrackerChoice.ReadOutbound(provider, TrackerChoice.Linked(userId), out only) is { } problem) {
            return BadRequest(problem);
        }

        OperationKind kind = inbound ? OperationKind.PreviewFromTrackers : OperationKind.PreviewToTrackers;
        OperationKind syncKind = inbound ? OperationKind.SyncFromTrackers : OperationKind.SyncToTrackers;
        try {
            // A preview would wait behind a sync in the same direction anyway; 409 says which run to watch instead.
            Guid? active = store.FindActive(userId, kind) ?? store.FindActive(userId, syncKind);
            if (active is { } running)
                return Conflict(new OperationStarted(running));
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Unavailable);
        }

        var delayer = new Delayer();
        Func<OperationContext, Task> run;
        string source;
        if (inbound) {
            var sync = new Sync(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, users, library, applicationPaths,
                userData, memoryCache, delayer, only!.Value, status);
            run = new InboundPreview(sync, rows, users, userData, only.Value, userId).Run;
            source = $"{only} ({status})";
        } else {
            run = new OutboundPreview(users, library, loggerFactory, httpClientFactory, serverApplicationHost, httpContextAccessor,
                applicationPaths, memoryCache, delayer, matches, rows, watchState, userId) { OnlyProvider = only }.Run;
            source = only?.ToString() ?? "All linked trackers";
        }
        OperationRunner.Handle? handle = runner.Enqueue(userId, kind, source, run);
        if (handle == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Unavailable);
        return Accepted(new OperationStarted(handle.OperationId));
    }

    [HttpGet("{operationId:guid}")]
    public ActionResult<PreviewView> Read(Guid operationId) => Guarded<PreviewView>(() => {
        if (store.Read(operationId) is not { Kind: OperationKind.PreviewToTrackers or OperationKind.PreviewFromTrackers } view)
            return NotFound();
        return new PreviewView(view, rows.Counts(operationId), rows.IsApplicable(view));
    });

    [HttpGet("{operationId:guid}/rows")]
    public ActionResult<PreviewPage> Rows(Guid operationId, [FromQuery] PreviewRowKind? kind, [FromQuery] ApiName? provider,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50) {
        if (offset < 0 || limit < 1 || limit > PreviewStore.MaxPageSize)
            return BadRequest($"Use an offset of 0 or more and a limit from 1 to {PreviewStore.MaxPageSize}.");
        return Guarded<PreviewPage>(() => {
            if (store.Read(operationId) is not { Kind: OperationKind.PreviewToTrackers or OperationKind.PreviewFromTrackers })
                return NotFound();
            return rows.Page(operationId, kind, provider, offset, limit);
        });
    }

    private ActionResult<T> Guarded<T>(Func<ActionResult<T>> read) {
        try {
            return read();
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Unavailable);
        }
    }
}
