# Tracker logos

These local assets identify their respective services. Names and marks belong to
their owners; their inclusion does not imply endorsement. No logo is fetched from
an external server at runtime.

## Simple Icons

The AniList, MyAnimeList, Kitsu, Shikimori, and Simkl SVG drawings come from
[Simple Icons](https://github.com/simple-icons/simple-icons) at revision
`98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d` (retrieved 2026-10-09).
Simple Icons distributes its drawings under **CC0 1.0 Universal**; its complete
license is retained in `LICENSE-Simple-Icons.txt`. Trademark rights are unaffected.

| Local file | Source drawing | Primary brand source recorded by Simple Icons | Color |
| --- | --- | --- | --- |
| `provider-anilist.svg` | [anilist.svg](https://github.com/simple-icons/simple-icons/blob/98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d/icons/anilist.svg) | https://anilist.co | `#02A9FF` |
| `provider-mal.svg` | [myanimelist.svg](https://github.com/simple-icons/simple-icons/blob/98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d/icons/myanimelist.svg) | https://myanimelist.net/wrap-up/anime-logo-mobile.svg | `#2E51A2` |
| `provider-kitsu.svg` | [kitsu.svg](https://github.com/simple-icons/simple-icons/blob/98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d/icons/kitsu.svg) | https://kitsu.io | `#FD755C` |
| `provider-shikimori.svg` | [shikimori.svg](https://github.com/simple-icons/simple-icons/blob/98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d/icons/shikimori.svg) | https://shikimori.one | `#343434` |
| `provider-simkl.svg` | [simkl.svg](https://github.com/simple-icons/simple-icons/blob/98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d/icons/simkl.svg) | https://simkl.com | `#000000` |

The path drawing, title, and viewBox are unchanged. The only SVG modification is
an explicit root fill using the brand color from Simple Icons' metadata. Each SVG
was inspected: it contains only an SVG root, title, and path, with no scripts,
event handlers, external resources, stylesheets, or embedded images.

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
