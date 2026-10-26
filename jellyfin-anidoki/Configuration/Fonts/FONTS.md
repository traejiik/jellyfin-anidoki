# Fonts

The sign-in callback page renders outside Jellyfin's web client, so it ships the font
the client uses for its Modern layout: **Noto Sans** (the MUI theme's
`fontFamily: '"Noto Sans", sans-serif'`), at the two weights the client bundles.

| Local file | Source |
| --- | --- |
| `noto-sans-latin-400.woff2` | `@fontsource/noto-sans` 5.3.0, `files/noto-sans-latin-400-normal.woff2` |
| `noto-sans-latin-700.woff2` | `@fontsource/noto-sans` 5.3.0, `files/noto-sans-latin-700-normal.woff2` |
| `noto-sans-latin-ext-400.woff2` | `@fontsource/noto-sans` 5.3.0, `files/noto-sans-latin-ext-400-normal.woff2` |
| `noto-sans-latin-ext-700.woff2` | `@fontsource/noto-sans` 5.3.0, `files/noto-sans-latin-ext-700-normal.woff2` |

These are the same files Jellyfin 12.3's web client serves (byte-identical for the
latin 400 subset), unmodified. The page's `unicode-range` values match the client's.

Copyright 2022 The Noto Project Authors. Licensed under the **SIL Open Font License,
Version 1.1**, retained in `LICENSE-Noto-Sans.txt`.
