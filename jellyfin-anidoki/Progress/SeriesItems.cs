using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace jellyfin_anidoki.Progress;

/// <summary>A series or movie as the progress popup shows it, with the match sources it holds.</summary>
/// <param name="Images">The image types it has (Primary, Logo, Backdrop); the client builds the URLs.</param>
/// <param name="SourceKeys">One per numbered season in order, or the movie's own key.</param>
/// <param name="OpenedSeason">The season the popup was opened from (a season or episode ID); null otherwise.</param>
public sealed record SeriesItem(Guid Id, string Name, bool IsMovie, int? Year, int? EndYear, float? CommunityRating, string? OfficialRating,
    string? Status, IReadOnlyList<string> Images, IReadOnlyList<string> SourceKeys, int? OpenedSeason);

/// <summary>Finds the series or movie behind any of its IDs. The boundary between the progress API and Jellyfin's items.</summary>
public interface ISeriesItems {
    /// <returns>Null when the item does not exist, is not a series, season, episode or movie, or the user cannot see it.</returns>
    SeriesItem? Find(Guid itemId, Guid userId);
}

public sealed class JellyfinSeriesItems(ILibraryManager library, IUserManager users) : ISeriesItems {
    private static readonly ImageType[] Shown = [ImageType.Primary, ImageType.Logo, ImageType.Backdrop];

    public SeriesItem? Find(Guid itemId, Guid userId) {
        User? user = users.GetUserById(userId);
        BaseItem? item = library.GetItemById(itemId);
        if (user == null || item == null || Root(item) is not { } root || !root.IsVisible(user, false))
            return null;
        IReadOnlyList<string> keys = root is Series series
            ? series.Children.OfType<Season>().Where(season => season.IndexNumber != null).Select(season => season.IndexNumber!.Value)
                .Order().Select(number => SeasonKey(series.Id, number)).ToList()
            : [MovieKey(root.Id)];
        return new SeriesItem(root.Id, root.Name, root is Movie, root.ProductionYear, root.EndDate?.Year, root.CommunityRating, root.OfficialRating,
            (root as Series)?.Status?.ToString(), Shown.Where(type => root.HasImage(type, 0)).Select(type => type.ToString()).ToList(), keys,
            OpenedSeason(item));
    }

    /// <summary>The series an episode or season belongs to, the series or movie itself, or null for anything else.</summary>
    internal static BaseItem? Root(BaseItem item) => item switch {
        Episode episode => episode.Series,
        Season season => season.Series,
        Series or Movie => item,
        _ => null
    };

    internal static int? OpenedSeason(BaseItem item) => item switch {
        Episode episode => episode.Season?.IndexNumber ?? episode.ParentIndexNumber,
        Season season => season.IndexNumber,
        _ => null
    };

    internal static string SeasonKey(Guid seriesId, int season) => $"season:{seriesId:N}:{season}";

    internal static string MovieKey(Guid movieId) => $"movie:{movieId:N}";
}
