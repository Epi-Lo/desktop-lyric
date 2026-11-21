using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopLyric.Services;

/// <summary>
/// Per-track lyric timing offset in milliseconds, saved to offsets.json.
/// Positive = show lyrics earlier; negative = later.
/// </summary>
public static class LyricOffsetStore
{
    private static string _storePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopLyric", "offsets.json");

    public const double RateStep = 0.005;
    public const double RateMin = 0.85;
    public const double RateMax = 1.20;

    private static Dictionary<string, TrackTiming>? _cache;
    private static readonly object _lock = new();

    public const int StepMs = 50;
    public const int MediumStepMs = 250;
    public const int FastStepMs = 1_000;
    public const int MinMs = -300_000;
    public const int MaxMs = 300_000;
    public const int HoldDelayMs = 400;
    public const int HoldAccelMs = 1_200;
    public const int HoldFastMs = 2_500;
    public const int HoldStepMs = 250;
    public const int HoldMinMs = -30_000;
    public const int HoldMaxMs = 180_000;

    /// <summary>
    /// Extra step while a ＋/− button is held. 0 until HoldDelayMs so the
    /// initial MouseDown 50ms step is not immediately doubled.
    /// </summary>
    public static int StepForHoldMs(double heldMs)
    {
        if (heldMs < HoldDelayMs) return 0;
        if (heldMs < HoldAccelMs) return StepMs;
        if (heldMs < HoldFastMs) return MediumStepMs;
        return FastStepMs;
    }

    internal static void ResetForTests(string storePath)
    {
        lock (_lock)
        {
            _storePath = storePath;
            _cache = null;
        }
    }

    public static int GetMs(string? title, string? artist) => GetTiming(title, artist).OffsetMs;

    public static TrackTiming GetTiming(string? title, string? artist)
    {
        var dict = LoadDict();
        if (dict.Count == 0) return TrackTiming.Default;

        foreach (var key in LyricChoiceStore.FingerprintKeys(title, artist))
        {
            if (dict.TryGetValue(key, out var v)) return v;
        }

        var wantTitles = LyricChoiceStore.TitleKeys(title);
        if (wantTitles.Count == 0) return TrackTiming.Default;
        TrackTiming? unique = null;
        var uniqueCount = 0;
        foreach (var kv in dict)
        {
            var i = kv.Key.IndexOf('|');
            var storedTitle = i < 0 ? kv.Key : kv.Key[(i + 1)..];
            var storedTitles = LyricChoiceStore.TitleKeys(storedTitle);
            if (!wantTitles.Any(w => storedTitles.Contains(w))) continue;
            uniqueCount++;
            unique = kv.Value;
        }
        return uniqueCount == 1 ? unique!.Value : TrackTiming.Default;
    }

    public static void SetMs(string? title, string? artist, int ms)
        => SetTiming(title, artist, GetTiming(title, artist) with { OffsetMs = ms });

    public static void SetTiming(string? title, string? artist, TrackTiming timing)
    {
        timing = timing.Clamped();
        var keys = LyricChoiceStore.FingerprintKeys(title, artist);
        if (keys.Count == 0) return;
        lock (_lock)
        {
            var dict = LoadDict();
            var titles = LyricChoiceStore.TitleKeys(title);
            foreach (var existing in dict.Keys.ToList())
            {
                if (keys.Contains(existing)) continue;
                var i = existing.IndexOf('|');
                var storedTitle = i < 0 ? existing : existing[(i + 1)..];
                if (titles.Any(t => LyricChoiceStore.TitleKeys(storedTitle).Contains(t)))
                    dict.Remove(existing);
            }
            foreach (var key in keys)
            {
                if (timing.IsIdentity) dict.Remove(key);
                else dict[key] = timing;
            }
            _cache = dict;
        }
        SaveDict();
    }

