using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.AnimeIds;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Preview;

/// <summary>Starts, reads and applies previews of a manual sync. Admin only, like manual sync (D9).</summary>
[ApiController]
[Route("AniDoki/preview")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class PreviewController(OperationRunner runner, OperationStore store, PreviewStore rows, MatchStore matches,
    ISeasonWatchState watchState, IUserManager users, ILibraryManager library, IUserDataManager userData, ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor,
    ArmClient arm, AnimeListSource animeList, IMemoryCache memoryCache) : ControllerBase {
    private const string Unavailable = "AniDoki storage is unavailable, so the preview could not be started, read or applied.";
    private const string Refresh = "This preview can no longer be applied. Refresh the preview.";

    /// <summary>The most rows one apply takes.</summary>
    public const int MaxApplyRows = 10000;

    [HttpPost]
    public ActionResult<OperationStarted> Start([FromQuery] Guid userId, [FromQuery] SyncAction syncAction, [FromQuery] string? provider,
        [FromQuery] SyncHelper.StatusFilter status = SyncHelper.StatusFilter.Both) {
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
            var sync = new Sync(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, users, library, arm, animeList,
                userData, memoryCache, delayer, only!.Value, status);
            run = new InboundPreview(sync, rows, users, userData, only.Value, userId).Run;
            source = $"{only} ({status})";
        } else {
            run = new OutboundPreview(users, library, loggerFactory, httpClientFactory, serverApplicationHost, httpContextAccessor,
                arm, animeList, memoryCache, delayer, matches, rows, watchState, userId) { OnlyProvider = only }.Run;
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
        return new PreviewView(view, rows.Counts(operationId), rows.IsApplicable(view), rows.AppliedBy(operationId));
    });

    [HttpGet("{operationId:guid}/rows")]
    public ActionResult<PreviewPage> Rows(Guid operationId, [FromQuery] PreviewRowKind[]? kind, [FromQuery] ApiName? provider,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] bool applied = false) {
        if (offset < 0 || limit < 1 || limit > PreviewStore.MaxPageSize)
            return BadRequest($"Use an offset of 0 or more and a limit from 1 to {PreviewStore.MaxPageSize}.");
        return Guarded<PreviewPage>(() => {
            if (store.Read(operationId) is not { Kind: OperationKind.PreviewToTrackers or OperationKind.PreviewFromTrackers })
                return NotFound();
            return rows.Page(operationId, kind, provider, offset, limit, applied);
        });
    }

    /// <summary>Every row of a preview that can be applied, with whether it starts selected, so the page can select rows it has not loaded.</summary>
    [HttpGet("{operationId:guid}/selectable")]
    public ActionResult<IReadOnlyList<SelectableRow>> Selectable(Guid operationId) => Guarded<IReadOnlyList<SelectableRow>>(() => {
        if (store.Read(operationId) is not { Kind: OperationKind.PreviewToTrackers or OperationKind.PreviewFromTrackers })
            return NotFound();
        return Ok(rows.Selectable(operationId));
    });

    /// <summary>
    /// Applies the chosen rows of a preview, each as previewed or not at all (D4): a preview to trackers as a <c>SyncToTrackers</c>
    /// run, a preview from trackers as a <c>SyncFromTrackers</c> run, either naming its preview. Only the newest cleanly finished
    /// preview, within a day and not yet applied, can be applied.
    /// </summary>
    [HttpPost("{operationId:guid}/apply")]
    public ActionResult<OperationStarted> Apply(Guid operationId, [FromBody] ApplyRequest? request) {
        IReadOnlyList<int> seqs = request?.Rows?.Distinct().ToList() ?? [];
        if (seqs.Count == 0 || seqs.Count > MaxApplyRows)
            return BadRequest($"Choose from 1 to {MaxApplyRows} rows to apply.");
        try {
            if (store.Read(operationId) is not { Kind: OperationKind.PreviewToTrackers or OperationKind.PreviewFromTrackers } preview)
                return NotFound();
            bool inbound = preview.Kind == OperationKind.PreviewFromTrackers;
            OperationKind syncKind = inbound ? OperationKind.SyncFromTrackers : OperationKind.SyncToTrackers;
            if ((store.FindActive(preview.UserId, syncKind) ?? store.FindActive(preview.UserId, preview.Kind)) is { } running)
                return Conflict(new OperationStarted(running));
            if (!rows.IsApplicable(preview))
                return Conflict(Refresh);
            IReadOnlyList<PreviewRow> chosen = rows.Rows(operationId, seqs);
            if (chosen.Count != seqs.Count || chosen.Any(row => !row.Selectable))
                return BadRequest("Choose only rows of this preview that can be applied.");
            Guid applyId = Guid.NewGuid();
            // Taking the rows before queueing means two applies of one preview cannot both start.
            if (!rows.Claim(operationId, seqs, applyId))
                return Conflict(Refresh);
            Func<OperationContext, Task> run = inbound
                ? new InboundApply(new Sync(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, users, library,
                    arm, animeList, userData, memoryCache, new Delayer(), chosen[0].Provider, SyncHelper.StatusFilter.Both), library, users, userData,
                    loggerFactory, preview.UserId, chosen).Run
                : new PreviewApply(library, loggerFactory, httpClientFactory, serverApplicationHost, httpContextAccessor, memoryCache,
                    new Delayer(), matches, watchState, preview.UserId, chosen).Run;
            string source = inbound ? Seasons(chosen.Count) : Selected(chosen.Count);
            OperationRunner.Handle? handle = runner.Enqueue(preview.UserId, syncKind, source, run, applyId, operationId);
            if (handle == null) {
                rows.Release(operationId, applyId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, Unavailable);
            }
            return Accepted(new OperationStarted(handle.OperationId));
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Unavailable);
        }
    }

    private static string Selected(int count) => count == 1 ? "1 selected change from a preview" : $"{count} selected changes from a preview";

    private static string Seasons(int count) => count == 1 ? "1 selected season from a preview" : $"{count} selected seasons from a preview";

    private ActionResult<T> Guarded<T>(Func<ActionResult<T>> read) {
        try {
            return read();
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Unavailable);
        }
    }
}
