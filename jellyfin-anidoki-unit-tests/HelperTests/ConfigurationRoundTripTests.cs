using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki_unit_tests.Characterization;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

/// <summary>
/// Pins the plugin configuration's XML (what Jellyfin stores) and JSON (what the settings pages read and post), full
/// and empty, so annotating these types cannot change a byte of either.
/// </summary>
[TestFixture]
public class ConfigurationRoundTripTests {
    private static PluginConfiguration Full() => new() {
        UserConfig = [
            new UserConfig {
                UserId = new Guid("11111111-1111-1111-1111-111111111111"),
                PlanToWatchOnly = false,
                RewatchCompleted = false,
                ShowLogNotifications = false,
                LibraryToCheck = ["library-a", "library-b"],
                DiscordNotificationsEnabled = true,
                DiscordWebhookUrl = "https://discord.com/api/webhooks/1/hidden",
                DiscordNotificationRevision = 3,
                UserApiAuth = [
                    new UserApiAuth {
                        Name = ApiName.AniList,
                        ConnectionGeneration = new Guid("22222222-2222-2222-2222-222222222222"),
                        AccessToken = "access-hidden",
                        RefreshToken = "refresh-hidden",
                    },
                    new UserApiAuth { Name = ApiName.Kitsu },
                ],
                KeyPairs = [new KeyPairs { Key = "KitsuUserId", Value = "42" }, new KeyPairs()],
            },
            new UserConfig { UserId = new Guid("33333333-3333-3333-3333-333333333333") },
        ],
        ProviderApiAuth = [
            new ProviderApiAuth { Name = ApiName.Mal, ClientId = "mal-client", ClientSecret = "secret-hidden" },
            new ProviderApiAuth { Name = ApiName.Simkl },
        ],
        AnimeLibraries = [
            new AnimeLibrary { LibraryId = new Guid("44444444-4444-4444-4444-444444444444"), Mode = AnimeLibraryMode.Mixed },
        ],
        callbackUrl = "https://jf.example/jellyfin",
        callbackRedirectUrl = "https://jf.example/web",
        animeListSaveLocation = "/config/anidoki",
        watchedTickboxUpdatesProvider = true,
        shikimoriAppName = "anidoki-app",
        simklUpdateAll = true,
        applySavedMatches = false,
        LogFileDebug = false,
        DefaultTrackerThresholdPercent = 70,
        updateNsfw = true,
        authenticationLinkExpireTimeMinutes = 15,
        armServerBaseUrl = "https://arm.example",
        enableUserPages = true,
        shikimoriDomain = "shikimori.example",
    };

    // Each pass is pinned, not compared with the one before: reading XML turns a missing list into an empty one
    // (a user saved without KeyPairs reads back with <KeyPairs />), and that is today's behaviour to keep.
    [Test]
    public void XmlIsUnchanged() {
        string full = Xml(Full());
        string empty = Xml(new PluginConfiguration());

        ApprovedFile.Verify("configuration-xml", Sections(
            ("Full, saved", full), ("Full, read back and saved again", Xml(FromXml(full))),
            ("Empty, saved", empty), ("Empty, read back and saved again", Xml(FromXml(empty)))));
    }

    [Test]
    public void JsonIsUnchanged() {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string full = JsonSerializer.Serialize(Full(), options);
        string empty = JsonSerializer.Serialize(new PluginConfiguration(), options);
        string Again(string json) => JsonSerializer.Serialize(JsonSerializer.Deserialize<PluginConfiguration>(json), options);

        ApprovedFile.Verify("configuration-json", Sections(
            ("Full, saved", full), ("Full, read back and saved again", Again(full)),
            ("Empty, saved", empty), ("Empty, read back and saved again", Again(empty))));
    }

    /// <summary>A configuration saved by AniDōki 2.0.0 on a test server, with its IDs replaced; Jellyfin's XML names no .NET namespace.</summary>
    [Test]
    public void A2_0_0ConfigurationStillLoads() {
        var loaded = FromXml(File.ReadAllText(Fixture("configuration-2.0.0.xml")));

        Assert.That(loaded.UserConfig, Has.Exactly(2).Items);
        Assert.That(loaded.UserConfig?[0].UserApiAuth?.Select(auth => auth.Name), Is.EqualTo(new[] { ApiName.AniList, ApiName.Kitsu }));
        Assert.That(loaded.UserConfig?[0].KeyPairs?.Single().Value, Is.EqualTo("1000"));
        Assert.That(loaded.UserConfig?[1].TrackerThresholdPercent, Is.EqualTo(60));
        Assert.That(loaded.ProviderApiAuth?.Single().ClientId, Is.EqualTo("client-id"));
        Assert.That(loaded.AnimeLibraries?.Select(library => library.Mode), Is.EqualTo(Enumerable.Repeat(AnimeLibraryMode.Dedicated, 3)));
        Assert.That(loaded.DefaultTrackerThresholdPercent, Is.EqualTo(70));
        Assert.That(loaded.enableUserPages, Is.True);
    }

    private static string Fixture(string name, [CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "Approved", name);

    private static string Sections(params (string Title, string Text)[] sections) =>
        string.Join("\n", sections.Select(section => $"# {section.Title}\n{section.Text}\n"));

    private static string Xml(PluginConfiguration configuration) {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, configuration);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static PluginConfiguration FromXml(string xml) {
        using var reader = new StringReader(xml);
        return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
    }
}
