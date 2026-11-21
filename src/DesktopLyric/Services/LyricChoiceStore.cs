using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopLyric.Services;

/// <summary>Remembers which lyrics candidate the user picked for a playing track.</summary>
public static class LyricChoiceStore
{
    private static string _storePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopLyric", "choices.json");

    private static Dictionary<string, string>? _cache;
    private static readonly object _lock = new();

    // YouTube anime OP dumps: （ LEVEL5 -judgelight-／ fripSide）
    private static readonly Regex SongSlashArtist = new(
        @"[（(]\s*([^／/）)]+?)\s*[／/]\s*([^）)]+?)\s*[）)]",
        RegexOptions.CultureInvariant);
    private static readonly Regex CampaignBrackets = new(
        @"\s*【.*?】\s*", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingJunk = new(
        @"\s*[\(\[（【].*?[\)\]）】]\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex LeadingTrackNo = new(
        @"^\d+[\.．、]\s*", RegexOptions.CultureInvariant);
    private static readonly Regex TopicSuffix = new(
        @"\s*-\s*topic$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex QuotedJp = new(
        @"[「『]([^」』]{2,})[」』]", RegexOptions.CultureInvariant);

    internal static void ResetForTests(string storePath)
    {
        lock (_lock)
        {
            _storePath = storePath;
            _cache = null;
        }
    }

    public static string? Get(string? title, string? artist)
    {
        var dict = Load();
        if (dict.Count == 0) return null;

        var wantTitles = TitleKeys(title);
        if (wantTitles.Count == 0) return null;
        var wantArtists = ArtistKeys(artist, title);

        foreach (var t in wantTitles)
        foreach (var a in wantArtists)
        {
            if (dict.TryGetValue($"{a}|{t}", out var exact))
                return exact;
        }

        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in dict)
        {
            var (storedArtist, storedTitle) = SplitKey(kv.Key);
            var storedTitles = TitleKeys(storedTitle);
            if (!TitlesOverlap(wantTitles, storedTitles, title, storedTitle))
                continue;
            var na = NormArtist(storedArtist);
            var artistHit = wantArtists.Any(w => w.Length > 0 && na.Length > 0 && ArtistsMatch(w, na));
            hits[kv.Value] = hits.GetValueOrDefault(kv.Value) + (artistHit ? 3 : 1);
        }

        if (hits.Count == 0) return null;
        var best = hits.OrderByDescending(h => h.Value).ToList();
        if (best.Count == 1 || best[0].Value > best[1].Value)
            return best[0].Key;
        return null;
    }

    public static void Set(string? title, string? artist, string candidateKey)
    {
        var keys = AllKeys(title, artist);
        if (keys.Count == 0) return;
        var titles = TitleKeys(title);
        var artists = ArtistKeys(artist, title);
        lock (_lock)
        {
            var dict = Load();
            foreach (var existing in dict.Keys.ToList())
            {
                if (keys.Contains(existing)) continue;
                var (sa, st) = SplitKey(existing);
                if (!TitlesOverlap(titles, TitleKeys(st), title, st)) continue;
                var na = NormArtist(sa);
                var sameSong = na.Length == 0
                    || artists.Any(a => a.Length == 0 || ArtistsMatch(a, na));
                if (sameSong)
                    dict.Remove(existing);
            }
            foreach (var key in keys)
                dict[key] = candidateKey;
            _cache = dict;
        }
        Save();
    }

    public static void Clear(string? title, string? artist)
    {
        var keys = new HashSet<string>(AllKeys(title, artist));
        lock (_lock)
        {
            var dict = Load();
            foreach (var key in keys)
                dict.Remove(key);
            _cache = dict;
        }
        Save();
    }