    public static int Nudge(string? title, string? artist, int deltaMs)
    {
        var cur = GetTiming(title, artist);
        var next = cur with { OffsetMs = Math.Clamp(cur.OffsetMs + deltaMs, MinMs, MaxMs) };
        SetTiming(title, artist, next);
        return next.OffsetMs;
    }

    public static string Format(int ms)
    {
        var sign = ms > 0 ? "+" : ms < 0 ? "−" : "±";
        return $"{sign}{Math.Abs(ms) / 1000.0:0.00}s";
    }

    public static string FormatRate(double rate)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate)) rate = 1;
        var pct = (rate - 1.0) * 100.0;
        if (Math.Abs(pct) < 0.05) return "1.000×";
        var sign = pct > 0 ? "快" : "慢";
        return $"{rate:0.000}×（{sign} {Math.Abs(pct):0.0}%）";
    }

    public static string FormatLabel(int ms, double rate)
    {
        var off = Format(ms);
        if (Math.Abs(rate - 1.0) < 0.0005) return off;
        return $"{off}  {rate:0.000}×";
    }

    public static string FormatHold(int ms)
    {
        if (ms == 0) return "0.00s";
        return Format(ms);
    }

    private static Dictionary<string, TrackTiming> LoadDict()
    {
        lock (_lock)
        {
            if (_cache != null) return _cache;
            _cache = ReadFile(_storePath);
            return _cache;
        }
    }

    private static Dictionary<string, TrackTiming> ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var json = File.ReadAllText(path);
            if (json.Contains("\"OffsetMs\"", StringComparison.OrdinalIgnoreCase)
                || json.Contains("\"Rate\"", StringComparison.OrdinalIgnoreCase))
            {
                return JsonSerializer.Deserialize<Dictionary<string, TrackTiming>>(json) ?? new();
            }
            var ints = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (ints == null) return new();
            var converted = new Dictionary<string, TrackTiming>(ints.Count);
            foreach (var kv in ints)
                converted[kv.Key] = new TrackTiming(kv.Value, 1.0);
            return converted;
        }
        catch
        {
            return new();
        }
    }

    private static void SaveDict()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            Dictionary<string, TrackTiming> snap;
            lock (_lock) { snap = new(_cache ?? new()); }
            File.WriteAllText(_storePath, JsonSerializer.Serialize(snap, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));
        }
        catch { }
    }
}

public readonly record struct AddedLyric(int AtMs, string Text, string Id, string? Trans = null);

