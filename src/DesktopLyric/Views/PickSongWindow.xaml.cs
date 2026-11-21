using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopLyric.Services;

namespace DesktopLyric.Views;

public partial class PickSongWindow : Window
{
    private readonly LyricsService _lyrics;
    private readonly TimeSpan? _trackDuration;

    public LyricCandidate? Chosen { get; private set; }
    public bool Remember => ChkRemember.IsChecked == true;
    public string SearchTitle => TxtTitle.Text.Trim();
    public string SearchArtist => TxtArtist.Text.Trim();
    private string _playTitle;
    private string _playArtist;

    public PickSongWindow(LyricsService lyrics, string title, string artist, TimeSpan? trackDuration = null)
    {
        InitializeComponent();
        _lyrics = lyrics;
        _trackDuration = trackDuration;
        _playTitle = title ?? "";
        _playArtist = artist ?? "";
        TxtTitle.Text = LyricChoiceStore.SearchTitle(title);
        TxtArtist.Text = LyricChoiceStore.SearchArtist(title, artist);
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _ime = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, (_, e) =>
            _ime = e.TextComposition is { CompositionText.Length: > 0 });
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) => _ime = false);
        Loaded += async (_, _) => await RunSearch();
    }

    private bool _ime;

    private void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInsideInput(e.OriginalSource)) return;
        try { DragMove(); } catch { }
    }

    private static bool IsInsideInput(object? source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TextBox or PasswordBox or Button or ListBox or ListBoxItem or Thumb)
                return true;
            if (d is Window) break;
        }
        return false;
    }

    public void FollowPlaying(string title, string artist)
    {
        _playTitle = title ?? "";
        _playArtist = artist ?? "";
        if (_ime) return;
        if (TxtTitle != null && ChkLockTitle?.IsChecked != true && !TxtTitle.IsKeyboardFocused)
            TxtTitle.Text = LyricChoiceStore.SearchTitle(title);
        if (TxtArtist != null && ChkLockArtist?.IsChecked != true && !TxtArtist.IsKeyboardFocused)
            TxtArtist.Text = LyricChoiceStore.SearchArtist(title, artist);
        PaintLocks();
    }

    private void ReadPlaying_Click(object sender, RoutedEventArgs e)
    {
        if (ChkLockTitle?.IsChecked != true)
            TxtTitle.Text = LyricChoiceStore.SearchTitle(_playTitle);
        if (ChkLockArtist?.IsChecked != true)
            TxtArtist.Text = LyricChoiceStore.SearchArtist(_playTitle, _playArtist);
        _ = RunSearch();
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => PaintLocks();

    private void PaintLocks()
    {
        PaintLock(ChkLockTitle);
        PaintLock(ChkLockArtist);
    }

    private static void PaintLock(System.Windows.Controls.Primitives.ToggleButton? btn)
    {
        if (btn == null) return;
        var on = btn.IsChecked == true;
        btn.Foreground = new SolidColorBrush(on
            ? Color.FromRgb(0x00, 0xd4, 0xff)
            : Color.FromRgb(0xA0, 0xB0, 0xC0));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearch();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_ime || e.ImeProcessedKey != Key.None) return;
        e.Handled = true;
        await RunSearch();
    }

    private async Task RunSearch()
    {
        var title = TxtTitle.Text.Trim();
        var artist = TxtArtist.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            TxtStatus.Text = "輸入歌名再搜";
            return;
        }

        TxtStatus.Text = "搜尋中...";
        LstResults.ItemsSource = null;
        RunLog.Write("pick-search " + title);
        try
        {
            var list = await _lyrics.SearchCandidatesAsync(title, artist, _trackDuration);
            LstResults.ItemsSource = list;
            RunLog.Write("pick-results n=" + list.Count);
            if (list.Count > 0)
            {
                LstResults.SelectedIndex = 0;
                TxtStatus.Text = $"搵到 {list.Count} 首，雙擊或按「使用」";
            }
            else TxtStatus.Text = "搵唔到，試下改歌名／歌手";
        }
        catch (Exception ex)
        {
            RunLog.Write("pick-search-ex " + ex.GetType().Name + " " + ex.Message);
            TxtStatus.Text = "搜尋失敗";
        }
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (LstResults.SelectedItem is LyricCandidate c)
        {
            Chosen = c;
            Close();
        }
    }
}
