using System;

namespace jellyfin_anidoki.Trackers;

/// <summary>How a tracker account shows a score.</summary>
public enum ScoreFormat { Point100, Point10Decimal, Point10Half, Point10, Point5, Point3 }

/// <summary>
/// Converts between the raw score the tracker contracts carry and what a user sees in a <see cref="ScoreFormat"/>. A raw
/// score is 1–100; an unrated entry is null. 0 is never a score: clearing is a separate flag on the change.
/// </summary>
public static class ScoreScale {
    private const int Point3Low = 35;
    private const int Point3Middle = 60;
    private const int Point3High = 85;

    public static int Maximum(ScoreFormat format) => format switch {
        ScoreFormat.Point100 => 100,
        ScoreFormat.Point10Decimal or ScoreFormat.Point10Half or ScoreFormat.Point10 => 10,
        ScoreFormat.Point5 => 5,
        ScoreFormat.Point3 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    public static decimal Step(ScoreFormat format) => format switch {
        ScoreFormat.Point10Decimal => 0.1m,
        ScoreFormat.Point10Half => 0.5m,
        ScoreFormat.Point100 or ScoreFormat.Point10 or ScoreFormat.Point5 or ScoreFormat.Point3 => 1m,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    /// <summary>The lowest score the format can hold: one step, except Kitsu's half steps, which start at 1.0 (ratingTwenty 2).</summary>
    public static decimal Minimum(ScoreFormat format) => format == ScoreFormat.Point10Half ? 1m : Step(format);

    /// <returns>The score as shown in <paramref name="format"/>, or null when unrated.</returns>
    public static decimal? ToDisplay(int? raw, ScoreFormat format) {
        if (raw is not { } value)
            return null;
        if (value is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(raw), raw, "A raw score is 1–100");
        return format switch {
            ScoreFormat.Point100 => value,
            ScoreFormat.Point10Decimal => Math.Round(value / 10m, 1, MidpointRounding.AwayFromZero),
            ScoreFormat.Point10Half => Math.Max(1, Math.Round(value / 5m, MidpointRounding.AwayFromZero) / 2),
            ScoreFormat.Point10 => Math.Max(1, Math.Round(value / 10m, MidpointRounding.AwayFromZero)),
            ScoreFormat.Point5 => Math.Max(1, Math.Round(value / 20m, MidpointRounding.AwayFromZero)),
            ScoreFormat.Point3 => value <= Point3Low ? 1 : value <= Point3Middle ? 2 : 3,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    /// <returns>The raw score for a value shown in <paramref name="format"/>, or null when <paramref name="display"/> is null.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a score in the format: below its minimum, above its maximum, or off its step.</exception>
    public static int? ToRaw(decimal? display, ScoreFormat format) {
        if (display is not { } value)
            return null;
        if (value < Minimum(format) || value > Maximum(format) || value % Step(format) != 0)
            throw new ArgumentOutOfRangeException(nameof(display), display, $"Not a {format} score");
        return format switch {
            ScoreFormat.Point100 => (int)value,
            ScoreFormat.Point10Decimal or ScoreFormat.Point10Half or ScoreFormat.Point10 => (int)(value * 10),
            ScoreFormat.Point5 => (int)(value * 20),
            ScoreFormat.Point3 => value == 1 ? Point3Low : value == 2 ? Point3Middle : Point3High,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    /// <summary>Two formats share a scale when they have the same maximum, so one value can be written to both.</summary>
    public static bool SameScale(ScoreFormat a, ScoreFormat b) => Maximum(a) == Maximum(b);

    /// <returns>The format every account on the tracker uses, or null when it is per account (AniList) or there is none (Annict).</returns>
    public static ScoreFormat? FixedFormat(ApiName provider) => provider switch {
        ApiName.Mal or ApiName.Shikimori or ApiName.Simkl => ScoreFormat.Point10,
        ApiName.Kitsu => ScoreFormat.Point10Half,
        _ => null
    };
}
