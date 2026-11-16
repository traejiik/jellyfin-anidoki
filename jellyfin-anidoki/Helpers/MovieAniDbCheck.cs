using System;
using System.Linq;
using MediaBrowser.Controller.Entities.Movies;

namespace jellyfin_anidoki.Helpers;

/// <summary>
/// How far a movie's AniDB ID can be trusted. Jellyfin's AniDB metadata plugin finds movies by title, so it can give a
/// Western film the ID of an unrelated anime; the anime list's TMDB and IMDb IDs tell the two apart. Tried in this order.
/// </summary>
public enum MovieAniDbVerdict {
    /// <summary>Shoko matched the file by hash, so its AniDB ID is not checked.</summary>
    Trusted,

    /// <summary>The list gives the AniDB entry the movie's TMDB or IMDb ID.</summary>
    Agrees,

    /// <summary>The list maps the movie's TMDB ID to another AniDB entry, which is used instead.</summary>
    Replaced,

    /// <summary>The list gives the AniDB entry TMDB or IMDb IDs of the kind the movie has, and none is the movie's.</summary>
    Conflicts,

    /// <summary>The list has the AniDB entry, but nothing to compare with the movie.</summary>
    Unconfirmed,

    /// <summary>The list has no row for the AniDB entry.</summary>
    NotListed
}

/// <param name="AniDbId">The AniDB ID to use: the movie's when it agrees or is trusted, the list's when replaced, else null.</param>
public sealed record MovieAniDbCheck(MovieAniDbVerdict Verdict, int? AniDbId) {
    /// <summary>Whether the check shows the movie is the anime its AniDB ID names.</summary>
    public bool Confirms => Verdict is MovieAniDbVerdict.Trusted or MovieAniDbVerdict.Agrees or MovieAniDbVerdict.Replaced;

    /// <returns>The check of the movie's AniDB ID, or null when it has none that parses.</returns>
    public static MovieAniDbCheck? For(Movie movie, AnimeListIndex animeList) {
        if (!AnimeListHelpers.TryGetProviderId(movie.ProviderIds, "AniDB", out string raw) || !int.TryParse(raw, out int aniDbId))
            return null;
        if (movie.ProviderIds.Keys.Any(key => key.StartsWith("Shoko", StringComparison.OrdinalIgnoreCase)))
            return new MovieAniDbCheck(MovieAniDbVerdict.Trusted, aniDbId);
        AnimeListHelpers.TryGetProviderId(movie.ProviderIds, "Tmdb", out string tmdbId);
        AnimeListHelpers.TryGetProviderId(movie.ProviderIds, "Imdb", out string imdbId);
        return animeList.CheckMovie(aniDbId, tmdbId, imdbId);
    }
}