    public static IReadOnlyList<SavedChoice> ListAll()
    {
        var dict = Load();
        return dict
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .Select(g => SavedChoice.From(g.Key, g.Select(kv => kv.Key).ToList()))
            .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Artist, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool RemoveCandidate(string candidateKey)
    {
        if (string.IsNullOrEmpty(candidateKey)) return false;
        var removed = false;
        lock (_lock)
        {
            var dict = Load();
            foreach (var key in dict.Keys.ToList())
            {
                if (!string.Equals(dict[key], candidateKey, StringComparison.Ordinal)) continue;
                dict.Remove(key);
                removed = true;
            }
            _cache = dict;
        }
        if (removed) Save();
        return removed;
    }

    public static void Retarget(string oldCandidate, string newCandidate)
    {
        if (string.IsNullOrEmpty(oldCandidate) || string.IsNullOrEmpty(newCandidate)) return;
        if (oldCandidate == newCandidate) return;
        lock (_lock)
        {
            var dict = Load();
            foreach (var key in dict.Keys.ToList())
            {
                if (string.Equals(dict[key], oldCandidate, StringComparison.Ordinal))
                    dict[key] = newCandidate;
            }
            _cache = dict;
        }
        Save();
    }

    /// <summary>Title to send to lyrics APIs: song inside 「」 or （name／artist）, not the YouTube dump.</summary>
    public static string SearchTitle(string? title)
    {
        var song = ExtractParenSong(title);
        if (string.IsNullOrEmpty(song))
            song = ExtractQuotedSong(title);
        if (string.IsNullOrEmpty(song))
        {
            song = StripCampaign(Norm(title));
            song = LeadingTrackNo.Replace(song, "").Trim();
        }
        if (string.IsNullOrEmpty(song))
            song = (title ?? "").Trim();
        if (LooksLikeTvOp(title) && !LooksLikeTvSize(song))
            song += " TVサイズ";
        return song;
    }

    internal static string? ExtractQuotedSong(string? title)
    {
        var m = QuotedJp.Match(title ?? "");
        if (!m.Success) return null;
        var song = m.Groups[1].Value.Trim();
        return song.Length >= 2 ? song : null;
    }

    internal static bool LooksLikeTvOp(string? title)
    {
        var t = title ?? "";
        return t.Contains("OP映像", StringComparison.OrdinalIgnoreCase)
            || t.Contains("ED映像", StringComparison.OrdinalIgnoreCase)
            || t.Contains("後期OP", StringComparison.OrdinalIgnoreCase)
            || t.Contains("前期OP", StringComparison.OrdinalIgnoreCase)
            || t.Contains("ノンクレジット", StringComparison.OrdinalIgnoreCase)
            || t.Contains("オープニング", StringComparison.OrdinalIgnoreCase)
            || LooksLikeTvSize(t);
    }

    internal static bool LooksLikeTvSize(string? title)
    {
        var t = title ?? "";
        return t.Contains("tvサイズ", StringComparison.OrdinalIgnoreCase)
            || t.Contains("tv size", StringComparison.OrdinalIgnoreCase)
            || t.Contains("tv-size", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(t, @"\(tv\)", RegexOptions.IgnoreCase);
    }

    /// <summary>Artist to send to lyrics APIs. Channel names like "NBCUNIVERSAL ANIME/MUSIC" yield the paren artist.</summary>
    public static string SearchArtist(string? title, string? artist)
    {
        var paren = ExtractParenArtist(title);
        var a = TopicSuffix.Replace((artist ?? "").Trim(), "").Trim();
        if (!string.IsNullOrEmpty(paren) && (string.IsNullOrWhiteSpace(a) || LooksLikeChannel(a)))
            return paren;
        return a;
    }

    internal static string NormTitle(string? s) => TitleKeys(s).LastOrDefault() ?? "";

    internal static string NormArtist(string? s)
    {
        s = (s ?? "").Trim();
        foreach (var sep in new[] { "/", "／", ",", "、", "&", " feat.", " ft.", " feat ", " ft " })
        {
            var i = s.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0) s = s[..i].Trim();
        }
        s = TopicSuffix.Replace(s, "").Trim();
        return Norm(s);
    }

    internal static List<string> TitleKeys(string? raw)
    {
        var list = new List<string>();
        void Add(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return;
            s = LeadingTrackNo.Replace(s, "").Trim();
            if (s.Length == 0) return;
            if (!list.Contains(s)) list.Add(s);
        }

        var n = Norm(raw);
        Add(n);
        Add(StripCampaign(n));

        foreach (Match m in SongSlashArtist.Matches(n))
            Add(Norm(m.Groups[1].Value));

        var stripped = n;
        for (int i = 0; i < 3; i++)
        {
            var next = TrailingJunk.Replace(stripped, "").Trim();
            if (next == stripped) break;
            stripped = next;
            Add(stripped);
        }

        return list;
    }

    internal static string? ExtractParenSong(string? title)
    {
        var n = Norm(title);
        var m = SongSlashArtist.Match(n);
        if (!m.Success) return null;
        var song = m.Groups[1].Value.Trim();
        return song.Length >= 2 ? song : null;
    }

    internal static string? ExtractParenArtist(string? title)
    {
        var n = Norm(title);
        var m = SongSlashArtist.Match(n);
        if (!m.Success) return null;
        var artist = m.Groups[2].Value.Trim();
        return artist.Length >= 2 ? artist : null;
    }

    private static List<string> ArtistKeys(string? artist, string? title)
    {
        var list = new List<string>();
        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = NormArtist(s);
            if (s.Length == 0 || list.Contains(s)) return;
            list.Add(s);
        }
        Add(artist);
        Add(ExtractParenArtist(title));
        if (list.Count == 0) list.Add("");
        return list;
    }

