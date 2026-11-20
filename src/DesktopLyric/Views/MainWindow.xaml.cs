using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using DesktopLyric.Services;

namespace DesktopLyric.Views;

public partial class MainWindow : Window
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private DispatcherTimer _pollTimer;
    private DispatcherTimer _syncTimer;

    private readonly LyricsService _lyrics = new();
    private readonly LyricSearchTracker _lyricsTracker = new();
    private static readonly System.Net.Http.HttpClient _romajiHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    private string _lastTitle = "";
    private List<LrcLine> _lines = new();
    private string _lastRomajiInput = "";
    private string _lastRomajiOutput = ""; // cache so we don't hit google every 100ms

    // smtc events exist but they're unreliable on some players, so poll
    private readonly Stopwatch _sw = new();
    private TimeSpan _basePos = TimeSpan.Zero;
    private bool _isPlaying;
    private OverlayWindow? _overlay;
    private AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        Loaded += OnLoaded;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += (_, _) => PollNowPlaying();

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _syncTimer.Tick += (_, _) => SyncLyrics();

        ApplySettings();
    }

    private void ApplySettings()
    {
        TxtCurrent.FontWeight = _settings.BoldLyrics
            ? FontWeights.Bold : FontWeights.Normal;
        try
        {
            TxtCurrent.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_settings.AccentColor));
        }
        catch { }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _session = _mgr.GetCurrentSession();

            if (_session != null)
            {
                TxtStatus.Text = "connected";
                StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x00, 0xd4, 0xff));
                PollNowPlaying();
                _pollTimer.Start();
                _syncTimer.Start();
            }
            else
            {
                TxtStatus.Text = "no media session — play something";
            }

            _mgr.CurrentSessionChanged += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _session = _mgr.GetCurrentSession();
                    if (_session != null)
                    {
                        TxtStatus.Text = "connected";
                        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0x00, 0xd4, 0xff));
                        _pollTimer.Start();
                        _syncTimer.Start();
                    }
                });
            };
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "smtc error: " + ex.Message;
        }
    }

    private async void PollNowPlaying()
    {
        if (_session == null) return;
        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            if (props == null) return;
            var title = props.Title ?? "";
            var artist = props.Artist ?? "";

            if (!string.IsNullOrEmpty(title))
            {
                TxtTitle.Text = title;
                TxtArtist.Text = artist;

                // load album art
                _ = LoadAlbumArt(props);
                _overlay?.SetTrackInfo(title, artist);

                if (_lyricsTracker.Start(title) is { } request)
                {
                    _lastTitle = title;
                    _lyrics.Cancel();
                    // Drop the previous track's lines before awaiting, so the
                    // 100ms sync timer cannot keep painting them for the new
                    // track while the search is in flight.
                    _lines = new();
                    _overlay?.UpdateLyrics("", null, null);
                    TxtCurrent.Text = "searching...";
                    TxtTrans.Text = "";
                    TxtPrev.Text = "";
                    TxtNext.Text = "";
                    TxtRomaji.Text = "";

                    var result = await _lyrics.SearchAsync(title, artist, GetTrackDuration());

                    // A cancelled search returns null too, which looks exactly
                    // like "no lyrics found". If another track has started in
                    // the meantime, this result belongs to the old one and must
                    // not touch the current track's state.
                    if (_lyricsTracker.IsCurrent(request))
                    {
                        if (result != null && result.Count > 0)
                        {
                            _lines = result;
                            TxtCurrent.Text = "♪";
                        }
                        else
                        {
                            _lines = new();
                            TxtCurrent.Text = "no lyrics found";
                        }
                    }
                }
            }

            // playback position
            var info = _session.GetPlaybackInfo();
            _isPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (_isPlaying)
            {
                var tl = _session.GetTimelineProperties();
                _basePos = tl.Position;
                _sw.Restart();
            }
            else
            {
                _sw.Stop();
            }
        }
        catch { }
    }

    private async Task LoadAlbumArt(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        try
        {
            var thumb = props.Thumbnail;
            if (thumb == null) { AlbumArt.Source = null; return; }
            using var stream = await thumb.OpenReadAsync();
            using var ms = new MemoryStream();
            using var inp = stream.AsStreamForRead();
            await inp.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            Dispatcher.BeginInvoke(() => AlbumArt.Source = bmp);
        }
        catch { }
    }

    private void SyncLyrics()
    {
        if (_lines == null || _lines.Count == 0 || !_isPlaying) return;

        var pos = _basePos + _sw.Elapsed;
        TxtTime.Text = $"{(int)pos.TotalMinutes}:{pos.Seconds:D2}";

        // apply offset
        var extra = LyricOffsetStore.GetMs(_lastTitle, "");
        var lyricPos = pos + TimeSpan.FromMilliseconds(_settings.GlobalOffsetMs + extra);
        if (lyricPos < TimeSpan.Zero) lyricPos = TimeSpan.Zero;

        int idx = -1;
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].Time <= lyricPos) { idx = i; break; }
        }

        if (idx >= 0)
        {
            var text = _lines[idx].Text;
            var trans = _lines[idx].TranslatedText;
            if (_settings.ForceTraditional)
            {
                text = S2TConverter.Convert(text);
                if (trans != null) trans = S2TConverter.Convert(trans);
            }

            TxtCurrent.Text = text;
            TxtTrans.Text = _settings.HideTranslation ? "" : (trans ?? "");
            TxtPrev.Text = idx > 0 ? _lines[idx - 1].Text : "";
            TxtNext.Text = idx < _lines.Count - 1 ? _lines[idx + 1].Text : "";

            _overlay?.UpdateLyrics(text,
                _settings.HideTranslation ? null : trans,
                idx < _lines.Count - 1 ? _lines[idx + 1].Text : null,
                _lines[idx].WordTimings,
                (lyricPos - _lines[idx].Time).TotalMilliseconds);

            // romaji
            if (_settings.ShowRomaji && HasJapanese(text))
                UpdateRomaji(text);
            else
                TxtRomaji.Text = "";
        }
    }

    private void UpdateRomaji(string text)
    {
        if (text == _lastRomajiInput)
        {
            TxtRomaji.Text = _lastRomajiOutput;
            return;
        }

        var input = text;
        _ = Task.Run(async () =>
        {
            try
            {
                var q = Uri.EscapeDataString(input);
                var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=ja&tl=en&dt=rm&q={q}";
                var resp = await _romajiHttp.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return;
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.GetArrayLength() == 0) return;

                var sb = new System.Text.StringBuilder();
                var first = root[0];
                if (first.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var seg in first.EnumerateArray())
                    {
                        if (seg.ValueKind == System.Text.Json.JsonValueKind.Array && seg.GetArrayLength() > 3)
                        {
                            var rm = seg[3].GetString();
                            if (!string.IsNullOrEmpty(rm)) sb.Append(rm);
                        }
                    }
                }
                var romaji = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(romaji))
                {
                    await Dispatcher.BeginInvoke(() =>
                    {
                        _lastRomajiInput = input;
                        _lastRomajiOutput = romaji;
                        TxtRomaji.Text = romaji;
                    });
                }
            }
            catch { }
        });
    }

    private static bool HasJapanese(string text)
    {
        foreach (var c in text)
            if ((c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF) ||
                (c >= 0x4E00 && c <= 0x9FFF)) return true;
        return false;
    }

    private TimeSpan? GetTrackDuration()
    {
        if (_session == null) return null;
        try
        {
            var tl = _session.GetTimelineProperties();
            var dur = tl.EndTime - tl.StartTime;
            return dur >= TimeSpan.FromSeconds(12) ? dur : null;
        }
        catch { return null; }
    }

    // --- UI handlers ---

    private void Window_Drag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // TODO: settings window, for now just open the json
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopLyric", "settings.json");
        if (File.Exists(path))
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
        }
        else
        {
            _settings.Save();
            MessageBox.Show($"settings saved to:\n{path}\n\nedit and restart to apply", "settings");
        }
    }

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay == null || !_overlay.IsVisible)
        {
            _overlay = new OverlayWindow();
            _overlay.Opacity = _settings.OverlayOpacity / 100.0;
            _overlay.SetTrackInfo(_lastTitle, TxtArtist.Text);
            _overlay.Closed += (_, _) => _overlay = null;
            _overlay.Show();
        }
        else
        {
            _overlay.Close();
            _overlay = null;
        }
    }

    private void ExportLrc_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0)
        {
            MessageBox.Show("no lyrics loaded", "export");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "LRC files|*.lrc",
            FileName = $"{_lastTitle}.lrc"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[ti:{_lastTitle}]");
                sb.AppendLine("[by:desktop-lyric]");
                sb.AppendLine();
                foreach (var line in _lines)
                {
                    var t = line.Time;
                    sb.AppendLine($"[{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds / 10:D2}]{line.Text}");
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                MessageBox.Show($"saved {_lines.Count} lines", "export");
            }
            catch (Exception ex)
            {
                MessageBox.Show("export failed: " + ex.Message, "error");
            }
        }
    }
}
