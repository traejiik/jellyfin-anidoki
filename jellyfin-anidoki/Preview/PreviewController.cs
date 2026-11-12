using System;
using System.Net.Http;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Enums;
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
    ISeasonWatchState watchState, IUserManager users, ILibraryManager library, ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor,
    IApplicationPaths applicationPaths, IMemoryCache memoryCache) : ControllerBase {
    private const string Unavailable = "AniDoki storage is unavailable, so the preview could not be started or read.";

    [HttpPost]
    public ActionResult<OperationStarted> Start([FromQuery] Guid userId, [FromQuery] SyncAction syncAction, [FromQuery] string? provider) {
        if (userId == Guid.Empty || users.GetUserById(userId) == null)
            return BadRequest("Select an existing Jellyfin user.");
        if (syncAction != SyncAction.UpdateProvider)
            return BadRequest("Only a sync to trackers can be previewed so far.");
        if (TrackerChoice.ReadOutbound(provider, TrackerChoice.Linked(userId), out ApiName? only) is { } problem)
            return BadRequest(problem);
        try {
            // A preview would wait behind a sync to trackers anyway; 409 says which run to watch instead.
            Guid? active = store.FindActive(userId, OperationKind.PreviewToTrackers) ?? store.FindActive(userId, OperationKind.SyncToTrackers);
            if (active is { } running)
                return Conflict(new OperationStarted(running));
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Unavailable);
        }

        var preview = new OutboundPreview(users, library, loggerFactory, httpClientFactory, serverApplicationHost, httpContextAccessor,
            applicationPaths, memoryCache, new Delayer(), matches, rows, watchState, userId) { OnlyProvider = only };
        OperationRunner.Handle? handle = runner.Enqueue(userId, OperationKind.PreviewToTrackers, only?.ToString() ?? "All linked trackers", preview.Run);
        if (handle == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Unavailable);
        return Accepted(new OperationStarted(handle.OperationId));
    }

    [HttpGet("{operationId:guid}")]
    public ActionResult<PreviewView> Read(Guid operationId) => Guarded<PreviewView>(() => {
        if (store.Read(operationId) is not { Kind: OperationKind.PreviewToTrackers } view)
            return NotFound();
        return new PreviewView(view, rows.Counts(operationId), rows.IsApplicable(view));
    });

    [HttpGet("{operationId:guid}/rows")]
    public ActionResult<PreviewPage> Rows(Guid operationId, [FromQuery] PreviewRowKind? kind, [FromQuery] ApiName? provider,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50) {
        if (offset < 0 || limit < 1 || limit > PreviewStore.MaxPageSize)
            return BadRequest($"Use an offset of 0 or more and a limit from 1 to {PreviewStore.MaxPageSize}.");
        return Guarded<PreviewPage>(() => {
            if (store.Read(operationId) is not { Kind: OperationKind.PreviewToTrackers })
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
