using System;
using System.Collections.Generic;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Matching;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki_unit_tests.Matching;

/// <summary>Builders for match tests. Entries are AniList entries named by their ID.</summary>
internal static class MatchTestSupport {
    public const string SourceKey = "season:00000000000000000000000000000001:1";

    public const int A = 101;
    public const int B = 102;
    public const int C = 103;
    public const int D = 104;
    public const int Special = 900;

    public static readonly MatchScope SharedScope = new(ApiName.AniList, SourceKey);

    public static EntryRef Ref(int id) => new(ApiName.AniList, id, null);

    public static MatchTarget Target(int id) => new(Ref(id), $"Entry {id}");

    public static MatchRange Range(int start, int? end, int entry, int targetStart = 1) => new(start, end, Target(entry), targetStart);

    public static TargetException SendTo(int episode, int entry, int targetEpisode) => new(episode, Target(entry), targetEpisode);

    public static IgnoreException Skip(int episode) => new(episode);

    public static Match Shared(params MatchRange[] ranges) => new(SharedScope, ranges, []);

    public static Match Shared(MatchRange[] ranges, params MatchException[] exceptions) => new(SharedScope, ranges, exceptions);

    /// <summary>Entries as read, by ID and total; 0 is an unknown length.</summary>
    public static Dictionary<EntryRef, EntryState> Entries(params (int Id, int Total)[] entries) =>
        entries.ToDictionary(entry => Ref(entry.Id), entry => new EntryState(Ref(entry.Id), entry.Total, null, DateTimeOffset.UnixEpoch));

    /// <summary>Episodes 1–48 as four 12-episode entries.</summary>
    public static Match FourTimesTwelve() => Shared(Range(1, 12, A), Range(13, 24, B), Range(25, 36, C), Range(37, 48, D));

    public static Dictionary<EntryRef, EntryState> FourEntries() => Entries((A, 12), (B, 12), (C, 12), (D, 12));
}