    internal static List<string> FingerprintKeys(string? title, string? artist)
        => AllKeys(title, artist);

    private static List<string> AllKeys(string? title, string? artist)
    {
        var keys = new List<string>();
        var titles = TitleKeys(title);
        var artists = ArtistKeys(artist, title);
        foreach (var t in titles)
        foreach (var a in artists)
        {
            var key = $"{a}|{t}";
            if (key.EndsWith('|') || keys.Contains(key)) continue;
            keys.Add(key);
        }
        return keys;
    }

    private static bool TitlesOverlap(List<string> want, List<string> stored, string? rawWant, string? rawStored)
    {
        foreach (var w in want)
        foreach (var s in stored)
        {
            if (w == s) return true;
            if (ContainsSong(w, s) || ContainsSong(s, w)) return true;
        }

        var a = Norm(rawWant);
        var b = Norm(rawStored);
        return ContainsSong(a, b) || ContainsSong(b, a);
    }

    private static bool ContainsSong(string haystack, string needle)
    {
        if (needle.Length < 5 || haystack.Length < needle.Length) return false;
        return haystack.Contains(needle, StringComparison.Ordinal);
    }

    private static bool LooksLikeChannel(string? artist)
    {
        var a = artist ?? "";
        if (a.Length == 0) return true;
        if (a.Contains('/') || a.Contains('／') || a.Contains('✕') || a.Contains('×')) return true;
        if (a.Contains("topic", StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Contains("official", StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Contains("anime", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string StripCampaign(string s)
        => CampaignBrackets.Replace(s, " ").Trim();

    private static (string artist, string title) SplitKey(string key)
    {
        var i = key.IndexOf('|');
        if (i < 0) return ("", key);
        return (key[..i], key[(i + 1)..]);
    }

    private static bool ArtistsMatch(string a, string b)
        => a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);

    private static string Norm(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return s;
        try { s = s.Normalize(NormalizationForm.FormKC); }
        catch { }
        if (!LyricFonts.HasKana(s))
            s = S2TConverter.Convert(s);
        s = s.ToLowerInvariant();
        s = Regex.Replace(s, @"\s+", " ");
        s = Regex.Replace(s, @"\s*-\s*", "-");
        return s;
    }

    private static Dictionary<string, string> Load()
    {
        lock (_lock)
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(_storePath))
                {
                    _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(_storePath)) ?? new();
                    return _cache;
                }
            }
            catch { }
            _cache = new();
            return _cache;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            Dictionary<string, string> snap;
            lock (_lock) { snap = new(_cache ?? new()); }
            File.WriteAllText(_storePath, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
