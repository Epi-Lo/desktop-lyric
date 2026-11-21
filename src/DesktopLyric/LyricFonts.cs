using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DesktopLyric;

/// <summary>
/// Transparent WPF disables ClearType. Hinted UI fonts (JhengHei) then look
/// like old Windows. Lyrics are drawn as vectors; Chinese default is Chiron
/// GoRound TC. Japanese kana uses Yu Gothic UI.
/// </summary>
public static class LyricFonts
{
    public const string JapaneseStack =
        "Yu Gothic UI, Yu Gothic Medium, Yu Gothic, Meiryo UI, Segoe UI";

    public static readonly (string Family, string Label)[] ChineseChoices =
    [
        ("Chiron GoRound TC", "昭源圓體"),
        ("Chiron GoRound TC Medium", "昭源圓體 M"),
        ("Noto Sans HK Medium", "Noto HK"),
        ("Noto Sans TC Medium", "Noto TC"),
        ("Noto Sans HK", "Noto HK 常規"),
        ("Microsoft JhengHei UI", "正黑體"),
        ("Microsoft YaHei UI", "雅黑"),
    ];

    public static FontFamily Japanese { get; } = new(JapaneseStack);

    public static FontFamily ChineseDefault =>
        FontLoader.BundledChiron ?? new FontFamily("Chiron GoRound TC");

    public static FontFamily FromSettings(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Equals("Chiron GoRound TC", StringComparison.OrdinalIgnoreCase))
            return ChineseDefault;
        if (name.Equals("Chiron GoRound TC Medium", StringComparison.OrdinalIgnoreCase) &&
            FontLoader.FontsUri != null)
            return new FontFamily(FontLoader.FontsUri, "./#Chiron GoRound TC Medium");
        return new FontFamily(name);
    }

    public static CultureInfo CultureFor(string? text, string? lineHint = null)
        => IsJapaneseLine(text, lineHint)
            ? CultureInfo.GetCultureInfo("ja-JP")
            : CultureInfo.GetCultureInfo("zh-HK");

    public static Typeface TypefaceFor(string? text, string? settingsFont, string? lineHint = null)
    {
        // Kanji-only karaoke pieces have no kana, but belong on a Japanese line.
        // Using Chiron/Noto for those glyphs squashes neighbouring Yu Gothic kana.
        try
        {
            var family = IsJapaneseLine(text, lineHint) ? Japanese : FromSettings(settingsFont);
            // Weight lives in the family name for Noto ("... Medium"). Faux-bold looks XP-era.
            return new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        }
        catch
        {
            return new Typeface("Segoe UI");
        }
    }

    public static bool IsJapaneseLine(string? text, string? lineHint = null)
        => HasKana(text) || HasKana(lineHint);

    public static string CurrentLabel(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return ChineseChoices[0].Label;
        foreach (var (fam, label) in ChineseChoices)
            if (fam.Equals(family, StringComparison.OrdinalIgnoreCase)) return label;
        return family;
    }

    public static string CycleChinese(string? current)
    {
        int i = 0;
        if (!string.IsNullOrWhiteSpace(current))
        {
            for (int n = 0; n < ChineseChoices.Length; n++)
                if (ChineseChoices[n].Family.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    i = n;
                    break;
                }
        }
        return ChineseChoices[(i + 1) % ChineseChoices.Length].Family;
    }

    public static double LineSize(string? text, bool current, double delta = 0)
    {
        var jp = HasKana(text);
        var n = current ? (jp ? 24.0 : 28.0) : (jp ? 16.0 : 22.0);
        return Math.Clamp(n + delta, 10, 48);
    }

    public const double ScaleStep = 0.08;
    public const double ScaleMin = 0.55;
    public const double ScaleMax = 1.85;

    public readonly record struct OverlaySizes(
        double CurrentFont, double TransFont, double NextFont,
        double TransMaxHeight, double NextMaxHeight);

    /// <summary>
    /// Original vs translation sizes. Scale is applied to a comfortable base
    /// so 原± / 譯± change the type before the shared height budget shrinks
    /// both. Overflow is split proportionally so translation cannot crush the
    /// current line (Auto row still gets TransMaxHeight).
    /// Default: Japanese original a bit smaller, Chinese translation a bit larger.
    /// </summary>
    public static OverlaySizes FitOverlaySizes(
        double area,
        bool hasTrans,
        bool hasNext,
        bool originalIsJapanese,
        double originalScale,
        double translationScale,
        double fontCap = 52)
    {
        if (double.IsNaN(area) || double.IsInfinity(area) || area < 24) area = 24;
        if (double.IsNaN(fontCap) || fontCap < 24) fontCap = 24;
        if (double.IsNaN(originalScale) || double.IsInfinity(originalScale)) originalScale = 1;
        if (double.IsNaN(translationScale) || double.IsInfinity(translationScale)) translationScale = 1.15;
        originalScale = Math.Clamp(originalScale, ScaleMin, ScaleMax);
        translationScale = Math.Clamp(translationScale, ScaleMin, ScaleMax);

        const double lineFactor = 1.28;
        var nextFont = hasNext ? Math.Clamp(area * 0.10, 10, Math.Min(28, fontCap * 0.35)) : 0;
        var nextH = hasNext ? nextFont * lineFactor : 0;
        var budget = Math.Max(area - nextH, 24);

        var baseCur = area * (originalIsJapanese ? 0.22 : 0.26);
        var baseTrans = area * (originalIsJapanese ? 0.24 : 0.20);
        var wantCur = Math.Clamp(baseCur * originalScale, 12, fontCap);
        var wantTrans = hasTrans ? Math.Clamp(baseTrans * translationScale, 11, fontCap) : 0;

        var curH = wantCur * lineFactor;
        var transH = wantTrans * lineFactor;
        var used = curH + transH;
        if (used > budget && used > 0.001)
        {
            var shrink = budget / used;
            curH *= shrink;
            transH *= shrink;
            wantCur = curH / lineFactor;
            wantTrans = hasTrans ? transH / lineFactor : 0;
        }

        return new OverlaySizes(
            Math.Clamp(wantCur, 12, fontCap),
            hasTrans ? Math.Clamp(wantTrans, 10, fontCap) : 0,
            nextFont,
            hasTrans ? transH : 0,
            nextH);
    }

    public static bool HasKana(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (c is (>= '\u3040' and <= '\u309F')
                or (>= '\u30A0' and <= '\u30FF')
                or (>= '\u31F0' and <= '\u31FF')
                or (>= '\uFF66' and <= '\uFF9D'))
                return true;
        }
        return false;
    }
}
