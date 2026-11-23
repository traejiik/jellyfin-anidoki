using System.Threading.Tasks;

namespace jellyfin_anidoki.Trackers;

/// <summary>A client whose scoring format is set per account (AniList), read from the signed-in account.</summary>
public interface IScoreFormatSource {
    /// <returns>The account's format, or null when it cannot be read or is not one AniDōki knows.</returns>
    Task<ScoreFormat?> GetScoreFormat();
}
