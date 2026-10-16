#nullable enable
using System;
using System.Linq;
using System.Text;
using jellyfin_anidoki.Models.Mal;
namespace jellyfin_anidoki.Notifications.Discord;

public static class DiscordEmbedBuilder {
    public const string AvatarUrl = "https://raw.githubusercontent.com/traejiik/jellyfin-anidoki/2c4d36d516dfa2ea4540f63bc9953ef9c50b6238/docs/assets/anidoki-logo.png";
    public const string JellyfinLogoUrl = "https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/logos/PNG-4x/jellyfin-icon--color-on-dark.png";
    internal static string Limit(string? value, int max) => string.Concat((value ?? "").EnumerateRunes().Take(max).Select(r => r.ToString()));
    internal static string Escape(string? value) => string.Concat((value ?? "").Where(c => !char.IsControl(c))).Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("[", "\\[").Replace("]", "\\]").Replace("@", "@\u200b");
    public static string EpisodeLine(SyncOperationSummary summary) => summary.IsMovie ? "Movie" :
        (summary.EpisodeNumber.HasValue ? $"Episode {summary.EpisodeNumber}" + (summary.EpisodeEndNumber > summary.EpisodeNumber ? $"–{summary.EpisodeEndNumber}" : "") : "Episode number unavailable") + " - " + (string.IsNullOrWhiteSpace(summary.EpisodeTitle) ? "Episode title unavailable" : Limit(summary.EpisodeTitle, 400));
    private static string ConfirmedResult(ProviderWriteOutcome outcome) {
        var status = outcome.ConfirmedStatus?.ToString().Replace('_', ' ');
        if (outcome.StatusOnly || !outcome.ConfirmedProgress.HasValue)
            return status ?? "Confirmed update";
        var prefix = outcome.ConfirmedStatus switch {
            Status.Completed => "Completed: ",
            Status.Watching when outcome.ConfirmedProgress == 1 => "Started watching: ",
            Status.Watching or null => "",
            _ => status + ": "
        };
        return prefix + "Updated progress to " + outcome.ConfirmedProgress;
    }
    internal static object[] Fields(SyncOperationSummary summary) => summary.Outcomes.GroupBy(o => o.Provider).Take(6).Select(group => {
        // Keep uncertainty visible even when a multi-season operation has many receipts.
        var results = group.GroupBy(o => (o.TargetId, o.Kind, o.Step)).Select(g => g.Last()).ToArray();
        var selected = results.OrderBy(o => o.Kind switch {
            OutcomeKind.Unconfirmed => 0,
            OutcomeKind.Failed => 1,
            OutcomeKind.Unresolved => 2,
            OutcomeKind.Skipped => 3,
            OutcomeKind.Confirmed => 4,
            _ => 5
        }).Take(6).ToArray();
        var lines = selected.Select(outcome => {
            var result = outcome.Kind != OutcomeKind.Confirmed ? outcome.Kind.ToString() : ConfirmedResult(outcome);
            var target = string.IsNullOrWhiteSpace(outcome.TargetTitle) || string.Equals(outcome.TargetTitle, summary.ShowTitle, StringComparison.OrdinalIgnoreCase)
                ? "" : " · " + Escape(outcome.TargetTitle);
            return Limit(result + target, 95);
        });
        var omitted = results.Length - selected.Length;
        return (object)new {
            name = group.Key.ToString(),
            value = string.Join("\n", lines) + (omitted > 0 ? $"\n+{omitted} additional results" : ""),
            inline = false
        };
    }).ToArray();
    private static string Description(SyncOperationSummary summary, bool poster) =>
        Limit(Escape(EpisodeLine(summary)), 500) +
        (summary.Outcomes.Any(o => o.Kind is OutcomeKind.Failed or OutcomeKind.Unconfirmed or OutcomeKind.Unresolved) ?
            "\nSome tracker results could not be confirmed." : "") + (poster ? "" : "\nPoster unavailable.");
    public static object Build(SyncOperationSummary summary, string address, string server, bool poster, bool test = false,
        string? serverName = null, DateTimeOffset? sentUtc = null) => new {
            username = "AniDoki",
            avatar_url = AvatarUrl,
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[] {
            new {
                title = Limit(summary.ShowTitle, 240),
                url = JellyfinShowLink.Build(address, summary.ShowItemId, server),
                color = 0x8d5cc7,
                description = Description(summary, poster),
                thumbnail = poster ? new { url = "attachment://poster.jpg" } : null,
                fields = Fields(summary),
                footer = new { icon_url = JellyfinLogoUrl, text = Limit(string.IsNullOrWhiteSpace(serverName) ? "Jellyfin" : serverName, 200) + (test ? " · Test" : "") },
                timestamp = (sentUtc ?? summary.CreatedUtc).ToString("O")
            }
        }
        };
    public static object GenericTest(string? serverName = null, DateTimeOffset? sentUtc = null) => new {
        username = "AniDoki",
        avatar_url = AvatarUrl,
        allowed_mentions = new { parse = Array.Empty<string>() },
        embeds = new[] { new { title = "AniDoki test notification", description = "Webhook connectivity test. No viewing data or tracker changes.",
            footer = new { icon_url = JellyfinLogoUrl, text = Limit(string.IsNullOrWhiteSpace(serverName) ? "Jellyfin" : serverName, 200) + " · Test" },
            timestamp = (sentUtc ?? DateTimeOffset.UtcNow).ToString("O") } }
    };
}
