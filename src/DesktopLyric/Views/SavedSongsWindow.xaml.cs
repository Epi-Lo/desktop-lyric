using System.Windows;
using System.Windows.Input;
using DesktopLyric.Services;

namespace DesktopLyric.Views;

public partial class SavedSongsWindow : Window
{
    private readonly LyricsService _lyrics;
    private List<SavedChoice> _all = [];

    public bool Dirty { get; private set; }

    public SavedSongsWindow(LyricsService lyrics)
    {
        InitializeComponent();
        ShellWindow.NeverInTaskbar(this);
        _lyrics = lyrics;
        Reload();
    }

    private void Reload()
    {
        _all = LyricChoiceStore.ListAll().ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = (TxtFilter?.Text ?? "").Trim();
        IEnumerable<SavedChoice> view = _all;
        if (q.Length > 0)
        {
            view = _all.Where(c =>
                c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Artist.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.SourceLabel.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.CandidateKey.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = view.ToList();
        LstSaved.ItemsSource = list;
        var empty = list.Count == 0;
        TxtEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LstSaved.Visibility = empty && q.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtEmpty.Text = q.Length > 0 && empty
            ? "冇符合嘅記憶"
            : "未記住任何歌。用「選歌」揀正確版本，就會出現喺呢度。";
        TxtCount.Text = _all.Count == 0 ? "" : $"{list.Count} / {_all.Count} 首";
        if (list.Count > 0 && LstSaved.SelectedIndex < 0)
            LstSaved.SelectedIndex = 0;
    }

    private SavedChoice? Selected => LstSaved.SelectedItem as SavedChoice;

    private void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = Dirty;

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) Delete_Click(sender, e);
        else if (e.Key == Key.Enter) Change_Click(sender, e);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        var ok = MessageBox.Show(
            this,
            $"刪除「{item.Title}」嘅記憶？",
            "已記選歌",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        LyricChoiceStore.RemoveCandidate(item.CandidateKey);
        Dirty = true;
        Reload();
    }

    private async void Change_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        var pick = new PickSongWindow(_lyrics, item.Title, item.Artist) { Owner = this };
        if (pick.ShowDialog() != true || pick.Chosen == null) return;

        var lines = await _lyrics.FetchAsync(pick.Chosen);
        if (lines == null) return;
        if (lines.Count == 0)
        {
            MessageBox.Show(this, "呢首冇歌詞", "已記選歌");
            return;
        }

        LyricChoiceStore.Retarget(item.CandidateKey, pick.Chosen.Key);
        LyricChoiceStore.Set(pick.SearchTitle, pick.SearchArtist, pick.Chosen.Key);
        Dirty = true;
        Reload();
        var next = _all.FirstOrDefault(c => c.CandidateKey == pick.Chosen.Key);
        if (next != null) LstSaved.SelectedItem = next;
    }
}
