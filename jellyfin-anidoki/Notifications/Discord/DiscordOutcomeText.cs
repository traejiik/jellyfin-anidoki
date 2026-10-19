using System;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;

namespace jellyfin_anidoki.Notifications.Discord;

/// <summary>
/// The Discord embed's tracker lines, worded as Configuration/OutcomeLinesJs.js words the web toasts.
/// </summary>
internal static class DiscordOutcomeText {
    internal static string ProviderName(ApiName provider) => provider switch {
        ApiName.Mal => "MyAnimeList",
        _ => provider.ToString()
    };

    private static string? Reason(string step) => step switch {
        "already-watched" => "Episode already watched",
        "manual-forward-only" => "Already at or past this episode",
        "rewatch-disabled" => "Skipped · completed, and rewatching is off",
        "rewatch-unsupported" => "Skipped · rewatching is not supported",
        "plan-to-watch-only" => "Skipped · not on Plan to Watch",
        _ => null
    };

    internal static string Result(ProviderWriteOutcome outcome, bool isMovie) {
        if (outcome.Kind != OutcomeKind.Confirmed)
            return Reason(outcome.Step) ?? outcome.Kind switch {
                OutcomeKind.NoChange => "Already up to date",
                OutcomeKind.Skipped => "Skipped",
                OutcomeKind.Unresolved => "No matching entry found",
                OutcomeKind.Failed => "Update failed",
                _ => "Update not confirmed"
            };
        var status = outcome.ConfirmedStatus?.ToString().Replace('_', ' ');
        if (isMovie || outcome.StatusOnly || !outcome.ConfirmedProgress.HasValue)
            return status ?? "Updated";
        var lead = outcome.ConfirmedStatus switch {
            Status.Watching when outcome.ConfirmedProgress == 1 => "Started watching",
            null => "Updated",
            _ => status
        };
        return $"{lead} · Episode {outcome.ConfirmedProgress}";
    }

    /// <summary>Cut at a word boundary and say so, instead of stopping mid-word.</summary>
    internal static string Shorten(string text, int max) {
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length <= max)
            return text;
        var cut = string.Concat(runes.Take(max - 1).Select(r => r.ToString()));
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd() + "…";
    }

    /// <summary>One result: the outcome on its own line, with the entry it applied to as Discord subtext below it.</summary>
    internal static string Line(ProviderWriteOutcome outcome, string showTitle, bool isMovie) {
        var named = !string.IsNullOrWhiteSpace(outcome.TargetTitle) &&
            !string.Equals(outcome.TargetTitle, showTitle, StringComparison.OrdinalIgnoreCase);
        return Result(outcome, isMovie) + (named ? "\n-# " + DiscordEmbedBuilder.Escape(Shorten(outcome.TargetTitle, 100)) : "");
    }
}
