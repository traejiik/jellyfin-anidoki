using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Extensions;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.Sqlite;

namespace jellyfin_anidoki.Matching;

/// <summary>Admin routes for shared matches, and for any user's override (D12).</summary>
[ApiController]
[Route("AniDoki/matches")]
[Authorize(Policy = Policies.RequiresElevation)]
[MatchErrors]
public sealed class MatchesController(MatchService matches, IUserManager users) : ControllerBase {
    [HttpGet("sources")]
    public IActionResult Sources([FromQuery] string? search, [FromQuery] string? filter, [FromQuery] int page = 1) =>
        this.From(matches.ListSources(null, search, filter, page));

    [HttpGet("entries")]
    public Task<IActionResult> SearchEntries([FromQuery] ApiName provider, [FromQuery] string? query, CancellationToken cancellationToken) =>
        Requested(requester => matches.SearchEntries(requester, null, provider, query, cancellationToken));

    [HttpGet("entries/{provider}/{id:int}")]
    public Task<IActionResult> ReadEntry(ApiName provider, int id, [FromQuery] string? alternativeId, CancellationToken cancellationToken) =>
        Requested(requester => matches.ReadEntry(requester, provider, id, alternativeId, cancellationToken));

    [HttpPost("overrides/{userId:guid}/{sourceKey}/{provider}/promote")]
    public Task<IActionResult> Promote(Guid userId, string sourceKey, ApiName provider, [FromBody] PromoteRequest request, CancellationToken cancellationToken) =>
        Requested(requester => matches.Promote(userId, provider, sourceKey, request, requester, cancellationToken));

    [HttpGet("orphans")]
    public IActionResult Orphans() => this.From(matches.ListOrphans());

    [HttpPost("orphans/{sourceKey}/reattach")]
    public IActionResult Reattach(string sourceKey, [FromBody] ReattachRequest request) => this.From(matches.Reattach(sourceKey, request));

    [HttpDelete("orphans/{sourceKey}")]
    public IActionResult DeleteOrphan(string sourceKey) => this.From(matches.DeleteOrphan(sourceKey));

    [HttpGet("users/{userId:guid}/{sourceKey}/{provider}")]
    public IActionResult ReadOverride(Guid userId, string sourceKey, ApiName provider) =>
        Target(userId) is { } owner ? this.From(matches.ReadOverride(new MatchScope(provider, sourceKey, owner))) : NotFound();

    /// <summary>Saved for the user, as if they had saved it: it lives in their library, not the admin's.</summary>
    [HttpPut("users/{userId:guid}/{sourceKey}/{provider}")]
    public Task<IActionResult> SaveOverride(Guid userId, string sourceKey, ApiName provider, [FromBody] SaveMatchRequest request, CancellationToken cancellationToken) =>
        Target(userId) is { } owner
            ? Requested(requester => matches.Save(new MatchScope(provider, sourceKey, owner), request, requester, owner, cancellationToken))
            : Task.FromResult<IActionResult>(NotFound());

    [HttpDelete("users/{userId:guid}/{sourceKey}/{provider}")]
    public Task<IActionResult> DeleteOverride(Guid userId, string sourceKey, ApiName provider, [FromQuery] long expectedRevision) =>
        Requested(requester => Task.FromResult(matches.Delete(new MatchScope(provider, sourceKey, userId), expectedRevision, requester, null)));

    [HttpGet("{sourceKey}")]
    public IActionResult Detail(string sourceKey) => this.From(matches.Detail(sourceKey));

    [HttpGet("{sourceKey}/{provider}/automatic")]
    public Task<IActionResult> Automatic(string sourceKey, ApiName provider, CancellationToken cancellationToken) =>
        Requested(requester => matches.Automatic(sourceKey, provider, requester, null, cancellationToken));

    [HttpPut("{sourceKey}/{provider}")]
    public Task<IActionResult> Save(string sourceKey, ApiName provider, [FromBody] SaveMatchRequest request, CancellationToken cancellationToken) =>
        Requested(requester => matches.Save(new MatchScope(provider, sourceKey), request, requester, null, cancellationToken));

    [HttpDelete("{sourceKey}/{provider}")]
    public Task<IActionResult> Delete(string sourceKey, ApiName provider, [FromQuery] long expectedRevision) =>
        Requested(requester => Task.FromResult(matches.Delete(new MatchScope(provider, sourceKey), expectedRevision, requester, null)));

