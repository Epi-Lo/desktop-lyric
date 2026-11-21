namespace DesktopLyric.Services;

public sealed class LyricCandidate
{
    public string Key { get; init; } = "";
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public TimeSpan Duration { get; init; }

    public string DurationText =>
        Duration.TotalSeconds >= 1
            ? $"{(int)Duration.TotalMinutes}:{Duration.Seconds:D2}"
            : "";

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Artist)) parts.Add(Artist);
            if (!string.IsNullOrWhiteSpace(Album)) parts.Add(Album);
            if (Duration.TotalSeconds >= 1) parts.Add(DurationText);
            parts.Add(Source);
            return string.Join("  ·  ", parts);
        }
    }
}
