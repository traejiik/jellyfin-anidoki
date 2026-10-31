# AniDōki identity

The existing AniDōki logo, refined: an interwoven A and D between two sync arrows, with the wordmark below. The original was a raster image; this kit rebuilds it as editable SVG geometry that follows the original closely (about 93% pixel overlap) with one stroke weight, exact overlaps and a 60° left leg. The design is unchanged.

## Files

Same set as the Task Timeline kit: `anidoki-banner.svg`, `anidoki-card.png`/`.svg` (1600 x 900 catalogue card), `anidoki-logo.png`/`.svg` (1024 square on Midnight), `anidoki-stacked*.svg`, `anidoki-horizontal*.svg`, `anidoki-wordmark.svg`, `anidoki-symbol*.svg` (colour, black, white, small cut), `anidoki-icon.svg`/`.png` (512 tile), `anidoki-touch-icon.png` (180), `anidoki-favicon.svg`/`.ico`/`-16.png`/`-32.png`, `brand-preview.png`/`.html` (QA) and `brand-presentation.html`.

SVG masters are filled paths only: no live text, raster images, masks or filters. One gradient fades the D's top bar from indigo to periwinkle as it passes under the A.

## Palette and type

| Colour | HEX | Role |
| --- | --- | --- |
| Indigo | `#4337EE` | A left leg and crossbar, lower arrow, "Ani" |
| Periwinkle | `#7080FD` | A right leg and D |
| Sky | `#909FFD` | Upper arrow, "Dōki" |
| Lavender | `#A6B3FE` | Where the ribbon crosses itself |
| Midnight | `#121726` | Backgrounds |

The wordmark is Lexend Black (SIL Open Font License 1.1, notice in `OFL-Lexend.txt`) with softened corners, round dots and a custom notched A, all outlined.

## Usage

- Colour logo on Midnight; black or white one-colour versions elsewhere.
- Full symbol at 48 px and above; the small cut (heavier arrows) below that.
- Keep the aspect ratio; do not recolour parts, add effects or remove the arrows.

## Verification and limits

Rendered and compared against the original at each step; the SVG audit's remaining warnings are the original's own five colours and gradient, and font-derived near-angles in the wordmark. Not checked in print. Not a trademark clearance.

## Repository layout

Required masters and assets used by the plugin are tracked in this folder. The
white symbol remains tracked because it supplies the configuration-page headers;
the logo PNG supplies Discord's avatar.

Unused optional exports and previews live in `generated/`, which is ignored by
Git and excluded from the Docker build context. They remain available locally.

`kit-originals/` preserves the supplied banner and stacked card SVG/PNG, which
differ from the active README banner and horizontal catalog card. The active
card is `anidoki-card.png`; release and manual-install packages copy it without
changing its filename.

## README banner

The README keeps its full-width navy banner at 240 px high. The banner embeds
the unchanged `anidoki-stacked.svg` in a centered 240 × 240 image area with
`preserveAspectRatio="xMidYMid meet"`, matching Task Timeline's banner layout.
The explicit square image area keeps the logo proportional at different README
widths. It replaces the previous raster image and fade mask; the background,
README dimensions, and standalone logo masters are unchanged.
