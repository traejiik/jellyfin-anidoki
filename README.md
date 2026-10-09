<h1>AniDōki Jellyfin Plugin</h1>

<p><img src="docs/assets/anidoki-logo.png" width="240" alt="AniDōki logo: interwoven A and D with synchronization arrows"></p>

> [!IMPORTANT]
> This repository is a fork of [vosmiic/jellyfin-ani-sync](https://github.com/vosmiic/jellyfin-ani-sync). AniDōki builds on the upstream project as its own evolution.

## About

AniDōki synchronizes your anime watch progress between Jellyfin and anime tracking services.

The name reflects that evolution: Ani-Sync meant Anime-Sync, and *dōki* (同期) is Japanese for synchronization. AniDōki keeps that meaning and drops the hyphen for a cleaner name.

## Installation

### Automatic (recommended)

1. Navigate to Settings > Admin Dashboard > Plugins > Repositories
2. Add a new repository with a `Repository URL` of `https://raw.githubusercontent.com/traejiik/jellyfin-anidoki/development/manifest.json`. The name can be anything you like.
3. Save, and navigate to Catalogue.
4. AniDōki should be present. Click on it and install the latest beta version.

### Manual

[See the official Jellyfin documentation for install instructions](https://jellyfin.org/docs/general/server/plugins/index.html#installing).

1. Download a version from the [releases tab](https://github.com/traejiik/jellyfin-anidoki/releases) that matches your Jellyfin version.
2. Copy all package files (`meta.json`, `jellyfin-anidoki.dll`, and `anidoki-card.png`) into `plugins/anidoki` (see above official documentation on where to find the `plugins` folder).
3. Restart your Jellyfin instance.
4. Navigate to Plugins in Jellyfin (Settings > Admin Dashboard > Plugins).
5. Adjust the settings accordingly. I would advise following the detailed instructions on the [wiki page](https://github.com/traejiik/jellyfin-anidoki/wiki).

### Admin and user pages

The AniDōki entry under Dashboard > Plugins opens the administrator settings. It works without Plugin Pages.

To let users connect their own tracking accounts, install Plugin Pages and its File Transformation prerequisite in versions compatible with Jellyfin 12.2. Enable user authentication pages in AniDōki and restart Jellyfin to apply the setting.

Uninstalling AniDōki through Jellyfin removes its Plugin Pages registration. Restart Jellyfin and refresh open clients as required by the plugin lifecycle. Other plugins' pages and AniDōki's saved settings are preserved. Manually deleting the plugin binaries does not run the uninstall callback.

## Build

1. To build this plugin you will need [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

2. Build plugin with following command
  ```
  dotnet publish --configuration Release --output bin
  ```

3. Place the dll-file in the `plugins/anidoki` folder (you might need to create the folders) of your JF install

## Services/providers

### Currently supported

1. MyAnimeList
2. AniList
3. (Beta) Kitsu
4. (Limited support) Annict
5. Shikimori
6. Simkl
