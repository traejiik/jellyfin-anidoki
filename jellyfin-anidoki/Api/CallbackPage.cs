using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Notifications.Discord;

namespace jellyfin_anidoki.Api;

public enum CallbackOutcome {
    Linked,
    Expired,
    WrongServer,
    Declined,
    ProviderError
}

/// <param name="Assets">Prefix for AniDōki asset URLs, relative to the callback route.</param>
/// <param name="RedirectUrl">This server's callback URL, shown on the wrong-server page.</param>
/// <param name="ErrorCode">An OAuth error code, already reduced by <see cref="CallbackPage.SafeErrorCode"/>.</param>
internal sealed record CallbackPageModel(
    CallbackOutcome Outcome,
    ApiName? Provider,
    string? JellyfinUser,
    string Assets,
    string SettingsHref,
    string? RedirectUrl = null,
    string? ErrorCode = null,
    long LinkExpiryMinutes = 1440);

/// <summary>
/// The page a tracker's sign-in redirects back to. The route is anonymous, so the page shows
/// only what the browser already knew (provider, outcome, OAuth error code) plus the user's
/// Jellyfin name, never a provider response body.
/// </summary>
internal static partial class CallbackPage {
    private static readonly Lazy<string> Template = new(() => {
        using var stream = typeof(CallbackPage).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Configuration.CallbackPage.html")
            ?? throw new InvalidOperationException("CallbackPage.html is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$")]
    private static partial Regex ErrorCodePattern();

    [GeneratedRegex("\"error\"\\s*:\\s*\"([A-Za-z0-9_.-]{1,40})\"")]
    private static partial Regex ErrorCodeInBody();

    /// <summary>Keeps an OAuth error code only when it is a short token, so nothing else is echoed.</summary>
    internal static string? SafeErrorCode(string? code) =>
        code != null && ErrorCodePattern().IsMatch(code) ? code : null;

    /// <summary>Finds the OAuth <c>error</c> field in a failed token response's message.</summary>
    internal static string? ErrorCodeFrom(string? message) =>
        message == null ? null : SafeErrorCode(ErrorCodeInBody().Match(message) is { Success: true } match ? match.Groups[1].Value : null);

    internal static int StatusCode(CallbackOutcome outcome) => outcome switch {
        CallbackOutcome.Linked => 200,
        CallbackOutcome.ProviderError => 500,
        _ => 400
    };

    internal static string Render(CallbackPageModel model) {
        // p reads mid-sentence ("your tracker"), P starts one ("Your tracker").
        string p = Encode(model.Provider is { } known ? DiscordOutcomeText.ProviderName(known) : "your tracker");
        string P = Encode(model.Provider is { } named ? DiscordOutcomeText.ProviderName(named) : "Your tracker");
        string assets = Encode(model.Assets);

        var (tone, badgeLabel, badgeIcon) = model.Outcome switch {
            CallbackOutcome.Linked => ("success", "Linked", Icons.Check),
            CallbackOutcome.Expired => ("warning", "Link expired", Icons.Clock),
            CallbackOutcome.WrongServer => ("warning", "Different server", Icons.Server),
            _ => ("error", "Not linked", Icons.Cross)
        };

        string title, heading, lead, body, footer, primary;
        switch (model.Outcome) {
            case CallbackOutcome.Linked:
                title = $"{P} linked";
                heading = $"{P} is linked to AniDōki";
                lead = "";
                body = Account(model.JellyfinUser, "success", $"{P} account", "Linked")
                    + Note("What happens next", List(
                        $"AniDōki updates your {p} list as you finish episodes in Jellyfin.",
                        $"It never lowers progress you already have on {p}.",
                        $"To check the connection, select Test next to {p} in your AniDōki settings."));
                footer = $"You can unlink {p} at any time from your AniDōki settings in Jellyfin. It’s safe to close this tab.";
                primary = "Back to AniDōki settings";
                break;
            case CallbackOutcome.Expired:
                title = "Sign-in link expired";
                heading = "This sign-in link has expired";
                lead = Lead($"{P} approved the sign-in, but AniDōki no longer recognises the link it came from, so nothing was linked.");
                body = Note("A sign-in link stops working when", List(
                    "it has already been used, for example the page was refreshed or opened twice",
                    $"it is older than {Encode(Duration(model.LinkExpiryMinutes))}, the link expiry your administrator set",
                    "Jellyfin restarted between selecting Link and approving"));
                footer = $"Start again from your AniDōki settings in Jellyfin and select Link next to {p}.";
                primary = "Start again in AniDōki settings";
                break;
            case CallbackOutcome.WrongServer:
                title = "Sign-in reached a different server";
                heading = "This sign-in started on another server";
                lead = Lead($"{P} sent you back to this Jellyfin server, but you selected Link on a different one. The {p} app’s redirect URL usually points to the wrong server.");
                body = RedirectField(model.RedirectUrl)
                    + Note("To fix it", List(ordered: true,
                        $"Ask your Jellyfin administrator to set the {p} app’s redirect URL to the address above.",
                        "Open AniDōki settings on this server and select Link again."));
                footer = $"Nothing was linked, and no {p} data was stored on this server.";
                primary = "Open AniDōki settings here";
                break;
            case CallbackOutcome.Declined:
                title = $"{P} not linked";
                heading = $"{P} didn’t link your account";
                lead = Lead($"Access was declined on {p}, so AniDōki can’t update your list there. If that was a mistake, try again and choose Allow.");
                body = Account(model.JellyfinUser, "error", "", $"{P} not linked")
                    + Note("Still not working?",
                        $"<p>If this keeps happening after you choose Allow, ask your Jellyfin administrator to check the {p} app’s client ID and secret in AniDōki settings.</p>"
                        + Details(model.ErrorCode));
                footer = "Nothing was linked. Your other trackers are unaffected.";
                primary = "Try again in AniDōki settings";
                break;
            default:
                title = $"{P} not linked";
                heading = $"{P} didn’t accept the sign-in";
                lead = Lead($"{P} returned an error while AniDōki was finishing the link, so nothing was linked.");
                body = Account(model.JellyfinUser, "error", "", $"{P} not linked")
                    + Note("What to try",
                        List(
                            "Select Link again from your AniDōki settings.",
                            $"If it keeps failing, ask your Jellyfin administrator to check the {p} app’s client ID, secret and redirect URL in AniDōki settings.")
                        + Details(model.ErrorCode));
                footer = "Nothing was linked. Your other trackers are unaffected.";
                primary = "Try again in AniDōki settings";
                break;
        }

        return Template.Value
            .Replace("{{providerLogo}}", ProviderLogo(model.Provider, assets, P))
            .Replace("{{tone}}", tone)
            .Replace("{{badgeLabel}}", badgeLabel)
            .Replace("{{badgeIcon}}", badgeIcon)
            .Replace("{{title}}", title)
            .Replace("{{heading}}", heading)
            .Replace("{{lead}}", lead)
            .Replace("{{body}}", body)
            .Replace("{{footer}}", footer)
            .Replace("{{primary}}", primary)
            .Replace("{{settingsHref}}", Encode(model.SettingsHref))
            .Replace("{{assets}}", assets);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string Lead(string encodedText) => $"<p>{encodedText}</p>";

    private static string ProviderLogo(ApiName? provider, string assets, string encodedName) {
        if (provider is not { } known)
            return $"<div class=\"doki-cb-provider doki-cb-unknown\" role=\"img\" aria-label=\"Tracker\">{Icons.Link}</div>";
        string file = known == ApiName.Annict ? "provider-annict.png" : $"provider-{known.ToString().ToLowerInvariant()}.svg";
        return $"<div class=\"doki-cb-provider\"><img src=\"{assets}{file}\" alt=\"{encodedName}\"></div>";
    }

    private static string Account(string? jellyfinUser, string tone, string encodedLabel, string encodedState) {
        if (string.IsNullOrWhiteSpace(jellyfinUser))
            return "";
        string state = encodedLabel.Length == 0
            ? encodedState
            : $"{encodedState}<small>{encodedLabel}</small>";
        return "<div class=\"doki-cb-account\">"
            + $"<div class=\"doki-cb-avatar\" aria-hidden=\"true\">{Icons.User}</div>"
            + $"<div class=\"doki-cb-who\"><strong>{Encode(jellyfinUser)}</strong><span>Jellyfin account</span></div>"
            + $"<div class=\"doki-cb-state doki-cb-{tone}\">{state}</div>"
            + "</div>";
    }

    private static string RedirectField(string? redirectUrl) {
        if (string.IsNullOrWhiteSpace(redirectUrl))
            return "";
        return "<div class=\"doki-cb-field\">"
            + "<label for=\"redirectUrl\">Redirect URL for this server</label>"
            + "<div class=\"doki-cb-copy\">"
            + $"<input id=\"redirectUrl\" type=\"text\" readonly value=\"{Encode(redirectUrl)}\">"
            + $"<button class=\"doki-cb-button doki-cb-small\" type=\"button\" data-copy=\"redirectUrl\">{Icons.Copy}<span> Copy</span></button>"
            + "</div></div>";
    }

    private static string Note(string encodedHeading, string innerHtml) =>
        $"<section class=\"doki-cb-note\">{Icons.Info}<div><h2>{encodedHeading}</h2>{innerHtml}</div></section>";

    private static string List(params string[] encodedItems) => List(false, encodedItems);

    private static string List(bool ordered, params string[] encodedItems) {
        var builder = new StringBuilder(ordered ? "<ol>" : "<ul>");
        foreach (string item in encodedItems)
            builder.Append("<li>").Append(item).Append("</li>");
        return builder.Append(ordered ? "</ol>" : "</ul>").ToString();
    }

    private static string Details(string? errorCode) =>
        errorCode == null ? "" : $"<details><summary>Technical details</summary><code>error: {Encode(errorCode)}</code></details>";

    internal static string Duration(long minutes) => minutes switch {
        <= 0 => "the set time",
        < 60 => minutes == 1 ? "1 minute" : $"{minutes} minutes",
        _ when minutes % 1440 == 0 => minutes == 1440 ? "24 hours" : $"{minutes / 1440} days",
        _ when minutes % 60 == 0 => minutes == 60 ? "1 hour" : $"{minutes / 60} hours",
        _ => $"{minutes} minutes"
    };

    private static class Icons {
        private const string Open = "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">";
        internal const string Check = Open + "<path d=\"M20 6 9 17l-5-5\"/></svg>";
        internal const string Clock = Open + "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7v5l3 2\"/></svg>";
        internal const string Server = Open + "<rect x=\"3\" y=\"4\" width=\"18\" height=\"7\" rx=\"2\"/><rect x=\"3\" y=\"13\" width=\"18\" height=\"7\" rx=\"2\"/><path d=\"M7 7.5h.01M7 16.5h.01\"/></svg>";
        internal const string Cross = Open + "<path d=\"M18 6 6 18M6 6l12 12\"/></svg>";
        internal const string Link = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7\"/><path d=\"M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7\"/></svg>";
        internal const string User = "<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><circle cx=\"12\" cy=\"8\" r=\"4\"/><path d=\"M4 21a8 8 0 0 1 16 0\"/></svg>";
        internal const string Info = "<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 11v5M12 8h.01\"/></svg>";
        internal const string Copy = "<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><rect x=\"9\" y=\"9\" width=\"12\" height=\"12\" rx=\"2\"/><path d=\"M5 15V5a2 2 0 0 1 2-2h10\"/></svg>";
    }
}
