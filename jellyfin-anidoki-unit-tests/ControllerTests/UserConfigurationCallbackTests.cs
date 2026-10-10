using System;
using System.Net;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

public partial class UserConfigurationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task OAuthCallbackWaitsForTokenBeforeSuccessOrRedirect(bool redirect)
    {
        _plugin.Configuration.callbackRedirectUrl = redirect ? "http://{{LocalIpAddress}}:{{LocalPort}}/done" : "";
        var grantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grantResponse = new TaskCompletionSource<System.Net.Http.HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _xmlSerializer.Setup(serializer => serializer.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Callback(() => tokenSaved.TrySetResult());
        SetHttp(request => {
            grantStarted.TrySetResult();
            return grantResponse.Task;
        });
        string state = MemoryCacheHelper.GenerateState(_memoryCache, _requester.Id, ApiName.Mal);
        var callback = _controller.AuthCallback("fixture-code", state);
        await grantStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool returnedBeforeGrant = callback.IsCompleted;
        grantResponse.SetResult(TokenResponse(null));
        var result = await callback;
        await tokenSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() => {
            Assert.That(returnedBeforeGrant, Is.False);
            Assert.That(_requesterConfig.UserApiAuth[0].AccessToken == "new-fixture-access", Is.True);
            if (redirect)
                Assert.That((result as RedirectResult)?.Url, Is.EqualTo("http://127.0.0.1:8096/done"));
            else
                Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(200));
        });
    }

    [TestCase(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_grant\"}")]
    [TestCase(HttpStatusCode.OK, "{}")]
    [TestCase(HttpStatusCode.OK, "{\"error\":\"invalid_grant\"}")]
    [TestCase(HttpStatusCode.OK, "{\"access_token\":\"\"}")]
    public async Task FailedOAuthCallbackDoesNotClaimSuccessOrRedirect(HttpStatusCode responseCode, string responseBody)
    {
        _plugin.Configuration.callbackRedirectUrl = "https://host/done";
        SetHttp(request => Task.FromResult(Response(request, responseCode, responseBody)));
        string state = MemoryCacheHelper.GenerateState(_memoryCache, _requester.Id, ApiName.Mal);
        var result = await _controller.AuthCallback("fixture-code", state);
        Assert.Multiple(() => {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(500));
            Assert.That(_requesterConfig.UserApiAuth[0].AccessToken == "requester-mal-fixture", Is.True);
            Assert.That(_requesterConfig.UserApiAuth[0].RefreshToken == "requester-refresh-fixture", Is.True);
            Assert.That(MemoryCacheHelper.ConsumeState(_memoryCache, state), Is.Null);
        });
    }

    [Test]
    public async Task NullTokenOAuthCallbackReturnsFailure()
    {
        ConfigureOAuthProvider(ApiName.Shikimori);
        _plugin.Configuration.shikimoriAppName = "";
        SetSuccessfulKitsuHttp();
        string state = MemoryCacheHelper.GenerateState(_memoryCache, _requester.Id, ApiName.Shikimori);
        var result = await _controller.AuthCallback("fixture-code", state);
        Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(500));
    }
}