    /// <param name="userId">Whose list state and watched episodes the progress uses; none for the episode table alone.</param>
    [HttpPost("{sourceKey}/{provider}/preview")]
    public Task<IActionResult> Preview(string sourceKey, ApiName provider, [FromQuery] Guid? userId, [FromBody] PreviewRequest request,
        CancellationToken cancellationToken) =>
        Requested(requester => matches.Preview(new MatchScope(provider, sourceKey), request, requester, userId == Guid.Empty ? null : userId, null, cancellationToken));

    /// <summary>The user an admin route names, through the same check as the settings routes.</summary>
    private Guid? Target(Guid userId) => users.GetUser(User, userId)?.Id;

    private async Task<IActionResult> Requested(Func<Guid, Task<MatchResult>> run) =>
        users.GetUser(User, null) is { } requester ? this.From(await run(requester.Id)) : Unauthorized();
}

/// <summary>Routes for the signed-in user's own overrides. The user always comes from the sign-in, never the request.</summary>
[ApiController]
[Route("AniDoki/user/matches")]
[Authorize]
[MatchErrors]
public sealed class UserMatchesController(MatchService matches, IUserManager users) : ControllerBase {
    [HttpGet("sources")]
    public IActionResult Sources([FromQuery] string? search, [FromQuery] string? filter, [FromQuery] int page = 1) =>
        Me() is { } user ? this.From(matches.ListSources(user, search, filter, page)) : Unauthorized();

    /// <summary>Through the user's own link only.</summary>
    [HttpGet("entries")]
    public async Task<IActionResult> SearchEntries([FromQuery] ApiName provider, [FromQuery] string? query, CancellationToken cancellationToken) =>
        Me() is { } user ? this.From(await matches.SearchEntries(user, user, provider, query, cancellationToken)) : Unauthorized();

    [HttpGet("{sourceKey}")]
    public IActionResult Detail(string sourceKey) => Me() is { } user ? this.From(matches.UserDetail(sourceKey, user)) : Unauthorized();

    /// <summary>What automatic matching finds for the season, through the user's own link only.</summary>
    [HttpGet("{sourceKey}/{provider}/automatic")]
    public async Task<IActionResult> Automatic(string sourceKey, ApiName provider, CancellationToken cancellationToken) =>
        Me() is { } user ? this.From(await matches.Automatic(sourceKey, provider, user, user, cancellationToken)) : Unauthorized();

    [HttpPut("{sourceKey}/{provider}")]
    public async Task<IActionResult> Save(string sourceKey, ApiName provider, [FromBody] SaveMatchRequest request, CancellationToken cancellationToken) =>
        Me() is { } user ? this.From(await matches.Save(new MatchScope(provider, sourceKey, user), request, user, user, cancellationToken)) : Unauthorized();

    [HttpDelete("{sourceKey}/{provider}")]
    public IActionResult Delete(string sourceKey, ApiName provider, [FromQuery] long expectedRevision) =>
        Me() is { } user ? this.From(matches.Delete(new MatchScope(provider, sourceKey, user), expectedRevision, user, user)) : Unauthorized();

    [HttpPost("{sourceKey}/{provider}/preview")]
    public async Task<IActionResult> Preview(string sourceKey, ApiName provider, [FromBody] PreviewRequest request, CancellationToken cancellationToken) =>
        Me() is { } user ? this.From(await matches.Preview(new MatchScope(provider, sourceKey, user), request, user, user, user, cancellationToken)) : Unauthorized();

    private Guid? Me() => users.GetUser(User, null)?.Id;
}

internal static class MatchResponses {
    public static IActionResult From(this ControllerBase controller, MatchResult result) => result.Outcome switch {
        MatchOutcome.Ok => controller.Ok(result.Body),
        MatchOutcome.NotFound => controller.NotFound(),
        MatchOutcome.BadRequest => controller.BadRequest(result.Body),
        MatchOutcome.Invalid => controller.UnprocessableEntity(result.Body),
        MatchOutcome.Conflict => controller.Conflict(result.Body),
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };
}

/// <summary>Storage that is unavailable answers 503, and a tracker that fails while checking a match 502; neither has written anything.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class MatchErrorsAttribute : ExceptionFilterAttribute {
    public override void OnException(ExceptionContext context) {
        (int status, string message)? answer = context.Exception switch {
            TrackerFailureException failure => (StatusCodes.Status502BadGateway, $"The tracker did not answer ({failure.Kind}). Nothing was saved."),
            SqliteException or InvalidOperationException => (StatusCodes.Status503ServiceUnavailable, "AniDoki storage is unavailable. Nothing was saved."),
            _ => null
        };
        if (answer is var (status, message)) {
            context.Result = new ObjectResult(message) { StatusCode = status };
            context.ExceptionHandled = true;
        }
    }
}
