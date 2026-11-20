using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopLyric.Services;

public class LyricsService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private int _searchGen;

    static LyricsService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<List<LrcLine>?> SearchAsync(string title, string artist, TimeSpan? trackDuration = null)
    {
        var gen = ++_searchGen;

        var tasks = new[]
        {
            SearchNetease(title, artist),
            SearchQQ(title, artist),
            SearchKugou(title, artist),
            SearchLrcLib(title, artist)
        };
        var results = await Task.WhenAll(tasks);
        if (gen != _searchGen) return null;

        // if we have track duration, score each result by how close its last line is
        List<LrcLine>? result;
        if (trackDuration is { TotalSeconds: >= 20 } dur)
        {
            result = PickByDuration(results, dur);
        }
        else
        {
            result = results.FirstOrDefault(r => r != null && r.Count > 0);
        }

        // retry with cleaned title
        if (result == null || result.Count == 0)
        {
            var clean = CleanTitle(title);
            if (clean != title)
            {
                var retry = await Task.WhenAll(
                    SearchNetease(clean, artist),
                    SearchLrcLib(clean, artist));
                if (gen != _searchGen) return null;
                result = retry.FirstOrDefault(r => r != null && r.Count > 0);
            }
        }

        if (result != null && result.Count > 0)
        {
            var needsTrans = result.Any(l => string.IsNullOrEmpty(l.TranslatedText));
            if (needsTrans)
                _ = Task.Run(() => TranslateInBackground(result, gen));
        }

        return result;
    }

    private static List<LrcLine>? PickByDuration(List<LrcLine>?[] results, TimeSpan trackDur)
    {
        var valid = results.Where(r => r != null && r.Count > 0).ToList();
        if (valid.Count == 0) return null;
        if (valid.Count == 1) return valid[0];

        // score: how close is last lyric line to track duration
        var scored = valid.Select(r =>
        {
            var lastSec = r!.Where(l => !string.IsNullOrWhiteSpace(l.Text))
                .Max(l => l.Time.TotalSeconds);
            var ratio = lastSec / trackDur.TotalSeconds;
            // ideal: 0.88 ~ 1.03
            double score = ratio switch
            {
                > 1.14 => -100,
                > 1.05 => -40,
                >= 0.88 and <= 1.03 => 100,
                >= 0.75 => 50,
                _ => -20
            };
            return (lines: r!, score);
        }).OrderByDescending(x => x.score).ToList();

        return scored[0].lines;
    }

    public void Cancel() => _searchGen++;

    private async Task<List<LrcLine>?> SearchNetease(string title, string artist)
    {
        try
        {
            var q = Uri.EscapeDataString(title + " " + artist);
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://music.163.com/api/search/get?s=" + q + "&type=1&limit=8");
            req.Content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");
            req.Headers.Referrer = new Uri("https://music.163.com");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
            if (!result.TryGetProperty("songs", out var songs)) return null;
            if (songs.GetArrayLength() == 0) return null;

            var songId = PickBest(songs, title, artist, "name", "artists");
            if (songId < 0) return null;

            using var lReq = new HttpRequestMessage(HttpMethod.Get,
                "https://music.163.com/api/song/lyric?id=" + songId + "&lv=1&tv=1&yv=1");
            lReq.Headers.Referrer = new Uri("https://music.163.com");
            var lResp = await _http.SendAsync(lReq);
            if (!lResp.IsSuccessStatusCode) return null;

            using var lDoc = JsonDocument.Parse(await lResp.Content.ReadAsStringAsync());
            if (!lDoc.RootElement.TryGetProperty("lrc", out var lrc)) return null;
            if (!lrc.TryGetProperty("lyric", out var lyricEl)) return null;
            var lrcStr = lyricEl.GetString();
            if (string.IsNullOrEmpty(lrcStr)) return null;

            var lyrics = ParseLrc(lrcStr);
            if (lyrics.Count == 0) return null;
            if (IsInstrumentalPlaceholder(lyrics)) return null;

            // try merge yrc word timings
            if (lDoc.RootElement.TryGetProperty("yrc", out var yrcRoot) &&
                yrcRoot.TryGetProperty("lyric", out var yrcEl))
            {
                var yrcStr = yrcEl.GetString();
                if (!string.IsNullOrEmpty(yrcStr))
                    MergeYrcTimings(lyrics, yrcStr);
            }

            if (lDoc.RootElement.TryGetProperty("tlyric", out var tl) &&
                tl.TryGetProperty("lyric", out var tlEl))
            {
                var transStr = tlEl.GetString();
                if (!string.IsNullOrEmpty(transStr))
                    MergeTranslation(lyrics, ParseLrc(transStr));
            }
            return lyrics;
        }
        catch { return null; }
    }

    private async Task<List<LrcLine>?> SearchQQ(string title, string artist)
    {
        try
        {
            var qqQuery = title + " " + artist;
            var body = JsonSerializer.Serialize(new
            {
                comm = new { ct = 19, cv = 1845 },
                req = new
                {
                    method = "DoSearchForQQMusicDesktop",
                    module = "music.search.SearchCgiService",
                    param = new { num_per_page = 8, page_num = 1, query = qqQuery, search_type = 0 }
                }
            });
            using var sReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            sReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
            sReq.Headers.Referrer = new Uri("https://y.qq.com");
            var sResp = await _http.SendAsync(sReq);
            if (!sResp.IsSuccessStatusCode) return null;

            using var sDoc = JsonDocument.Parse(await sResp.Content.ReadAsStringAsync());
            var list = sDoc.RootElement.GetProperty("req").GetProperty("data")
                .GetProperty("body").GetProperty("song").GetProperty("list");
            if (list.GetArrayLength() == 0) return null;

            string? mid = null;
            var tLow = title.ToLowerInvariant().Trim();
            var aLow = artist.ToLowerInvariant().Trim();
            int best = -1;
            foreach (var s in list.EnumerateArray())
            {
                var n = (s.GetProperty("name").GetString() ?? "").ToLowerInvariant();
                int sc = 0;
                if (n == tLow) sc += 100; else if (n.Contains(tLow) || tLow.Contains(n)) sc += 50;
                if (s.TryGetProperty("singer", out var singers))
                    foreach (var si in singers.EnumerateArray())
                    {
                        var sn = (si.GetProperty("name").GetString() ?? "").ToLowerInvariant();
                        if (sn == aLow || aLow.Contains(sn) || sn.Contains(aLow)) { sc += 30; break; }
                    }
                if (sc > best) { best = sc; mid = s.GetProperty("mid").GetString(); }
            }
            if (best < 10 || string.IsNullOrEmpty(mid)) return null;

            var lyricBody = JsonSerializer.Serialize(new
            {
                comm = new { ct = 19, cv = 1845 },
                req = new
                {
                    method = "GetPlayLyricInfo",
                    module = "music.musichallSong.PlayLyricInfo",
                    param = new { songMID = mid!, songID = 0 }
                }
            });
            using var lReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            lReq.Content = new StringContent(lyricBody, Encoding.UTF8, "application/json");
            lReq.Headers.Referrer = new Uri("https://y.qq.com");
            var lResp = await _http.SendAsync(lReq);
            if (!lResp.IsSuccessStatusCode) return null;

            using var lDoc = JsonDocument.Parse(await lResp.Content.ReadAsStringAsync());
            var data = lDoc.RootElement.GetProperty("req").GetProperty("data");
            var lyricB64 = data.GetProperty("lyric").GetString();
            if (string.IsNullOrEmpty(lyricB64)) return null;

            var lrcStr = Encoding.UTF8.GetString(Convert.FromBase64String(lyricB64));
            var lyrics = ParseLrc(lrcStr);
            if (lyrics.Count == 0) return null;

            if (data.TryGetProperty("trans", out var transEl))
            {
                var transB64 = transEl.GetString();
                if (!string.IsNullOrEmpty(transB64))
                    MergeTranslation(lyrics, ParseLrc(Encoding.UTF8.GetString(Convert.FromBase64String(transB64))));
            }
            return lyrics;
        }
        catch { return null; }
    }

    private async Task<List<LrcLine>?> SearchKugou(string title, string artist)
    {
        try
        {
            var kw = Uri.EscapeDataString(title + " " + artist);
            var sResp = await _http.GetAsync(
                "http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword=" + kw + "&page=1&pagesize=8");
            if (!sResp.IsSuccessStatusCode) return null;

            using var sDoc = JsonDocument.Parse(await sResp.Content.ReadAsStringAsync());
            var info = sDoc.RootElement.GetProperty("data").GetProperty("info");
            if (info.GetArrayLength() == 0) return null;

            string? hash = null;
            var tLow = title.ToLowerInvariant().Trim();
            var aLow = artist.ToLowerInvariant().Trim();
            int best = -1;
            foreach (var s in info.EnumerateArray())
            {
                var n = (s.GetProperty("songname").GetString() ?? "").ToLowerInvariant();
                var sn = (s.GetProperty("singername").GetString() ?? "").ToLowerInvariant();
                int sc = 0;
                if (n == tLow) sc += 100; else if (n.Contains(tLow) || tLow.Contains(n)) sc += 50;
                if (sn == aLow || aLow.Contains(sn) || sn.Contains(aLow)) sc += 30;
                if (sc > best) { best = sc; hash = s.GetProperty("hash").GetString(); }
            }
            if (best < 20 || string.IsNullOrEmpty(hash)) return null;

            var lsResp = await _http.GetAsync(
                "https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=" + kw + "&hash=" + hash);
            if (!lsResp.IsSuccessStatusCode) return null;

            using var lsDoc = JsonDocument.Parse(await lsResp.Content.ReadAsStringAsync());
            var cands = lsDoc.RootElement.GetProperty("candidates");
            if (cands.GetArrayLength() == 0) return null;

            var c0 = cands[0];
            var id = c0.GetProperty("id").GetString();
            var ak = c0.GetProperty("accesskey").GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(ak)) return null;

            var dlResp = await _http.GetAsync(
                "https://lyrics.kugou.com/download?ver=1&client=pc&id=" + id + "&accesskey=" + ak + "&fmt=lrc&charset=utf8");
            if (!dlResp.IsSuccessStatusCode) return null;

            using var dlDoc = JsonDocument.Parse(await dlResp.Content.ReadAsStringAsync());
            var contentB64 = dlDoc.RootElement.GetProperty("content").GetString();
            if (string.IsNullOrEmpty(contentB64)) return null;

            var lrcStr = Encoding.UTF8.GetString(Convert.FromBase64String(contentB64));
            var lyrics = ParseLrc(lrcStr);
            return lyrics.Count > 0 ? lyrics : null;
        }
        catch { return null; }
    }

    private async Task<List<LrcLine>?> SearchLrcLib(string title, string artist)
    {
        try
        {
            var url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title);
            if (!string.IsNullOrEmpty(artist))
                url += "&artist_name=" + Uri.EscapeDataString(artist);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("DesktopLyric/0.1");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.GetArrayLength() == 0) return null;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("syncedLyrics", out var sl) &&
                    sl.ValueKind == JsonValueKind.String)
                {
                    var lrcStr = sl.GetString();
                    if (!string.IsNullOrEmpty(lrcStr))
                    {
                        var lines = ParseLrc(lrcStr);
                        if (lines.Count > 0) return lines;
                    }
                }
            }
            return null;
        }
        catch (HttpRequestException)
        {
            // lrclib is sometimes down, not a big deal
            return null;
        }
        catch { return null; }
    }

    private async Task TranslateInBackground(List<LrcLine> lines, int gen)
    {
        var toTr = lines.Where(l =>
            !string.IsNullOrWhiteSpace(l.Text) && string.IsNullOrEmpty(l.TranslatedText)).ToList();
        if (toTr.Count == 0) return;

        // skip if lyrics are already chinese
        var sample = string.Join("", toTr.Take(8).Select(l => l.Text));
        if (LooksLikeChinese(sample)) return;

        // batch translate, 10 lines at a time so google doesn't get mad
        for (int i = 0; i < toTr.Count; i += 10)
        {
            if (gen != _searchGen) return;
            var batch = toTr.Skip(i).Take(10).ToList();
            var combined = string.Join("\n", batch.Select(l => l.Text));

            try
            {
                var q = Uri.EscapeDataString(combined);
                var resp = await _http.GetAsync(
                    "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-TW&dt=t&q=" + q);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) continue;

                var sb = new StringBuilder();
                var segs = root[0];
                if (segs.ValueKind == JsonValueKind.Array)
                    foreach (var seg in segs.EnumerateArray())
                        if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0)
                            sb.Append(seg[0].GetString());

                var translated = sb.ToString().Trim();
                if (string.IsNullOrEmpty(translated)) continue;

                var tLines = translated.Split('\n');
                for (int j = 0; j < batch.Count && j < tLines.Length; j++)
                {
                    var t = tLines[j].Trim();
                    if (!string.IsNullOrEmpty(t) && t != batch[j].Text)
                        batch[j].TranslatedText = t;
                }
            }
            catch { }

            await Task.Delay(80);
        }
    }

    // --- helpers ---

    private static readonly Regex YrcLineRegex = new(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex YrcWordRegex = new(@"\((\d+),(\d+),\d+\)", RegexOptions.Compiled);

    private static void MergeYrcTimings(List<LrcLine> lyrics, string yrcBody)
    {
        var yrcLines = ParseYrcLines(yrcBody);
        if (yrcLines.Count == 0) return;

        int yi = 0;
        foreach (var line in lyrics)
        {
            int ms = (int)Math.Round(line.Time.TotalMilliseconds);
            while (yi < yrcLines.Count && yrcLines[yi].startMs + 150 < ms) yi++;
            if (yi >= yrcLines.Count) break;
            if (Math.Abs(yrcLines[yi].startMs - ms) <= 1200)
            {
                line.WordTimings = yrcLines[yi].words;
                yi++;
            }
        }
    }

    private static List<(int startMs, List<KaraokeWordTiming> words)> ParseYrcLines(string yrc)
    {
        var result = new List<(int, List<KaraokeWordTiming>)>();
        foreach (var raw in yrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            var hm = YrcLineRegex.Match(line);
            if (!hm.Success) continue;
            if (!int.TryParse(hm.Groups[1].Value, out int lineStart)) continue;

            var rest = line[hm.Length..];
            var matches = YrcWordRegex.Matches(rest);
            if (matches.Count == 0) continue;

            var words = new List<KaraokeWordTiming>();
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                if (!int.TryParse(m.Groups[1].Value, out int absStart) ||
                    !int.TryParse(m.Groups[2].Value, out int dur)) continue;
                int textStart = m.Index + m.Length;
                int textEnd = i + 1 < matches.Count ? matches[i + 1].Index : rest.Length;
                var txt = rest[textStart..textEnd];
                if (string.IsNullOrEmpty(txt)) continue;
                int rel = absStart - lineStart;
                if (rel < 0) rel = 0;
                words.Add(new KaraokeWordTiming(rel, dur, txt));
            }
            if (words.Count > 0)
                result.Add((lineStart, words));
        }
        return result;
    }

    private static long PickBest(JsonElement songs, string title, string artist, string nameKey, string artistsKey)
    {
        long bestId = -1; int bestScore = -1;
        var tLow = title.ToLowerInvariant().Trim();
        var aLow = artist.ToLowerInvariant().Trim();
        foreach (var song in songs.EnumerateArray())
        {
            var name = (song.GetProperty(nameKey).GetString() ?? "").ToLowerInvariant();
            int sc = 0;
            if (name == tLow) sc += 100; else if (name.Contains(tLow) || tLow.Contains(name)) sc += 50;
            if (song.TryGetProperty(artistsKey, out var arts))
                foreach (var a in arts.EnumerateArray())
                {
                    var an = (a.GetProperty("name").GetString() ?? "").ToLowerInvariant();
                    if (an == aLow || aLow.Contains(an) || an.Contains(aLow)) { sc += 30; break; }
                }
            if (sc > bestScore) { bestScore = sc; bestId = song.GetProperty("id").GetInt64(); }
        }
        return bestScore >= 20 ? bestId : -1;
    }

    private static void MergeTranslation(List<LrcLine> orig, List<LrcLine> trans)
    {
        foreach (var t in trans)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            var match = orig.MinBy(o => Math.Abs((o.Time - t.Time).Ticks));
            if (match != null && Math.Abs((match.Time - t.Time).TotalMilliseconds) < 500)
                match.TranslatedText = t.Text;
        }
    }

    /// <summary>netease/qq sometimes return "纯音乐，请欣赏" as lyrics for instrumentals</summary>
    private static bool IsInstrumentalPlaceholder(List<LrcLine> lyrics)
    {
        var meaningful = lyrics.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (meaningful.Count == 0) return false;
        return meaningful.All(l =>
        {
            var s = l.Text.Replace(" ", "");
            return s.Contains("纯音乐") || s.Contains("純音樂") ||
                   s.Contains("请欣赏") || s.Contains("請欣賞") ||
                   s.Contains("没有歌词") || s.Contains("沒有歌詞");
        });
    }

    private static bool LooksLikeChinese(string text)
    {
        int cjk = 0, total = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c)) continue;
            total++;
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
        }
        return total > 0 && (double)cjk / total > 0.3;
    }

    private static readonly Regex LrcRegex = new(@"\[(\d+):(\d+)\.(\d{2,3})\](.*)");
    private static readonly Regex TitleCleanRegex = new(@"\s*[\(\[（].*?[\)\]）]\s*$");

    private static string CleanTitle(string title)
    {
        var cleaned = TitleCleanRegex.Replace(title, "").Trim();
        return string.IsNullOrEmpty(cleaned) ? title : cleaned;
    }

    public static List<LrcLine> ParseLrc(string raw)
    {
        var lines = new List<LrcLine>();
        foreach (var line in raw.Split('\n'))
        {
            var m = LrcRegex.Match(line);
            if (m.Success)
            {
                var min = int.Parse(m.Groups[1].Value);
                var sec = int.Parse(m.Groups[2].Value);
                var msRaw = m.Groups[3].Value;
                var ms = msRaw.Length == 2 ? int.Parse(msRaw) * 10 : int.Parse(msRaw);
                var text = m.Groups[4].Value.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                var time = TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec) + TimeSpan.FromMilliseconds(ms);
                lines.Add(new LrcLine(time, text));
            }
        }
        return lines.OrderBy(l => l.Time).ToList();
    }

    // wrote this thinking I'd need it for plain text lyrics but never used it
    public static List<LrcLine> ParsePlain(string text)
    {
        var lines = new List<LrcLine>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                lines.Add(new LrcLine(TimeSpan.Zero, trimmed));
        }
        return lines;
    }
}

public record LrcLine(TimeSpan Time, string Text)
{
    public string? TranslatedText { get; set; }
    public List<KaraokeWordTiming>? WordTimings { get; set; }
}
