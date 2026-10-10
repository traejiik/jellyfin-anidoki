using System;
using System.Security.Claims;
using jellyfin_anidoki.Enums;
using jellyfin_anidoki.Extensions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

[TestFixture]
public class UserManagerExtensionsTests
{
    private Mock<IUserManager> _userManager = null!;
    private User _requester = null!;
    private User _target = null!;

    [SetUp]
    public void SetUp()
    {
        _userManager = new Mock<IUserManager>();
        _requester = CreateUser("requester");
        _target = CreateUser("target");
        _userManager.Setup(manager => manager.GetUserById(_requester.Id)).Returns(_requester);
        _userManager.Setup(manager => manager.GetUserById(_target.Id)).Returns(_target);
    }

    [Test]
    public void MissingUserClaimReturnsNull()
    {
        Assert.That(_userManager.Object.GetUser(new ClaimsPrincipal(), _target.Id), Is.Null);
    }

    [TestCase("invalid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void InvalidUserClaimReturnsNull(string claim)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimValues.UserId, claim)]));
        Assert.That(_userManager.Object.GetUser(principal, _target.Id), Is.Null);
    }

    [Test]
    public void DeletedRequesterReturnsNull()
    {
        _userManager.Setup(manager => manager.GetUserById(_requester.Id)).Returns((User?)null);
        Assert.That(_userManager.Object.GetUser(Principal(), _target.Id), Is.Null);
    }

    [Test]
    public void SelfWithoutTargetReturnsRequester()
    {
        Assert.That(_userManager.Object.GetUser(Principal(), null), Is.SameAs(_requester));
    }

    [Test]
    public void SelfWithTargetReturnsRequester()
    {
        Assert.That(_userManager.Object.GetUser(Principal(), _requester.Id), Is.SameAs(_requester));
    }

    [Test]
    public void OrdinaryUserCannotAccessAnotherUser()
    {
        Assert.That(_userManager.Object.GetUser(Principal(), _target.Id), Is.Null);
    }

    [Test]
    public void AdministratorRequestingAnotherUserReturnsTarget()
    {
        _requester.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        Assert.That(_userManager.Object.GetUser(Principal(), _target.Id), Is.SameAs(_target));
    }

    [Test]
    public void AdministratorRequestingMissingUserReturnsNull()
    {
        _requester.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        Assert.That(_userManager.Object.GetUser(Principal(), Guid.NewGuid()), Is.Null);
    }

    private ClaimsPrincipal Principal() => new(new ClaimsIdentity([
        new Claim(ClaimValues.UserId, _requester.Id.ToString())
    ]));

    private static User CreateUser(string name) => new(name, "authentication", "password-reset")
    {
        Id = Guid.NewGuid()
    };
}
