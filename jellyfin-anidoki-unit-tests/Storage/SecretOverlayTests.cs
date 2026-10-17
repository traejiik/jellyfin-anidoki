using System;
using System.Collections.Generic;
using System.IO;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Storage;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Storage;

[TestFixture]
public class SecretOverlayTests {
    private static readonly Guid User = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
    private const string Owner = "3f2504e04f8911d39a0c0305e82c3301";

    private static PluginConfiguration WithSecrets() => new() {
        ProviderApiAuth = [new ProviderApiAuth { Name = ApiName.Mal, ClientId = "client", ClientSecret = "client-secret" }],
        UserConfig = [
            new UserConfig {
                UserId = User,
                DiscordWebhookUrl = "https://discord.com/api/webhooks/1/token",
                UserApiAuth = [new UserApiAuth { Name = ApiName.AniList, AccessToken = "access", RefreshToken = "refresh" }]
            }
        ]
    };

    private static PluginConfiguration WithoutSecrets() => new() {
        ProviderApiAuth = [new ProviderApiAuth { Name = ApiName.Mal, ClientId = "client" }],
        UserConfig = [new UserConfig { UserId = User, UserApiAuth = [new UserApiAuth { Name = ApiName.AniList }] }]
    };

    [Test]
    public void ExtractFindsEveryCredential() {
        Assert.That(SecretOverlay.Extract(WithSecrets()), Is.EquivalentTo(new Dictionary<SecretKey, string> {
            [new SecretKey(SecretKey.ClientSecretKind, "", "Mal")] = "client-secret",
            [new SecretKey(SecretKey.DiscordWebhookKind, Owner, "")] = "https://discord.com/api/webhooks/1/token",
            [new SecretKey(SecretKey.AccessTokenKind, Owner, "AniList")] = "access",
            [new SecretKey(SecretKey.RefreshTokenKind, Owner, "AniList")] = "refresh"
        }));
    }

    [Test]
    public void ApplyRestoresWhatExtractFound() {
        var configuration = WithoutSecrets();

        SecretOverlay.Apply(configuration, SecretOverlay.Extract(WithSecrets()));

        Assert.That(SecretOverlay.Extract(configuration), Is.EquivalentTo(SecretOverlay.Extract(WithSecrets())));
    }

    [Test]
    public void ApplyIgnoresCredentialsWhoseRecordIsGone() {
        var configuration = new PluginConfiguration { ProviderApiAuth = [], UserConfig = [] };

        SecretOverlay.Apply(configuration, SecretOverlay.Extract(WithSecrets()));

        Assert.That(SecretOverlay.Extract(configuration), Is.Empty);
    }

    [Test]
    public void LegacyXmlCredentialsAreRead() {
        string path = Path.Combine(Path.GetTempPath(), "anidoki-legacy-" + Guid.NewGuid() + ".xml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <UserConfig>
                <UserConfig>
                  <PlanToWatchOnly>true</PlanToWatchOnly>
                  <UserId>{User}</UserId>
                  <DiscordWebhookUrl>https://discord.com/api/webhooks/1/token</DiscordWebhookUrl>
                  <UserApiAuth>
                    <UserApiAuth>
                      <Name>AniList</Name>
                      <ConnectionGeneration>{Guid.NewGuid()}</ConnectionGeneration>
                      <AccessToken>access</AccessToken>
                      <RefreshToken>refresh</RefreshToken>
                    </UserApiAuth>
                  </UserApiAuth>
                </UserConfig>
              </UserConfig>
              <ProviderApiAuth>
                <ProviderApiAuth>
                  <Name>Mal</Name>
                  <ClientId>client</ClientId>
                  <ClientSecret>client-secret</ClientSecret>
                </ProviderApiAuth>
              </ProviderApiAuth>
              <callbackUrl>http://localhost:8096</callbackUrl>
            </PluginConfiguration>
            """);
        try {
            Assert.That(LegacySecrets.Read(path), Is.EquivalentTo(SecretOverlay.Extract(WithSecrets())));
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public void MissingLegacyXmlReadsNothing() =>
        Assert.That(LegacySecrets.Read(Path.Combine(Path.GetTempPath(), "anidoki-missing-" + Guid.NewGuid() + ".xml")), Is.Empty);

    [Test]
    public void MalformedLegacyXmlThrows() {
        string path = Path.Combine(Path.GetTempPath(), "anidoki-malformed-" + Guid.NewGuid() + ".xml");
        File.WriteAllText(path, "<PluginConfiguration><UserConfig><UserConfig><UserId>not-a-guid</UserId></UserConfig></UserConfig></PluginConfiguration>");
        try {
            Assert.Throws<InvalidOperationException>(() => LegacySecrets.Read(path));
        } finally {
            File.Delete(path);
        }
    }
}
