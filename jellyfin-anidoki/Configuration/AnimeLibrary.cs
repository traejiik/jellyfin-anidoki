using System;

namespace jellyfin_anidoki.Configuration;

/// <summary>How AniDōki treats one Jellyfin library.</summary>
public enum AnimeLibraryMode {
    /// <summary>A library set aside for anime: every title is matched normally.</summary>
    Dedicated,

    /// <summary>Only titles carrying an anime provider ID sync, and never through title search.</summary>
    Mixed,

    /// <summary>Never scanned.</summary>
    Off
}

/// <summary>The admin's choice for one library. Once any library has one, a library without one is Off.</summary>
public sealed class AnimeLibrary {
    /// <summary>The library's ID (a Jellyfin virtual folder's <c>ItemId</c>).</summary>
    public Guid LibraryId { get; set; }

    public AnimeLibraryMode Mode { get; set; }
}
