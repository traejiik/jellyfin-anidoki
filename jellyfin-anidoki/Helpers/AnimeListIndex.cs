using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace jellyfin_anidoki.Helpers;

/// <summary>
/// The anime list's TMDB movie IDs and the AniDB entries they map to, and each AniDB entry's own TMDB and IMDb IDs,
/// read once per version of the file. The list holds only anime, so a movie whose TMDB ID it maps is an anime movie.
/// A missing or unreadable list knows no movie.
/// </summary>
/// <param name="path">Where the anime list file is; null or a throw means there is none.</param>
public sealed class AnimeListIndex(Func<string?> path) {
    /// <summary>The index of the file the anime list task keeps up to date.</summary>
    public static AnimeListIndex Shared { get; } = new(() => Plugin.Instance?.AnimeListPath);

    /// <summary>The TMDB movie IDs and IMDb IDs the list gives one AniDB entry.</summary>
    private sealed record Links(List<string> TmdbIds, List<string> ImdbIds);

    private sealed record Contents(Dictionary<string, int> Movies, Dictionary<int, Links> Entries) {
        public static readonly Contents Empty = new(new Dictionary<string, int>(), new Dictionary<int, Links>());
    }

    private readonly object _gate = new();
    private string? _loadedPath;
    private DateTime _loadedWritten;
    private Contents _contents = Contents.Empty;

    /// <returns>The AniDB ID the list gives this TMDB movie ID, or null when the list does not map it.</returns>
    public int? AniDbIdForMovie(string? tmdbId) {
        if (string.IsNullOrWhiteSpace(tmdbId))
            return null;
        return Read().Movies.TryGetValue(tmdbId.Trim(), out int aniDbId) ? aniDbId : null;
    }

    /// <summary>
    /// Whether the list ties <paramref name="aniDbId"/> to the movie with these TMDB and IMDb IDs. See
    /// <see cref="MovieAniDbVerdict"/> for the order the answers are tried in.
    /// </summary>
    public MovieAniDbCheck CheckMovie(int aniDbId, string? tmdbId, string? imdbId) {
        Contents contents = Read();
        string? tmdb = Clean(tmdbId);
        string? imdb = Clean(imdbId);
        contents.Entries.TryGetValue(aniDbId, out Links? links);
        if (links != null && (Lists(links.TmdbIds, tmdb) || Lists(links.ImdbIds, imdb)))
            return new MovieAniDbCheck(MovieAniDbVerdict.Agrees, aniDbId);
        if (tmdb != null && contents.Movies.TryGetValue(tmdb, out int listed) && listed != aniDbId)
            return new MovieAniDbCheck(MovieAniDbVerdict.Replaced, listed);
        if (links == null)
            return new MovieAniDbCheck(MovieAniDbVerdict.NotListed, null);
        bool comparable = (tmdb != null && links.TmdbIds.Count > 0) || (imdb != null && links.ImdbIds.Count > 0);
        return new MovieAniDbCheck(comparable ? MovieAniDbVerdict.Conflicts : MovieAniDbVerdict.Unconfirmed, null);
    }

    private static string? Clean(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();

    private static bool Lists(List<string> ids, string? id) => id != null && ids.Contains(id, StringComparer.OrdinalIgnoreCase);

    private Contents Read() {
        string? file;
        try {
            file = path();
        } catch (Exception) {
            file = null;
        }
        lock (_gate) {
            if (file == null || !File.Exists(file)) {
                _loadedPath = null;
                _contents = Contents.Empty;
                return _contents;
            }
            DateTime written = File.GetLastWriteTimeUtc(file);
            if (file != _loadedPath || written != _loadedWritten) {
                _contents = Read(file);
                _loadedPath = file;
                _loadedWritten = written;
            }
            return _contents;
        }
    }

    /// <summary>Streams the file for each row's <c>anidbid</c>, <c>tmdbid</c> and <c>imdbid</c>; the rest of the list is not needed here.</summary>
    private static Contents Read(string file) {
        var movies = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = new Dictionary<int, Links>();
        try {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
            while (reader.ReadToFollowing("anime")) {
                if (!int.TryParse(reader.GetAttribute("anidbid"), out int aniDbId))
                    continue;
                string[] tmdbIds = Split(reader.GetAttribute("tmdbid"));
                string[] imdbIds = Split(reader.GetAttribute("imdbid"));
                foreach (string tmdbId in tmdbIds)
                    movies.TryAdd(tmdbId, aniDbId);
                if (!entries.TryGetValue(aniDbId, out Links? links))
                    entries[aniDbId] = links = new Links([], []);
                links.TmdbIds.AddRange(tmdbIds);
                links.ImdbIds.AddRange(imdbIds);
            }
        } catch (Exception e) when (e is XmlException or IOException) {
            // A half-written or corrupt list maps nothing until the task replaces it.
            return Contents.Empty;
        }
        return new Contents(movies, entries);
    }

    private static string[] Split(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
