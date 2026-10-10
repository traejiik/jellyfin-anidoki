using System;
using System.Text.Json;
using System.Linq;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Notifications.Discord;
using NUnit.Framework;
namespace jellyfin_anidoki_unit_tests.NotificationTests.Discord;

[TestFixture]
public class DiscordFormattingTests
{
    [TestCase("https://discord.com/api/webhooks/123456789012345678/")]
    [TestCase("http://discord.com/api/webhooks/123456789012345678/secret")]
    [TestCase("https://discord.com.example/api/webhooks/123456789012345678/secret")]
    [TestCase("https://localhost/api/webhooks/123456789012345678/secret")]
    [TestCase("https://discord.com/api/webhooks/123456789012345678/secret?wait=true")]
    [TestCase("https://x@discord.com/api/webhooks/123456789012345678/secret")]
    [TestCase("https://discord.com:444/api/webhooks/123456789012345678/secret")]
    [TestCase("https://discord.com/api/webhooks/123456789012345678/a%2Fb")]
    public void RejectsUnsafeDestinations(string value) => Assert.That(DiscordWebhookAddress.Parse(value), Is.Null);
    [Test]
    public void CanonicalizesOnlyIncomingWebhooks()
    {
        Assert.That(DiscordWebhookAddress.Parse("https://discord.com/api/webhooks/123456789012345678/fixture_token"), Is.EqualTo("https://discord.com/api/v10/webhooks/123456789012345678/fixture_token"));
    }
    [Test]
    public void ShowLinkPreservesBasePathAndUsesShowIdWithoutCredentials()
    {
        var show = Guid.NewGuid(); var link = JellyfinShowLink.Build("https://jf.example/jellyfin/", show, "server");
        Assert.That(link, Is.EqualTo($"https://jf.example/jellyfin/web/index.html#/details?id={show:N}&serverId=server"));
        Assert.Throws<ArgumentException>(() => JellyfinShowLink.Build("https://x:secret@jf.example", show, "server"));
    }
    [Test]
    public void EmbedUsesFixedIdentityBoldTitleLinkAndRequiredEpisodeDetails()
    {
        var summary = new SyncOperationSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "<show>", 12, null, "Required episode", false, DateTimeOffset.UtcNow, SyncNotificationTrigger.Playback, []);
        var payload = DiscordEmbedBuilder.Build(summary, "https://jf.example", "server", true, test: true);
        var json = JsonSerializer.Serialize(payload); using var document = JsonDocument.Parse(json); var root = document.RootElement;
        Assert.That(root.GetProperty("username").GetString(), Is.EqualTo("AniDoki"));
        var embed = root.GetProperty("embeds")[0]; Assert.That(embed.GetProperty("url").GetString(), Does.Contain(summary.ShowItemId.ToString("N")));
        Assert.That(embed.GetProperty("description").GetString(), Does.Contain("Episode 12").And.Contain("Required episode"));
        Assert.That(embed.GetProperty("thumbnail").GetProperty("url").GetString(), Is.EqualTo("attachment://poster.jpg"));
        Assert.That(root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength(), Is.Zero);
    }
    [TestCase("Show S01E01", "Episode title unavailable")]
    [TestCase("Pilot", "Pilot")]
    public void EpisodeDetailsUseMetadataRatherThanTheFileName(string name, string expected)
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode {
            Id = Guid.NewGuid(), SeriesId = Guid.NewGuid(), SeriesName = "Show", IndexNumber = 1,
            Name = name, Path = "/shows/Show S01E01.mkv"
        };
        var summary = SyncOperationSummary.Capture(episode, Guid.NewGuid(), Guid.NewGuid(), SyncNotificationTrigger.Playback, new());
        Assert.That(DiscordEmbedBuilder.EpisodeLine(summary), Is.EqualTo("Episode 1 - " + expected));
    }

    [Test] public void DensePartialResultsKeepUncertaintyVisibleAndLabelOmittedDetails()
    {
        var outcomes = System.Linq.Enumerable.Range(0, 8).Select(i => new ProviderWriteOutcome(
            jellyfin_anidoki.Configuration.ApiName.AniList, i.ToString(), "A long target title " + new string('x', 200),
            i == 7 ? OutcomeKind.Unconfirmed : OutcomeKind.Confirmed, i, null, false, "progress", i != 7)).ToArray();
        var summary = new SyncOperationSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Show", 1, null,
            "Pilot", false, DateTimeOffset.UtcNow, SyncNotificationTrigger.Playback, outcomes);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordEmbedBuilder.Build(summary, "https://jf.example", "server", false)));
        var embed = payload.RootElement.GetProperty("embeds")[0];
        Assert.That(embed.GetProperty("description").GetString(), Does.Contain("Some tracker results could not be confirmed"));
        Assert.That(embed.GetProperty("fields")[0].GetProperty("value").GetString(), Does.Contain("Unconfirmed").And.Contain("additional results"));
    }

    [TestCase(1, jellyfin_anidoki.Models.Mal.Status.Watching, "Started watching: Updated progress to 1")]
    [TestCase(6, jellyfin_anidoki.Models.Mal.Status.Watching, "Updated progress to 6")]
    [TestCase(12, jellyfin_anidoki.Models.Mal.Status.Watching, "Updated progress to 12")]
    [TestCase(12, jellyfin_anidoki.Models.Mal.Status.Completed, "Completed: Updated progress to 12")]
    [TestCase(1, jellyfin_anidoki.Models.Mal.Status.Rewatching, "Rewatching: Updated progress to 1")]
    public void ConfirmedProgressUsesConciseUpdateWording(int progress, jellyfin_anidoki.Models.Mal.Status status, string expected)
    {
        var summary = new SyncOperationSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Show", 12, null,
            "Pilot", false, DateTimeOffset.UtcNow, SyncNotificationTrigger.Playback,
            [new ProviderWriteOutcome(jellyfin_anidoki.Configuration.ApiName.AniList, "1", "Show", OutcomeKind.Confirmed,
                progress, status, false, "progress", true)]);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordEmbedBuilder.Build(summary, "https://jf.example", "server", true)));
        var result = payload.RootElement.GetProperty("embeds")[0].GetProperty("fields")[0].GetProperty("value").GetString();
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test] public void FooterUsesJellyfinIdentityServerNameAndTransmissionTime()
    {
        var summary = new SyncOperationSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Show", 1, null,
            "Pilot", false, DateTimeOffset.UtcNow.AddMinutes(-5), SyncNotificationTrigger.Playback, []);
        var sent = DateTimeOffset.UtcNow;
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordEmbedBuilder.Build(summary, "https://jf.example", "server", true,
            serverName: "Living room", sentUtc: sent)));
        var embed = payload.RootElement.GetProperty("embeds")[0];
        Assert.That(embed.GetProperty("footer").GetProperty("text").GetString(), Is.EqualTo("Living room"));
        Assert.That(embed.GetProperty("footer").GetProperty("icon_url").GetString(), Is.EqualTo(DiscordEmbedBuilder.JellyfinLogoUrl));
        Assert.That(embed.GetProperty("timestamp").GetString(), Is.EqualTo(sent.ToString("O")));
    }

}
