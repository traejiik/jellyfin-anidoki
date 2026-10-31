<h1 align="center">AniDōki</h1>
<h2 align="center">A Jellyfin Plugin</h2>

<p align="center"><img src="docs/assets/branding/anidoki-banner.svg" width="100%" height="240" alt="AniDōki logo: interwoven A and D with synchronization arrows"></p>

> [!IMPORTANT]
> ***Credit:***  
> *The original code and concept for this plugin came from [vosmiic/jellyfin-ani-sync](https://github.com/vosmiic/jellyfin-ani-sync) and AniDōki is a fork that builds on the upstream project as its own evolution.*

## About

AniDōki syncs your anime watch progress between Jellyfin and anime tracking services.

*dōki* (同期) is Japanese for synchronization. AniDōki keeps that meaning of the name from Ani-sync and drops the hyphen for a cleaner name.

## Features

### Admin and user pages

The AniDōki entry under Dashboard > Plugins opens the administrator settings. It works without Plugin Pages.

To let users connect their own tracking accounts, enable user authentication pages in AniDōki and restart Jellyfin to apply the setting.

Uninstalling AniDōki through Jellyfin removes its Plugin Pages registration. Restart Jellyfin and refresh open clients as required by the plugin lifecycle. Other plugins' pages and AniDōki's saved settings are preserved. Manually deleting the plugin binaries does not run the uninstall callback.

### Playback update notifications

After playback stops, AniDōki can show an informational toast for confirmed tracker changes in the web session that played the item. Turn **Show tracker update toasts** on or off in your tracking settings. It is enabled by default.

Web notifications require compatible File Transformation and a web refresh after installation. Plugin Pages is needed for the personal settings page, but is independent of toast delivery. Core synchronization works without either frontend integration.

Toasts show confirmed progress or status, including provider details when results differ. Annict reports status only. They stay visible for six seconds, pause while hovered or focused, and can be dismissed. Container fullscreen is supported; video-only fullscreen suppresses the overlay. Native clients do not display these web toasts.

### Manual watched confirmations

When an administrator enables watched-checkbox tracker updates, a compatible web session asks **Update trackers?** after you mark an individual episode watched. Jellyfin changes immediately; **Update trackers** approves the tracker update and **Dismiss** leaves trackers alone. Registration starts when the updated web client loads and persists for that authentication session across server restarts. Native and unregistered clients retain automatic manual updates.

Pending confirmations do not auto-dismiss. They expire after two minutes and are not replayed after a server restart. Marking the episode unwatched cancels a pending or queued update; once sending has started, it cannot undo a tracker write. Confirmed manual marks only move progress forward and do not restart completed or active rewatches. Bulk marks, movies and another user's marks in an enrolled request require manual sync. Manual sync does not produce these notifications.

### Discord notifications

Each user can enable Discord updates and save their own incoming **Webhook link** in tracking settings. Use a webhook for a normal Discord text channel ([Discord webhook documentation](https://docs.discord.com/developers/resources/webhook)). AniDoki's name and avatar are fixed. **Preview** shows an accessible library example without sending a message; **Send test** explicitly sends a connectivity test after the settings are saved.

An administrator must set the server **Public address** so the bold show title links back to Jellyfin. Notifications include the episode number and title, a processed poster when available, and confirmed tracker results. Partial results show the other outcomes too. Only meaningful confirmed updates from playback or watched marks are sent; pending prompts, dismissed changes, manual sync and failed-only operations stay silent.

Webhook URLs, like tracker tokens and client secrets, are kept in the AniDōki database in the Jellyfin data folder and are never returned to clients. Delivery is bounded and best effort: only explicit Discord rate limits are retried, at most twice. An ambiguous network response is not resent. A rejected webhook pauses that saved revision until it is replaced or a test succeeds.

### Services/providers

The following services are currently supported:

1. MyAnimeList
2. AniList
3. (Beta) Kitsu
4. (Limited support) Annict
5. Shikimori
6. Simkl

## Installation

### Prerequisites

The following are needed for the plugin to run:

- [Jellyfin 12.2](https://github.com/jellyfin/jellyfin/releases/tag/v12.2) or newer.
- [File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) for toast notifications.
- [Plugin Pages plugin](https://github.com/IAmParadox27/jellyfin-plugin-pages) for User Configuration pages.

AniDōki is designed for stock Jellyfin 12.2 and works with any theme: its pages keep the same layout and take only their colours and font from the active theme. Themes and [KefinTweaks](https://github.com/ranaldsgift/KefinTweaks/releases) (5.0 and later) are optional, and AniDōki does not require either.

### Automatic (recommended)

1. Navigate to Settings > Admin Dashboard > Plugins > Repositories
2. Add a new repository with the name `TJK Plugins`(or anything you like) and a `Repository URL` of:

   ```link
   https://raw.githubusercontent.com/traejiik/jellyfin-tjk-plugins/main/manifest.json
   ```

3. Save, refresh and navigate to Catalogue.
4. Select **AniDōki** and install the latest compatible version.
5. Restart Jellyfin, then open **AniDōki** in the dashboard’s plugin section.

### Manual

[See the official Jellyfin documentation for install instructions](https://jellyfin.org/docs/general/server/plugins/index.html#installing).

1. Download a version from the [releases tab](https://github.com/traejiik/jellyfin-anidoki/releases) that matches your Jellyfin version.
2. Copy all package files (`meta.json`, `jellyfin-anidoki.dll`, and `anidoki-card.png`) into `plugins/anidoki` (see above official documentation on where to find the `plugins` folder).
3. Restart your Jellyfin instance.
4. Navigate to Plugins in Jellyfin (Settings > Admin Dashboard > Plugins).
5. Adjust the settings accordingly. I would advise following the detailed instructions on the wiki page *(work in progress)*.

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Python 3 and git.

1. From the repository root, build an installable plugin folder:

   ```sh
   python3 scripts/release.py local
   ```

   This publishes the plugin and writes `jellyfin-anidoki.dll`, `meta.json` and `anidoki-card.png` to `bin/anidoki/`. Use `--output <folder>` to write them somewhere else.

2. Copy all three files into the `plugins/anidoki` folder of your Jellyfin install (create the folder if needed), then restart Jellyfin.

Development builds take their version from the latest release tag plus the number of commits since it. For example, 27 commits after `v1.1.1` builds as `1.1.1.27`, and `meta.json` records the exact commit. Mention that version when you report a bug.

To build without installing the SDK, use the Docker image. It copies the same three files into the folder mounted at `/out`:

```sh
docker build -t anidoki-build .
docker run --rm -v "$PWD/bin/anidoki:/out" anidoki-build
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the development workflow and [ROADMAP.md](ROADMAP.md) for planned work.
