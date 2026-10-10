#nullable enable
using System;
using System.Text.RegularExpressions;
namespace jellyfin_anidoki.Notifications.Discord;

public static partial class DiscordWebhookAddress
{
    [GeneratedRegex("^/api/(?:v10/)?webhooks/([0-9]{17,20})/([A-Za-z0-9_.-]{1,256})$", RegexOptions.CultureInvariant)]
    private static partial Regex PathGrammar();
    public static string? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 512 || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "discord.com" || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || input.Contains('%') || input.Contains('\\') || input.Contains("/../") || input.Contains("/./")) return null;
        var match = PathGrammar().Match(uri.AbsolutePath);
        return match.Success ? $"https://discord.com/api/v10/webhooks/{match.Groups[1].Value}/{match.Groups[2].Value}" : null;
    }
}
