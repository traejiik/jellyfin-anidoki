using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Resolution;

/// <summary>How a tracker entry's titles are compared with the titles Jellyfin holds.</summary>
public static class TitleMatching {
    /// <summary>
    /// True when any tracker title equals any candidate, ignoring case and symbols. Candidates are every title Jellyfin
    /// holds for the item (display name and original title), so a library can show English or romaji titles and still
    /// match the tracker's native one.
    /// </summary>
    /// <remarks>
    /// Metadata providers such as Shokofin populate OriginalTitle with the series' native title, and AniList carries a
    /// native title for effectively every entry, so this matches even when the romanisations disagree (e.g. AniDB
    /// "Otome Kaijuu Caramelise" vs AniList "Otome Kaijuu Caraméliser").
    /// </remarks>
    public static bool Matches(EntryTitles titles, IEnumerable<string?> candidates) {
        foreach (var candidate in candidates) {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if ((titles.Main != null && CompareStrings(titles.Main, candidate)) ||
                (titles.English != null && CompareStrings(titles.English, candidate)) ||
                (titles.Japanese != null && CompareStrings(titles.Japanese, candidate)) ||
                titles.Synonyms.Any(synonym => CompareStrings(synonym, candidate)))
                return true;
        }

        return false;
    }

    /// <summary>True if the strings are equal, ignoring symbols and case.</summary>
    public static bool CompareStrings(string first, string second) =>
        string.Compare(first, second, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols) == 0;

    /// <summary>True if <paramref name="first"/> contains <paramref name="second"/>, ignoring symbols and case.</summary>
    public static bool ContainsExtended(string first, string second) =>
        StringFormatter.RemoveSpecialCharacters(first).Contains(StringFormatter.RemoveSpecialCharacters(second), StringComparison.OrdinalIgnoreCase);

    /// <summary>The main title, else the first synonym, else the Japanese title; empty when there are none.</summary>
    public static string Display(EntryTitles? titles) =>
        titles == null ? ""
        : !string.IsNullOrWhiteSpace(titles.Main) ? titles.Main
        : titles.Synonyms.Count > 0 ? titles.Synonyms[0]
        : titles.Japanese ?? "";
}
