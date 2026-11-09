using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Operations;

/// <summary>Reads the <c>provider</c> query value of a manual run: every linked tracker, or one by name.</summary>
internal static class TrackerChoice {
    /// <summary>The value that means every linked tracker. Outbound only.</summary>
    public const string All = "All";

    /// <summary>The tracker named exactly (ignoring case), or null. Numbers and lists are not names.</summary>
    public static ApiName? One(string? value) =>
        Enum.GetValues<ApiName>().Cast<ApiName?>()
            .FirstOrDefault(name => string.Equals(name.ToString(), value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads an outbound choice. Empty or <see cref="All"/> gives null, meaning every linked tracker, as before 1.6.0.
    /// Returns the reason to refuse, or null when the choice is usable.
    /// </summary>
    public static string? ReadOutbound(string? value, IEnumerable<ApiName> linked, out ApiName? only) {
        only = null;
        if (string.IsNullOrEmpty(value) || string.Equals(value, All, StringComparison.OrdinalIgnoreCase))
            return null;
        only = One(value);
        if (only == null)
            return "Choose All or one of this user's linked trackers.";
        return linked.Contains(only.Value) ? null : $"{only} is not linked for this user. Choose All or a linked tracker.";
    }
}
