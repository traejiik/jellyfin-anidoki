using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>
/// Builds library items whose Season and Series resolve through a mocked library manager, the way
/// Jellyfin resolves them at runtime. Sets the static <see cref="BaseItem.LibraryManager"/>.
/// </summary>
internal sealed class LibraryFixture {
    public Mock<ILibraryManager> Library { get; } = new();

    public LibraryFixture() => BaseItem.LibraryManager = Library.Object;

    public Episode Episode(string seriesName, int seasonNumber, int? episodeNumber,
        Dictionary<string, string>? seriesProviderIds = null, string? episodeName = null) {
        var series = new Series {
            Id = Guid.NewGuid(),
            Name = seriesName,
            ProviderIds = seriesProviderIds ?? new Dictionary<string, string>()
        };
        var season = new Season {
            Id = Guid.NewGuid(),
            Name = $"Season {seasonNumber}",
            IndexNumber = seasonNumber,
            ParentId = series.Id,
            SeriesId = series.Id,
            ProviderIds = new Dictionary<string, string>()
        };
        Register(series);
        Register(season);
        return new Episode {
            Id = Guid.NewGuid(),
            Name = episodeName ?? $"Episode {episodeNumber}",
            IndexNumber = episodeNumber,
            ParentId = season.Id,
            SeasonId = season.Id,
            SeriesId = series.Id,
            SeriesName = seriesName,
            ProviderIds = new Dictionary<string, string>()
        };
    }

    public Movie Movie(string name, Dictionary<string, string>? providerIds = null) =>
        new() { Id = Guid.NewGuid(), Name = name, ProviderIds = providerIds ?? new Dictionary<string, string>() };

    private void Register(Series series) {
        Library.Setup(m => m.GetItemById(series.Id)).Returns(series);
        Library.Setup(m => m.GetItemById<Series>(series.Id)).Returns(series);
        Library.Setup(m => m.GetItemById<BaseItem>(series.Id)).Returns(series);
    }

    private void Register(Season season) {
        Library.Setup(m => m.GetItemById(season.Id)).Returns(season);
        Library.Setup(m => m.GetItemById<Season>(season.Id)).Returns(season);
        Library.Setup(m => m.GetItemById<BaseItem>(season.Id)).Returns(season);
    }
}
