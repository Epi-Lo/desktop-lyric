using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DesktopLyric.Services;

namespace DesktopLyric.Views;

public partial class FullscreenWindow : Window
{
    private readonly AppSettings _settings;
    private bool _applyingSize;
    private bool _closed;

    public event Action? TraditionalToggled;
    public event Action? PickSongRequested;

    public FullscreenWindow(AppSettings settings)
    {
        InitializeComponent();
        ShellWindow.NeverInTaskbar(this);
        _settings = settings;
        ApplyTradButton();
        ApplyLayout();
        ApplyAccent();
        Loaded += (_, _) =>
        {
            SizeCover();
            ApplyLineSizes();
        };
    }

    public void SetTrackInfo(string title, string artist)
    {
        if (!WindowGuard.CanTouch(this) || _closed) return;
        FsTitle.Text = title ?? "";
        FsArtist.Text = artist ?? "";
    }

    public void SetAlbumArt(ImageSource? src)
    {
        if (!WindowGuard.CanTouch(this) || _closed) return;
        CoverArt.Source = src;
        BgArt.Source = src;
    }

    public void RefreshFonts()
    {
        if (!WindowGuard.CanTouch(this) || _closed) return;
        var custom = _settings.FontFamily ?? "";
        FsCurrent.SettingsFont = custom;
        FsTrans.SettingsFont = custom;
        FsNext.SettingsFont = custom;
        ApplyLineSizes();
    }

    public void RefreshAppearance()
    {
        if (!WindowGuard.CanTouch(this) || _closed) return;
        ApplyAccent();
        ApplyTradButton();
        ApplyLayout();
        RefreshFonts();
    }

    public void RefreshTradButton() => ApplyTradButton();

    public void UpdateLyrics(string current, string? translated, string? next = null,
        List<KaraokeWordTiming>? wordTimings = null, double lineElapsedMs = 0)
    {
        if (!WindowGuard.CanTouch(this) || _closed) return;
        try
        {
            var custom = _settings.FontFamily ?? "";
            var needLayout = FsCurrent.Text != (current ?? "")
                || FsTrans.Text != (translated ?? "")
                || FsNext.Text != (next ?? "");
            if (!needLayout
                && ReferenceEquals(FsCurrent.Words, wordTimings)
                && Math.Abs(FsCurrent.LineElapsedMs - lineElapsedMs) < 8)
                return;
            FsCurrent.SettingsFont = custom;
            FsCurrent.Text = current ?? "";
            FsCurrent.Words = wordTimings;
            FsCurrent.LineElapsedMs = lineElapsedMs;
            FsTrans.SettingsFont = custom;
            FsTrans.Text = translated ?? "";
            FsNext.SettingsFont = custom;
            FsNext.Text = next ?? "";
            if (needLayout) ApplyLineSizes();
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    private void ApplyAccent()
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(_settings.AccentColor);
            var b = new SolidColorBrush(c);
            b.Freeze();
            FsCurrent.AccentBrush = b;
            FsCurrent.Foreground = b;
        }
        catch { }
    }

    private void ApplyTradButton()
    {
        BtnTrad.Foreground = _settings.ForceTraditional
            ? new SolidColorBrush(Color.FromRgb(0x00, 0xd4, 0xff))
            : new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
    }

    private void ApplyLayout()
    {
        try
        {
            var album = _settings.FullscreenAlbumLayout;
            ArtPanel.Visibility = album ? Visibility.Visible : Visibility.Collapsed;
            ArtCol.Width = album ? new GridLength(0.40, GridUnitType.Star) : new GridLength(0);
            BtnLayout.Content = album ? "專輯" : "歌詞";
            BtnLayout.Foreground = album
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xd4, 0xff))
                : new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
            SizeCover();
            ApplyLineSizes();
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    private void SizeCover()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (double.IsNaN(w) || double.IsNaN(h) || w < 80 || h < 80) return;
        var side = Math.Clamp(Math.Min(w * 0.28, h * 0.52), 180, 520);
        if (Math.Abs(CoverFrame.Width - side) < 1) return;
        CoverFrame.Width = side;
        CoverFrame.Height = side;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_applyingSize) return;
        SizeCover();
        ApplyLineSizes();
    }

    private void ApplyLineSizes()
    {
        if (_applyingSize || _closed) return;
        _applyingSize = true;
        try
        {
            var area = LyricsHost?.ActualHeight > 1
                ? LyricsHost.ActualHeight
                : Math.Max(120, ActualHeight - 160);
            if (double.IsNaN(area) || double.IsInfinity(area)) return;
            var sizes = LyricFonts.FitOverlaySizes(
                area,
                hasTrans: !string.IsNullOrWhiteSpace(FsTrans.Text),
                hasNext: !string.IsNullOrWhiteSpace(FsNext.Text),
                originalIsJapanese: LyricFonts.HasKana(FsCurrent.Text),
                originalScale: _settings.OverlayOriginalScale,
                translationScale: _settings.OverlayTranslationScale,
                fontCap: 140);
            WindowGuard.SetFontSize(FsCurrent, sizes.CurrentFont);
            WindowGuard.SetFontSize(FsTrans, sizes.TransFont);
            WindowGuard.SetMaxHeight(FsTrans, sizes.TransMaxHeight);
            WindowGuard.SetFontSize(FsNext, sizes.NextFont);
            WindowGuard.SetMaxHeight(FsNext, sizes.NextMaxHeight);
            FsCurrent.InvalidateVisual();
            FsTrans.InvalidateVisual();
            FsNext.InvalidateVisual();
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
        finally { _applyingSize = false; }
    }

    private void NudgeOriginal(double delta)
    {
        _settings.OverlayOriginalScale = Math.Clamp(
            _settings.OverlayOriginalScale + delta, LyricFonts.ScaleMin, LyricFonts.ScaleMax);
        _settings.Save();
        ApplyLineSizes();
    }

    private void NudgeTranslation(double delta)
    {
        _settings.OverlayTranslationScale = Math.Clamp(
            _settings.OverlayTranslationScale + delta, LyricFonts.ScaleMin, LyricFonts.ScaleMax);
        _settings.Save();
        ApplyLineSizes();
    }

    private void OnOriginalSmaller(object sender, RoutedEventArgs e) => NudgeOriginal(-LyricFonts.ScaleStep);
    private void OnOriginalLarger(object sender, RoutedEventArgs e) => NudgeOriginal(LyricFonts.ScaleStep);
    private void OnTransSmaller(object sender, RoutedEventArgs e) => NudgeTranslation(-LyricFonts.ScaleStep);
    private void OnTransLarger(object sender, RoutedEventArgs e) => NudgeTranslation(LyricFonts.ScaleStep);

    private void OnToggleLayout(object sender, RoutedEventArgs e)
    {
        _settings.FullscreenAlbumLayout = !_settings.FullscreenAlbumLayout;
        _settings.Save();
        ApplyLayout();
    }

    private void OnToggleTrad(object sender, RoutedEventArgs e)
    {
        _settings.ForceTraditional = !_settings.ForceTraditional;
        ApplyTradButton();
        _settings.Save();
        TraditionalToggled?.Invoke();
    }

    private void OnPickSong(object sender, RoutedEventArgs e) => PickSongRequested?.Invoke();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.F11)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        // fullscreen; ignore drag
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(160));
        ControlBar.BeginAnimation(OpacityProperty, anim);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(0.45, TimeSpan.FromMilliseconds(220));
        ControlBar.BeginAnimation(OpacityProperty, anim);
    }
}
