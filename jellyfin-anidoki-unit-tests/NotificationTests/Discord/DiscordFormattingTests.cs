using System;
using System.Text.Json;
using System.Linq;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Notifications.Discord;
using NUnit.Framework;
namespace jellyfin_anidoki_unit_tests.NotificationTests.Discord;

[TestFixture]
public class DiscordFormattingTests {
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
    public void CanonicalizesOnlyIncomingWebhooks() {
        Assert.That(DiscordWebhookAddress.Parse("https://discord.com/api/webhooks/123456789012345678/fixture_token"), Is.EqualTo("https://discord.com/api/v10/webhooks/123456789012345678/fixture_token"));
    }
    [Test]
    public void ShowLinkPreservesBasePathAndUsesShowIdWithoutCredentials() {
        var show = Guid.NewGuid();
        var link = JellyfinShowLink.Build("https://jf.example/jellyfin/", show, "server");
        Assert.That(link, Is.EqualTo($"https://jf.example/jellyfin/web/index.html#/details?id={show:N}&serverId=server"));
        Assert.Throws<ArgumentException>(() => JellyfinShowLink.Build("https://x:secret@jf.example", show, "server"));
    }
    [Test]
    public void EmbedUsesFixedIdentityBoldTitleLinkAndRequiredEpisodeDetails() {
        var summary = new SyncOperationSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "<show>", 12, null, "Required episode", false, DateTimeOffset.UtcNow, SyncNotificationTrigger.Playback, []);
        var payload = DiscordEmbedBuilder.Build(summary, "https://jf.example", "server", true, test: true);
        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.That(root.GetProperty("username").GetString(), Is.EqualTo("AniDoki"));
        var embed = root.GetProperty("embeds")[0];
        Assert.That(embed.GetProperty("url").GetString(), Does.Contain(summary.ShowItemId.ToString("N")));
        Assert.That(embed.GetProperty("description").GetString(), Does.Contain("Episode 12").And.Contain("Required episode"));
        Assert.That(embed.GetProperty("thumbnail").GetProperty("url").GetString(), Is.EqualTo("attachment://poster.jpg"));
        Assert.That(root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength(), Is.Zero);
    }
    [TestCase("Show S01E01", "Episode title unavailable")]
    [TestCase("Pilot", "Pilot")]
    public void EpisodeDetailsUseMetadataRatherThanTheFileName(string name, string expected) {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode {
            Id = Guid.NewGuid(),
            SeriesId = Guid.NewGuid(),
            SeriesName = "Show",
            IndexNumber = 1,
            Name = name,
            Path = "/shows/Show S01E01.mkv"
        };
        var summary = SyncOperationSummary.Capture(episode, Guid.NewGuid(), Guid.NewGuid(), SyncNotificationTrigger.Playback, new());
        Assert.That(DiscordEmbedBuilder.EpisodeLine(summary), Is.EqualTo("Episode 1 - " + expected));
    }

    [Test]
    public void DensePartialResultsKeepUncertaintyVisibleAndLabelOmittedDetails() {
        var outcomes = System.Linq.Enumerable.Range(0, 8).Select(i => new ProviderWriteOutcome(
            jellyfin_anidoki.Configuration.ApiName.AniList, i.ToString(), "A long target title " + new string('x', 200),
            i == 7 ? OutcomeKind.Unconfirmed : OutcomeKind.Confirmed, i, null, false, "progress", i != 7)).ToArray();
        var summary = new SyncOperationSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Show", 1, null,
            "Pilot", false, DateTimeOffset.UtcNow, SyncNotificationTrigger.Playback, outcomes);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordEmbedBuilder.Build(summary, "https://jf.example", "server", false)));
        var embed = payload.RootElement.GetProperty("embeds")[0];
        Assert.That(embed.GetProperty("description").GetString(), Does.Contain("Some tracker results could not be confirmed"));
        var value = embed.GetProperty("fields")[0].GetProperty("value").GetString();
        Assert.That(value, Does.Contain("Update not confirmed").And.Contain("additional results"));
        Assert.That(value!.Length, Is.LessThanOrEqualTo(1024), "Discord rejects field values over 1024 characters");
    }

    [TestCase(1, jellyfin_anidoki.Models.Mal.Status.Watching, "Started watching · Episode 1")]
    [TestCase(6, jellyfin_anidoki.Models.Mal.Status.Watching, "Watching · Episode 6")]
    [TestCase(12, jellyfin_anidoki.Models.Mal.Status.Watching, "Watching · Episode 12")]
    [TestCase(12, jellyfin_anidoki.Models.Mal.Status.Completed, "Completed · Episode 12")]
    [TestCase(1, jellyfin_anidoki.Models.Mal.Status.Rewatching, "Rewatching · Episode 1")]
    public void ConfirmedProgressUsesConciseUpdateWording(int progress, jellyfin_anidoki.Models.Mal.Status status, string expected) {
        var summary = new SyncOperationSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Show", 12, null,
            "Pilot", false, DateTimeOffset.UtcNow, SyncNotificationTrigger.Playback,
            [new ProviderWriteOutcome(jellyfin_anidoki.Configuration.ApiName.AniList, "1", "Show", OutcomeKind.Confirmed,
                progress, status, false, "progress", true)]);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(DiscordEmbedBuilder.Build(summary, "https://jf.example", "server", true)));
        var result = payload.RootElement.GetProperty("embeds")[0].GetProperty("fields")[0].GetProperty("value").GetString();
        Assert.That(result, Is.EqualTo(expected));
    }

    private static JsonElement Fields(string show, bool isMovie, params ProviderWriteOutcome[] outcomes) {
        var summary = new SyncOperationSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), show, 2, null,
            "The Sheriff's Request to Mountain Tim", isMovie, DateTimeOffset.UtcNow, SyncNotificationTrigger.ConfirmedManual, outcomes);
        var json = JsonSerializer.Serialize(DiscordEmbedBuilder.Build(summary, "https://jf.example", "server", true));
        return JsonDocument.Parse(json).RootElement.GetProperty("embeds")[0].GetProperty("fields").Clone();
    }

    [Test]
    public void SteelBallRunRepeatRunNamesEachEntryUnderItsResult() {
        const string first = "STEEL BALL RUN JoJo's Bizarre Adventure 1st STAGE";
        const string sequel = "STEEL BALL RUN JoJo's Bizarre Adventure 2nd - 3rd STAGE";
        var fields = Fields("JoJo's Bizarre Adventure", false,
            new ProviderWriteOutcome(jellyfin_anidoki.Configuration.ApiName.AniList, "a", first, OutcomeKind.Confirmed, 1,
                jellyfin_anidoki.Models.Mal.Status.Completed, false, "progress", true),
            new ProviderWriteOutcome(jellyfin_anidoki.Configuration.ApiName.AniList, "b", sequel, OutcomeKind.NoChange, null, null, false,
                "manual-forward-only", false));
        Assert.That(fields[0].GetProperty("value").GetString(), Is.EqualTo(
            $"Completed · Episode 1\n-# {first}\nAlready at or past this episode\n-# {sequel}"));
    }

    [Test]
    public void ProvidersAndKindsUseDisplayNames() {
        var fields = Fields("Show", false,
            new ProviderWriteOutcome(jellyfin_anidoki.Configuration.ApiName.Mal, "1", "Show", OutcomeKind.NoChange, 3,
                jellyfin_anidoki.Models.Mal.Status.Watching, false, "progress", false),
            new ProviderWriteOutcome(jellyfin_anidoki.Configuration.ApiName.Kitsu, "1", "Show", OutcomeKind.Skipped, null, null, false,
                "rewatch-disabled", false));
        Assert.That(fields[0].GetProperty("name").GetString(), Is.EqualTo("MyAnimeList"));
        Assert.That(fields[0].GetProperty("value").GetString(), Is.EqualTo("Already up to date"));
        Assert.That(fields[1].GetProperty("value").GetString(), Is.EqualTo("Skipped · completed, and rewatching is off"));
    }

    [Test]
    public void LongEntryTitlesEndAtAWordWithAnEllipsis() {
        var title = string.Join(' ', Enumerable.Repeat("STAGE", 40));
        var value = Fields("Show", false, new ProviderWriteOutcome(jellyfin_anidoki.Configuration.ApiName.AniList, "1", title,
            OutcomeKind.Confirmed, 2, jellyfin_anidoki.Models.Mal.Status.Watching, false, "progress", true))[0].GetProperty("value").GetString()!;
        var subtext = value.Split('\n')[1];
        Assert.That(subtext, Does.StartWith("-# STAGE").And.EndWith("STAGE…"));
        Assert.That(subtext.Length, Is.LessThanOrEqualTo("-# ".Length + 100));
    }

    [Test]
    public void MovieResultsNeverClaimAnEpisode() {
        var value = Fields("Movie", true, new ProviderWriteOutcome(jellyfin_anidoki.Configuration.ApiName.AniList, "1", "Movie",
            OutcomeKind.Confirmed, 1, jellyfin_anidoki.Models.Mal.Status.Completed, false, "progress", true))[0].GetProperty("value").GetString();
        Assert.That(value, Is.EqualTo("Completed"));
    }

    [Test]
    public void FooterUsesJellyfinIdentityServerNameAndTransmissionTime() {
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
