using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Operations;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Matching;

public sealed record ResolutionCheckReport(
    [property: JsonPropertyName("operation")] OperationView Operation,
    [property: JsonPropertyName("rows")] IReadOnlyList<ResolutionCheckRow> Rows);

/// <summary>Starts and reads the read-only resolution check. Admin only: its report shows what a user has watched.</summary>
[ApiController]
[Route("AniDoki/matches/check")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ResolutionCheckController(OperationRunner runner, OperationStore store, ResolutionCheckStore rows, MatchStore matches,
    ISeasonWatchState watchState, IUserManager users, ILibraryManager library, IUserDataManager userData, ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor,
    IApplicationPaths applicationPaths, IMemoryCache memoryCache) : ControllerBase {
    [HttpPost]
    public ActionResult<OperationStarted> Start([FromQuery] Guid userId) {
        if (userId == Guid.Empty || users.GetUserById(userId) == null)
            return BadRequest("Select an existing Jellyfin user.");
        if (store.FindActive(userId, OperationKind.ResolutionCheck) is { } active)
            return Conflict(new OperationStarted(active));
        var check = new ResolutionCheck(users, library, userData, loggerFactory, httpClientFactory, serverApplicationHost, httpContextAccessor,
            applicationPaths, memoryCache, new Delayer(), matches, rows, watchState, userId);
        OperationRunner.Handle? handle = runner.Enqueue(userId, OperationKind.ResolutionCheck, "Resolution check", check.Run);
        if (handle == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "AniDoki storage is unavailable, so the check was not started.");
        return Accepted(new OperationStarted(handle.OperationId));
    }

    [HttpGet("{operationId:guid}")]
    public ActionResult<ResolutionCheckReport> Read(Guid operationId) =>
        store.Read(operationId) is { Kind: OperationKind.ResolutionCheck } view ? Ok(new ResolutionCheckReport(view, rows.Read(operationId))) : NotFound();
}
