using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using jellyfin_anidoki.Enums;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Matching;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchApiFixture;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

/// <summary>
/// Who may reach which match route. Jellyfin enforces the policies, so the attributes are checked here; the ownership
/// rules the controllers enforce themselves are run.
/// </summary>
[TestFixture]
public class MatchAuthorizationTests {
    private MatchApiFixture _fixture = null!;

    [SetUp]
    public void SetUp() {
        _fixture = new MatchApiFixture();
        _fixture.Admin.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        _fixture.Trackers.Link(_fixture.Admin.Id, _fixture.Catalogue);
        _fixture.Trackers.Link(_fixture.Alice.Id, _fixture.Catalogue);
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    private static ControllerContext SignedIn(User? user) => new() {
        HttpContext = new DefaultHttpContext {
            User = user == null ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimValues.UserId, user.Id.ToString())]))
        }
    };

    private UserMatchesController UserRoutes(User? user) => new(_fixture.Service, _fixture.Users.Object) { ControllerContext = SignedIn(user) };

    private MatchesController AdminRoutes(User? user) => new(_fixture.Service, _fixture.Users.Object) { ControllerContext = SignedIn(user) };

    private static int? Status(IActionResult result) => result switch {
        IStatusCodeActionResult status => status.StatusCode,
        _ => null
    };

    // What Jellyfin enforces, from the attributes

    [Test]
    public void EveryAdminRouteRequiresElevation() {
        Assert.That(typeof(MatchesController).GetCustomAttribute<AuthorizeAttribute>()?.Policy, Is.EqualTo(Policies.RequiresElevation));
        Assert.That(typeof(ResolutionCheckController).GetCustomAttribute<AuthorizeAttribute>()?.Policy, Is.EqualTo(Policies.RequiresElevation));
    }

    [Test]
    public void EveryUserRouteRequiresASignedInUser() {
        var authorize = typeof(UserMatchesController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.That(authorize, Is.Not.Null);
        Assert.That(authorize!.Policy, Is.Null);
    }

    [TestCase(typeof(MatchesController))]
    [TestCase(typeof(UserMatchesController))]
    public void NoMatchRouteAllowsAnonymousRequests(Type controller) {
        Assert.That(controller.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Null);
        Assert.That(Actions(controller).Where(action => action.GetCustomAttribute<AllowAnonymousAttribute>() != null), Is.Empty);
    }

    [Test]
    public void NoUserRouteTakesAUserIdFromTheRequest() {
        foreach (MethodInfo action in Actions(typeof(UserMatchesController))) {
            foreach (ParameterInfo parameter in action.GetParameters()) {
                Assert.That(parameter.Name, Is.Not.EqualTo("userId").IgnoreCase, action.Name);
                Assert.That(parameter.ParameterType.GetProperties().Select(property => property.Name), Has.None.EqualTo("UserId").IgnoreCase, $"{action.Name}({parameter.Name})");
            }
        }
    }

    // Anonymous

    [Test]
    public async Task ARequestWithNoSignedInUserGets401() {
        Assert.That(Status(UserRoutes(null).Detail(_fixture.Key)), Is.EqualTo(401));
        Assert.That(Status(await UserRoutes(null).Save(_fixture.Key, Provider, FourByTwelve(), CancellationToken.None)), Is.EqualTo(401));
        Assert.That(Status(await AdminRoutes(null).Save(_fixture.Key, Provider, FourByTwelve(), CancellationToken.None)), Is.EqualTo(401));
    }

    // Ordinary users

    [Test]
    public async Task AUserSavesOnlyTheirOwnOverrideNeverTheSharedMatch() {
        IActionResult result = await UserRoutes(_fixture.Alice).Save(_fixture.Key, Provider, FourByTwelve(), CancellationToken.None);

        Assert.That(Status(result), Is.EqualTo(200));
        Assert.That(_fixture.Store.Get(_fixture.PersonalScope(_fixture.Alice)), Is.Not.Null);
        Assert.That(_fixture.Store.Get(_fixture.SharedScope), Is.Null);
    }

    [Test]
    public async Task AUserCannotDeleteTheSharedMatch() {
        await AdminRoutes(_fixture.Admin).Save(_fixture.Key, Provider, FourByTwelve(), CancellationToken.None);

        var result = (ConflictObjectResult)UserRoutes(_fixture.Alice).Delete(_fixture.Key, Provider, 1);

        Assert.That(((RevisionConflictContract)result.Value!).CurrentRevision, Is.EqualTo(0), "her own override, which does not exist");
        Assert.That(_fixture.Store.Get(_fixture.SharedScope), Is.Not.Null);
    }

    [Test]
    public async Task AUserCannotReachAnotherUsersOverride() {
        _fixture.Trackers.Link(_fixture.Bob.Id, _fixture.Catalogue);
        await UserRoutes(_fixture.Bob).Save(_fixture.Key, Provider, FourByTwelve(), CancellationToken.None);

        var detail = (UserSourceDetailContract)((OkObjectResult)UserRoutes(_fixture.Alice).Detail(_fixture.Key)).Value!;

        Assert.That(detail.Providers.Single().Personal, Is.Null);
        Assert.That(Status(UserRoutes(_fixture.Alice).Delete(_fixture.Key, Provider, 1)), Is.EqualTo(409), "her own, which does not exist");
        Assert.That(_fixture.Store.Get(_fixture.PersonalScope(_fixture.Bob)), Is.Not.Null);
    }

    [Test]
    public void AnAdminRouteNamingAnotherUserIsRefusedToAnOrdinaryUser() =>
        Assert.That(Status(AdminRoutes(_fixture.Alice).ReadOverride(_fixture.Bob.Id, _fixture.Key, Provider)), Is.EqualTo(404),
            "Jellyfin's policy refuses first; the controller does not rely on it");

    [Test]
    public async Task ASeasonOutsideTheUsersAccessIs404OnEveryRoute() {
        string hidden = _fixture.Hidden.Info.SourceKey;
        UserMatchesController alice = UserRoutes(_fixture.Alice);

        Assert.That(Status(alice.Detail(hidden)), Is.EqualTo(404));
        Assert.That(Status(await alice.Save(hidden, Provider, FourByTwelve(), CancellationToken.None)), Is.EqualTo(404));
        Assert.That(Status(await alice.Preview(hidden, Provider, Draft(FourByTwelve()), CancellationToken.None)), Is.EqualTo(404));
        Assert.That(Status(alice.Delete(hidden, Provider, 0)), Is.EqualTo(404));
        var list = (SourcePageContract)((OkObjectResult)alice.Sources(null, null)).Value!;
        Assert.That(list.Items.Select(item => item.SourceKey), Has.None.EqualTo(hidden));
    }

    [Test]
    public async Task AUsersAutomaticRouteNeverOpensASeasonOutsideTheirLibrary() {
        IActionResult hidden = await UserRoutes(_fixture.Alice).Automatic(_fixture.Hidden.Info.SourceKey, Provider, CancellationToken.None);
        IActionResult own = await UserRoutes(_fixture.Alice).Automatic(_fixture.Key, Provider, CancellationToken.None);

        Assert.That(Status(hidden), Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(Status(own), Is.EqualTo(StatusCodes.Status200OK));
    }

    // Admins

    [Test]
    public async Task AnAdminCanReadWriteAndDeleteTheSharedMatch() {
        MatchesController admin = AdminRoutes(_fixture.Admin);

        Assert.That(Status(await admin.Save(_fixture.Key, Provider, FourByTwelve(), CancellationToken.None)), Is.EqualTo(200));
        Assert.That(Status(admin.Detail(_fixture.Key)), Is.EqualTo(200));
        Assert.That(Status(await admin.Delete(_fixture.Key, Provider, 1)), Is.EqualTo(200));
        Assert.That(_fixture.Store.Get(_fixture.SharedScope), Is.Null);
    }

    [Test]
    public async Task AnAdminCanEditAndDeleteAUsersOverrideForThem() {
        MatchesController admin = AdminRoutes(_fixture.Admin);

        Assert.That(Status(await admin.SaveOverride(_fixture.Alice.Id, _fixture.Key, Provider, FourByTwelve(), CancellationToken.None)), Is.EqualTo(200));
        Assert.That(Status(admin.ReadOverride(_fixture.Alice.Id, _fixture.Key, Provider)), Is.EqualTo(200));
        StoredMatch stored = _fixture.Store.Get(_fixture.PersonalScope(_fixture.Alice))!;
        Assert.That(stored.UpdatedBy, Is.EqualTo(_fixture.Admin.Id.ToString("D")));
        Assert.That(stored.ConnectionGeneration, Is.EqualTo(_fixture.Trackers.Links(_fixture.Alice.Id)[Provider].ToString("D")), "Alice's link, not the admin's");
        Assert.That(Status(await admin.DeleteOverride(_fixture.Alice.Id, _fixture.Key, Provider, 1)), Is.EqualTo(200));
    }

    [Test]
    public async Task AnAdminSavingForAUserIsLimitedToThatUsersLibrary() =>
        Assert.That(Status(await AdminRoutes(_fixture.Admin).SaveOverride(_fixture.Alice.Id, _fixture.Hidden.Info.SourceKey, Provider, FourByTwelve(),
            CancellationToken.None)), Is.EqualTo(404));

    [Test]
    public async Task ResultsBecomeTheirStatusCodes() {
        MatchesController admin = AdminRoutes(_fixture.Admin);
        await admin.Save(_fixture.Key, Provider, FourByTwelve(), CancellationToken.None);

        Assert.That(Status(await admin.Save(_fixture.Key, Provider, FourByTwelve(0), CancellationToken.None)), Is.EqualTo(409));
        Assert.That(Status(await admin.Save(_fixture.Key, Provider, new SaveMatchRequest(1, [Span(1, 12, 999)], [], null), CancellationToken.None)), Is.EqualTo(422));
        Assert.That(Status(await admin.Save(_fixture.Key, Provider, new SaveMatchRequest(1, [new RangeContract(1, 2, null, 1)], [], null), CancellationToken.None)), Is.EqualTo(400));
        Assert.That(Status(admin.Sources(null, "nonsense")), Is.EqualTo(400));
    }

    // Failures

    [TestCase(typeof(InvalidOperationException), 503)]
    [TestCase(typeof(TrackerFailureException), 502)]
    public void StorageAndTrackerFailuresBecomeStatusCodes(Type failure, int status) {
        Exception exception = failure == typeof(TrackerFailureException) ? new TrackerFailureException(TrackerFailureKind.Transient) : new InvalidOperationException("closed");
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) { Exception = exception };

        new MatchErrorsAttribute().OnException(context);

        Assert.That(context.ExceptionHandled, Is.True);
        Assert.That(((ObjectResult)context.Result!).StatusCode, Is.EqualTo(status));
    }

    [Test]
    public void OtherFailuresAreLeftToJellyfin() {
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) {
            Exception = new ArgumentException("bug")
        };

        new MatchErrorsAttribute().OnException(context);

        Assert.That(context.ExceptionHandled, Is.False);
    }

    private static MethodInfo[] Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
}
