namespace jellyfin_anidoki.Matching;

/// <summary>Which saved match applies to one user, provider and source.</summary>
public static class MatchPrecedence {
    /// <summary>
    /// A personal override wins while its provider is linked, and stays dormant otherwise; it is complete, so nothing
    /// from the shared match is mixed in.
    /// </summary>
    /// <returns>The match to resolve through, or null when automatic resolution applies.</returns>
    public static Match? Effective(Match? personal, Match? shared, bool providerLinked) =>
        providerLinked && personal != null ? personal : shared;
}
