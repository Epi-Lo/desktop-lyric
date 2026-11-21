using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopLyric.Services;

namespace DesktopLyric.Views;

public partial class OverlayWindow : Window
{
    private AppSettings _settings;
    private bool _applyingSize;
    private bool _closed;
    private HoldRepeat? _offsetHold;
    private DispatcherTimer? _hitTimer;

    public event Action? TraditionalToggled;
    public event Action<int>? OffsetNudged;
    public event Action? PickSongRequested;
    public event Action? FullscreenRequested;
    public event Action? TimingEditorRequested;
    public event Action<bool>? LockToggled;

    public OverlayWindow() : this(AppSettings.Load()) { }

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        ShowActivated = false;
        _settings = settings;
        ApplyAccentColor();
        ApplyTradButton();
        ApplyTopmost();
        ApplyLock();
        ApplyFont();
        _offsetHold = new HoldRepeat(delta => OffsetNudged?.Invoke(delta));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource src && src.CompositionTarget != null)
                src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            _hitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _hitTimer.Tick += (_, _) => UpdateClickThrough();
            _hitTimer.Start();
            UpdateClickThrough();
            RunLog.Write("overlay-sw-render");
        }
        catch (Exception ex) { RunLog.Write("overlay-sw-ex " + ex.GetType().Name); }
    }

    private void UpdateClickThrough()
    {
        if (_closed || !IsVisible) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        ShellWindow.ClickThrough(this, !CursorOverWindow());
    }

    private bool CursorOverWindow()
    {
        if (!GetCursorPos(out var pt)) return false;
        try
        {
            var p = PointFromScreen(new Point(pt.X, pt.Y));
            return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointI pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointI
    {
        public int X;
        public int Y;
    }

    private void ApplyFont()
    {
        FontFamily = LyricFonts.FromSettings(_settings.FontFamily);
        OvTrackTitle.FontFamily = FontFamily;
        OvTrackArtist.FontFamily = FontFamily;
    }

    private void ApplyAccentColor()
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(_settings.AccentColor);
            var b = new SolidColorBrush(c);
            b.Freeze();
            OvCurrent.AccentBrush = b;
            OvCurrent.Foreground = b;
        }
        catch { }
    }

    public void SetTrackInfo(string title, string artist)
    {
        if (!WindowGuard.CanTouch(this) || _closed) return;
        OvTrackTitle.Text = title ?? "";
        OvTrackArtist.Text = artist ?? "";
    }

    public void RefreshTradButton() => ApplyTradButton();

    public void SetOffsetLabel(int ms, double rate = 1.0, bool? custom = null)
    {
        if (!WindowGuard.CanTouch(this)) return;
        BtnOffset.Content = LyricOffsetStore.FormatLabel(ms, rate);
        var isCustom = custom ?? (ms != 0 || Math.Abs(rate - 1.0) >= 0.0005);
        BtnOffset.Foreground = isCustom
            ? new SolidColorBrush(Color.FromRgb(0x00, 0xd4, 0xff))
            : new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
    }

    public void RefreshFonts()
    {
        if (!WindowGuard.CanTouch(this) || _closed) return;
        var custom = _settings.FontFamily ?? "";
        OvCurrent.SettingsFont = custom;
        OvTrans.SettingsFont = custom;
        OvNext.SettingsFont = custom;
        ApplyLineSizes();
    }

    public void RefreshAppearance()
    {
        if (!WindowGuard.CanTouch(this) || _closed) return;
        ApplyAccentColor();
        ApplyTradButton();
        ApplyTopmost();
        ApplyFont();
        Opacity = Math.Clamp(_settings.OverlayOpacity / 100.0, 0.35, 1);
        RefreshFonts();
    }

    private void ApplyTradButton()
    {
        BtnTrad.Foreground = _settings.ForceTraditional
            ? new SolidColorBrush(Color.FromRgb(0x00, 0xd4, 0xff))
            : new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
    }

    private void ApplyTopmost()
    {
        Topmost = _settings.OverlayTopmost;
        BtnTopmost.Foreground = _settings.OverlayTopmost
            ? new SolidColorBrush(Color.FromRgb(0x00, 0xd4, 0xff))
            : new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
    }

    public void UpdateLyrics(string current, string? translated, string? next = null,
        List<KaraokeWordTiming>? wordTimings = null, double lineElapsedMs = 0)
    {
        if (!WindowGuard.CanTouch(this) || _closed) return;
        try
        {
            var custom = _settings.FontFamily ?? "";
            var needLayout = OvCurrent.Text != (current ?? "")
                || OvTrans.Text != (translated ?? "")
                || OvNext.Text != (next ?? "");
            if (!needLayout && ReferenceEquals(OvCurrent.Words, wordTimings))
            {
                if (KaraokeWordTiming.OverlayFrozen(wordTimings, OvCurrent.LineElapsedMs)
                    && KaraokeWordTiming.OverlayFrozen(wordTimings, lineElapsedMs))
                    return;
                if (Math.Abs(OvCurrent.LineElapsedMs - lineElapsedMs) < 8)
                    return;
            }
            OvCurrent.SettingsFont = custom;
            OvCurrent.Text = current ?? "";
            OvCurrent.Words = wordTimings;
            OvCurrent.LineElapsedMs = lineElapsedMs;
            OvTrans.SettingsFont = custom;
            OvTrans.Text = translated ?? "";
            OvNext.SettingsFont = custom;
            OvNext.Text = next ?? "";
            if (needLayout) ApplyLineSizes();
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_applyingSize) ApplyLineSizes();
    }

    private void ApplyLineSizes()
    {
        if (_applyingSize || _closed || !IsLoaded) return;
        _applyingSize = true;
        try
        {
            var area = LyricsHost?.ActualHeight > 1
                ? LyricsHost.ActualHeight
                : Math.Max(50, ActualHeight - 96);
            if (double.IsNaN(area) || double.IsInfinity(area)) return;
            var sizes = LyricFonts.FitOverlaySizes(
                area,
                hasTrans: !string.IsNullOrWhiteSpace(OvTrans.Text),
                hasNext: !string.IsNullOrWhiteSpace(OvNext.Text),
                originalIsJapanese: LyricFonts.HasKana(OvCurrent.Text),
                originalScale: _settings.OverlayOriginalScale,
                translationScale: _settings.OverlayTranslationScale);
            WindowGuard.SetFontSize(OvCurrent, sizes.CurrentFont);
            WindowGuard.SetFontSize(OvTrans, sizes.TransFont);
            WindowGuard.SetMaxHeight(OvTrans, sizes.TransMaxHeight);
            WindowGuard.SetFontSize(OvNext, sizes.NextFont);
            WindowGuard.SetMaxHeight(OvNext, sizes.NextMaxHeight);
            OvCurrent.InvalidateVisual();
            OvTrans.InvalidateVisual();
            OvNext.InvalidateVisual();
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
        finally { _applyingSize = false; }
    }

    // --- controls ---

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        try { DragMove(); } catch { }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(150));
        ControlBar.BeginAnimation(OpacityProperty, anim);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(0.55, TimeSpan.FromMilliseconds(150));
        ControlBar.BeginAnimation(OpacityProperty, anim);
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

    private void OnFontLarger(object sender, RoutedEventArgs e)
    {
        GrowWindow(1.12);
    }

    private void OnFontSmaller(object sender, RoutedEventArgs e)
    {
        GrowWindow(1 / 1.12);
    }

    private void GrowWindow(double factor)
    {
        _applyingSize = true;
        try
        {
            var h = Height * factor;
            if (double.IsNaN(h) || double.IsInfinity(h)) return;
            Height = Math.Clamp(h, MinHeight, MaxHeight);
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
        finally { _applyingSize = false; }
        ApplyLineSizes();
    }

    private void OnOffsetEarlierDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _offsetHold?.Down(1, sender as IInputElement);
    }

    private void OnOffsetLaterDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _offsetHold?.Down(-1, sender as IInputElement);
    }

    private void OnOffsetHoldUp(object sender, MouseEventArgs e)
        => _offsetHold?.Up();

    private void OnOffsetReset(object sender, RoutedEventArgs e)
        => OffsetNudged?.Invoke(int.MinValue);

    private void OnToggleTrad(object sender, RoutedEventArgs e)
    {
        _settings.ForceTraditional = !_settings.ForceTraditional;
        ApplyTradButton();
        _settings.Save();
        TraditionalToggled?.Invoke();
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        _settings.OverlayTopmost = !_settings.OverlayTopmost;
        _settings.Save();
        ApplyTopmost();
    }

    public bool LyricsLocked { get; private set; }

    public void SetLyricsLocked(bool locked)
    {
        LyricsLocked = locked;
        ApplyLock();
    }

    private void ApplyLock()
    {
        if (BtnLock == null) return;
        BtnLock.Foreground = LyricsLocked
            ? new SolidColorBrush(Color.FromRgb(0x00, 0xd4, 0xff))
            : new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
        BtnLock.ToolTip = LyricsLocked
            ? "已鎖定歌詞。再撳解除，跟返播放器。"
            : "鎖定而家呢首歌詞。播放器換歌／YouTube 預覽都唔會改。再撳解除。";
    }

    private void OnToggleLock(object sender, RoutedEventArgs e)
    {
        LyricsLocked = !LyricsLocked;
        ApplyLock();
        LockToggled?.Invoke(LyricsLocked);
    }

    private void OnTimingEditor(object sender, RoutedEventArgs e) => TimingEditorRequested?.Invoke();

    private void OnPickSong(object sender, RoutedEventArgs e) => PickSongRequested?.Invoke();

    private void OnFullscreen(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke();

    private void OnShowMain(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow main)
            main.RestoreFromOverlay();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        // Only the ✕ button quits when main is hidden. Closed from style/HWND
        // changes must not kill the process (startup lasted ~4s with no dump).
        if (Application.Current is { MainWindow: MainWindow { IsVisible: false } main })
            main.QuitApp("overlay-close");
        else
            Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _hitTimer?.Stop();
        _hitTimer = null;
        _offsetHold?.Dispose();
        _offsetHold = null;
        try
        {
            var vis = Application.Current?.MainWindow?.IsVisible;
            RunLog.Write("overlay-closed mainVisible=" + vis);
        }
        catch { }
        base.OnClosed(e);
    }
}
