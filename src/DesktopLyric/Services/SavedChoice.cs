namespace DesktopLyric.Services;

public sealed class SavedChoice
{
    public string CandidateKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public IReadOnlyList<string> Keys { get; init; } = [];

    public string SourceLabel => CandidateKey.Split(':')[0] switch
    {
        "ncm" => "網易雲",
        "qq" => "QQ",
        "kg" => "酷狗",
        "lrc" => "LRCLIB",
        var s => s,
    };

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Artist)) parts.Add(Artist);
            parts.Add(SourceLabel);
            if (Keys.Count > 1) parts.Add($"{Keys.Count} 個對應");
            return string.Join("  ·  ", parts);
        }
    }

    internal static SavedChoice From(string candidateKey, List<string> keys)
    {
        var parsed = keys.Select(k =>
        {
            var i = k.IndexOf('|');
            var artist = i < 0 ? "" : k[..i];
            var title = i < 0 ? k : k[(i + 1)..];
            return (artist, title, display: LyricChoiceStore.SearchTitle(title));
        }).ToList();

        var title = parsed.Select(p => p.title)
                .FirstOrDefault(t => LyricChoiceStore.LooksLikeTvSize(t))
            ?? parsed.Select(p => LyricChoiceStore.ExtractParenSong(p.title))
                .FirstOrDefault(t => !string.IsNullOrEmpty(t))
            ?? parsed.Select(p => p.display)
                .Where(t => t.Length is > 0 and < 70 && !t.Contains("映像"))
                .OrderBy(t => t.Length)
                .FirstOrDefault()
            ?? parsed.Select(p => p.title).FirstOrDefault(t => t.Length > 0)
            ?? "";

        var artist = parsed
            .Select(p => LyricChoiceStore.SearchArtist(p.title, p.artist))
            .FirstOrDefault(a => a.Length > 0 && !LooksLikeChannelName(a))
            ?? parsed.Select(p => LyricChoiceStore.SearchArtist(p.title, p.artist))
                .FirstOrDefault(a => a.Length > 0)
            ?? "";

        return new SavedChoice
        {
            CandidateKey = candidateKey,
            Title = title,
            Artist = artist,
            Keys = keys,
        };
    }

    private static bool LooksLikeChannelName(string a)
        => a.Contains("topic", StringComparison.OrdinalIgnoreCase)
           || a.Contains("official", StringComparison.OrdinalIgnoreCase)
           || a.Contains("anime", StringComparison.OrdinalIgnoreCase)
           || a.Contains('/') || a.Contains('／') || a.Contains('✕');
}
