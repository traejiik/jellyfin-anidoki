# Tracker logos

These local assets identify their respective services. Names and marks belong to
their owners; their inclusion does not imply endorsement. No logo is fetched from
an external server at runtime.

## Simple Icons

The AniList, MyAnimeList, and Simkl SVG drawings come from
[Simple Icons](https://github.com/simple-icons/simple-icons). None of the three
publishes its own logo under a license (checked 2026-10-26: no brand kit or licensed
source for AniList, MyAnimeList, or Simkl), so their Simple Icons drawings stay. Revision
`98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d` (retrieved 2026-10-09).
Simple Icons distributes its drawings under **CC0 1.0 Universal**; its complete
license is retained in `LICENSE-Simple-Icons.txt`. Trademark rights are unaffected.

| Local file | Source drawing | Primary brand source recorded by Simple Icons | Color |
| --- | --- | --- | --- |
| `provider-anilist.svg` | [anilist.svg](https://github.com/simple-icons/simple-icons/blob/98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d/icons/anilist.svg) | https://anilist.co | `#02A9FF` |
| `provider-mal.svg` | [myanimelist.svg](https://github.com/simple-icons/simple-icons/blob/98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d/icons/myanimelist.svg) | https://myanimelist.net/wrap-up/anime-logo-mobile.svg | `#2E51A2` |
| `provider-simkl.svg` | [simkl.svg](https://github.com/simple-icons/simple-icons/blob/98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d/icons/simkl.svg) | https://simkl.com | `#000000` |

The path drawing, title, and viewBox are unchanged. The only SVG modification is
an explicit root fill using the brand color from Simple Icons' metadata. Each SVG
was inspected: it contains only an SVG root, title, and path, with no scripts,
event handlers, external resources, stylesheets, or embedded images.

## Kitsu

`provider-kitsu.svg` is an unmodified copy of Kitsu's own fox icon,
[src/assets/favicon.svg](https://github.com/hummingbird-me/kitsu-web/blob/2791b69cccfef97070697f6252b5d6e6f12864b0/src/assets/favicon.svg)
in [hummingbird-me/kitsu-web](https://github.com/hummingbird-me/kitsu-web) at revision
`2791b69cccfef97070697f6252b5d6e6f12864b0` (retrieved 2026-10-26). The repository is
licensed under the **Apache License, Version 2.0**, retained in `LICENSE-Kitsu.txt`;
it has no NOTICE file. Section 6 grants no trademark rights.

## Shikimori

`provider-shikimori.svg` is an unmodified copy of Shikimori's own glyph,
[app/assets/images/src/favicon.svg](https://github.com/shikimori/shikimori/blob/a900114c48be8fdbc212139017b18e81a1c9a76f/app/assets/images/src/favicon.svg)
in [shikimori/shikimori](https://github.com/shikimori/shikimori) at revision
`a900114c48be8fdbc212139017b18e81a1c9a76f` (retrieved 2026-10-26; the repository is
archived). It is licensed under the **Open Software License 3.0**, retained in
`LICENSE-Shikimori.txt`; this file is its own source. No trademark license is granted.

Both were inspected: they contain only paths, groups, and circles, with no scripts,
event handlers, stylesheets, embedded images, or external resources.

## Jellyfin

The sign-in callback page shows Jellyfin's own icon and banner, unmodified copies of
the official artwork in [jellyfin-ux](https://github.com/jellyfin/jellyfin-ux) at
revision `69a2b7b1237732a134dd501ff55b9404cf2d1601` (retrieved 2026-10-26):

| Local file | Source file |
| --- | --- |
| `jellyfin.svg` | [branding/SVG/icon-transparent.svg](https://github.com/jellyfin/jellyfin-ux/blob/69a2b7b1237732a134dd501ff55b9404cf2d1601/branding/SVG/icon-transparent.svg) |
| `jellyfin-banner.svg` | [branding/SVG/banner-dark.svg](https://github.com/jellyfin/jellyfin-ux/blob/69a2b7b1237732a134dd501ff55b9404cf2d1601/branding/SVG/banner-dark.svg) (the version for dark backgrounds) |

Copyright belongs to the Jellyfin contributors. The artwork is licensed under
**Creative Commons Attribution-ShareAlike 4.0 International**; each file keeps its
license block and the full license is retained in `LICENSE-Jellyfin-branding.txt`.
Both were inspected: they contain only a gradient, paths, and a title, with no
scripts, event handlers, stylesheets, or external resources.

## Annict

`provider-annict.png` is an unmodified copy of the official Annict 512px pink and
white icon:

- [Source file](https://github.com/annict/annict/blob/03b1528583ff9b04ef290b430ff1cc0c64a4be8c/rails/public/images/icon-512.png)
- [Official project](https://github.com/annict/annict), revision
  `03b1528583ff9b04ef290b430ff1cc0c64a4be8c` (retrieved 2026-10-09)
- [Repository license](https://github.com/annict/annict/blob/03b1528583ff9b04ef290b430ff1cc0c64a4be8c/LICENSE):
  **GNU Affero General Public License, version 3**, retained verbatim in
  `LICENSE-Annict.txt`. This notice describes this separately sourced asset;
  AniDōki's own source keeps its existing license.

The repository's `rails/public/icon.svg` is a plain red-square placeholder, so the
actual official PNG is used rather than inventing a vector drawing.

The notices and licenses are also embedded in AniDōki's assembly so that the DLL
distribution retains them. They are not exposed by the public asset whitelist.
