using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private static readonly System.Net.Http.HttpClient _romajiHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    private string _lastTitle = "";
    private string _lastArtist = "";
    private int _trackOffsetMs;
    private double _trackRate = 1.0;
    private Dictionary<string, int> _lineShifts = new();
    private Dictionary<string, int> _lineHolds = new();
    private Dictionary<string, string> _lineTexts = new();
    private Dictionary<string, string> _lineTrans = new();
    private List<AddedLyric> _addedLines = new();
    private HoldRepeat? _offsetHold;
    private TimingEditorWindow? _timingEditor;
    private DispatcherTimer? _offsetSave;
    private List<LrcLine> _lines = new();
    private string _lastRomajiInput = "";
    private string _lastRomajiOutput = ""; // cache so we don't hit google every 100ms

    // Many players freeze SMTC Position while playing and only update it on pause/seek.
    // Polling that stale value used to rewind the interpolator every 2s.
    private readonly PlaybackClock _clock = new();
    private OverlayWindow? _overlay;
    private FullscreenWindow? _fullscreen;
    private ImageSource? _albumArt;
    private string _lastArtKey = "";
    private bool _overlayHiddenForFullscreen;
    private AppSettings _settings;
    private volatile bool _forceClose;
    private int _karaokeLineIdx = -1;
    private List<KaraokeWordTiming>? _karaokeWords;
    private List<KaraokeWordTiming>? _karaokeSrc;
    private List<LrcLine>? _shown;
    private string _romajiInFlight = "";
    private int _pollGen;
    private int _artGen;
    private volatile bool _clockQueued;
    private volatile bool _pollQueued;
    private TimeSpan? _trackDuration;
    private int _clockFails;
    private bool _artMissing;
    private int _paintIdx = int.MinValue;
    private string _paintSig = "";
    private string _paintText = "";
    private string? _paintTrans;
    private string? _paintNext;
    private List<KaraokeWordTiming>? _paintWords;
    private double _paintElapsed = double.NaN;
    private int _paintPhase = int.MinValue;
    private bool? _lastPlaying;
    private long _lastTlMs;
    private long _lastClockQueueMs;
    private volatile bool _forceTimeline;
    private readonly Dictionary<string, string> _s2t = new();
    private GlobalSystemMediaTransportControlsSessionPlaybackInfo? _heldPlayback;
    private GlobalSystemMediaTransportControlsSessionTimelineProperties? _heldTimeline;
    private System.Threading.Timer? _watchdog;
    private DispatcherTimer? _artTimer;
    private bool _loggedFirstPaint;
    private bool _artBusy;
    private bool _artDeferred;
    private string _pendingTitle = "";
    private string _pendingArtist = "";
    private DispatcherTimer? _titleHold;
    private bool _lyricsLocked;
    private PickSongWindow? _pick;
    private string _smtcTitle = "";
    private string _smtcArtist = "";

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        Loaded += OnLoaded;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += (_, _) =>
        {
            try { PollNowPlaying(); }
            catch (Exception ex) { ErrorLog.Write(ex); }
        };

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _syncTimer.Tick += (_, _) =>
        {
            try { SyncLyrics(); }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                RunLog.Write("sync-ex " + ex.GetType().Name);
            }
        };

        ApplySettings();
        ApplyTradButton();
        ApplyFontButton();
        WireTray();
        _offsetHold = new HoldRepeat(NudgeOffset);
        var hb = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        hb.Tick += (_, _) =>
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                RunLog.Write("hb ws=" + (p.WorkingSet64 / 1_048_576) + "MB"
                    + " gc=" + (GC.GetTotalMemory(false) / 1_048_576) + "MB"
                    + " play=" + _clock.IsPlaying
                    + " editor=" + (_timingEditor != null)
                    + " lines=" + _lines.Count
                    + " pos=" + (int)_clock.Position.TotalSeconds);
            }
            catch { }
        };
        hb.Start();
        // Independent of the UI thread. If this continues and hb stops, the dispatcher is stuck.
        _watchdog = new System.Threading.Timer(OnWatchdog, null, 2500, 5000);
    }

    private void OnWatchdog(object? _)
    {
        if (_forceClose) return;
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            RunLog.Write("wd ws=" + (p.WorkingSet64 / 1_048_576) + "MB"
                + " play=" + _clock.IsPlaying
                + " pos=" + (int)_clock.Position.TotalSeconds);
        }
        catch { }
    }

    private void WireTray()
    {
        if (Application.Current is not App { Tray: { } tray }) return;
        tray.ShowMainRequested += RestoreFromOverlay;
        tray.ShowOverlayRequested += ShowOverlay;
        tray.SettingsRequested += OpenSettings;
        tray.ExitRequested += () => QuitApp("tray");
    }

    private void ApplySettings()
    {
        FontFamily = LyricFonts.FromSettings(_settings.FontFamily);
        try
        {
            var b = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_settings.AccentColor));
            b.Freeze();
            TxtCurrent.AccentBrush = b;
            TxtCurrent.Foreground = b;
            TxtCurrent.SettingsFont = _settings.FontFamily ?? "";
            TxtTrans.SettingsFont = _settings.FontFamily ?? "";
        }
        catch { }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RunLog.Write("loaded visible=" + IsVisible);
        ShowOverlay();
        RunLog.Write("overlay-shown visible=" + (_overlay?.IsVisible == true));
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_forceClose)
            {
                WinRtLifetime.Suppress(_mgr);
                _mgr = null;
                return;
            }
            BindSession(_mgr.GetCurrentSession());
            _mgr.CurrentSessionChanged += OnCurrentSessionChanged;
            RunLog.Write("smtc-ok session=" + (_session != null));
            ApplySessionUi();
        }
        catch (Exception ex)
        {
            RunLog.Write("smtc-error " + ex.GetType().Name);
            TxtStatus.Text = "smtc error: " + ex.Message;
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
    {
        try
        {
            WinRtLifetime.Suppress(args);
            var d = Dispatcher;
            if (d.HasShutdownStarted || d.HasShutdownFinished) return;
            d.BeginInvoke(() =>
            {
                if (_forceClose || _mgr == null) return;
                try
                {
                    if (!BindSession(_mgr.GetCurrentSession())) return;
                    ApplySessionUi();
                }
                catch (Exception ex) { ErrorLog.Write(ex); }
            });
        }
        catch { }
    }

    private void ApplySessionUi()
    {
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
            _pollTimer.Stop();
            _syncTimer.Stop();
            _clock.Freeze();
            SyncLyrics();
        }
    }

    private async void PollNowPlaying()
    {
        if (_session == null || _forceClose) return;
        var gen = ++_pollGen;
        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            if (gen != _pollGen || _forceClose)
            {
                WinRtLifetime.Suppress(props);
                return;
            }
            if (props == null) return;
            try
            {
                var title = props.Title ?? "";
                var artist = props.Artist ?? "";

                if (!string.IsNullOrEmpty(title))
                {
                    _smtcTitle = title;
                    _smtcArtist = artist;
                    if (_lyricsLocked)
                    {
                        RefreshClock();
                        return;
                    }
                    // Hover previews (YouTube mini-player / thumbnail) briefly
                    // rewrite SMTC title. Keep the committed track until the
                    // new name stays put and does not look like a 0:00 preview.
                    if (!string.IsNullOrEmpty(_lastTitle) && title != _lastTitle)
                    {
                        ArmTitleHold(title, artist);
                        return;
                    }
                    CancelTitleHold();

                    TxtTitle.Text = ToDisplay(title);
                    TxtArtist.Text = ToDisplay(artist);

                    var artKey = title + "\n" + artist;
                    if (artKey != _lastArtKey)
                    {
                        _artMissing = false;
                        _artDeferred = false;
                        _albumArt = null;
                    }
                    var needArt = artKey != _lastArtKey
                        || (_albumArt == null && !_artMissing && !_artDeferred);
                    if (needArt) _lastArtKey = artKey;
                    _overlay?.SetTrackInfo(ToDisplay(title), ToDisplay(artist));
                    _fullscreen?.SetTrackInfo(ToDisplay(title), ToDisplay(artist));

                    var artistFilled = string.IsNullOrEmpty(_lastArtist)
                        && !string.IsNullOrEmpty(artist);
                    RefreshClock();
                    if (string.IsNullOrEmpty(_lastTitle) || artistFilled)
                    {
                        await CommitTrackAsync(title, artist, search: true);
                    }
                    if (needArt && WindowGuard.CanTouch(_fullscreen))
                        ScheduleAlbumArt();
                    else if (needArt)
                    {
                        _artDeferred = true;
                        RunLog.Write("art-hold");
                    }
                }
                else
                    RefreshClock();
            }
            finally { WinRtLifetime.Suppress(props); }
        }
        catch { }
    }

    private void ArmTitleHold(string title, string artist)
    {
        if (title == _pendingTitle)
        {
            _pendingArtist = artist;
            return;
        }
        _pendingTitle = title;
        _pendingArtist = artist;
        RunLog.Write("title-hold");
        _titleHold ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _titleHold.Tick -= OnTitleHold;
        _titleHold.Tick += OnTitleHold;
        _titleHold.Stop();
        _titleHold.Start();
    }

    private void CancelTitleHold()
    {
        _titleHold?.Stop();
        _pendingTitle = "";
        _pendingArtist = "";
    }

    private async void OnTitleHold(object? sender, EventArgs e)
    {
        _titleHold?.Stop();
        var title = _pendingTitle;
        var artist = _pendingArtist;
        if (_forceClose || string.IsNullOrEmpty(title) || title == _lastTitle)
        {
            CancelTitleHold();
            return;
        }
        if (LooksLikeHoverPreview())
        {
            RunLog.Write("title-ignore-preview");
            _pendingTitle = "";
            _pendingArtist = "";
            return;
        }
        if (_lyricsLocked)
        {
            CancelTitleHold();
            RefreshClock();
            return;
        }
        RunLog.Write("title-commit");
        _pendingTitle = "";
        _pendingArtist = "";
        try
        {
            await CommitTrackAsync(title, artist, search: true);
            RefreshClock();
            _pick?.FollowPlaying(title, artist);
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    private bool LooksLikeHoverPreview()
    {
        if (!_clock.IsPlaying) return false;
        if (_session == null) return false;
        try
        {
            var tl = _session.GetTimelineProperties();
            var pos = tl.Position;
            HoldSmtc(ref _heldTimeline, tl);
            var clock = _clock.Position;
            if ((pos - clock).Duration() < TimeSpan.FromSeconds(1.5))
                return true;
            return _lastPlaying == true && clock.TotalSeconds >= 8 && pos.TotalSeconds < 4;
        }
        catch
        {
            return false;
        }
    }

    private async Task CommitTrackAsync(string title, string artist, bool search)
    {
        var titleChanged = title != _lastTitle;
        _lastTitle = title;
        _lastArtist = artist;
        TxtTitle.Text = ToDisplay(title);
        TxtArtist.Text = ToDisplay(artist);
        _overlay?.SetTrackInfo(ToDisplay(title), ToDisplay(artist));
        _fullscreen?.SetTrackInfo(ToDisplay(title), ToDisplay(artist));
        _pick?.FollowPlaying(title, artist);
        if (!search) return;
        if (titleChanged)
        {
            _trackDuration = null;
            _loggedFirstPaint = false;
            LoadTrackOffset();
            _lyrics.Cancel();
            TxtCurrent.Text = "searching...";
            TxtTrans.Text = "";
            TxtPrev.Text = "";
            TxtNext.Text = "";
        }
        else
        {
            LoadTrackOffset();
            _lyrics.Cancel();
        }
        var requestedTitle = title;
        RunLog.Write("search-begin");
        var result = await _lyrics.SearchAsync(title, artist, GetTrackDuration());
        if (_forceClose || _lastTitle != requestedTitle) return;
        if (result == null)
        {
            RunLog.Write("search-cancel");
            return;
        }
        RunLog.Write("search-done n=" + result.Count);
        if (result.Count > 0)
        {
            SetLines(result);
            TxtCurrent.Text = "♪";
        }
        else
        {
            SetLines([]);
            TxtCurrent.Text = "no lyrics found";
        }
    }

    /// <returns>true if the live session changed.</returns>
    private bool BindSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null && session != null)
        {
            try
            {
                if (_session.SourceAppUserModelId == session.SourceAppUserModelId)
                {
                    WinRtLifetime.Suppress(session);
                    return false;
                }
            }
            catch { }
        }

        if (_session != null)
        {
            try
            {
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            }
            catch { }
            WinRtLifetime.Suppress(_session);
        }

        DropHeldSmtc();
        _session = session;
        if (_session == null) return true;

        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        return true;
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        try { WinRtLifetime.Suppress(args); }
        catch { }
        _forceTimeline = true;
        QueueRefreshClock();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        try { WinRtLifetime.Suppress(args); }
        catch { }
        if (!_clock.IsPlaying) return;
        QueueRefreshClock();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        try { WinRtLifetime.Suppress(args); }
        catch { }
        QueuePollNowPlaying();
    }

    private void QueueRefreshClock()
    {
        try
        {
            if (_forceClose) return;
            var d = Dispatcher;
            if (d.HasShutdownStarted || d.HasShutdownFinished) return;
            if (!d.CheckAccess())
            {
                d.BeginInvoke(QueueRefreshClock);
                return;
            }
            if (_clockQueued) return;
            if (!_forceTimeline)
            {
                var now = Environment.TickCount64;
                if (now - _lastClockQueueMs < 400) return;
                _lastClockQueueMs = now;
            }
            _clockQueued = true;
            d.BeginInvoke(() =>
            {
                _clockQueued = false;
                if (_forceClose || _session == null) return;
                RefreshClock();
            });
        }
        catch { }
    }

    private void QueuePollNowPlaying()
    {
        try
        {
            if (_forceClose) return;
            var d = Dispatcher;
            if (d.HasShutdownStarted || d.HasShutdownFinished) return;
            if (!d.CheckAccess())
            {
                d.BeginInvoke(QueuePollNowPlaying);
                return;
            }
            if (_pollQueued) return;
            _pollQueued = true;
            d.BeginInvoke(() =>
            {
                _pollQueued = false;
                if (_forceClose || _session == null) return;
                PollNowPlaying();
            });
        }
        catch { }
    }

    private void RefreshClock()
    {
        if (_session == null || _forceClose) return;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = null;
        GlobalSystemMediaTransportControlsSessionTimelineProperties? tl = null;
        try
        {
            info = _session.GetPlaybackInfo();
            var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var rate = info.PlaybackRate is > 0 and var r ? r : 1.0;
            var now = Environment.TickCount64;
            var transition = _lastPlaying != playing;
            // Paused used to force GetTimeline every call (poll + events), creating
            // SMTC wrappers that later finalize on an MTA thread and AV.
            var needTl = _forceTimeline || transition
                || (playing && (!_clock.IsPlaying || now - _lastTlMs >= 400));
            _forceTimeline = false;
            if (needTl)
            {
                tl = _session.GetTimelineProperties();
                _lastTlMs = now;
                _clock.Apply(tl.Position, playing, rate);
                var dur = tl.EndTime - tl.StartTime;
                _trackDuration = dur >= TimeSpan.FromSeconds(12) ? dur : null;
            }
            _clockFails = 0;
            if (_lastPlaying != playing)
            {
                _lastPlaying = playing;
                RunLog.Write(playing ? "play" : "pause pos=" + (int)_clock.Position.TotalSeconds);
            }
            if (playing)
            {
                if (!_syncTimer.IsEnabled) _syncTimer.Start();
                SetPollInterval(playing: true);
            }
            else
            {
                if (_syncTimer.IsEnabled) _syncTimer.Stop();
                SetPollInterval(playing: false);
                SyncLyrics();
            }
        }
        catch
        {
            if (++_clockFails >= 5)
            {
                _clockFails = 0;
                _clock.Freeze();
                RunLog.Write("clock-freeze smtc-fail");
            }
        }
        finally
        {
            HoldSmtc(ref _heldPlayback, info);
            if (tl != null)
                HoldSmtc(ref _heldTimeline, tl);
            GC.KeepAlive(_session);
        }
    }

    private static void HoldSmtc<T>(ref T? slot, T? next) where T : class
    {
        var prev = slot;
        slot = next;
        if (!ReferenceEquals(prev, next))
            WinRtLifetime.Suppress(prev);
    }

    private void SetPollInterval(bool playing)
    {
        var want = playing ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(15);
        if (_pollTimer.Interval == want) return;
        _pollTimer.Interval = want;
        if (!_pollTimer.IsEnabled) _pollTimer.Start();
        RunLog.Write(playing ? "poll-2s" : "poll-15s");
    }

    private void DropHeldSmtc()
    {
        WinRtLifetime.Suppress(_heldPlayback);
        WinRtLifetime.Suppress(_heldTimeline);
        _heldPlayback = null;
        _heldTimeline = null;
        _trackDuration = null;
    }

    private void ScheduleAlbumArt()
    {
        _artTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _artTimer.Tick -= OnArtTimer;
        _artTimer.Tick += OnArtTimer;
        _artTimer.Stop();
        _artTimer.Start();
    }

    private async void OnArtTimer(object? sender, EventArgs e)
    {
        _artTimer?.Stop();
        if (_forceClose || _session == null) return;
        if (_artBusy)
        {
            _artTimer?.Start();
            return;
        }
        _artBusy = true;
        RunLog.Write("art-begin");
        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try
        {
            props = await _session.TryGetMediaPropertiesAsync();
            if (_forceClose || props == null)
            {
                RunLog.Write("art-skip");
                return;
            }
            await LoadAlbumArt(props);
            RunLog.Write("art-done");
        }
        catch (Exception ex)
        {
            _artMissing = true;
            RunLog.Write("art-ex " + ex.GetType().Name);
        }
        finally
        {
            _artBusy = false;
            WinRtLifetime.Suppress(props);
        }
    }

    private async Task LoadAlbumArt(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        IRandomAccessStreamReference? thumb = null;
        try
        {
            thumb = props.Thumbnail;
            if (thumb == null)
            {
                _albumArt = null;
                _artMissing = true;
                AlbumArt.Source = null;
                _fullscreen?.SetAlbumArt(null);
                RunLog.Write("art-none");
                return;
            }
            _artMissing = false;
            var artGen = ++_artGen;
            RunLog.Write("art-open");
            var stream = await thumb.OpenReadAsync();
            using var ms = new MemoryStream();
            try
            {
                // AsStreamForRead owns and Closes the WinRT stream. Do not Dispose it again.
                using (var inp = stream.AsStreamForRead())
                    await inp.CopyToAsync(ms);
            }
            finally { WinRtLifetime.Suppress(stream); }
            RunLog.Write("art-copy n=" + ms.Length);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 512;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            if (_forceClose || !IsLoaded || artGen != _artGen) return;
            _albumArt = bmp;
            // Image.Source on an AllowsTransparency window has been taking
            // down wpfgfx ~2s after art-done (no dump). Keep the bitmap for
            // the opaque fullscreen view only.
            if (WindowGuard.CanTouch(_fullscreen))
                _fullscreen!.SetAlbumArt(bmp);
            RunLog.Write("art-ui-skip-layered");
        }
        catch (Exception ex)
        {
            _artMissing = true;
            RunLog.Write("art-load-ex " + ex.GetType().Name);
        }
        finally { WinRtLifetime.Suppress(thumb); }
    }

    private void SafeUpdateLyrics(string current, string? translated, string? next,
        List<KaraokeWordTiming>? words, double elapsed)
    {
        try
        {
            if (!_loggedFirstPaint && !string.IsNullOrEmpty(current))
            {
                _loggedFirstPaint = true;
                RunLog.Write("paint-first n=" + current.Length
                    + " words=" + (words?.Count ?? 0));
            }
            if (WindowGuard.CanTouch(_overlay))
                _overlay!.UpdateLyrics(current, translated, next, words, elapsed);
            if (WindowGuard.CanTouch(_fullscreen))
                _fullscreen!.UpdateLyrics(current, translated, next, words, elapsed);
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    private void SyncLyrics()
    {
        if (_lines == null || _lines.Count == 0)
        {
            if (_paintIdx != -2)
            {
                _paintIdx = -2;
                SafeUpdateLyrics("", null, null, null, 0);
            }
            return;
        }

        var pos = _clock.Position;
        var timeText = $"{(int)pos.TotalMinutes}:{pos.Seconds:D2}";
        if (TxtTime.Text != timeText)
            TxtTime.Text = timeText;

        var lyricPos = LyricClockPos();
        var lines = ShownLines();

        int idx = -1;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (LyricsService.LineIsActive(lines, i, lyricPos, _lineShifts, _lineHolds))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            int reached = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (LyricsService.TimeOf(lines[i], _lineShifts) > lyricPos) continue;
                if (string.IsNullOrWhiteSpace(lines[i].Text)) continue;
                if (LyricsService.IsAttachedTranslationLine(lines, i)) continue;
                reached = i;
                break;
            }
            var upcoming = NextLyricText(lines, reached < 0 ? -1 : reached);
            var gapSig = $"-1|{reached}|{upcoming}";
            if (_paintIdx == -1 && _paintSig == gapSig) return;
            _paintIdx = -1;
            _paintSig = gapSig;
            var lastSung = reached >= 0 ? ToDisplay(lines[reached].Text) : "";
            TxtCurrent.Text = "";
            TxtTrans.Text = "";
            TxtPrev.Text = lastSung ?? "";
            TxtNext.Text = upcoming ?? "";
            TxtRomaji.Text = "";
            SafeUpdateLyrics("", null, upcoming, null, 0);
            return;
        }

        {
            var nextIdx = LyricsService.NextSungIndex(lines, idx);
            var sig = $"{idx}|{lines[idx].Text}|{lines[idx].TranslatedText}|{nextIdx}|{_settings.HideTranslation}";
            var elapsed = (lyricPos - LyricsService.TimeOf(lines[idx], _lineShifts)).TotalMilliseconds;
            if (elapsed < 0) elapsed = 0;
            if (idx == _paintIdx && sig == _paintSig)
            {
                if (!_clock.IsPlaying) return;
                // Hold-to-next / 14.5s gap must keep the text, not keep redrawing.
                if (KaraokeWordTiming.OverlayFrozen(_paintWords, _paintElapsed)
                    && KaraokeWordTiming.OverlayFrozen(_paintWords, elapsed))
                    return;
                var phase = KaraokePhase(_paintWords, elapsed);
                if (phase == _paintPhase && elapsed - _paintElapsed < 180)
                    return;
                _paintPhase = phase;
                _paintElapsed = elapsed;
                SafeUpdateLyrics(_paintText,
                    _settings.HideTranslation ? null : _paintTrans,
                    _paintNext, _paintWords, elapsed);
                return;
            }

            var text = ToDisplay(lines[idx].Text);
            var trans = ToDisplay(LyricsService.ResolvedTranslation(lines, lines[idx]));
            var prevIdx = LyricsService.PrevSungIndex(lines, idx);
            var prev = prevIdx >= 0 ? ToDisplay(lines[prevIdx].Text) : "";
            var next = nextIdx >= 0 ? ToDisplay(lines[nextIdx].Text) : "";
            var words = KaraokeWordsForLine(lines, idx);
            _paintIdx = idx;
            _paintSig = sig;
            _paintText = text;
            _paintTrans = trans;
            _paintNext = string.IsNullOrEmpty(next) ? null : next;
            _paintWords = words;
            _paintElapsed = elapsed;
            _paintPhase = KaraokePhase(words, elapsed);

            TxtCurrent.Text = text;
            TxtTrans.Text = _settings.HideTranslation ? "" : (trans ?? "");
            TxtPrev.Text = prev;
            TxtNext.Text = next;
            ApplyLineFonts(text, trans, prev, next);

            SafeUpdateLyrics(text,
                _settings.HideTranslation ? null : trans,
                _paintNext,
                words,
                elapsed);

            if (_settings.ShowRomaji && LyricFonts.HasKana(text))
                UpdateRomaji(text);
            else
                TxtRomaji.Text = "";
        }
    }

    private List<LrcLine> ShownLines()
        => _shown ??= LyricsService.ApplyEdits(_lines, CurrentTiming());

    private void SetLines(List<LrcLine> lines)
    {
        _lines = lines ?? [];
        InvalidateLyricCache();
    }

    private void InvalidateLyricCache()
    {
        _shown = null;
        _paintIdx = int.MinValue;
        _paintSig = "";
        _paintElapsed = double.NaN;
        _paintPhase = int.MinValue;
        ResetKaraokeCache();
    }

    private string? NextLyricText(IReadOnlyList<LrcLine> lines, int afterIdx)
    {
        var i = LyricsService.NextSungIndex(lines, afterIdx);
        return i >= 0 ? ToDisplay(lines[i].Text) : null;
    }

    private static int KaraokePhase(List<KaraokeWordTiming>? words, double elapsed)
    {
        if (words == null || words.Count == 0) return -1;
        var n = Math.Min(words.Count, KaraokeWordTiming.MaxOverlayWords);
        for (int i = 0; i < n; i++)
        {
            var w = words[i];
            var start = w.StartMs;
            var end = start + Math.Max(0, w.DurationMs);
            if (elapsed < start) return i * 2;
            if (elapsed < end) return i * 2 + 1;
        }
        return n * 2;
    }

    private TimeSpan LyricClockPos()
    {
        var rate = _trackRate <= 0 || double.IsNaN(_trackRate) ? 1.0 : _trackRate;
        var ms = (_clock.Position.TotalMilliseconds + _settings.GlobalOffsetMs + _trackOffsetMs) * rate;
        if (ms < 0) ms = 0;
        return TimeSpan.FromMilliseconds(ms);
    }

    private void UpdateRomaji(string text)
    {
        if (text == _lastRomajiInput)
        {
            TxtRomaji.Text = _lastRomajiOutput;
            return;
        }

        if (text == _romajiInFlight) return;
        _romajiInFlight = text;
        var input = text;
        _ = Task.Run(async () =>
        {
            try
            {
                var q = Uri.EscapeDataString(input);
                var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=ja&tl=en&dt=rm&q={q}";
                using var resp = await _romajiHttp.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    await Dispatcher.BeginInvoke(() =>
                    {
                        if (_romajiInFlight != input) return;
                        _lastRomajiInput = input;
                        _lastRomajiOutput = "";
                        _romajiInFlight = "";
                    });
                    return;
                }
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
                        if (_forceClose) return;
                        _lastRomajiInput = input;
                        _lastRomajiOutput = romaji;
                        _romajiInFlight = "";
                        if (TxtCurrent.Text == input)
                            TxtRomaji.Text = romaji;
                    });
                }
            }
            catch { }
        });
    }

    private void ApplyLineFonts(string text, string? trans, string prev, string next)
    {
        var custom = _settings.FontFamily ?? "";
        TxtCurrent.SettingsFont = custom;
        TxtTrans.SettingsFont = custom;
        TxtCurrent.FontSize = Math.Clamp(
            LyricFonts.LineSize(text, current: true) * _settings.OverlayOriginalScale, 10, 48);
        TxtTrans.FontSize = Math.Clamp(
            LyricFonts.LineSize(trans, current: false) * _settings.OverlayTranslationScale, 10, 48);
        TxtPrev.FontFamily = LyricFonts.HasKana(prev) ? LyricFonts.Japanese : LyricFonts.FromSettings(custom);
        TxtNext.FontFamily = LyricFonts.HasKana(next) ? LyricFonts.Japanese : LyricFonts.FromSettings(custom);
    }

    private string ToDisplay(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!_settings.ForceTraditional) return text;
        if (_s2t.TryGetValue(text, out var hit)) return hit;
        var converted = S2TConverter.Convert(text);
        if (_s2t.Count > 4000) _s2t.Clear();
        _s2t[text] = converted;
        return converted;
    }

    private void ResetKaraokeCache()
    {
        _karaokeLineIdx = -1;
        _karaokeWords = null;
        _karaokeSrc = null;
    }

    private List<KaraokeWordTiming>? KaraokeWordsForLine(IReadOnlyList<LrcLine> lines, int idx)
    {
        var src = lines[idx].WordTimings;
        if (idx == _karaokeLineIdx && ReferenceEquals(src, _karaokeSrc))
            return _karaokeWords;
        _karaokeLineIdx = idx;
        _karaokeSrc = src;
        _karaokeWords = ToDisplay(src);
        return _karaokeWords;
    }

    private List<KaraokeWordTiming>? ToDisplay(List<KaraokeWordTiming>? words)
    {
        if (words == null || words.Count == 0 || !_settings.ForceTraditional) return words;
        var converted = new List<KaraokeWordTiming>(words.Count);
        foreach (var w in words)
            converted.Add(w with { Text = S2TConverter.Convert(w.Text) });
        return converted;
    }

    private TimeSpan? GetTrackDuration() => _trackDuration;

    // --- UI handlers ---

    private void Window_Drag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => HideToOverlay();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            HideToOverlay();
            return;
        }
        StopSmtc();
        FlushOffsetSave();
        _offsetHold?.Dispose();
        base.OnClosing(e);
    }

    private void HideToOverlay()
    {
        FlushOffsetSave();
        if (_fullscreen is not { IsVisible: true })
            ShowOverlay();
        Hide();
        ShellWindow.Unpin(this);
        RunLog.Write("hide-to-overlay");
    }

    public void RestoreFromOverlay()
    {
        ShellWindow.Pin(this);
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void QuitApp(string reason = "quit")
    {
        if (_forceClose) return;
        RunLog.Write("quit " + reason);
        _forceClose = true;
        try { _watchdog?.Dispose(); } catch { }
        _watchdog = null;
        _artTimer?.Stop();
        _titleHold?.Stop();
        StopSmtc();
        _offsetHold?.Dispose();
        _offsetHold = null;
        FlushOffsetSave();
        Application.Current.Shutdown();
    }

    private void StopSmtc()
    {
        _pollTimer.Stop();
        _syncTimer.Stop();
        if (_mgr != null)
        {
            try { _mgr.CurrentSessionChanged -= OnCurrentSessionChanged; }
            catch { }
        }
        BindSession(null);
        WinRtLifetime.Suppress(_mgr);
        _mgr = null;
    }

    private void OpenSettings() => Settings_Click(this, new RoutedEventArgs());

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new SettingsWindow(_settings);
            if (IsVisible) win.Owner = this;
            win.Topmost = true;
            win.Changed += ApplySettingsLive;
            try { win.ShowDialog(); }
            finally { win.Changed -= ApplySettingsLive; }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    private void ApplySettingsLive()
    {
        try
        {
            _paintIdx = int.MinValue;
            _paintSig = "";
            ApplySettings();
            ApplyTradButton();
            ApplyFontButton();
            ApplyLineFonts(TxtCurrent.Text, TxtTrans.Text, TxtPrev.Text, TxtNext.Text);
            _overlay?.RefreshAppearance();
            _fullscreen?.RefreshAppearance();
            SyncLyrics();
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    private async void PickSong_Click(object sender, RoutedEventArgs e)
        => await PickSongAsync(this);

    private bool _picking;

    private async Task PickSongAsync(Window? owner)
    {
        if (_picking) return;
        _picking = true;
        var win = new PickSongWindow(_lyrics, _lastTitle, _lastArtist, GetTrackDuration());
        if (IsVisible)
            win.Owner = this;
        _pick = win;
        LyricCandidate? chosen = null;
        var remember = false;
        var searchTitle = "";
        var searchArtist = "";
        try
        {
            RunLog.Write("pick-open");
            var closed = new TaskCompletionSource<bool>();
            win.Closed += (_, _) =>
            {
                if (_pick == win) _pick = null;
                closed.TrySetResult(true);
            };
            win.Show();
            await closed.Task;
            chosen = win.Chosen;
            remember = win.Remember;
            searchTitle = win.SearchTitle;
            searchArtist = win.SearchArtist;
            RunLog.Write("pick-close ok=" + (chosen != null));
        }
        catch (Exception ex)
        {
            RunLog.Write("pick-ex " + ex.GetType().Name + " " + ex.Message);
            ErrorLog.Write(ex);
        }
        finally
        {
            _picking = false;
        }
        if (chosen == null) return;
        TxtCurrent.Text = "loading...";
        if (remember)
        {
            LyricChoiceStore.Set(_lastTitle, _lastArtist, chosen.Key);
            LyricChoiceStore.Set(searchTitle, searchArtist, chosen.Key);
            LyricChoiceStore.Set(chosen.Title, chosen.Artist, chosen.Key);
        }
        var lines = await _lyrics.FetchAsync(chosen);
        if (lines == null || lines.Count == 0)
            lines = await _lyrics.SearchAsync(_lastTitle, _lastArtist, GetTrackDuration());
        if (lines == null) return;
        if (lines.Count > 0)
        {
            SetLines(lines);
            TxtCurrent.Text = "♪";
            TxtStatus.Text = $"歌詞：{chosen.Title} · {chosen.Source}";
        }
        else
        {
            TxtCurrent.Text = "no lyrics found";
            TxtStatus.Text = remember ? "已記住，但呢首暫時撈唔到歌詞" : "呢首冇歌詞";
        }
    }

    private async void SavedSongs_Click(object sender, RoutedEventArgs e)
    {
        var win = new SavedSongsWindow(_lyrics) { Owner = this };
        win.ShowDialog();
        if (!win.Dirty || string.IsNullOrEmpty(_lastTitle)) return;
        var result = await _lyrics.SearchAsync(_lastTitle, _lastArtist, GetTrackDuration());
        if (result == null) return;
        if (result.Count > 0)
        {
            SetLines(result);
            TxtCurrent.Text = "♪";
        }
    }

    private void CycleFont_Click(object sender, RoutedEventArgs e)
    {
        _settings.FontFamily = LyricFonts.CycleChinese(_settings.FontFamily);
        _settings.Save();
        ApplyFontButton();
        FontFamily = LyricFonts.FromSettings(_settings.FontFamily);
        ApplyLineFonts(TxtCurrent.Text, TxtTrans.Text, TxtPrev.Text, TxtNext.Text);
        _overlay?.RefreshFonts();
        _fullscreen?.RefreshFonts();
    }

    private void ApplyFontButton()
    {
        BtnFont.Content = LyricFonts.CurrentLabel(_settings.FontFamily);
    }

    private void ToggleTraditional_Click(object sender, RoutedEventArgs e)
    {
        _settings.ForceTraditional = !_settings.ForceTraditional;
        _settings.Save();
        ApplyTradButton();
        _overlay?.RefreshTradButton();
        _fullscreen?.RefreshTradButton();
        _paintIdx = int.MinValue;
        _paintSig = "";
        ResetKaraokeCache();
        SyncLyrics();
    }

    private void ApplyTradButton()
    {
        BtnTrad.Foreground = new System.Windows.Media.SolidColorBrush(
            _settings.ForceTraditional
                ? System.Windows.Media.Color.FromRgb(0x00, 0xd4, 0xff)
                : System.Windows.Media.Color.FromRgb(0xa0, 0xb0, 0xc0));
    }

    private void OnLyricsLockToggled(bool locked)
    {
        _lyricsLocked = locked;
        RunLog.Write(locked ? "lyrics-lock" : "lyrics-unlock");
        if (locked || string.IsNullOrEmpty(_smtcTitle)) return;
        _ = CommitTrackAsync(_smtcTitle, _smtcArtist, search: true);
    }

    private void ShowOverlay()
    {
        if (_overlay != null)
        {
            if (!_overlay.IsVisible) _overlay.Show();
            return;
        }
        _overlay = new OverlayWindow(_settings);
        _overlay.Opacity = _settings.OverlayOpacity / 100.0;
        _overlay.SetTrackInfo(ToDisplay(_lastTitle), ToDisplay(TxtArtist.Text));
        _overlay.TraditionalToggled += ApplyTradButton;
        _overlay.OffsetNudged += NudgeOffset;
        _overlay.PickSongRequested += () => _ = PickSongAsync(_overlay);
        _overlay.FullscreenRequested += ShowFullscreen;
        _overlay.TimingEditorRequested += OpenTimingEditor;
        _overlay.LockToggled += OnLyricsLockToggled;
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
        RefreshOffsetUi();
    }

    private void ShowFullscreen()
    {
        if (_fullscreen is { IsVisible: true })
        {
            _fullscreen.Activate();
            return;
        }
        if (_overlay is { IsVisible: true })
        {
            _overlayHiddenForFullscreen = true;
            _overlay.Hide();
        }
        else _overlayHiddenForFullscreen = false;

        _fullscreen = new FullscreenWindow(_settings);
        _fullscreen.TraditionalToggled += ApplyTradButton;
        _fullscreen.PickSongRequested += () => _ = PickSongAsync(_fullscreen);
        _fullscreen.SetTrackInfo(ToDisplay(_lastTitle), ToDisplay(TxtArtist.Text));
        _fullscreen.SetAlbumArt(_albumArt);
        if (_albumArt == null && !_artMissing)
        {
            _artDeferred = false;
            ScheduleAlbumArt();
        }
        _fullscreen.Closed += (_, _) =>
        {
            _fullscreen = null;
            if (_overlayHiddenForFullscreen)
            {
                _overlayHiddenForFullscreen = false;
                ShowOverlay();
            }
        };
        _fullscreen.Show();
        SyncLyrics();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ShowFullscreen();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            if (_fullscreen is { IsVisible: true }) _fullscreen.Close();
            else ShowFullscreen();
        }
    }

    private void LoadTrackOffset()
    {
        FlushOffsetSave();
        var t = LyricOffsetStore.GetTiming(_lastTitle, _lastArtist);
        ApplyTimingState(t);
        RefreshOffsetUi();
    }

    private void OpenTimingEditor()
    {
        if (_timingEditor != null)
        {
            _timingEditor.Activate();
            return;
        }
        try
        {
            _timingEditor = new TimingEditorWindow(
                () => _lastTitle,
                () => _lastArtist,
                () => _clock.Position,
                ShownLines,
                () => _lines,
                LyricClockPos,
                () => _settings.GlobalOffsetMs,
                () => CurrentTiming(),
                ApplyTiming);
            if (IsVisible) _timingEditor.Owner = this;
            _timingEditor.Closed += (_, _) => _timingEditor = null;
            RunLog.Write("timing-editor-open");
            _timingEditor.Show();
        }
        catch (Exception ex)
        {
            _timingEditor = null;
            ErrorLog.Write(ex);
        }
    }

    private TrackTiming CurrentTiming()
        => new TrackTiming(
            _trackOffsetMs,
            _trackRate,
            _lineShifts.Count == 0 ? null : new Dictionary<string, int>(_lineShifts),
            _lineHolds.Count == 0 ? null : new Dictionary<string, int>(_lineHolds),
            _lineTexts.Count == 0 ? null : new Dictionary<string, string>(_lineTexts),
            _addedLines.Count == 0 ? null : [.. _addedLines],
            _lineTrans.Count == 0 ? null : new Dictionary<string, string>(_lineTrans));

    private void ApplyTimingState(TrackTiming timing)
    {
        timing = timing.Clamped();
        _trackOffsetMs = timing.OffsetMs;
        _trackRate = timing.Rate;
        _lineShifts = timing.Lines is { Count: > 0 } ? new Dictionary<string, int>(timing.Lines) : new();
        _lineHolds = timing.Holds is { Count: > 0 } ? new Dictionary<string, int>(timing.Holds) : new();
        _lineTexts = timing.Texts is { Count: > 0 } ? new Dictionary<string, string>(timing.Texts) : new();
        _addedLines = timing.Added is { Count: > 0 } ? [.. timing.Added] : [];
        _lineTrans = timing.Trans is { Count: > 0 } ? new Dictionary<string, string>(timing.Trans) : new();
        InvalidateLyricCache();
    }

    private void ApplyTiming(TrackTiming timing)
    {
        ApplyTimingState(timing);
        ScheduleOffsetSave();
        RefreshOffsetUi();
        SyncLyrics();
        RunLog.Trace("timing-apply off=" + timing.OffsetMs
            + " rate=" + timing.Rate.ToString("0.000")
            + " shifts=" + (timing.Lines?.Count ?? 0)
            + " holds=" + (timing.Holds?.Count ?? 0));
    }

    private void TimingEditor_Click(object sender, RoutedEventArgs e) => OpenTimingEditor();

    private void OffsetEarlier_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _offsetHold?.Down(1, sender as IInputElement);
    }

    private void OffsetLater_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _offsetHold?.Down(-1, sender as IInputElement);
    }

    private void OffsetHold_Up(object sender, MouseEventArgs e)
    {
        _offsetHold?.Up();
        FlushOffsetSave();
    }

    private void OffsetReset_Click(object sender, RoutedEventArgs e)
        => NudgeOffset(int.MinValue);

    private void NudgeOffset(int delta)
    {
        if (string.IsNullOrEmpty(_lastTitle)) return;
        if (delta == int.MinValue)
        {
            _trackOffsetMs = 0;
            _trackRate = 1.0;
            _lineShifts.Clear();
        }
        else
            _trackOffsetMs = Math.Clamp(_trackOffsetMs + delta, LyricOffsetStore.MinMs, LyricOffsetStore.MaxMs);
        RefreshOffsetUi();
        SyncLyrics();
        ScheduleOffsetSave();
    }

    private void ScheduleOffsetSave()
    {
        _offsetSave ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _offsetSave.Tick -= SaveOffsetNow;
        _offsetSave.Tick += SaveOffsetNow;
        _offsetSave.Stop();
        _offsetSave.Start();
    }

    private void SaveOffsetNow(object? sender, EventArgs e)
    {
        _offsetSave?.Stop();
        if (!string.IsNullOrEmpty(_lastTitle))
            LyricOffsetStore.SetTiming(_lastTitle, _lastArtist, CurrentTiming());
    }

    private void FlushOffsetSave()
    {
        if (_offsetSave is { IsEnabled: true })
            SaveOffsetNow(null, EventArgs.Empty);
    }

    private void RefreshOffsetUi()
    {
        var label = LyricOffsetStore.FormatLabel(_trackOffsetMs, _trackRate);
        BtnOffset.Content = label;
        var custom = _trackOffsetMs != 0
            || Math.Abs(_trackRate - 1.0) >= 0.0005
            || _lineShifts.Count > 0
            || _lineHolds.Count > 0
            || _lineTexts.Count > 0
            || _lineTrans.Count > 0
            || _addedLines.Count > 0;
        BtnOffset.Foreground = new System.Windows.Media.SolidColorBrush(
            custom
                ? System.Windows.Media.Color.FromRgb(0x00, 0xd4, 0xff)
                : System.Windows.Media.Color.FromRgb(0xa0, 0xb0, 0xc0));
        _overlay?.SetOffsetLabel(_trackOffsetMs, _trackRate, custom);
    }

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay == null || !_overlay.IsVisible)
            ShowOverlay();
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
                var shown = ShownLines();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[ti:{_lastTitle}]");
                sb.AppendLine("[by:desktop-lyric]");
                sb.AppendLine();
                sb.Append(LyricsService.FormatShownLrc(shown, CurrentTiming()));
                File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                MessageBox.Show($"saved {shown.Count} lines", "export");
            }
            catch (Exception ex)
            {
                MessageBox.Show("export failed: " + ex.Message, "error");
            }
        }
    }
}
