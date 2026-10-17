using System.Linq;
using System.Text.Json;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

public partial class UserConfigurationTests {
    [Test]
    public void ProviderSecretStateReportsPresenceWithoutTheValue() {
        var states = (ProviderSecretState[])((OkObjectResult)_controller.GetProviderSecrets().Result!).Value!;

        Assert.That(states.Single(state => state.Provider == "Mal").HasSecret, Is.True);
        Assert.That(states.Single(state => state.Provider == "AniList").HasSecret, Is.False);
        Assert.That(JsonSerializer.Serialize(states), Does.Not.Contain("fixture-secret"));
    }

    [Test]
    public void ReplacingAProviderSecretSavesItTrimmed() {
        var result = _controller.SetProviderSecret(new ProviderSecretRequest { Provider = ApiName.AniList, Secret = " new-secret " });

        Assert.That(((ProviderSecretState)((OkObjectResult)result).Value!).HasSecret, Is.True);
        Assert.That(_plugin.Configuration.ProviderApiAuth.Single(auth => auth.Name == ApiName.AniList).ClientSecret, Is.EqualTo("new-secret"));
    }

    [Test]
    public void ClearingAProviderSecretRemovesIt() {
        var result = _controller.SetProviderSecret(new ProviderSecretRequest { Provider = ApiName.Mal, Clear = true });

        Assert.That(((ProviderSecretState)((OkObjectResult)result).Value!).HasSecret, Is.False);
        Assert.That(_plugin.Configuration.ProviderApiAuth.Single(auth => auth.Name == ApiName.Mal).ClientSecret, Is.Null);
    }

    [Test]
    public void AProviderSecretRequestNeedsASecretOrClear() {
        Assert.That(_controller.SetProviderSecret(null), Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_controller.SetProviderSecret(new ProviderSecretRequest { Provider = ApiName.Mal, Secret = "  " }), Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_plugin.Configuration.ProviderApiAuth.Single(auth => auth.Name == ApiName.Mal).ClientSecret, Is.EqualTo("fixture-secret"));
    }

    [Test]
    public void StorageStateReportsAvailability() {
        var state = (StorageState)((OkObjectResult)_controller.GetStorageState().Result!).Value!;

        Assert.That(state.Available, Is.True);
        Assert.That(state.Message, Is.Null);
    }
}
