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

        var saved = LyricChoiceStore.Get(title, artist);
        if (!string.IsNullOrEmpty(saved))
        {
            var pinned = await FetchByKeyAsync(saved);
            if (gen != _searchGen) return null;
            if (pinned is { Count: > 0 })
            {
                EnsureTranslation(pinned, gen);
                return pinned;
            }
        }

        var qTitle = LyricChoiceStore.SearchTitle(title);
        var qArtist = LyricChoiceStore.SearchArtist(title, artist);
        var searchDur = UsableTrackDuration(trackDuration);

        var tasks = new[]
        {
            SearchNetease(qTitle, qArtist, searchDur),
            SearchQQ(qTitle, qArtist),
            SearchKugou(qTitle, qArtist),
            SearchLrcLib(qTitle, qArtist)
        };
        var results = await Task.WhenAll(tasks);
        if (gen != _searchGen) return null;

        // if we have track duration, score each result by how close its last line is
        List<LrcLine>? result;
        if (UsableTrackDuration(trackDuration) is { } dur)
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
            var clean = CleanTitle(qTitle);
            if (clean != qTitle)
            {
                var retry = await Task.WhenAll(
                    SearchNetease(clean, qArtist, searchDur),
                    SearchLrcLib(clean, qArtist));
                if (gen != _searchGen) return null;
                result = retry.FirstOrDefault(r => r != null && r.Count > 0);
            }
        }

        if (result != null && result.Count > 0)
        {
            EnsureTranslation(result, gen);
            return result;
        }

        // Empty = finished with nothing. Null is reserved for Cancel / generation mismatch.
        return [];
    }

    public async Task<List<LyricCandidate>> SearchCandidatesAsync(string title, string artist, TimeSpan? trackDuration = null)
    {
        var bags = await Task.WhenAll(
            CandidatesNetease(title, artist),
            CandidatesQQ(title, artist),
            CandidatesKugou(title, artist),
            CandidatesLrcLib(title, artist));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<LyricCandidate>();
        foreach (var bag in bags)
        {
            foreach (var c in bag)
            {
                if (string.IsNullOrEmpty(c.Key) || !seen.Add(c.Key)) continue;
                list.Add(c);
            }
        }
        if (UsableTrackDuration(trackDuration) is { } dur)
        {
            list = list
                .OrderBy(c => DurationDelta(c.Duration, dur))
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return list;
    }

    /// <summary>YouTube mixes are an hour long; scoring by that hides the real 4-minute song.</summary>
    internal static TimeSpan? UsableTrackDuration(TimeSpan? trackDuration)
    {
        if (trackDuration is not { TotalSeconds: >= 20 } d) return null;
        if (d.TotalMinutes >= 12) return null;
        return d;
    }

    private static double DurationDelta(TimeSpan song, TimeSpan track)
    {
        if (song.TotalSeconds < 8) return 10_000;
        return Math.Abs(song.TotalSeconds - track.TotalSeconds);
    }

    public async Task<List<LrcLine>?> FetchAsync(LyricCandidate candidate)
    {
        var gen = ++_searchGen;
        var lines = await FetchByKeyAsync(candidate.Key);
        if (gen != _searchGen) return null;
        if (lines is { Count: > 0 })
        {
            EnsureTranslation(lines, gen);
            return lines;
        }
        return [];
    }

    private void EnsureTranslation(List<LrcLine> result, int gen)
    {
        SplitMixedLyrics(result);
        var needsTrans = result.Any(l =>
            !string.IsNullOrWhiteSpace(l.Text) && string.IsNullOrEmpty(l.TranslatedText));
        if (needsTrans)
            _ = Task.Run(() => TranslateInBackground(result, gen));
    }

    /// <summary>
    /// NetEase karaoke/YRC often packs Japanese + Chinese into one line.
    /// Split so overlay can show JP as current and CN as translation.
    /// </summary>
    public static void SplitMixedLyrics(List<LrcLine> lyrics)
    {
        for (int i = 0; i < lyrics.Count - 1;)
        {
            var a = lyrics[i];
            var b = lyrics[i + 1];
            if (string.IsNullOrWhiteSpace(a.Text) || string.IsNullOrWhiteSpace(b.Text)) { i++; continue; }
            if (Math.Abs((a.Time - b.Time).TotalMilliseconds) > 150) { i++; continue; }
            if (string.IsNullOrEmpty(a.TranslatedText) && IsJapaneseLine(a.Text) && IsChineseOnly(b.Text))
            {
                a.TranslatedText = b.Text;
                lyrics.RemoveAt(i + 1);
                continue;
            }
            if (string.IsNullOrEmpty(b.TranslatedText) && IsChineseOnly(a.Text) && IsJapaneseLine(b.Text))
            {
                b.TranslatedText = a.Text;
                lyrics.RemoveAt(i);
                continue;
            }
            i++;
        }

        for (int i = 0; i < lyrics.Count; i++)
        {
            var line = lyrics[i];
            var (orig, trans) = SplitBilingual(line.Text);
            if (trans == null && line.WordTimings is { Count: > 0 })
            {
                var full = string.Concat(line.WordTimings.Select(w => w.Text ?? ""));
                (orig, trans) = SplitBilingual(full);
            }
            if (trans == null) continue;
            var n = line with { Text = orig };
            n.TranslatedText = string.IsNullOrEmpty(line.TranslatedText) ? trans : line.TranslatedText;
            n.WordTimings = SliceWords(line.WordTimings, orig.Length) ?? line.WordTimings;
            lyrics[i] = n;
        }
    }

    public static (string orig, string? trans) SplitBilingual(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (text, null);
        if (!LyricFonts.HasKana(text) || !LooksLikeChinese(text)) return (text, null);

        foreach (var sep in new[] { " / ", "／", " /", "/ ", " | ", "｜", " // " })
        {
            var at = text.IndexOf(sep, StringComparison.Ordinal);
            if (at <= 0) continue;
            var left = text[..at].Trim();
            var right = text[(at + sep.Length)..].Trim();
            if (left.Length == 0 || right.Length == 0) continue;
            if (IsJapaneseLine(left) && IsChineseOnly(right)) return (left, right);
            if (IsChineseOnly(left) && IsJapaneseLine(right)) return (right, left);
        }

        int lastKana = -1;
        for (int i = 0; i < text.Length; i++)
            if (IsKana(text[i])) lastKana = i;
        if (lastKana < 0 || lastKana >= text.Length - 2) return (text, null);

        int split = lastKana + 1;
        while (split < text.Length && (char.IsWhiteSpace(text[split]) || "·・/／|｜".Contains(text[split])))
            split++;
        var rest = text[split..].Trim();
        var head = text[..split].Trim();
        if (rest.Length >= 2 && IsChineseOnly(rest) && IsJapaneseLine(head))
            return (head, rest);
        return (text, null);
    }

    private static List<KaraokeWordTiming>? SliceWords(List<KaraokeWordTiming>? words, int origChars)
    {
        if (words == null || origChars <= 0) return words;
        var jp = new List<KaraokeWordTiming>();
        var pos = 0;
        foreach (var w in words)
        {
            var len = (w.Text ?? "").Length;
            if (pos >= origChars) break;
            jp.Add(w);
            pos += len;
        }
        return jp.Count > 0 ? jp : words;
    }

    private static bool IsKana(char c) =>
        c is (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF')
            or (>= '\u31F0' and <= '\u31FF') or (>= '\uFF66' and <= '\uFF9D');

    private static bool IsJapaneseLine(string? s) =>
        !string.IsNullOrWhiteSpace(s) && LyricFonts.HasKana(s);

    private static bool IsChineseOnly(string? s) =>
        !string.IsNullOrWhiteSpace(s) && LooksLikeChinese(s) && !LyricFonts.HasKana(s);

    private async Task<List<LrcLine>?> FetchByKeyAsync(string key)
    {
        var i = key.IndexOf(':');
        if (i <= 0) return null;
        var kind = key[..i];
        var id = key[(i + 1)..];
        return kind switch
        {
            "ncm" when long.TryParse(id, out var ncmId) => await FetchNeteaseLyrics(ncmId),
            "qq" => await FetchQQLyrics(id),
            "kg" => await FetchKugouLyrics(id, id),
            "lrc" => await FetchLrcLibById(id),
            _ => null,
        };
    }

    private async Task<List<LyricCandidate>> CandidatesNetease(string title, string artist)
    {
        try
        {
            using var doc = await NeteaseCloudSearch(title, artist, 20);
            if (doc == null || !TryNeteaseSongs(doc.RootElement, out var songs)) return [];
            var list = new List<LyricCandidate>();
            foreach (var s in songs.EnumerateArray())
            {
                list.Add(new LyricCandidate
                {
                    Key = "ncm:" + s.GetProperty("id").GetInt64(),
                    Source = "網易雲",
                    Title = s.GetProperty("name").GetString() ?? "",
                    Artist = NeteaseArtistNames(s),
                    Album = NeteaseAlbumName(s),
                    Duration = TimeSpan.FromMilliseconds(NeteaseDurationMs(s)),
                });
            }
            return list;
        }
        catch { return []; }
    }

    internal static string QqSearchBody(string title, string artist, int perPage)
    {
        var query = title + " " + artist;
        return JsonSerializer.Serialize(new
        {
            comm = new { ct = 19, cv = 1845 },
            req = new
            {
                method = "DoSearchForQQMusicDesktop",
                module = "music.search.SearchCgiService",
                param = new { num_per_page = perPage, page_num = 1, query = query, search_type = 0 }
            }
        });
    }

    internal static string QqLyricBody(string mid)
    {
        return JsonSerializer.Serialize(new
        {
            comm = new { ct = 19, cv = 1845 },
            req = new
            {
                method = "GetPlayLyricInfo",
                module = "music.musichallSong.PlayLyricInfo",
                param = new { songMID = mid, songID = 0 }
            }
        });
    }

    private async Task<List<LyricCandidate>> CandidatesQQ(string title, string artist)
    {
        try
        {
            var body = QqSearchBody(title, artist, 20);
            using var sReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            sReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
            sReq.Headers.Referrer = new Uri("https://y.qq.com");
            using var sResp = await _http.SendAsync(sReq);
            if (!sResp.IsSuccessStatusCode) return [];
            using var sDoc = JsonDocument.Parse(await sResp.Content.ReadAsStringAsync());
            var listEl = sDoc.RootElement.GetProperty("req").GetProperty("data")
                .GetProperty("body").GetProperty("song").GetProperty("list");
            var list = new List<LyricCandidate>();
            foreach (var s in listEl.EnumerateArray())
            {
                var mid = s.GetProperty("mid").GetString();
                if (string.IsNullOrEmpty(mid)) continue;
                var singers = "";
                if (s.TryGetProperty("singer", out var sg))
                    singers = string.Join(", ", sg.EnumerateArray().Select(a => a.GetProperty("name").GetString()));
                var album = "";
                if (s.TryGetProperty("album", out var al) && al.TryGetProperty("name", out var an))
                    album = an.GetString() ?? "";
                var dur = TimeSpan.Zero;
                if (s.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var sec) && sec > 0)
                    dur = TimeSpan.FromSeconds(sec);
                list.Add(new LyricCandidate
                {
                    Key = "qq:" + mid,
                    Source = "QQ",
                    Title = s.GetProperty("name").GetString() ?? "",
                    Artist = singers,
                    Album = album,
                    Duration = dur,
                });
            }
            return list;
        }
        catch { return []; }
    }

    private async Task<List<LyricCandidate>> CandidatesKugou(string title, string artist)
    {
        try
        {
            var kw = Uri.EscapeDataString(title + " " + artist);
            using var sResp = await _http.GetAsync(
                "http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword=" + kw + "&page=1&pagesize=20");
            if (!sResp.IsSuccessStatusCode) return [];
            using var sDoc = JsonDocument.Parse(await sResp.Content.ReadAsStringAsync());
            var info = sDoc.RootElement.GetProperty("data").GetProperty("info");
            var list = new List<LyricCandidate>();
            foreach (var s in info.EnumerateArray())
            {
                var hash = s.GetProperty("hash").GetString();
                if (string.IsNullOrEmpty(hash)) continue;
                var dur = TimeSpan.Zero;
                if (s.TryGetProperty("duration", out var d) && d.TryGetInt32(out var sec) && sec > 0)
                    dur = TimeSpan.FromSeconds(sec);
                list.Add(new LyricCandidate
                {
                    Key = "kg:" + hash,
                    Source = "酷狗",
                    Title = s.GetProperty("songname").GetString() ?? "",
                    Artist = s.GetProperty("singername").GetString() ?? "",
                    Album = s.TryGetProperty("album_name", out var al) ? al.GetString() ?? "" : "",
                    Duration = dur,
                });
            }
            return list;
        }
        catch { return []; }
    }

    private async Task<List<LyricCandidate>> CandidatesLrcLib(string title, string artist)
    {
        try
        {
            var url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title);
            if (!string.IsNullOrEmpty(artist))
                url += "&artist_name=" + Uri.EscapeDataString(artist);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("DesktopLyric/0.1");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return [];
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var list = new List<LyricCandidate>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                if (!item.TryGetProperty("syncedLyrics", out var sl) || sl.ValueKind != JsonValueKind.String)
                    continue;
                if (string.IsNullOrEmpty(sl.GetString())) continue;
                var dur = TimeSpan.Zero;
                if (item.TryGetProperty("duration", out var d))
                {
                    if (d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var sec) && sec > 0)
                        dur = TimeSpan.FromSeconds(sec);
                }
                list.Add(new LyricCandidate
                {
                    Key = "lrc:" + idEl.GetRawText().Trim('"'),
                    Source = "LRCLIB",
                    Title = item.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "",
                    Artist = item.TryGetProperty("artistName", out var ar) ? ar.GetString() ?? "" : "",
                    Album = item.TryGetProperty("albumName", out var al) ? al.GetString() ?? "" : "",
                    Duration = dur,
                });
            }
            return list;
        }
        catch { return []; }
    }

    private async Task<List<LrcLine>?> FetchNeteaseLyrics(long songId)
    {
        try
        {
            using var lReq = new HttpRequestMessage(HttpMethod.Get,
                "https://music.163.com/api/song/lyric?id=" + songId + "&lv=1&tv=1&yv=1");
            lReq.Headers.Referrer = new Uri("https://music.163.com");
            using var lResp = await _http.SendAsync(lReq);
            if (!lResp.IsSuccessStatusCode) return null;
            using var lDoc = JsonDocument.Parse(await lResp.Content.ReadAsStringAsync());
            if (!lDoc.RootElement.TryGetProperty("lrc", out var lrc)) return null;
            if (!lrc.TryGetProperty("lyric", out var lyricEl)) return null;
            var lrcStr = lyricEl.GetString();
            if (string.IsNullOrEmpty(lrcStr)) return null;
            var lyrics = ParseLrc(lrcStr);
            if (lyrics.Count == 0) return null;
            if (IsInstrumentalPlaceholder(lyrics)) return null;
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
            if (lDoc.RootElement.TryGetProperty("ytlrc", out var ytl) &&
                ytl.TryGetProperty("lyric", out var ytlEl))
            {
                var ytlStr = ytlEl.GetString();
                if (!string.IsNullOrEmpty(ytlStr))
                    MergeTranslation(lyrics, ParseLrc(ytlStr));
            }
            return lyrics;
        }
        catch { return null; }
    }

    private async Task<List<LrcLine>?> FetchQQLyrics(string mid)
    {
        try
        {
            var lyricBody = QqLyricBody(mid);
            using var lReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            lReq.Content = new StringContent(lyricBody, Encoding.UTF8, "application/json");
            lReq.Headers.Referrer = new Uri("https://y.qq.com");
            using var lResp = await _http.SendAsync(lReq);
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

    private async Task<List<LrcLine>?> FetchKugouLyrics(string hash, string keyword)
    {
        try
        {
            var kw = Uri.EscapeDataString(keyword);
            using var lsResp = await _http.GetAsync(
                "https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=" + kw + "&hash=" + hash);
            if (!lsResp.IsSuccessStatusCode) return null;
            using var lsDoc = JsonDocument.Parse(await lsResp.Content.ReadAsStringAsync());
            var cands = lsDoc.RootElement.GetProperty("candidates");
            if (cands.GetArrayLength() == 0) return null;
            var c0 = cands[0];
            var id = c0.GetProperty("id").GetString();
            var ak = c0.GetProperty("accesskey").GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(ak)) return null;
            using var dlResp = await _http.GetAsync(
                "https://lyrics.kugou.com/download?ver=1&client=pc&id=" + id + "&accesskey=" + ak + "&fmt=lrc&charset=utf8");
            if (!dlResp.IsSuccessStatusCode) return null;
            using var dlDoc = JsonDocument.Parse(await dlResp.Content.ReadAsStringAsync());
            var contentB64 = dlDoc.RootElement.GetProperty("content").GetString();
            if (string.IsNullOrEmpty(contentB64)) return null;
            var lyrics = ParseLrc(Encoding.UTF8.GetString(Convert.FromBase64String(contentB64)));
            return lyrics.Count > 0 ? lyrics : null;
        }
        catch { return null; }
    }

    private async Task<List<LrcLine>?> FetchLrcLibById(string id)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://lrclib.net/api/get/" + Uri.EscapeDataString(id));
            req.Headers.UserAgent.ParseAdd("DesktopLyric/0.1");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("syncedLyrics", out var sl)) return null;
            var lrcStr = sl.GetString();
            if (string.IsNullOrEmpty(lrcStr)) return null;
            var lines = ParseLrc(lrcStr);
            return lines.Count > 0 ? lines : null;
        }
        catch { return null; }
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

    private async Task<List<LrcLine>?> SearchNetease(string title, string artist, TimeSpan? trackDur = null)
    {
        try
        {
            using var doc = await NeteaseCloudSearch(title, artist, 8);
            if (doc == null || !TryNeteaseSongs(doc.RootElement, out var songs)) return null;
            if (songs.GetArrayLength() == 0) return null;

            var songId = PickBest(songs, title, artist, trackDur);
            if (songId < 0) return null;

            using var lReq = new HttpRequestMessage(HttpMethod.Get,
                "https://music.163.com/api/song/lyric?id=" + songId + "&lv=1&tv=1&yv=1");
            lReq.Headers.Referrer = new Uri("https://music.163.com");
            using var lResp = await _http.SendAsync(lReq);
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
            if (lDoc.RootElement.TryGetProperty("ytlrc", out var ytl) &&
                ytl.TryGetProperty("lyric", out var ytlEl))
            {
                var ytlStr = ytlEl.GetString();
                if (!string.IsNullOrEmpty(ytlStr))
                    MergeTranslation(lyrics, ParseLrc(ytlStr));
            }
            return lyrics;
        }
        catch { return null; }
    }

    private async Task<List<LrcLine>?> SearchQQ(string title, string artist)
    {
        try
        {
            var body = QqSearchBody(title, artist, 8);
            using var sReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            sReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
            sReq.Headers.Referrer = new Uri("https://y.qq.com");
            using var sResp = await _http.SendAsync(sReq);
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

            var lyricBody = QqLyricBody(mid!);
            using var lReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            lReq.Content = new StringContent(lyricBody, Encoding.UTF8, "application/json");
            lReq.Headers.Referrer = new Uri("https://y.qq.com");
            using var lResp = await _http.SendAsync(lReq);
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
            using var sResp = await _http.GetAsync(
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

            using var lsResp = await _http.GetAsync(
                "https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=" + kw + "&hash=" + hash);
            if (!lsResp.IsSuccessStatusCode) return null;

            using var lsDoc = JsonDocument.Parse(await lsResp.Content.ReadAsStringAsync());
            var cands = lsDoc.RootElement.GetProperty("candidates");
            if (cands.GetArrayLength() == 0) return null;

            var c0 = cands[0];
            var id = c0.GetProperty("id").GetString();
            var ak = c0.GetProperty("accesskey").GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(ak)) return null;

            using var dlResp = await _http.GetAsync(
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
            using var resp = await _http.SendAsync(req);
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
                using var resp = await _http.GetAsync(
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
                if (yrcLines[yi].durMs > 0)
                    line.Duration = TimeSpan.FromMilliseconds(yrcLines[yi].durMs);
                yi++;
            }
        }
    }

    /// <summary>
    /// NetEase YRC: [lineStartMs,lineDur](absOrRelStart,dur,0)word...
    /// Word timestamps are usually absolute; some dumps are already relative to the line.
    /// </summary>
    internal static List<(int startMs, int durMs, List<KaraokeWordTiming> words)> ParseYrcLines(string yrc)
    {
        var result = new List<(int, int, List<KaraokeWordTiming>)>();
        foreach (var raw in yrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            var hm = YrcLineRegex.Match(line);
            if (!hm.Success) continue;
            if (!int.TryParse(hm.Groups[1].Value, out int lineStart)) continue;
            int.TryParse(hm.Groups[2].Value, out int lineDur);

            var rest = line[hm.Length..];
            var matches = YrcWordRegex.Matches(rest);
            if (matches.Count == 0) continue;

            var rawWords = new List<(int start, int dur, string txt)>(matches.Count);
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                if (!int.TryParse(m.Groups[1].Value, out int start) ||
                    !int.TryParse(m.Groups[2].Value, out int dur)) continue;
                int textStart = m.Index + m.Length;
                if (textStart < 0 || textStart > rest.Length) continue;
                int textEnd = i + 1 < matches.Count ? matches[i + 1].Index : rest.Length;
                if (textEnd < textStart) textEnd = textStart;
                if (textEnd > rest.Length) textEnd = rest.Length;
                var txt = rest[textStart..textEnd];
                if (string.IsNullOrEmpty(txt)) continue;
                if (dur < 0) dur = 0;
                rawWords.Add((start, dur, txt));
            }
            if (rawWords.Count == 0) continue;

            // Absolute: first word start ≈ line start (typical NetEase).
            // Relative: first word start is a small offset (0, 80, …) even when lineStart is large.
            var first = rawWords[0].start;
            var absolute = first + 80 >= lineStart;
            var words = new List<KaraokeWordTiming>(rawWords.Count);
            foreach (var (start, dur, txt) in rawWords)
            {
                int rel = absolute ? start - lineStart : start;
                if (rel < 0) rel = 0;
                if (rel > 60_000) continue;
                words.Add(new KaraokeWordTiming(rel, dur, txt));
            }
            if (words.Count > 0)
                result.Add((lineStart, lineDur, words));
        }
        return result;
    }

    private async Task<JsonDocument?> NeteaseCloudSearch(string title, string artist, int limit)
    {
        var q = (title + " " + artist).Trim();
        if (q.Length == 0) return null;
        var body = "s=" + Uri.EscapeDataString(q)
            + "&type=1&limit=" + limit + "&offset=0&total=true";
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://music.163.com/api/cloudsearch/pc");
        req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        req.Headers.Referrer = new Uri("https://music.163.com");
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            RunLog.Write("ncm-search http=" + (int)resp.StatusCode);
            return null;
        }
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("code", out var codeEl)
            && codeEl.TryGetInt32(out var code) && code != 200)
        {
            RunLog.Write("ncm-search code=" + code);
            doc.Dispose();
            return null;
        }
        var n = TryNeteaseSongs(doc.RootElement, out var songs) ? songs.GetArrayLength() : 0;
        RunLog.Write("ncm-search n=" + n + " q=" + q);
        return doc;
    }

    private static bool TryNeteaseSongs(JsonElement root, out JsonElement songs)
    {
        songs = default;
        if (!root.TryGetProperty("result", out var result)) return false;
        if (!result.TryGetProperty("songs", out songs)) return false;
        return songs.ValueKind == JsonValueKind.Array;
    }

    private static string NeteaseArtistNames(JsonElement song)
    {
        if (TryNeteaseArtists(song, out var arts))
            return string.Join(", ", arts.EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        return "";
    }

    private static string NeteaseAlbumName(JsonElement song)
    {
        foreach (var key in new[] { "al", "album" })
            if (song.TryGetProperty(key, out var al)
                && al.ValueKind == JsonValueKind.Object
                && al.TryGetProperty("name", out var n))
                return n.GetString() ?? "";
        return "";
    }

    private static int NeteaseDurationMs(JsonElement song)
    {
        foreach (var key in new[] { "dt", "duration" })
            if (song.TryGetProperty(key, out var d) && d.TryGetInt32(out var ms) && ms > 0)
                return ms;
        return 0;
    }

    private static bool TryNeteaseArtists(JsonElement song, out JsonElement arts)
    {
        if (song.TryGetProperty("ar", out arts) && arts.ValueKind == JsonValueKind.Array) return true;
        if (song.TryGetProperty("artists", out arts) && arts.ValueKind == JsonValueKind.Array) return true;
        arts = default;
        return false;
    }

    private static long PickBest(JsonElement songs, string title, string artist, TimeSpan? trackDur = null)
    {
        long bestId = -1; int bestScore = int.MinValue;
        var tLow = title.ToLowerInvariant().Trim();
        var aLow = artist.ToLowerInvariant().Trim();
        var tCore = CoreTitle(tLow);
        var wantTv = LyricChoiceStore.LooksLikeTvSize(tLow) || LyricChoiceStore.LooksLikeTvOp(tLow);
        foreach (var song in songs.EnumerateArray())
        {
            var name = (song.GetProperty("name").GetString() ?? "").ToLowerInvariant();
            var nCore = CoreTitle(name);
            int sc = 0;
            if (nCore == tCore || name == tLow) sc += 100;
            else if (nCore.Contains(tCore) || tCore.Contains(nCore)) sc += 50;
            if (wantTv && LyricChoiceStore.LooksLikeTvSize(name)) sc += 50;
            if (!wantTv && LyricChoiceStore.LooksLikeTvSize(name)) sc -= 20;
            if (TryNeteaseArtists(song, out var arts))
                foreach (var a in arts.EnumerateArray())
                {
                    var an = (a.GetProperty("name").GetString() ?? "").ToLowerInvariant();
                    if (an == aLow || aLow.Contains(an) || an.Contains(aLow)) { sc += 30; break; }
                }
            var ms = NeteaseDurationMs(song);
            if (trackDur is { TotalSeconds: >= 20 } td && ms >= 8000)
            {
                var ratio = (ms / 1000.0) / td.TotalSeconds;
                if (ratio is >= 0.85 and <= 1.15) sc += 80;
                else if (ratio is >= 0.7 and <= 1.3) sc += 20;
                else sc -= 50;
            }
            if (sc > bestScore) { bestScore = sc; bestId = song.GetProperty("id").GetInt64(); }
        }
        return bestScore >= 20 ? bestId : -1;
    }

    private static string CoreTitle(string s)
    {
        s = Regex.Replace(s, @"\s*[\(\[（【].*?[\)\]）】]\s*", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"\s*-\s*", "-");
        return s;
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

    private static bool IsLrcJunk(string text)
    {
        if (text.Equals("undefined", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("by:", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("[by:", StringComparison.OrdinalIgnoreCase)) return true;
        var compact = text.Replace(" ", "");
        return compact.StartsWith("作词") || compact.StartsWith("作詞")
            || compact.StartsWith("作曲") || compact.StartsWith("编曲")
            || compact.StartsWith("編曲") || compact.StartsWith("歌词:")
            || compact.StartsWith("歌詞:");
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
                if (IsLrcJunk(text)) continue;
                var time = TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec) + TimeSpan.FromMilliseconds(ms);
                lines.Add(new LrcLine(time, text));
            }
        }
        return lines.OrderBy(l => l.Time).ToList();
    }

    /// <summary>
    /// ConsecutiveMs: still 連唱 — hold this line until the next stamp
    /// (no blank). Extra 停留 widens this window so a stretched live note
    /// is not treated as a chorus gap.
    /// DefaultLineMs: 副歌切換 / instrumental — next stamp is farther,
    /// so drop the previous section after this hold.
    /// Empty LRC stamps in between are not a cut.
    /// </summary>
    public const int ConsecutiveMs = 15_000;
    public const int DefaultLineMs = 14_500;
    public const int HoldAfterMs = 400;
    public const int TransPairMs = 4_000;

    public static int NextSungIndex(IReadOnlyList<LrcLine> lines, int afterIdx)
    {
        var cur = afterIdx >= 0 && afterIdx < lines.Count ? lines[afterIdx] : null;
        for (int i = afterIdx + 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i].Text)) continue;
            if (cur != null && IsAttachedTranslation(cur, lines[i])) continue;
            return i;
        }
        return -1;
    }

    public static string LineKey(LrcLine line)
        => string.IsNullOrEmpty(line.SourceKey)
            ? $"{(long)Math.Round(line.Time.TotalMilliseconds)}|{line.Text}"
            : line.SourceKey;

    public static bool IsAddedKey(string key)
        => key.StartsWith("add|", StringComparison.Ordinal);

    public static string AddedId(string key)
        => IsAddedKey(key) ? key[4..] : key;

    public static string AddedKey(string id) => "add|" + id;

    public static int PrevSungIndex(IReadOnlyList<LrcLine> lines, int beforeIdx)
    {
        var cur = beforeIdx >= 0 && beforeIdx < lines.Count ? lines[beforeIdx] : null;
        for (int i = beforeIdx - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i].Text)) continue;
            if (cur != null && (IsAttachedTranslation(lines[i], cur) || IsAttachedTranslation(cur, lines[i])))
                continue;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// Delayed Chinese stamp for a Japanese line — not a new sung phrase.
    /// Cutting the last chorus line at that stamp made it vanish too early.
    /// </summary>
    public static bool IsAttachedTranslation(LrcLine phrase, LrcLine other)
    {
        if (string.IsNullOrWhiteSpace(other.Text)) return false;
        if (!IsJapaneseLine(phrase.Text) || !IsChineseOnly(other.Text)) return false;
        var dt = (other.Time - phrase.Time).TotalMilliseconds;
        return dt >= -200 && dt <= TransPairMs;
    }

    public static bool IsAttachedTranslationLine(IReadOnlyList<LrcLine> lines, int idx)
    {
        if (idx < 0 || idx >= lines.Count) return false;
        if (!IsChineseOnly(lines[idx].Text)) return false;
        for (int i = idx - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i].Text)) continue;
            return IsAttachedTranslation(lines[i], lines[idx]);
        }
        return false;
    }

    public static TimeSpan TimeOf(LrcLine line, IReadOnlyDictionary<string, int>? shifts)
    {
        if (shifts is { Count: > 0 } && shifts.TryGetValue(LineKey(line), out var ms) && ms != 0)
            return line.Time + TimeSpan.FromMilliseconds(ms);
        return line.Time;
    }

    public static string DisplayText(LrcLine line, IReadOnlyDictionary<string, string>? texts)
    {
        if (texts is { Count: > 0 } && texts.TryGetValue(LineKey(line), out var t))
            return t ?? "";
        return line.Text;
    }

    /// <summary>
    /// Translation shown under a line: own TranslatedText, else a nearby
    /// Chinese-only stamp (same pairing overlay uses after SplitMixedLyrics).
    /// </summary>
    public static string? ResolvedTranslation(IReadOnlyList<LrcLine> lines, LrcLine line)
    {
        if (!string.IsNullOrWhiteSpace(line.TranslatedText))
            return line.TranslatedText;
        if (!IsJapaneseLine(line.Text)) return null;
        foreach (var other in lines)
        {
            if (ReferenceEquals(other, line)) continue;
            if (string.IsNullOrWhiteSpace(other.Text) || !IsChineseOnly(other.Text)) continue;
            if (IsAttachedTranslation(line, other))
                return other.Text;
        }
        return null;
    }

    /// <summary>
    /// Apply per-track text overrides, hidden lines, and inserted live lines.
    /// Original LineKey is kept on SourceKey so timing edits still match.
    /// </summary>
    public static List<LrcLine> ApplyEdits(IReadOnlyList<LrcLine> src, TrackTiming timing)
    {
        var result = new List<LrcLine>(src.Count + (timing.Added?.Count ?? 0));
        var texts = timing.Texts;
        var trans = timing.Trans;
        foreach (var line in src)
        {
            var key = LineKey(line);
            string? textOv = null;
            var hasText = texts is { Count: > 0 } && texts.TryGetValue(key, out textOv);
            string? transOv = null;
            var hasTrans = trans is { Count: > 0 } && trans.TryGetValue(key, out transOv);
            if (hasText && string.IsNullOrWhiteSpace(textOv)) continue;
            if (hasText || hasTrans)
            {
                var copy = new LrcLine(line.Time, hasText ? textOv! : line.Text)
                {
                    TranslatedText = hasTrans
                        ? (string.IsNullOrWhiteSpace(transOv) ? null : transOv)
                        : line.TranslatedText,
                    Duration = line.Duration,
                    SourceKey = key,
                    WordTimings = hasText ? null : line.WordTimings,
                };
                result.Add(copy);
            }
            else
                result.Add(line);
        }
        if (timing.Added is { Count: > 0 } added)
        {
            foreach (var a in added)
            {
                if (string.IsNullOrWhiteSpace(a.Text)) continue;
                var at = Math.Clamp(a.AtMs, 0, LyricOffsetStore.MaxMs);
                var id = string.IsNullOrEmpty(a.Id) ? $"t{at}" : a.Id;
                result.Add(new LrcLine(TimeSpan.FromMilliseconds(at), a.Text)
                {
                    SourceKey = AddedKey(id),
                    TranslatedText = string.IsNullOrWhiteSpace(a.Trans) ? null : a.Trans,
                });
            }
        }
        return result.Count > 1 ? result.OrderBy(l => l.Time).ToList() : result;
    }

    public static int EffectiveMs(LrcLine line, IReadOnlyDictionary<string, int>? shifts)
        => (int)Math.Round(TimeOf(line, shifts).TotalMilliseconds);

    /// <summary>
    /// Time to put a line between prev and next (new neighbors after a move).
    /// </summary>
    public static int PlacementMs(TimeSpan? prev, TimeSpan? next, int fallbackMs)
    {
        if (prev is { } p && next is { } n)
        {
            var gap = (n - p).TotalMilliseconds;
            if (gap > 80)
                return (int)Math.Round(p.TotalMilliseconds + gap / 2.0);
            return (int)Math.Round(p.TotalMilliseconds);
        }
        if (next is { } n2)
            return Math.Max(0, (int)Math.Round(n2.TotalMilliseconds) - 500);
        if (prev is { } p2)
            return (int)Math.Round(p2.TotalMilliseconds) + 1_000;
        return Math.Max(0, fallbackMs);
    }

    public static TrackTiming SetEffectiveTime(TrackTiming t, LrcLine line, int atMs)
    {
        atMs = Math.Clamp(atMs, 0, LyricOffsetStore.MaxMs);
        var key = LineKey(line);
        if (IsAddedKey(key))
        {
            var id = AddedId(key);
            var cur = t.Added?.FirstOrDefault(a => a.Id == id)
                ?? new AddedLyric(atMs, line.Text, id, line.TranslatedText);
            return t.ReplaceAdded(id, cur with { AtMs = atMs }).WithLineShift(key, 0);
        }
        var baseMs = (int)Math.Round(line.Time.TotalMilliseconds);
        return t.WithLineShift(key, atMs - baseMs);
    }

    public static TrackTiming DuplicateLine(TrackTiming t, LrcLine line, int atMs)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        t = t.WithAdded(new AddedLyric(atMs, line.Text, id, line.TranslatedText));
        var hold = 0;
        t.Holds?.TryGetValue(LineKey(line), out hold);
        if (hold != 0)
            t = t.WithLineHold(AddedKey(id), hold);
        return t;
    }

    public static string FormatStamp(TimeSpan t)
        => $"[{(int)t.TotalMinutes}:{t.Seconds:D2}.{t.Milliseconds / 10:D2}]";

    /// <summary>
    /// Overlay layout: original, then translation on the next line at the same stamp.
    /// </summary>
    public static string FormatShownLrc(IReadOnlyList<LrcLine> lines, IReadOnlyDictionary<string, int>? shifts)
        => FormatShownLrc(lines, new TrackTiming(0, 1.0, shifts is { Count: > 0 } ? new Dictionary<string, int>(shifts) : null), headers: false);

    public static string FormatShownLrc(IReadOnlyList<LrcLine> lines, TrackTiming timing, bool headers = true)
    {
        var sb = new System.Text.StringBuilder();
        if (headers)
        {
            sb.Append("[offset:");
            sb.Append(timing.OffsetMs);
            sb.AppendLine("]");
            sb.Append("[dl_rate:");
            sb.Append(timing.Rate.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("]");
        }
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            var stamp = FormatStamp(TimeOf(line, timing.Lines));
            sb.Append(stamp);
            sb.AppendLine(line.Text);
            if (!string.IsNullOrWhiteSpace(line.TranslatedText))
            {
                sb.Append(stamp);
                sb.AppendLine(line.TranslatedText);
            }
            var hold = 0;
            timing.Holds?.TryGetValue(LineKey(line), out hold);
            if (hold != 0)
            {
                sb.Append("[dl_hold:");
                sb.Append(hold);
                sb.AppendLine("]");
            }
        }
        return sb.ToString();
    }

    public static (int? OffsetMs, double? Rate) ParseTimingTags(string raw)
    {
        int? offset = null;
        double? rate = null;
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 8 || line[0] != '[') continue;
            var close = line.IndexOf(']');
            if (close < 2) continue;
            var inner = line[1..close];
            var colon = inner.IndexOf(':');
            if (colon <= 0) continue;
            var key = inner[..colon].Trim();
            var val = inner[(colon + 1)..].Trim();
            if (key.Equals("offset", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(val, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var ms))
                offset = Math.Clamp(ms, LyricOffsetStore.MinMs, LyricOffsetStore.MaxMs);
            else if ((key.Equals("dl_rate", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("rate", StringComparison.OrdinalIgnoreCase))
                && double.TryParse(val, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var r)
                && r > 0)
                rate = Math.Clamp(r, LyricOffsetStore.RateMin, LyricOffsetStore.RateMax);
        }
        return (offset, rate);
    }

    public readonly record struct ClipLyric(int AtMs, string Text, string? Trans = null, int HoldMs = 0);

    public static List<ClipLyric> ParseClipboardLyrics(string raw, int fallbackStartMs)
    {
        var result = new List<ClipLyric>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        var lrc = ParseLrc(raw);
        if (lrc.Count > 0 && raw.Contains('[', StringComparison.Ordinal))
        {
            SplitMixedLyrics(lrc);
            foreach (var line in lrc)
            {
                if (string.IsNullOrWhiteSpace(line.Text)) continue;
                result.Add(new ClipLyric(EffectiveMs(line, null), line.Text, line.TranslatedText));
            }
            if (result.Count > 0)
            {
                AttachHolds(raw, result);
                return result;
            }
        }

        var parts = new List<string>();
        foreach (var rawLine in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var text = rawLine.Trim();
            if (text.Length > 0) parts.Add(text);
        }
        var at = Math.Max(0, fallbackStartMs);
        for (int i = 0; i < parts.Count;)
        {
            var a = parts[i];
            if (i + 1 < parts.Count && !IsChineseOnly(a) && IsChineseOnly(parts[i + 1]))
            {
                result.Add(new ClipLyric(at, a, parts[i + 1]));
                i += 2;
            }
            else
            {
                result.Add(new ClipLyric(at, a));
                i++;
            }
            at += 1_000;
        }
        return result;
    }

    private static void AttachHolds(string raw, List<ClipLyric> clips)
    {
        if (clips.Count == 0) return;
        var used = new bool[clips.Count];
        var current = -1;
        TimeSpan? lastTime = null;
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[dl_hold:", StringComparison.OrdinalIgnoreCase))
            {
                var close = line.IndexOf(']');
                var val = close > 9 ? line[9..close].Trim() : "";
                if (current >= 0 && int.TryParse(val, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var ms))
                {
                    ms = Math.Clamp(ms, LyricOffsetStore.HoldMinMs, LyricOffsetStore.HoldMaxMs);
                    clips[current] = clips[current] with { HoldMs = ms };
                }
                continue;
            }
            var m = LrcRegex.Match(line);
            if (!m.Success) continue;
            var text = m.Groups[4].Value.Trim();
            if (string.IsNullOrWhiteSpace(text) || IsLrcJunk(text)) continue;
            var min = int.Parse(m.Groups[1].Value);
            var sec = int.Parse(m.Groups[2].Value);
            var msRaw = m.Groups[3].Value;
            var stampMs = msRaw.Length == 2 ? int.Parse(msRaw) * 10 : int.Parse(msRaw);
            var time = TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec) + TimeSpan.FromMilliseconds(stampMs);
            if (lastTime == time) continue;
            lastTime = time;
            var at = (int)Math.Round(time.TotalMilliseconds);
            current = -1;
            for (int i = 0; i < clips.Count; i++)
            {
                if (used[i] || clips[i].AtMs != at) continue;
                used[i] = true;
                current = i;
                break;
            }
        }
    }

    /// <summary>Original downloaded lyrics. Keeps whole-song offset/rate.</summary>
    public static TrackTiming RestoreLyrics(TrackTiming t) => new(t.OffsetMs, t.Rate);

    /// <summary>Hide source lines and drop inserts. Overlay goes blank. 還原 brings them back.</summary>
    public static TrackTiming ClearShown(TrackTiming t, IReadOnlyList<LrcLine> source)
    {
        Dictionary<string, string>? hide = null;
        foreach (var line in source)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            hide ??= new();
            hide[LineKey(line)] = "";
        }
        return new(t.OffsetMs, t.Rate, null, null, hide, null, null);
    }

    /// <summary>Replace the displayed set with imported clips. Does not stack on top.</summary>
    public static TrackTiming ReplaceShown(
        TrackTiming t, IReadOnlyList<LrcLine> source, IReadOnlyList<ClipLyric> clips,
        int? offsetMs = null, double? rate = null)
    {
        var cleared = ClearShown(t, source);
        var off = offsetMs ?? cleared.OffsetMs;
        var r = rate ?? cleared.Rate;
        if (clips.Count == 0)
            return new(off, r, null, null, cleared.Texts, null, null);
        var added = new List<AddedLyric>(clips.Count);
        Dictionary<string, int>? holds = null;
        foreach (var clip in clips)
        {
            if (string.IsNullOrWhiteSpace(clip.Text)) continue;
            var id = Guid.NewGuid().ToString("N")[..8];
            added.Add(new AddedLyric(clip.AtMs, clip.Text, id, clip.Trans));
            if (clip.HoldMs != 0)
            {
                holds ??= new();
                holds[AddedKey(id)] = Math.Clamp(clip.HoldMs,
                    LyricOffsetStore.HoldMinMs, LyricOffsetStore.HoldMaxMs);
            }
        }
        return new(off, r, null, holds, cleared.Texts,
            added.Count == 0 ? null : added, null);
    }

    public static bool LineIsActive(IReadOnlyList<LrcLine> lines, int idx, TimeSpan pos,
        IReadOnlyDictionary<string, int>? shifts = null,
        IReadOnlyDictionary<string, int>? holds = null)
    {
        if (idx < 0 || idx >= lines.Count) return false;
        var line = lines[idx];
        if (string.IsNullOrWhiteSpace(line.Text)) return false;
        if (pos < TimeOf(line, shifts)) return false;
        if (IsAttachedTranslationLine(lines, idx)) return false;

        var prev = PrevSungIndex(lines, idx);
        if (prev >= 0)
        {
            var start = TimeOf(line, shifts);
            var prevEnd = LineDisplayEnd(lines, prev, shifts, holds);
            if (prevEnd > start && pos < prevEnd)
                return false;
        }

        return pos < LineDisplayEnd(lines, idx, shifts, holds);
    }

    public static TimeSpan LineDisplayEnd(IReadOnlyList<LrcLine> lines, int idx,
        IReadOnlyDictionary<string, int>? shifts = null,
        IReadOnlyDictionary<string, int>? holds = null)
    {
        var line = lines[idx];
        var start = TimeOf(line, shifts);
        var nextSung = NextSungIndex(lines, idx);
        var next = nextSung >= 0 ? TimeOf(lines[nextSung], shifts) : TimeSpan.MaxValue;
        var extra = 0;
        holds?.TryGetValue(LineKey(line), out extra);
        var linkMs = ConsecutiveMs + Math.Max(0, extra);

        TimeSpan end;
        if (next < TimeSpan.MaxValue
            && (next - start).TotalMilliseconds <= linkMs)
            end = next;
        else
        {
            var hold = start + TimeSpan.FromMilliseconds(DefaultLineMs);
            if (line.Duration is { Ticks: > 0 } d)
            {
                var yrcEnd = start + d + TimeSpan.FromMilliseconds(HoldAfterMs);
                if (yrcEnd > hold) hold = yrcEnd;
            }
            else if (line.WordTimings is { Count: > 0 } w)
            {
                var last = w[^1];
                var sung = start + TimeSpan.FromMilliseconds(Math.Max(0, last.StartMs + last.DurationMs))
                    + TimeSpan.FromMilliseconds(HoldAfterMs);
                if (sung > hold) hold = sung;
            }
            end = next < hold ? next : hold;
        }

        if (extra != 0)
        {
            end += TimeSpan.FromMilliseconds(extra);
            var minEnd = start + TimeSpan.FromMilliseconds(80);
            if (end < minEnd) end = minEnd;
        }
        return end;
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
    public TimeSpan? Duration { get; set; }
    /// <summary>Original LineKey after a live text override or insert.</summary>
    public string? SourceKey { get; set; }
}
