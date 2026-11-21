using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DesktopLyric.Views;

/// <summary>
/// Draws lyrics as vector geometry instead of hinted TextBlock glyphs.
/// Transparent WPF disables ClearType; GDI-style hinting then looks like
/// old Windows UI fonts. Geometry stays smooth at overlay sizes.
/// </summary>
public class LyricLineView : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LyricLineView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutInvalidated));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(LyricLineView),
        new FrameworkPropertyMetadata(28.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutInvalidated));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(LyricLineView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(LyricLineView),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x00, 0xd4, 0xff)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SettingsFontProperty = DependencyProperty.Register(
        nameof(SettingsFont), typeof(string), typeof(LyricLineView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutInvalidated));

    public static readonly DependencyProperty WordsProperty = DependencyProperty.Register(
        nameof(Words), typeof(IList<KaraokeWordTiming>), typeof(LyricLineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutInvalidated));

    public static readonly DependencyProperty LineElapsedMsProperty = DependencyProperty.Register(
        nameof(LineElapsedMs), typeof(double), typeof(LyricLineView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.Register(
        nameof(IsCurrent), typeof(bool), typeof(LyricLineView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private string? _karaokeKey;
    private List<(string text, double x, double y, int start, int dur)>? _karaokeLayout;
    private double _karaokeFs;

    private static void OnLayoutInvalidated(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LyricLineView)d;
        view._karaokeLayout = null;
        view._karaokeKey = null;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public string SettingsFont
    {
        get => (string)GetValue(SettingsFontProperty);
        set => SetValue(SettingsFontProperty, value);
    }

    public IList<KaraokeWordTiming>? Words
    {
        get => (IList<KaraokeWordTiming>?)GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public double LineElapsedMs
    {
        get => (double)GetValue(LineElapsedMsProperty);
        set => SetValue(LineElapsedMsProperty, value);
    }

    public bool IsCurrent
    {
        get => (bool)GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }

    public LyricLineView()
    {
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
        ClipToBounds = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Animated);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxW = double.IsInfinity(availableSize.Width) ? 4096 : Math.Max(availableSize.Width, 1);
        var fs = SafeFontSize();
        // Current line: one-line height so wrapping cannot steal space from translation.
        if (IsCurrent)
            return new Size(maxW, fs * 1.25);
        var ft = CreateFormatted(Text ?? "", Foreground, maxW, fs);
        var h = Math.Max(ft.Height, fs * 1.2);
        if (!double.IsInfinity(availableSize.Height))
            h = Math.Min(h, Math.Max(availableSize.Height, fs * 1.2));
        return new Size(maxW, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var trace = System.Threading.Interlocked.Increment(ref _renderTraces) <= 2;
        if (trace)
            RunLog.Write("lyric-render-begin w=" + ActualWidth.ToString("0")
                + " h=" + ActualHeight.ToString("0") + " cur=" + IsCurrent);
        try
        {
            if (ActualWidth < 2 || ActualHeight < 2) return;
            RenderCore(dc);
        }
        catch
        {
            try
            {
                var fs = SafeFontSize();
                var body = CreateFormatted(Text ?? "", AccentColorBrush(), 0, fs);
                DrawVector(dc, body, new Point(0, 0), AccentColorBrush());
            }
            catch { }
        }
        if (trace) RunLog.Write("lyric-render-end");
    }

    private static int _renderTraces;

    private void RenderCore(DrawingContext dc)
    {
        var maxW = Math.Max(ActualWidth, 1);
        var fs = SafeFontSize();
        var words = Words;
        if (IsCurrent && words is { Count: > 0 } && words.Count < 400)
        {
            var layout = EnsureKaraokeLayout(words, maxW, fs);
            var elapsed = double.IsNaN(LineElapsedMs) ? 0 : LineElapsedMs;
            foreach (var (text, x, y, start, dur) in layout)
            {
                var origin = new Point(
                    double.IsNaN(x) || double.IsInfinity(x) ? 0 : x,
                    double.IsNaN(y) || double.IsInfinity(y) ? 0 : y);
                var ft = CreateFormatted(text, KaraokeBrush(elapsed, start, dur), 0, _karaokeFs);
                dc.DrawText(ft, origin);
            }
            return;
        }

        var brushFill = IsCurrent ? AccentColorBrush() : Foreground;
        // Current line lives in a * row. Wrap at FontSize instead of shrinking
        // to one line (FitScale cancelled 原±: drawn width is independent of FontSize).
        var body = CreateFormatted(Text ?? "", brushFill, maxW, fs);
        if (IsCurrent && body.Height > ActualHeight && ActualHeight > 8)
        {
            var scale = Math.Max(0.55, ActualHeight / body.Height);
            body = CreateFormatted(Text ?? "", brushFill, maxW, fs * scale);
        }
        var y0 = Math.Max(0, (ActualHeight - body.Height) / 2);
        DrawVector(dc, body, new Point(0, y0), brushFill);
    }

    private List<(string text, double x, double y, int start, int dur)> EnsureKaraokeLayout(
        IList<KaraokeWordTiming> words, double maxWidth, double fontSize)
    {
        var key = $"{maxWidth:0.#}|{ActualHeight:0.#}|{fontSize:0.##}|{SettingsFont}|{FontHint()}|{words.Count}|{Text}";
        if (_karaokeLayout != null && _karaokeKey == key)
            return _karaokeLayout;

        var unsung = Brushes.White;
        var n = Math.Min(words.Count, KaraokeWordTiming.MaxOverlayWords);
        var pieces = new List<(string text, double w, int start, int dur)>(n);
        double Measure(string piece, double size)
        {
            var ww = CreateFormatted(piece, unsung, 0, size).WidthIncludingTrailingWhitespace;
            if (double.IsNaN(ww) || double.IsInfinity(ww)) return size;
            return ww;
        }

        var fs = fontSize;
        for (int i = 0; i < n; i++)
        {
            var w = words[i];
            var piece = w.Text ?? "";
            if (piece.Length == 0) continue;
            pieces.Add((piece, Measure(piece, fs), w.StartMs, Math.Max(0, w.DurationMs)));
        }

        var maxRows = ActualHeight >= fs * 2.15 ? 2 : 1;
        var rows = PackKaraokeRows(pieces, maxWidth, maxRows);
        var widest = 0.0;
        foreach (var row in rows)
        {
            var rw = 0.0;
            foreach (var p in row) rw += p.w;
            if (rw > widest) widest = rw;
        }
        if (widest > maxWidth && widest > 0.001)
        {
            var scale = Math.Max(0.55, maxWidth / widest);
            fs = fontSize * scale;
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                pieces[i] = (p.text, Measure(p.text, fs), p.start, p.dur);
            }
            rows = PackKaraokeRows(pieces, maxWidth, maxRows);
        }

        var lineH = fs * 1.2;
        var blockH = rows.Count * lineH;
        var y0 = Math.Max(0, (ActualHeight - blockH) / 2);
        if (double.IsNaN(y0) || double.IsInfinity(y0)) y0 = 0;

        var layout = new List<(string, double, double, int, int)>(pieces.Count);
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var rw = 0.0;
            foreach (var p in row) rw += p.w;
            var x = Math.Max(0, (maxWidth - rw) / 2);
            var y = y0 + r * lineH;
            foreach (var p in row)
            {
                layout.Add((p.text, x, y, p.start, p.dur));
                x += p.w;
            }
        }
        _karaokeKey = key;
        _karaokeLayout = layout;
        _karaokeFs = fs;
        return layout;
    }

    private static List<List<(string text, double w, int start, int dur)>> PackKaraokeRows(
        List<(string text, double w, int start, int dur)> pieces, double maxWidth, int maxRows)
    {
        var rows = new List<List<(string text, double w, int start, int dur)>>();
        if (pieces.Count == 0)
        {
            rows.Add(new List<(string, double, int, int)>());
            return rows;
        }

        var row = new List<(string text, double w, int start, int dur)>();
        var rowW = 0.0;
        foreach (var p in pieces)
        {
            if (row.Count > 0 && maxRows > 1 && rows.Count < maxRows - 1 && rowW + p.w > maxWidth)
            {
                rows.Add(row);
                row = new List<(string, double, int, int)>();
                rowW = 0;
            }
            row.Add(p);
            rowW += p.w;
        }
        rows.Add(row);
        return rows;
    }

    private Brush KaraokeBrush(double elapsed, int startMs, int durMs)
    {
        var endMs = startMs + durMs;
        if (elapsed >= endMs) return AccentColorBrush();
        if (elapsed <= startMs || durMs <= 0) return UnsungBrush();
        var pct = Math.Clamp((elapsed - startMs) / durMs, 0, 1);
        var ac = AccentColor();
        if (_ramp == null || _rampAccent != ac)
        {
            _rampAccent = ac;
            _ramp = new Brush[8];
            for (int i = 0; i < 8; i++)
            {
                var t = (i + 1) / 8.0;
                var br = new SolidColorBrush(Color.FromRgb(
                    (byte)(0x58 + (ac.R - 0x58) * t),
                    (byte)(0x68 + (ac.G - 0x68) * t),
                    (byte)(0x78 + (ac.B - 0x78) * t)));
                br.Freeze();
                _ramp[i] = br;
            }
        }
        return _ramp[Math.Clamp((int)(pct * 8), 0, 7)];
    }

    private Color _rampAccent;
    private Brush[]? _ramp;

    private static readonly SolidColorBrush UnsungFrozen = CreateUnsung();
    private static SolidColorBrush CreateUnsung()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x58, 0x68, 0x78));
        b.Freeze();
        return b;
    }
    private static Brush UnsungBrush() => UnsungFrozen;

    private Color AccentColor()
        => AccentBrush is SolidColorBrush sb ? sb.Color : Color.FromRgb(0x00, 0xd4, 0xff);

    private Brush AccentColorBrush()
        => AccentBrush as SolidColorBrush ?? Brushes.Cyan;

    private string FontHint()
    {
        if (LyricFonts.HasKana(Text)) return Text ?? "";
        var words = Words;
        if (words is { Count: > 0 })
        {
            var all = string.Concat(words.Take(80).Select(w => w.Text ?? ""));
            if (LyricFonts.HasKana(all)) return all;
        }
        return Text ?? "";
    }

    private double SafeFontSize()
    {
        var fs = FontSize;
        if (double.IsNaN(fs) || double.IsInfinity(fs) || fs < 1) return 14;
        return Math.Clamp(fs, 8, 160);
    }

    private void DrawVector(DrawingContext dc, FormattedText ft, Point origin, Brush fill)
    {
        if (double.IsNaN(origin.X) || double.IsNaN(origin.Y)
            || double.IsInfinity(origin.X) || double.IsInfinity(origin.Y))
            origin = new Point(0, 0);
        dc.DrawText(ft, origin);
    }

    private FormattedText CreateFormatted(string text, Brush brush, double maxWidth, double fontSize)
    {
        if (double.IsNaN(fontSize) || fontSize < 1) fontSize = SafeFontSize();
        var dip = 1.0;
        try { dip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
        if (dip <= 0 || double.IsNaN(dip)) dip = 1.0;

        var hint = FontHint();
        var typeface = LyricFonts.TypefaceFor(text, SettingsFont, hint);
        var culture = LyricFonts.CultureFor(text, hint);
        if (text.Length > 400) text = text[..400];
        var ft = new FormattedText(
            string.IsNullOrEmpty(text) ? " " : text,
            culture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush,
            dip)
        {
            Trimming = TextTrimming.None,
            TextAlignment = maxWidth > 1 ? TextAlignment.Center : TextAlignment.Left,
        };
        if (maxWidth > 1 && !double.IsInfinity(maxWidth) && !double.IsNaN(maxWidth))
            ft.MaxTextWidth = maxWidth;
        return ft;
    }
}
