<h1 align="center">AniDōki</h1>
<h2 align="center">A Jellyfin Plugin</h2>

<p align="center"><img src="docs/assets/branding/anidoki-banner.svg" width="100%" alt="AniDōki logo: interwoven A and D with synchronization arrows"></p>

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

Administrators choose which libraries hold anime under Sync behaviour. **Dedicated** libraries are matched normally. **Mixed** libraries sync only titles that carry an AniList, AniDB, MyAnimeList or Kitsu ID, and movies whose TMDB ID the [anime list](https://github.com/Anime-Lists/anime-lists) maps to an anime, and never guess by title. **Off** libraries, and libraries added later until they are given a mode, are not synced. Music and photo libraries are not listed. Users sync only from these libraries, and only the ones they can open in Jellyfin. Until an administrator chooses, each user's earlier library choice still applies.

Uninstalling AniDōki through Jellyfin removes its Plugin Pages registration. Restart Jellyfin and refresh open clients as required by the plugin lifecycle. Other plugins' pages and AniDōki's saved settings are preserved. Manually deleting the plugin binaries does not run the uninstall callback.

### Matches

Some trackers split one Jellyfin season into several entries: AniList lists Steel Ball Run as separate "STAGE" entries, for example. AniDōki follows the tracker's sequel chain for these on its own. When that picks the wrong entry, a saved match says exactly which entry and episode each episode syncs to.

- Admins manage **shared matches** under Dashboard > Plugins > AniDōki > Matches. The list can show only seasons whose recent syncs found no entry (**Needs a match**), or where several entries' titles matched equally (**Needs review**), and it offers to re-attach matches left behind by a library rebuild.
- Users correct a season for themselves under **Advanced > Tracker matches** on their personal settings page. A **personal correction** applies to that user alone, and an admin can promote it to the shared match.
- For each tracker, a personal correction comes first, then the shared match, then automatic matching. An episode a saved match does not cover is not synced, rather than guessed.
- **Use saved matches** under Settings > Advanced is on by default. Turning it off matches every season automatically, without deleting any saved matches.

### Manual sync

Administrators run a sync by hand from the **Manual sync** tab, in either direction: **Tracker → Jellyfin** marks episodes played from one linked tracker's Completed and Watching lists, and **Jellyfin → Trackers** sends each season's highest watched episode to all linked trackers or one of them.

**Preview changes** opens a window showing what that sync would do without changing anything: one row per season and tracker entry, with the entry's current state, the proposed value and the reason. Tick the rows you want and press **Apply** (from trackers, **Mark … played**): changes start ticked. **Lowers progress** lists entries where the tracker is ahead of Jellyfin; they start unticked, a sync never lowers them by itself, and ticking one asks you to confirm first. **Problems** lists what could not be matched or read.

Each ticked row is checked again just before it is written, and one that changed since the preview is skipped rather than written. While the changes are applied, each row shows its result, and **Stop** finishes the row being written and leaves the rest. A preview can be applied once, within a day, and only while it is the newest; otherwise **Refresh preview** makes a new one. **Run sync without preview** applies the current rules straight away, as before. A progress bar below the form follows the latest run; you can leave the page and come back to it.

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

- [Jellyfin 12.4](https://github.com/jellyfin/jellyfin/releases/tag/v12.4) or newer, last tested on 12.4.0. Servers on Jellyfin 12.2 keep being offered AniDōki 1.x.
- [File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) for toast notifications.
- [Plugin Pages plugin](https://github.com/IAmParadox27/jellyfin-plugin-pages) 3.0.x for the personal settings page, including tracker matches. Only 3.0.1 is tested.

AniDōki is designed for stock Jellyfin 12.4 and works with any theme: its pages keep the same layout and take only their colours and font from the active theme. Themes and [KefinTweaks](https://github.com/ranaldsgift/KefinTweaks/releases) (5.0 and later) are optional, and AniDōki does not require either.

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

Downgrading from 1.5.0 to an earlier version pauses syncing until you upgrade again: 1.5.0 adds to AniDōki's database, and earlier versions refuse a database newer than they know. Nothing is deleted.

## Troubleshooting

### Where the logs are

Open **Logs** in Jellyfin's admin dashboard, or read the files named `log_yyyyMMdd.log` in Jellyfin's log folder (`/config/log` in the official Docker image; see [Jellyfin's configuration docs](https://jellyfin.org/docs/general/administration/configuration/) for other installs). AniDōki's lines are the ones whose source starts with `jellyfin_anidoki`.

Each sync, preview, playback update and manual confirmation is an operation. By default AniDōki writes one line per result and one when the operation finishes:

```text
[2026-11-16 01:17:20.241 +00:00] [INF] [9] jellyfin_anidoki.Operations.OperationRunner: PreviewFromTrackers ae39d23a-6331-4704-9539-80c61e122cf1 finished as Succeeded
```

The log file writes text values in quotes and tracker names and states without them.

### Turning on AniDōki's detailed log

To see every step AniDōki takes, create a file named `logging.json` next to `logging.default.json` in Jellyfin's config folder (`/config/config` in the official Docker image) with:

```json
{
    "Serilog": {
        "MinimumLevel": {
            "Override": {
                "jellyfin_anidoki": "Debug"
            }
        }
    }
}
```

Then restart Jellyfin. Only AniDōki's lines become more detailed; the rest of Jellyfin logs as before. Each operation now starts with a line such as `SyncFromTrackers ea09840b-… started for user d468bb03-…`, and the lines up to its `finished as` line belong to it. The detailed log is long, so delete the file and restart Jellyfin again once you are done.

### Reporting a problem

When you open an issue, include:

- the AniDōki version (Settings > Admin Dashboard > Plugins; a development build looks like `1.7.0.12`) and the Jellyfin version;
- the tracker involved, what you did and roughly when;
- the AniDōki lines from that operation, with the detailed log turned on if you can reproduce it.

AniDōki never writes tokens or passwords to the log, but check the excerpt for anything from other plugins before posting it publicly.

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
