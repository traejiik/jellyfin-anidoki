using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace jellyfin_anidoki.Helpers;

/// <summary>
/// The anime list's TMDB movie IDs and the AniDB entries they map to, read once per version of the file. The list
/// holds only anime, so a movie whose TMDB ID it maps is an anime movie. A missing or unreadable list knows no movie.
/// </summary>
/// <param name="path">Where the anime list file is; null or a throw means there is none.</param>
public sealed class AnimeListIndex(Func<string?> path) {
    /// <summary>The index of the file the anime list task keeps up to date.</summary>
    public static AnimeListIndex Shared { get; } = new(() => Plugin.Instance?.AnimeListPath);

    private readonly object _gate = new();
    private string? _loadedPath;
    private DateTime _loadedWritten;
    private Dictionary<string, int> _movies = new();

    /// <returns>The AniDB ID the list gives this TMDB movie ID, or null when the list does not map it.</returns>
    public int? AniDbIdForMovie(string? tmdbId) {
        if (string.IsNullOrWhiteSpace(tmdbId))
            return null;
        return Movies().TryGetValue(tmdbId.Trim(), out int aniDbId) ? aniDbId : null;
    }

    private Dictionary<string, int> Movies() {
        string? file;
        try {
            file = path();
        } catch (Exception) {
            file = null;
        }
        lock (_gate) {
            if (file == null || !File.Exists(file)) {
                _loadedPath = null;
                _movies = new Dictionary<string, int>();
                return _movies;
            }
            DateTime written = File.GetLastWriteTimeUtc(file);
            if (file != _loadedPath || written != _loadedWritten) {
                _movies = Read(file);
                _loadedPath = file;
                _loadedWritten = written;
            }
            return _movies;
        }
    }

    /// <summary>Streams the file for each row's <c>anidbid</c> and <c>tmdbid</c>; the rest of the list is not needed here.</summary>
    private static Dictionary<string, int> Read(string file) {
        var movies = new Dictionary<string, int>(StringComparer.Ordinal);
        try {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
            while (reader.ReadToFollowing("anime")) {
                if (!int.TryParse(reader.GetAttribute("anidbid"), out int aniDbId))
                    continue;
                foreach (string tmdbId in (reader.GetAttribute("tmdbid") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    movies.TryAdd(tmdbId, aniDbId);
            }
        } catch (Exception e) when (e is XmlException or IOException) {
            // A half-written or corrupt list maps nothing until the task replaces it.
            return new Dictionary<string, int>();
        }
        return movies;
    }
}
