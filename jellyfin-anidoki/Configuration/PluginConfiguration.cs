using jellyfin_anidoki.Models;
using MediaBrowser.Model.Plugins;

namespace jellyfin_anidoki.Configuration;
/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration {

    /// <summary>
    /// Custom user configuration details.
    /// </summary>
    public UserConfig[]? UserConfig { get; set; }

    /// <summary>
    /// Authentication details of the anime API providers.
    /// </summary>
    public ProviderApiAuth[]? ProviderApiAuth { get; set; }

    /// <summary>
    /// Which libraries hold anime. Empty until an admin chooses; until then every library is scanned as Dedicated.
    /// </summary>
    public AnimeLibrary[] AnimeLibraries { get; set; } = [];

    /// <summary>True once 2.0.0 has seeded <see cref="AnimeLibraries"/> from the users' earlier choices, so it runs once (D9).</summary>
    public bool AnimeLibrariesSeeded { get; set; }

    /// <summary>Which users chose each seeded library, for the admin's note; emptied when the admin saves a choice or dismisses it.</summary>
    public AnimeLibrarySeed[] AnimeLibrarySeeds { get; set; } = [];

    /// <summary>
    /// Overriden callback URL set if the user is using Jellyfin over the internet. Generally should not be set if the user is on LAN.
    /// </summary>
    public string? callbackUrl { get; set; }

    /// <summary>
    /// The URL to redirect the user to on successful authentication.
    /// </summary>
    public string? callbackRedirectUrl { get; set; }

    /// <summary>
    /// Save location of the anime list.
    /// </summary>
    public string? animeListSaveLocation { get; set; }

    /// <summary>
    /// Whether or not marking an episode/movie as watched sends an update to the provider API.
    /// </summary>
    public bool watchedTickboxUpdatesProvider { get; set; }

    /// <summary>
    /// Shikimori app name, which is required on all Shikimori API calls
    /// </summary>
    public string? shikimoriAppName { get; set; }

    /// <summary>
    /// True to update all simkl series episodes to current point.
    /// </summary>
    public bool simklUpdateAll { get; set; } = true;
    /// <summary>
    /// Set once <see cref="simklUpdateAll"/> has been switched on after the release where it started working. Before it,
    /// "off" was ignored, so every user got every episode; turning it on once keeps that, and "off" now holds.
    /// </summary>
    public bool simklUpdateAllHonoured { get; set; }

    /// <summary>
    /// True (the default from 1.5.0) to let saved matches decide what syncs write. Off, they are only compared and
    /// noted, and saved matches stay. A configuration saved without the field reads as on; an explicit false is kept.
    /// </summary>
    public bool applySavedMatches { get; set; } = true;

    /// <summary>
    /// True (the default) to write AniDoki's Debug lines to its own log file as well as Information and above.
    /// Jellyfin's log keeps its own level either way.
    /// </summary>
    public bool LogFileDebug { get; set; } = true;

    /// <summary>
    /// True to update NSFW anime.
    /// </summary>
    public bool updateNsfw { get; set; }

    /// <summary>
    /// How long the memory cache holds onto users authentication details in minutes.
    /// </summary>
    public long authenticationLinkExpireTimeMinutes { get; set; }

    /// <summary>
    /// Custom ARM server base URL.
    /// </summary>
    public string? armServerBaseUrl { get; set; }

    /// <summary>
    /// True to enable user authentication pages.
    /// </summary>
    public bool enableUserPages { get; set; }

    /// <summary>
    /// The custom domain for the Shikimori provider.
    /// </summary>
    public string? shikimoriDomain { get; set; }
}
