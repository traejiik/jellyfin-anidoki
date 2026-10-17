using System;
using System.IO;
using System.Text.Json;
using System.Xml.Serialization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Storage;

[TestFixture]
public class CredentialSerializationTests {
    private static PluginConfiguration WithSecrets() => new() {
        ProviderApiAuth = [new ProviderApiAuth { Name = ApiName.Mal, ClientId = "client-id-visible", ClientSecret = "client-secret-hidden" }],
        UserConfig = [
            new UserConfig {
                UserId = Guid.NewGuid(),
                DiscordWebhookUrl = "https://discord.com/api/webhooks/1/webhook-hidden",
                UserApiAuth = [new UserApiAuth { Name = ApiName.AniList, AccessToken = "access-hidden", RefreshToken = "refresh-hidden" }]
            }
        ]
    };

    private static readonly string[] Hidden = ["client-secret-hidden", "webhook-hidden", "access-hidden", "refresh-hidden"];

    [Test]
    public void JsonNeverContainsCredentials() {
        string json = JsonSerializer.Serialize(WithSecrets());

        Assert.That(json, Does.Contain("client-id-visible"));
        foreach (string secret in Hidden)
            Assert.That(json, Does.Not.Contain(secret));
    }

    [Test]
    public void XmlNeverContainsCredentials() {
        using var writer = new StringWriter();
        new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, WithSecrets());
        string xml = writer.ToString();

        Assert.That(xml, Does.Contain("client-id-visible"));
        foreach (string secret in Hidden)
            Assert.That(xml, Does.Not.Contain(secret));
    }

    [Test]
    public void PostedJsonCannotSetCredentials() {
        var configuration = JsonSerializer.Deserialize<PluginConfiguration>(
            """{"ProviderApiAuth":[{"Name":0,"ClientId":"id","ClientSecret":"posted-secret"}]}""")!;

        Assert.That(configuration.ProviderApiAuth[0].ClientSecret, Is.Null);
    }
}
