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
        using var stream = typeof(CallbackPage).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Configuration.ResultPage.html")
            ?? throw new InvalidOperationException("ResultPage.html is not embedded.");
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
            CallbackOutcome.WrongServer => ("warning", "Different server", Icons.Unlink),
            _ => ("error", "Not linked", Icons.Cross)
        };
        string facts = Facts(model, P, tone, model.Outcome == CallbackOutcome.Linked ? "Linked" : "Not linked");

        string title, heading, lead, body, closing, primary;
        switch (model.Outcome) {
            case CallbackOutcome.Linked:
                title = $"{P} linked";
                heading = $"{P} is linked to AniDōki";
                lead = Lead($"AniDōki now updates your {p} list as you finish episodes in Jellyfin.");
                body = facts
                    + Note("What happens next", List(
                        $"AniDōki never lowers progress you already have on {p}.",
                        $"To check the connection, select Test next to {p} in your AniDōki settings.",
                        $"You can unlink {p} at any time in your AniDōki settings."));
                closing = "";
                primary = "Back to AniDōki settings";
                break;
            case CallbackOutcome.Expired:
                title = "Sign-in link expired";
                heading = "This sign-in link has expired";
                lead = Lead("The link you used is no longer valid, so nothing was linked.");
                body = facts
                    + Note("A sign-in link stops working when", List(
                        "it has already been used, for example the page was refreshed or opened twice",
                        $"it is older than {Encode(Duration(model.LinkExpiryMinutes))}, the link expiry your administrator set",
                        "Jellyfin restarted between selecting Link and approving"));
                closing = $"Select Link next to {p} again to get a new sign-in link.";
                primary = "Start again in AniDōki settings";
                break;
            case CallbackOutcome.WrongServer:
                title = "Sign-in reached a different server";
                heading = "This sign-in started on another server";
                lead = Lead($"{P} sent you to this Jellyfin server, but the sign-in started on a different one. Usually the {p} app’s redirect URL points to the wrong server.");
                body = facts
                    + RedirectField(model.RedirectUrl)
                    + Note("To fix it", List(ordered: true,
                        $"Ask your Jellyfin administrator to set the {p} app’s redirect URL to the address above.",
                        "Open AniDōki settings on this server and select Link again."));
                closing = $"Nothing was linked, and no {p} data was stored on this server.";
                primary = "Open AniDōki settings here";
                break;
            case CallbackOutcome.Declined:
                title = $"{P} not linked";
                heading = $"{P} didn’t link your account";
                lead = Lead($"Access was declined on {p}, so AniDōki can’t update your list there. If that was a mistake, try again and choose Allow.");
                body = facts
                    + Note("Still not working?",
                        $"<p>If this keeps happening after you choose Allow, ask your Jellyfin administrator to check the {p} app’s client ID and secret in AniDōki settings.</p>"
                        + Details(model.ErrorCode));
                closing = "Nothing was linked. Your other trackers are unaffected.";
                primary = "Try again in AniDōki settings";
                break;
            default:
                title = $"{P} not linked";
                heading = $"{P} didn’t accept the sign-in";
                lead = Lead($"{P} returned an error while AniDōki was finishing the link, so nothing was linked.");
                body = facts
                    + Note("What to try",
                        List(
                            "Select Link again from your AniDōki settings.",
                            $"If it keeps failing, ask your Jellyfin administrator to check the {p} app’s client ID, secret and redirect URL in AniDōki settings.")
                        + Details(model.ErrorCode));
                closing = "Nothing was linked. Your other trackers are unaffected.";
                primary = "Try again in AniDōki settings";
                break;
        }

        string connection = ProviderLogo(model.Provider, assets, P)
            + $"<div class=\"doki-cb-link doki-cb-{tone}\" role=\"img\" aria-label=\"{badgeLabel}\">{badgeIcon}</div>";
        string actions = Closing(closing) + $"<a class=\"doki-cb-primary\" href=\"{Encode(model.SettingsHref)}\">{primary}</a>";
        return Fill(title, "", connection, heading, lead, body, actions, assets);
    }

    /// <summary>The page the address check in AniDōki settings opens. Reaching it is the whole result.</summary>
    internal static string RenderAddressCheck(string assets) =>
        Fill("Correct address", " doki-cb-centred", "", "This is the correct address", "", "", Closing("You can close this tab."), Encode(assets));

    private static string Fill(string title, string mainClass, string connection, string heading, string lead, string body, string actions, string assets) =>
        Template.Value
            .Replace("{{title}}", title)
            .Replace("{{mainClass}}", mainClass)
            .Replace("{{connection}}", connection)
            .Replace("{{heading}}", heading)
            .Replace("{{lead}}", lead)
            .Replace("{{body}}", body)
            .Replace("{{actions}}", actions)
            .Replace("{{assets}}", assets);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string Lead(string encodedText) => $"<p class=\"doki-cb-lead\">{encodedText}</p>";

    private static string Closing(string encodedText) => encodedText.Length == 0 ? "" : $"<p>{encodedText}</p>";

    private static string ProviderLogo(ApiName? provider, string assets, string encodedName) {
        if (provider is not { } known)
            return $"<div class=\"doki-cb-tile doki-cb-unknown\" role=\"img\" aria-label=\"Tracker\">{Icons.Link}</div>";
        string file = known == ApiName.Annict ? "provider-annict.png" : $"provider-{known.ToString().ToLowerInvariant()}.svg";
        return $"<div class=\"doki-cb-tile doki-cb-provider\"><img src=\"{assets}{file}\" alt=\"{encodedName}\"></div>";
    }

    /// <summary>Without a known tracker the table would only repeat the lead, so it is left out.</summary>
    private static string Facts(CallbackPageModel model, string encodedProvider, string tone, string encodedResult) {
        if (model.Provider == null)
            return "";
        string account = string.IsNullOrWhiteSpace(model.JellyfinUser) ? "" : Fact("Jellyfin account", Encode(model.JellyfinUser));
        return "<dl class=\"doki-cb-facts\">"
            + Fact("Tracker", encodedProvider)
            + account
            + $"<div><dt>Result</dt><dd class=\"doki-cb-result doki-cb-{tone}\">{encodedResult}</dd></div>"
            + "</dl>";
    }

    private static string Fact(string encodedLabel, string encodedValue) => $"<div><dt>{encodedLabel}</dt><dd>{encodedValue}</dd></div>";

    private static string RedirectField(string? redirectUrl) {
        if (string.IsNullOrWhiteSpace(redirectUrl))
            return "";
        return "<div class=\"doki-cb-field\">"
            + "<label for=\"redirectUrl\">Redirect URL for this server</label>"
            + "<div class=\"doki-cb-copy\">"
            + $"<input id=\"redirectUrl\" type=\"text\" readonly value=\"{Encode(redirectUrl)}\">"
            + "<button type=\"button\" data-copy=\"redirectUrl\">Copy</button>"
            + "</div></div>";
    }

    private static string Note(string encodedHeading, string innerHtml) =>
        $"<section class=\"doki-cb-note\"><h2>{encodedHeading}</h2>{innerHtml}</section>";

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
        private const string Open = "<svg width=\"24\" height=\"24\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">";
        internal const string Check = Open + "<path d=\"M20 6 9 17l-5-5\"/></svg>";
        internal const string Clock = Open + "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7v5l3 2\"/></svg>";
        internal const string Unlink = Open + "<path d=\"m18.84 12.25 1.72-1.71a5 5 0 0 0-7.07-7.07l-1.72 1.71\"/><path d=\"m5.17 11.75-1.71 1.71a5 5 0 0 0 7.07 7.07l1.71-1.71\"/><path d=\"M8 2v3M2 8h3M16 22v-3M22 16h-3\"/></svg>";
        internal const string Cross = Open + "<path d=\"M18 6 6 18M6 6l12 12\"/></svg>";
        internal const string Link = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7\"/><path d=\"M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7\"/></svg>";
    }
}
