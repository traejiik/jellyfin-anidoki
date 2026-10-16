#nullable enable
using System;

namespace jellyfin_anidoki.ManualMarks;

public sealed class ManualMarkRequestContext(Guid userId, string key, Guid sourceItemId,
    bool withhold, bool alreadyPlayed, bool ownEpisode) {
    internal const string ItemKey = "AniDoki.ManualMarkRequest";
    private readonly object _lock = new();
    public Guid UserId { get; } = userId;
    public string AuthenticationKey { get; } = key;
    public Guid SourceItemId { get; } = sourceItemId;
    public bool Withhold { get; } = withhold;
    public bool SavedOwnSource { get; private set; }
    public bool ShouldPrompt => Withhold && !alreadyPlayed && ownEpisode && SavedOwnSource;
    public bool Observe(Guid savedUser, Guid savedItem, bool played) {
        lock (_lock) {
            if (savedUser == UserId && savedItem == SourceItemId && played)
                SavedOwnSource = true;
            return Withhold;
        }
    }
}
