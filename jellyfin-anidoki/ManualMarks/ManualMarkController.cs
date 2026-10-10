#nullable enable
using System;
using System.Threading.Tasks;
using jellyfin_anidoki.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jellyfin_anidoki.ManualMarks;

[ApiController, Route("AniDoki/user/manual-marks"), Authorize]
public sealed class ManualMarkController(ManualMarkStore store, ManualMarkCapabilityRegistry registry,
    AuthenticatedSessionResolver resolver, ManualMarkQueue queue) : ControllerBase
{
    private static bool Enabled => Plugin.Instance?.PluginConfiguration.watchedTickboxUpdatesProvider == true;
    private async Task<AuthenticatedSessionResolution> Identity()
    { Response.Headers.CacheControl = "no-store"; return await resolver.Resolve(HttpContext); }
    private ActionResult Rejected(AuthenticatedSessionResolution identity) => identity.FailureStatus == 401 ? Unauthorized() : Forbid();
    [HttpPost("capability")]
    public async Task<IActionResult> Enroll()
    {
        var identity = await Identity(); if (identity.Identity == null) return Rejected(identity);
        return registry.Enroll(identity.Identity.UserId, identity.Identity.AuthenticationKey) ? NoContent() : StatusCode(503, "Confirmation enrollment could not be saved. No enrollment was acknowledged.");
    }
    [HttpGet]
    public async Task<ActionResult<ManualMarkReadResponse>> Read()
    {
        var result = await Identity(); if (result.Identity == null) return Rejected(result);
        if (!Enabled) store.CancelPending("Manual tracker updates disabled.");
        return new ManualMarkReadResponse(Enabled, store.Read(result.Identity.UserId, result.Identity.AuthenticationKey));
    }
    [HttpGet("{operationId:guid}")]
    public async Task<ActionResult<PublicManualMarkOperation>> ReadOne(Guid operationId)
    {
        var result = await Identity(); if (result.Identity == null) return Rejected(result);
        var operation = store.Read(result.Identity.UserId, result.Identity.AuthenticationKey, operationId);
        return operation == null ? NotFound() : Ok(operation);
    }
    [HttpPost("{operationId:guid}/confirm")]
    public Task<ActionResult<PublicManualMarkOperation>> Confirm(Guid operationId, [FromBody] ManualMarkCommand command) => Command(operationId, command, confirm: true);
    [HttpPost("{operationId:guid}/dismiss")]
    public Task<ActionResult<PublicManualMarkOperation>> Dismiss(Guid operationId, [FromBody] ManualMarkCommand command) => Command(operationId, command, confirm: false);
    private async Task<ActionResult<PublicManualMarkOperation>> Command(Guid operationId, ManualMarkCommand command, bool confirm)
    {
        var result = await Identity(); if (result.Identity == null) return Rejected(result);
        var identity = result.Identity;
        var operation = store.Read(identity.UserId, identity.AuthenticationKey, operationId);
        if (operation == null) return NotFound();
        if (!Enabled) { store.CancelPending("Manual tracker updates disabled."); return StatusCode(409, store.Read(identity.UserId, identity.AuthenticationKey, operationId)); }
        if (operation.State == ManualMarkState.Expired) return StatusCode(410, operation);
        if (confirm && operation.State != ManualMarkState.Pending || !confirm && operation.State is not (ManualMarkState.Pending or ManualMarkState.Queued)) return Ok(operation);
        if (operation.Revision != command.ExpectedRevision) return StatusCode(409, operation);
        if (!confirm)
        {
            store.Dismiss(identity.UserId, identity.AuthenticationKey, operationId, command.ExpectedRevision);
            return Ok(store.Read(identity.UserId, identity.AuthenticationKey, operationId));
        }
        if (!store.TryQueue(identity.UserId, identity.AuthenticationKey, operationId, command.ExpectedRevision))
            return StatusCode(409, store.Read(identity.UserId, identity.AuthenticationKey, operationId));
        if (!queue.TryWrite(operationId)) { store.RestorePending(operationId); return StatusCode(503, store.Read(identity.UserId, identity.AuthenticationKey, operationId)); }
        return Accepted(store.Read(identity.UserId, identity.AuthenticationKey, operationId));
    }
}
