namespace jellyfin_anidoki.Helpers;

/// <summary>Text shaping for log lines.</summary>
internal static class LogText {
    public const int MaxBodyLength = 400;

    /// <summary>A response body cut to <see cref="MaxBodyLength"/> characters, so one large answer cannot flood the log.</summary>
    public static string Truncate(string? text) {
        if (text == null)
            return "";
        return text.Length <= MaxBodyLength ? text : text[..MaxBodyLength] + "...";
    }
}