public readonly record struct TrackTiming(
    int OffsetMs,
    double Rate,
    Dictionary<string, int>? Lines = null,
    Dictionary<string, int>? Holds = null,
    Dictionary<string, string>? Texts = null,
    List<AddedLyric>? Added = null,
    Dictionary<string, string>? Trans = null)
{
    public static TrackTiming Default => new(0, 1.0);

    public bool IsIdentity =>
        OffsetMs == 0
        && Math.Abs(Rate - 1.0) < 0.0005
        && (Lines == null || Lines.Count == 0)
        && (Holds == null || Holds.Count == 0)
        && (Texts == null || Texts.Count == 0)
        && (Added == null || Added.Count == 0)
        && (Trans == null || Trans.Count == 0);

    public TrackTiming Clamped() => new(
        Math.Clamp(OffsetMs, LyricOffsetStore.MinMs, LyricOffsetStore.MaxMs),
        Math.Clamp(
            double.IsNaN(Rate) || double.IsInfinity(Rate) ? 1.0 : Rate,
            LyricOffsetStore.RateMin,
            LyricOffsetStore.RateMax),
        CopyInts(Lines, LyricOffsetStore.MinMs, LyricOffsetStore.MaxMs),
        CopyInts(Holds, LyricOffsetStore.HoldMinMs, LyricOffsetStore.HoldMaxMs),
        CopyStrs(Texts),
        Added is { Count: > 0 } ? [.. Added] : null,
        CopyStrs(Trans));

    public TrackTiming WithLineShift(string key, int ms)
    {
        var d = Lines is { Count: > 0 } ? new Dictionary<string, int>(Lines) : new();
        ms = Math.Clamp(ms, LyricOffsetStore.MinMs, LyricOffsetStore.MaxMs);
        if (ms == 0) d.Remove(key);
        else d[key] = ms;
        return new(OffsetMs, Rate, EmptyToNull(d), Holds, Texts, Added, Trans);
    }

    public TrackTiming WithLineHold(string key, int ms)
    {
        var d = Holds is { Count: > 0 } ? new Dictionary<string, int>(Holds) : new();
        ms = Math.Clamp(ms, LyricOffsetStore.HoldMinMs, LyricOffsetStore.HoldMaxMs);
        if (ms == 0) d.Remove(key);
        else d[key] = ms;
        return new(OffsetMs, Rate, Lines, EmptyToNull(d), Texts, Added, Trans);
    }

    /// <summary>
    /// Override displayed text. Empty string hides the line. null removes the override.
    /// </summary>
    public TrackTiming WithLineText(string key, string? text)
    {
        var d = Texts is { Count: > 0 } ? new Dictionary<string, string>(Texts) : new();
        if (text == null) d.Remove(key);
        else d[key] = text;
        return new(OffsetMs, Rate, Lines, Holds, d.Count == 0 ? null : d, Added, Trans);
    }

    /// <summary>
    /// Override Chinese/translation line. Empty string hides it. null removes the override.
    /// </summary>
    public TrackTiming WithLineTrans(string key, string? text)
    {
        var d = Trans is { Count: > 0 } ? new Dictionary<string, string>(Trans) : new();
        if (text == null) d.Remove(key);
        else d[key] = text;
        return new(OffsetMs, Rate, Lines, Holds, Texts, Added, d.Count == 0 ? null : d);
    }

    public TrackTiming WithAdded(AddedLyric line)
    {
        var list = Added is { Count: > 0 } ? new List<AddedLyric>(Added) : [];
        list.Add(line);
        return new(OffsetMs, Rate, Lines, Holds, Texts, list, Trans);
    }

    public TrackTiming ReplaceAdded(string id, AddedLyric line)
    {
        if (Added == null || Added.Count == 0) return WithAdded(line);
        var list = new List<AddedLyric>(Added.Count);
        var found = false;
        foreach (var a in Added)
        {
            if (a.Id == id)
            {
                list.Add(line);
                found = true;
            }
            else list.Add(a);
        }
        if (!found) list.Add(line);
        return new(OffsetMs, Rate, Lines, Holds, Texts, list, Trans);
    }

    public TrackTiming WithoutAdded(string id)
    {
        if (Added == null || Added.Count == 0) return this;
        var list = Added.Where(a => a.Id != id).ToList();
        return new(OffsetMs, Rate, Lines, Holds, Texts, list.Count == 0 ? null : list, Trans);
    }

    public TrackTiming WithoutLine(string key)
    {
        var next = WithLineShift(key, 0).WithLineHold(key, 0).WithLineText(key, null).WithLineTrans(key, null);
        const string prefix = "add|";
        return key.StartsWith(prefix, StringComparison.Ordinal)
            ? next.WithoutAdded(key[prefix.Length..])
            : next;
    }

    private static Dictionary<string, int>? CopyInts(Dictionary<string, int>? src, int min, int max)
    {
        if (src == null || src.Count == 0) return null;
        var d = new Dictionary<string, int>(src.Count);
        foreach (var kv in src)
        {
            var v = Math.Clamp(kv.Value, min, max);
            if (v != 0) d[kv.Key] = v;
        }
        return d.Count == 0 ? null : d;
    }

    private static Dictionary<string, int>? EmptyToNull(Dictionary<string, int> d)
        => d.Count == 0 ? null : d;

    private static Dictionary<string, string>? CopyStrs(Dictionary<string, string>? src)
        => src is { Count: > 0 } ? new Dictionary<string, string>(src) : null;
}
