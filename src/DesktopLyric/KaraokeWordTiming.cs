using System.Collections.Generic;

namespace DesktopLyric;

/// <summary>
/// word timing within a line — start/duration relative to line start
/// netease calls this YRC, each word gets (startMs, durationMs, 0)text
/// </summary>
public readonly record struct KaraokeWordTiming(int StartMs, int DurationMs, string Text)
{
    public const int MaxOverlayWords = 80;

    /// <summary>
    /// No further overlay frames: no word timings, or every word already sung.
    /// 14.5s line holds used to keep invalidating LineElapsedMs ~5Hz after the
    /// visual was frozen, which is the layered-window crash amplifier.
    /// </summary>
    public static bool OverlayFrozen(IList<KaraokeWordTiming>? words, double elapsedMs)
    {
        if (words == null || words.Count == 0) return true;
        var n = Math.Min(words.Count, MaxOverlayWords);
        var last = words[n - 1];
        return elapsedMs >= last.StartMs + Math.Max(0, last.DurationMs);
    }
}
