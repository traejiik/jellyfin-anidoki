using System;
using System.Net.Http;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Enums;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Operations;

/// <summary>Starts manual syncs in the background and reports on operations.</summary>
[ApiController]
[Route("AniDoki")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class SyncOperationsController(OperationRunner runner, OperationStore store, IUserManager userManager,
    ILibraryManager libraryManager, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory,
    IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor, IApplicationPaths applicationPaths,
    IUserDataManager userDataManager, IMemoryCache memoryCache) : ControllerBase {
    [HttpPost("sync")]
    public ActionResult<OperationStarted> StartSync([FromQuery] ApiName provider, [FromQuery] Guid userId,
        [FromQuery] SyncHelper.Status status, [FromQuery] SyncAction syncAction) {
        if (userId == Guid.Empty || userManager.GetUserById(userId) == null)
            return BadRequest("Select an existing Jellyfin user.");
        OperationKind kind = syncAction == SyncAction.UpdateProvider ? OperationKind.SyncToTrackers : OperationKind.SyncFromTrackers;
        if (store.FindActive(userId, kind) is { } active)
            return Conflict(new OperationStarted(active));
        var delayer = new Delayer();
        OperationRunner.Handle? handle = kind == OperationKind.SyncToTrackers
            ? runner.Enqueue(userId, kind, "Linked trackers", operation => new SyncProviderFromLocal(userManager, libraryManager, loggerFactory,
                httpClientFactory, serverApplicationHost, httpContextAccessor, applicationPaths, memoryCache, delayer, userId.ToString()).SyncFromLocal(operation))
            : runner.Enqueue(userId, kind, $"{provider} ({status})", operation => new Sync(httpClientFactory, loggerFactory, serverApplicationHost,
                httpContextAccessor, userManager, libraryManager, applicationPaths, userDataManager, memoryCache, delayer, provider, status).SyncFromProvider(userId.ToString(), operation));
        if (handle == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "AniDoki storage is unavailable, so the sync was not started.");
        return Accepted(new OperationStarted(handle.OperationId));
    }

    [HttpGet("operations/{operationId:guid}")]
    public ActionResult<OperationView> ReadOperation(Guid operationId) =>
        store.Read(operationId) is { } view ? Ok(view) : NotFound();

    [HttpPost("operations/{operationId:guid}/cancel")]
    public ActionResult<OperationView> CancelOperation(Guid operationId) {
        var view = store.Read(operationId);
        if (view == null)
            return NotFound();
        if (OperationStates.IsFinished(view.State))
            return Conflict(view);
        runner.Cancel(operationId);
        return Accepted(store.Read(operationId));
    }
}
