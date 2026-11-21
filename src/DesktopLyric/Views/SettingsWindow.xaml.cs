using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopLyric.Services;

namespace DesktopLyric.Views;

public partial class SettingsWindow : Window
{
    private static readonly string[] AccentPresets =
    [
        "#00d4ff", "#7dd3fc", "#a78bfa", "#f472b6", "#34d399", "#fbbf24", "#f0f0f5",
    ];

    private readonly AppSettings _settings;
    private bool _ready;
    private bool _dragging;

    public event Action? Changed;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        ShellWindow.NeverInTaskbar(this);
        _settings = settings;
        BuildAccentChips();
        FillFonts();
        LoadFromSettings();
        TxtFolder.Text = AppSettings.FolderPath;
        SldOpacity.ValueChanged += Slider_Changed;
        SldOriginal.ValueChanged += Slider_Changed;
        SldTrans.ValueChanged += Slider_Changed;
        SldOffset.ValueChanged += Slider_Changed;
        _ready = true;
    }

    private void FillFonts()
    {
        CmbFont.Items.Clear();
        foreach (var (_, label) in LyricFonts.ChineseChoices)
            CmbFont.Items.Add(label);
    }

    private void BuildAccentChips()
    {
        AccentHost.Children.Clear();
        foreach (var hex in AccentPresets)
        {
            Color color;
            try { color = (Color)ColorConverter.ConvertFromString(hex); }
            catch { continue; }
            var dot = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = new SolidColorBrush(color),
            };
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
                Cursor = Cursors.Hand,
                Tag = hex,
                Content = dot,
                ToolTip = hex,
            };
            btn.Click += Accent_Click;
            AccentHost.Children.Add(btn);
        }
        HighlightAccent();
    }

    private void HighlightAccent()
    {
        var current = (_settings.AccentColor ?? "").Trim();
        foreach (var child in AccentHost.Children)
        {
            if (child is not Button btn) continue;
            var hex = btn.Tag as string ?? "";
            var on = hex.Equals(current, StringComparison.OrdinalIgnoreCase);
            btn.BorderBrush = on
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xd4, 0xff))
                : Brushes.Transparent;
        }
    }

    private void LoadFromSettings()
    {
        ChkTrad.IsChecked = _settings.ForceTraditional;
        ChkHideTrans.IsChecked = _settings.HideTranslation;
        ChkRomaji.IsChecked = _settings.ShowRomaji;
        ChkTopmost.IsChecked = _settings.OverlayTopmost;
        ChkAlbum.IsChecked = _settings.FullscreenAlbumLayout;
        ChkStartup.IsChecked = StartupRegistration.IsEnabled();

        SelectFont(_settings.FontFamily);
        SldOpacity.Value = Math.Clamp(_settings.OverlayOpacity, 40, 100);
        SldOriginal.Value = Math.Clamp(_settings.OverlayOriginalScale, LyricFonts.ScaleMin, LyricFonts.ScaleMax);
        SldTrans.Value = Math.Clamp(_settings.OverlayTranslationScale, LyricFonts.ScaleMin, LyricFonts.ScaleMax);
        SldOffset.Value = Math.Clamp(_settings.GlobalOffsetMs, -1000, 1000);
        RefreshSliderLabels();
        HighlightAccent();
    }

    private void SelectFont(string? family)
    {
        for (int i = 0; i < LyricFonts.ChineseChoices.Length; i++)
        {
            if (LyricFonts.ChineseChoices[i].Family.Equals(family, StringComparison.OrdinalIgnoreCase))
            {
                CmbFont.SelectedIndex = i;
                return;
            }
        }
        CmbFont.SelectedIndex = 0;
    }

    private void RefreshSliderLabels()
    {
        if (LblOpacity == null || LblOriginal == null || LblTrans == null || LblOffset == null)
            return;
        LblOpacity.Text = $"{(int)SldOpacity.Value}%";
        LblOriginal.Text = $"{SldOriginal.Value:0.00}×";
        LblTrans.Text = $"{SldTrans.Value:0.00}×";
        LblOffset.Text = LyricOffsetStore.Format((int)SldOffset.Value);
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.ForceTraditional = ChkTrad.IsChecked == true;
        _settings.HideTranslation = ChkHideTrans.IsChecked == true;
        _settings.ShowRomaji = ChkRomaji.IsChecked == true;
        _settings.OverlayTopmost = ChkTopmost.IsChecked == true;
        _settings.FullscreenAlbumLayout = ChkAlbum.IsChecked == true;
        _settings.Save();
        if (sender == ChkStartup)
            StartupRegistration.SetEnabled(ChkStartup.IsChecked == true);
        Changed?.Invoke();
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var i = CmbFont.SelectedIndex;
        if (i < 0 || i >= LyricFonts.ChineseChoices.Length) return;
        _settings.FontFamily = LyricFonts.ChineseChoices[i].Family;
        _settings.Save();
        Changed?.Invoke();
    }

    private void Slider_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        CommitSliders();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        RefreshSliderLabels();
        if (_dragging) return;
        CommitSliders();
    }

    private void CommitSliders()
    {
        if (!_ready) return;
        _settings.OverlayOpacity = Math.Clamp(SldOpacity.Value, 40, 100);
        _settings.OverlayOriginalScale = Math.Clamp(SldOriginal.Value, LyricFonts.ScaleMin, LyricFonts.ScaleMax);
        _settings.OverlayTranslationScale = Math.Clamp(SldTrans.Value, LyricFonts.ScaleMin, LyricFonts.ScaleMax);
        _settings.GlobalOffsetMs = (int)Math.Clamp(SldOffset.Value, -1000, 1000);
        _settings.Save();
        Changed?.Invoke();
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        _settings.AccentColor = hex;
        _settings.Save();
        HighlightAccent();
        Changed?.Invoke();
    }

    private void ResetScale_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        SldOriginal.Value = 1.0;
        SldTrans.Value = 1.15;
        _ready = true;
        _settings.OverlayOriginalScale = 1.0;
        _settings.OverlayTranslationScale = 1.15;
        _settings.Save();
        RefreshSliderLabels();
        Changed?.Invoke();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.FolderPath);
            Process.Start(new ProcessStartInfo(AppSettings.FolderPath) { UseShellExecute = true });
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    private void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is Button or Slider or ComboBox or ComboBoxItem or CheckBox
            or System.Windows.Controls.Primitives.Thumb)
            return;
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
