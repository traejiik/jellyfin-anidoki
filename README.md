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

Administrators choose which libraries hold anime under Sync behaviour. **Dedicated** libraries are matched normally. **Mixed** libraries sync only titles that carry an AniList, AniDB, MyAnimeList or Kitsu ID, and movies whose TMDB ID the [anime list](https://github.com/Anime-Lists/anime-lists) maps to an anime, and never guess by title. **Off** libraries, and libraries added later until they are given a mode, are not synced. Music and photo libraries are not listed. Users sync only from these libraries, and only the ones they can open in Jellyfin. Until an administrator chooses, every library is scanned as Dedicated.

The **History** section lists every user's updates from playback, watched marks and retries over the last 30 days, ten to a page, and can be narrowed to one user. The **Users** table shows each user's last update and a line for any account that needs attention.

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

### Progress popup

The progress popup shows and changes your tracker entries without leaving Jellyfin. Open it with the AniDōki button next to Favourite on a series, season or episode page in an anime library, from the AniDōki entry in the Plugin Pages menu, or from a show's title in a Discord notification.

- **A series** shows its artwork and details, then one pill per linked tracker with its status and progress, plus **All trackers** when two or more are linked. Expand a pill to change the status, episodes watched and rating. With All trackers, one change sets every tracker; values that differ show as **Mixed**.
- **Edits wait until you sync.** **Sync to <tracker>** sends your edits, or Jellyfin's progress when Jellyfin is ahead. **Sync to Jellyfin** marks episodes played up to the tracker's count, for that series only; it never marks episodes unplayed. Lowering a tracker's progress warns first. If the entry changed on the tracker since the popup read it, nothing is written and the popup shows the current values. Unsynced edits are kept while the browser tab stays open.
- **Seasons:** a series matched to several entries, one per season or episode range, lists them under the poster. Pick one to see and edit its entries.
- **Related** lists the entries the tracker relates to it, such as sequels, prequels and side stories, and whether each is in your library. While a show is airing, the details show its weekday and next episode, from AniList.
- **Matches** shows which tracker entry and episodes each season syncs to, and whether that is automatic, a shared match or your own correction. You change matches in your settings.
- **Your anime**, from the Plugin Pages entry, lists the anime libraries with one tracker's progress beside Jellyfin's, as posters or a list, filtered by type and status, or searched. It covers MyAnimeList, AniList and Kitsu for now. Selecting a title opens it.

An entry being rewatched is shown but not editable, and a movie counts as one episode. Start and finish dates and rewatch counts are not editable yet.

Ratings use each tracker's own scale: AniList follows your account's scoring format (100-point, 10-point decimal, 10-point, 5 stars or 3 smileys), Kitsu is out of 10 in steps of 0.5, MyAnimeList, Simkl and Shikimori are out of 10, and Annict has no rating. All trackers offers a rating only when the linked trackers share a scale. Automatic syncs never change a rating.

The popup needs File Transformation, and the administrator's user authentication pages. Only the web client shows it; without File Transformation, the Plugin Pages entry keeps opening your settings page.

### What counts as watched

When you stop playing an episode or movie, AniDōki updates your trackers only if Jellyfin marked it played **and** you actually played it to Jellyfin's resume percentage (90% by default, set by the administrator under **Playback → Resume**).

- Skipping up to 3 minutes or 10% of the runtime, whichever is longer, still counts, so skipping an opening or ending is fine.
- Seeking to the credits does not count, even though Jellyfin marks the episode played.
- Resuming from where you left off counts the part you watched before.
- Watching an episode again counts as a rewatch only when you play it through.
- Items shorter than Jellyfin's minimum resume duration (5 minutes by default), and items with no known length, follow Jellyfin's own played mark.

Playback at up to 2× speed counts in full. A stop that did not count is written to the log as "Did not count … as watched".

#### Completion percentage

By default your trackers are updated when the player exits, as above. Under **When you watch**, you can instead choose a **completion percentage** from 60% up to the server's resume percentage, in steps of 5: your trackers are then updated during playback, as soon as you have watched that much. Jellyfin's own watched mark does not change, and the same rules about skipping and seeking apply.

Administrators set a **Default completion percentage** under Sync behaviour for users who have not chosen their own; in each user's choices it is marked **(default)**. It starts at Jellyfin's own, on player exit.

#### Which titles are updated

- **Only update anime on Plan to watch** limits changes to titles already on your plan-to-watch list.
- **Also add anime that isn't on your list**, under it, adds a title you watch that is not on your list at all as Watching. Titles already on another list are still left alone.
- **Rewatch completed anime** treats completed titles you watch again as rewatches. Simkl and Annict do not support it.

### Retries, history and attention

When a tracker is busy or unreachable, AniDōki tries the update again 2 minutes, 10 minutes, 1 hour and 6 hours later, and stops after the fourth try. A restart in the middle of an update runs it again once Jellyfin is back.

Your **History**, on your personal settings page, lists your updates from the last 30 days with one line per tracker, such as "AniList updated", "AniList failed, retrying" or "No change needed". When a tracker rejects your sign-in, for example because you revoked AniDōki's access, its account row says so and offers **Sign in again**; nothing is sent to that tracker until you do. The same line appears on the administrator's Users table.

### Playback update notifications

After playback stops, AniDōki can show an informational toast for confirmed tracker changes in the web session that played the item. Turn **Show tracker update toasts** on or off in your tracking settings. It is enabled by default.

Web notifications require compatible File Transformation and a web refresh after installation. Plugin Pages is needed for the personal settings page, but is independent of toast delivery. Core synchronization works without either frontend integration.

Toasts show confirmed progress or status, including provider details when results differ. Annict reports status only. They stay visible for six seconds, pause while hovered or focused, and can be dismissed. Container fullscreen is supported; video-only fullscreen suppresses the overlay. Native clients do not display these web toasts.

### Manual watched confirmations

When an administrator enables watched-checkbox tracker updates, a compatible web session asks **Update trackers?** after you mark an individual episode watched. Jellyfin changes immediately; **Update trackers** approves the tracker update and **Dismiss** leaves trackers alone. Registration starts when the updated web client loads and persists for that authentication session across server restarts. Native and unregistered clients retain automatic manual updates.

Pending confirmations do not auto-dismiss. They expire after two minutes and are not replayed after a server restart. Marking the episode unwatched cancels a pending or queued update; once sending has started, it cannot undo a tracker write. Confirmed manual marks only move progress forward and do not restart completed or active rewatches. Bulk marks, movies and another user's marks in an enrolled request require manual sync. Manual sync does not produce these notifications.

### Discord notifications

Each user can enable Discord updates and save their own incoming **Webhook link** in tracking settings. Use a webhook for a normal Discord text channel ([Discord webhook documentation](https://docs.discord.com/developers/resources/webhook)). AniDoki's name and avatar are fixed. **Preview** shows an accessible library example without sending a message; **Send test** explicitly sends a connectivity test after the settings are saved.

An administrator must set the server **Public address** so the bold show title links back to Jellyfin; in the web client the link opens the show's [progress popup](#progress-popup). Notifications include the episode number and title, a processed poster when available, and confirmed tracker results. Partial results show the other outcomes too. Only meaningful confirmed updates from playback or watched marks are sent; pending prompts, dismissed changes, manual sync and failed-only operations stay silent.

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
- [File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) for toast notifications and the progress popup.
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

### Upgrading to 2.0

- AniDōki 2.0 needs Jellyfin 12.4 or newer. Servers on Jellyfin 12.2 keep being offered AniDōki 1.x.
- Anime libraries are now chosen by the administrator alone. On its first start, 2.0 fills the administrator's list from the libraries your users had picked, marks those libraries **chosen by** the users who picked them, and shows a note under Sync behaviour. Review the list, then save or dismiss the note.
- Trackers are now updated only for episodes you actually played (see [What counts as watched](#what-counts-as-watched)), and failed updates are retried.

Downgrading to an earlier version pauses syncing until you upgrade again: newer versions add to AniDōki's database, and earlier versions refuse a database newer than they know. Nothing is deleted.

## Troubleshooting

### Where the logs are

AniDōki writes its own log, `anidoki_yyyyMMdd.log`, next to Jellyfin's in Jellyfin's log folder (`/config/log` in the official Docker image; see [Jellyfin's configuration docs](https://jellyfin.org/docs/general/administration/configuration/) for other installs). Open **Logs** in Jellyfin's admin dashboard to read or download it. A new file starts each day, or at 20 MB, and the last 7 are kept.

Each sync, preview, playback update and manual confirmation is an operation, and each line written during one starts with it, so one search finds them all:

```text
[2026-11-22 19:06:05.988 +00:00] [INF] [SyncToTrackers 120558ab-7999-4843-98f8-69144f50996e user d468bb03-9cfc-41f5-a7d4-67e541bcb9ee] jellyfin_anidoki.Operations.OperationRunner: SyncToTrackers 120558ab-7999-4843-98f8-69144f50996e finished as NoChange
```

By default the file also has AniDōki's **debug** lines: how each title was matched, which tracker calls were made and how long each step took. Turn **Write debug lines to the AniDōki log** off under Settings > Advanced to keep only results, warnings and errors. Jellyfin's own log (`log_yyyyMMdd.log`) keeps its usual level either way and still has AniDōki's result lines.

### Reporting a problem

When you open an issue, include:

- the AniDōki version (Settings > Admin Dashboard > Plugins; a development build looks like `1.7.0.12`) and the Jellyfin version;
- the tracker involved, what you did and roughly when;
- the lines from that operation in the AniDōki log, with debug lines on.

AniDōki never writes tokens or passwords to its log. The log does contain user IDs and anime titles, so check the excerpt before posting it publicly.

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
