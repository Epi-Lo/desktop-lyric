using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopLyric.Services;
using Microsoft.Win32;

namespace DesktopLyric.Views;

public partial class TimingEditorWindow : Window
{
    private readonly Func<string> _title;
    private readonly Func<string> _artist;
    private readonly Func<TimeSpan> _playPos;
    private readonly Func<IReadOnlyList<LrcLine>> _lines;
    private readonly Func<IReadOnlyList<LrcLine>> _source;
    private readonly Func<TimeSpan> _lyricPos;
    private readonly Func<int> _globalMs;
    private readonly Func<TrackTiming> _getTiming;
    private readonly Action<TrackTiming> _apply;
    private readonly DispatcherTimer _timer;
    private bool _ready;
    private bool _dragging;
    private bool _syncing;
    private string _listSig = "";
    private string? _followKey;
    private HoldRepeat? _lineHold;
    private HoldRepeat? _rateHold;
    private HoldRepeat? _stayHold;
    private LineRow? _dragRow;
    private Point _dragStart;
    private long _lastFollowScrollMs;

    public TimingEditorWindow(
        Func<string> title,
        Func<string> artist,
        Func<TimeSpan> playPos,
        Func<IReadOnlyList<LrcLine>> lines,
        Func<IReadOnlyList<LrcLine>> source,
        Func<TimeSpan> lyricPos,
        Func<int> globalMs,
        Func<TrackTiming> getTiming,
        Action<TrackTiming> apply)
    {
        InitializeComponent();
        _title = title;
        _artist = artist;
        _playPos = playPos;
        _lines = lines;
        _source = source;
        _lyricPos = lyricPos;
        _globalMs = globalMs;
        _getTiming = getTiming;
        _apply = apply;
        LoadFromTiming(_getTiming());
        RebuildLines();
        SldOffset.ValueChanged += Slider_Changed;
        _lineHold = new HoldRepeat(NudgeSelectedLine);
        _rateHold = new HoldRepeat(delta => NudgeRate(delta >= 0 ? 1 : -1));
        _stayHold = new HoldRepeat(delta => NudgeStay(delta >= 0
            ? LyricOffsetStore.HoldStepMs
            : -LyricOffsetStore.HoldStepMs));
        LstLines.SelectionChanged += (_, _) => FillEditBox();
        _ready = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();
        RefreshLive();
        RunLog.Write("timing-editor");
    }

    private void LoadFromTiming(TrackTiming t)
    {
        _syncing = true;
        SldOffset.Value = Math.Clamp(t.OffsetMs, SldOffset.Minimum, SldOffset.Maximum);
        _syncing = false;
        RefreshLabels();
    }

    private void RebuildLines()
    {
        var lines = _lines() ?? [];
        var t = _getTiming();
        var shifts = t.Lines;
        var shiftSum = 0;
        if (shifts != null)
            foreach (var kv in shifts) shiftSum += kv.Value + kv.Key.Length;
        if (t.Holds != null)
            foreach (var kv in t.Holds) shiftSum += kv.Value;
        if (t.Texts != null)
            foreach (var kv in t.Texts) shiftSum += kv.Value.Length;
        if (t.Trans != null)
            foreach (var kv in t.Trans) shiftSum += kv.Value.Length;
        shiftSum += t.Added?.Count ?? 0;
        var sig = $"{lines.Count}|{shifts?.Count ?? 0}|{t.Holds?.Count ?? 0}|{t.Texts?.Count ?? 0}|{t.Trans?.Count ?? 0}|{shiftSum}|{lines.FirstOrDefault()?.Text}|{lines.LastOrDefault()?.Text}";
        if (sig == _listSig && LstLines.Items.Count > 0)
        {
            FillEditBox();
            return;
        }
        if (LiveLocked) return;
        _listSig = sig;
        _followKey = null;
        var keepKey = (LstLines.SelectedItem as LineRow)?.Key;
        var rows = new List<LineRow>();
        LineRow? reselect = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            var key = LyricsService.LineKey(line);
            var shown = LyricsService.TimeOf(line, shifts);
            var extra = 0;
            shifts?.TryGetValue(key, out extra);
            var stay = 0;
            t.Holds?.TryGetValue(key, out stay);
            var mark = extra == 0 ? "" : $"  {LyricOffsetStore.Format(extra)}";
            if (stay != 0) mark += $"  停留{LyricOffsetStore.FormatHold(stay)}";
            if (LyricsService.IsAddedKey(key)) mark += "  +";
            else if (t.Texts != null && t.Texts.ContainsKey(key)) mark += "  改";
            var row = new LineRow(line, key,
                $"[{(int)shown.TotalMinutes}:{shown.Seconds:D2}.{shown.Milliseconds / 10:D2}]{mark}  {line.Text}",
                LyricsService.ResolvedTranslation(lines, line) ?? "");
            rows.Add(row);
            if (keepKey != null && key == keepKey) reselect = row;
        }
        LstLines.ItemsSource = rows;
        RunLog.Trace("timing-rebuild n=" + rows.Count);
        if (reselect != null) LstLines.SelectedItem = reselect;
        RefreshLineLabel();
        FillEditBox();
    }

    private bool LiveLocked =>
        _dragging || _dragRow != null
        || _lineHold?.IsHeld == true
        || _rateHold?.IsHeld == true
        || _stayHold?.IsHeld == true;

    private void RefreshLive()
    {
        try
        {
            var track = $"{_title()}  ·  {_artist()}";
            if (TxtTrack.Text != track) TxtTrack.Text = track;
            var play = _playPos();
            var lyric = _lyricPos();
            var clock = $"播放 {Fmt(play)}   →   歌詞 {Fmt(lyric)}";
            if (TxtClock.Text != clock) TxtClock.Text = clock;
            if (LiveLocked) return;
            if (ChkFollow?.IsChecked != false)
                HighlightCurrent(lyric);
            var t = _getTiming();
            if (Math.Abs(SldOffset.Value - t.OffsetMs) > 1)
                LoadFromTiming(t);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            RunLog.Write("timing-live-ex " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private void Lines_UserPick(object sender, MouseButtonEventArgs e)
    {
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        _dragRow = RowFromSource(e.OriginalSource);
        _dragStart = e.GetPosition(LstLines);
    }

    private void Lines_MouseUp(object sender, MouseButtonEventArgs e) => _dragRow = null;

    private void Lines_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow == null) return;
        var delta = e.GetPosition(LstLines) - _dragStart;
        if (Math.Abs(delta.X) < 8 && Math.Abs(delta.Y) < 8) return;
        var key = _dragRow.Key;
        _dragRow = null;
        try { DragDrop.DoDragDrop(LstLines, key, DragDropEffects.Move); }
        catch { }
    }

    private void Lines_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(string)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Lines_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(string)) is not string srcKey) return;
        var dest = RowFromSource(e.OriginalSource);
        if (dest == null || dest.Key == srcKey) return;
        var lines = ShownSung();
        var from = lines.FindIndex(l => LyricsService.LineKey(l) == srcKey);
        var destIdx = lines.FindIndex(l => LyricsService.LineKey(l) == dest.Key);
        if (from < 0 || destIdx < 0) return;
        var destInWithout = destIdx > from ? destIdx - 1 : destIdx;
        var lbi = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var after = lbi != null && e.GetPosition(lbi).Y > lbi.ActualHeight / 2;
        PlaceLine(lines, from, after ? destInWithout + 1 : destInWithout);
    }

    private static LineRow? RowFromSource(object? source)
    {
        var dep = source as DependencyObject;
        while (dep != null && dep is not ListBox)
        {
            if (dep is ListBoxItem { Content: LineRow row }) return row;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return null;
    }

    private static T? Ancestor<T>(DependencyObject? dep) where T : DependencyObject
    {
        while (dep != null)
        {
            if (dep is T match) return match;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return null;
    }

    private void HighlightCurrent(TimeSpan lyricPos)
    {
        var lines = _lines() ?? [];
        var timing = _getTiming();
        int idx = -1;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (LyricsService.LineIsActive(lines, i, lyricPos, timing.Lines, timing.Holds))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return;
        var wantKey = LyricsService.LineKey(lines[idx]);
        var itemIdx = -1;
        for (int i = 0; i < LstLines.Items.Count; i++)
        {
            if (LstLines.Items[i] is LineRow row && row.Key == wantKey)
            {
                itemIdx = i;
                break;
            }
        }
        if (itemIdx < 0) return;
        _followKey = wantKey;
        if (TxtFollow != null && LstLines.Items[itemIdx] is LineRow shown)
        {
            var n = LstLines.Items.Count;
            var label = $"跟 {itemIdx + 1}/{n}  {shown.Display}";
            if (TxtFollow.Text != label) TxtFollow.Text = label;
        }
        // Do not ScrollToVerticalOffset / SelectedIndex — ListBox scroll
        // native-crashes while the overlay is painting.
    }

    /// <summary>
    /// Logical-unit scroll (CanContentScroll). No SelectedIndex / ScrollIntoView /
    /// TransformToAncestor — those native-crash while playing.
    /// </summary>
    private void KeepRowInView(int index)
    {
        try
        {
            if (LiveLocked || ChkFollow?.IsChecked == false) return;
            var sv = FindScrollViewer(LstLines);
            if (sv == null || LstLines.Items.Count == 0) return;
            double dest;
            double slack;
            if (sv.CanContentScroll)
            {
                dest = Math.Max(0, index - sv.ViewportHeight * 0.35);
                slack = 0.8;
            }
            else
            {
                var avg = sv.ExtentHeight / Math.Max(1, LstLines.Items.Count);
                dest = Math.Max(0, avg * index - sv.ViewportHeight * 0.35);
                slack = Math.Max(12, avg * 0.4);
            }
            var maxOff = Math.Max(0, sv.ExtentHeight - sv.ViewportHeight);
            if (dest > maxOff) dest = maxOff;
            if (Math.Abs(dest - sv.VerticalOffset) < slack) return;
            var now = Environment.TickCount64;
            if (now - _lastFollowScrollMs < 350) return;
            _lastFollowScrollMs = now;
            sv.ScrollToVerticalOffset(dest);
            RunLog.Trace("follow-scroll i=" + index);
        }
        catch (Exception ex) { RunLog.Write("follow-scroll-ex " + ex.GetType().Name); }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var nested = FindScrollViewer(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static string Fmt(TimeSpan t)
        => $"{(int)t.TotalMinutes}:{t.Seconds:D2}.{t.Milliseconds / 10:D2}";

    private void RefreshLabels()
    {
        if (LblOffset == null || LblRate == null || SldOffset == null) return;
        LblOffset.Text = LyricOffsetStore.Format((int)SldOffset.Value);
        LblRate.Text = LyricOffsetStore.FormatRate(_getTiming().Rate);
        RefreshLineLabel();
    }

    private void RefreshLineLabel()
    {
        if (LblLine == null) return;
        var cyan = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xd4, 0xff));
        if (LstLines.SelectedItem is not LineRow row)
        {
            LblLine.Text = "±0.00s";
            if (LblStay != null) LblStay.Text = "0.00s";
            return;
        }
        var t = _getTiming();
        var extra = 0;
        t.Lines?.TryGetValue(row.Key, out extra);
        LblLine.Text = LyricOffsetStore.Format(extra);
        LblLine.Foreground = extra == 0 ? System.Windows.Media.Brushes.Gray : cyan;
        var stay = 0;
        t.Holds?.TryGetValue(row.Key, out stay);
        if (LblStay != null)
        {
            LblStay.Text = LyricOffsetStore.FormatHold(stay);
            LblStay.Foreground = stay == 0 ? System.Windows.Media.Brushes.Gray : cyan;
        }
    }

    private void FillEditBox()
    {
        if (TxtEdit == null) return;
        if (TxtEdit.IsKeyboardFocused || TxtTrans?.IsKeyboardFocused == true) return;
        if (LstLines.SelectedItem is LineRow row)
        {
            SetBox(TxtEdit, row.Line.Text);
            var trans = LyricsService.ResolvedTranslation(ShownSung(), row.Line)
                ?? row.Line.TranslatedText
                ?? row.Translation
                ?? "";
            SetBox(TxtTrans, trans);
        }
        else
        {
            SetBox(TxtEdit, "");
            SetBox(TxtTrans, "");
        }
    }

    private static void SetBox(TextBox? box, string value)
    {
        if (box == null) return;
        if (box.Text == value) return;
        box.Text = value;
    }

    private void Slider_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        Commit();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        RefreshLabels();
        if (_syncing || _dragging) return;
        Commit();
    }

    private void Commit()
    {
        if (!_ready || _syncing) return;
        var cur = _getTiming();
        var ms = (int)Math.Round(SldOffset.Value / 50.0) * 50;
        _apply(cur with { OffsetMs = ms });
    }

    private void NudgeRate(int dir)
    {
        var t = _getTiming();
        var step = LyricOffsetStore.RateStep;
        var rate = Math.Clamp(t.Rate + dir * step, LyricOffsetStore.RateMin, LyricOffsetStore.RateMax);
        rate = Math.Round(rate / step) * step;
        _apply(t with { Rate = rate });
        RefreshLabels();
    }

    private void RateFaster_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _rateHold?.Down(1, sender as IInputElement);
    }

    private void RateSlower_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _rateHold?.Down(-1, sender as IInputElement);
    }

    private void RateHold_Up(object sender, MouseEventArgs e) => _rateHold?.Up();

    private void NudgeSelectedLine(int delta)
    {
        if (LstLines.SelectedItem is not LineRow row) return;
        var t = _getTiming();
        var cur = 0;
        t.Lines?.TryGetValue(row.Key, out cur);
        _apply(t.WithLineShift(row.Key, cur + delta));
        RefreshLineLabel();
        if (!LiveLocked)
        {
            _listSig = "";
            RebuildLines();
        }
    }

    private void NudgeStay(int delta)
    {
        if (LstLines.SelectedItem is not LineRow row) return;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        var t = _getTiming();
        var cur = 0;
        t.Holds?.TryGetValue(row.Key, out cur);
        _apply(t.WithLineHold(row.Key, cur + delta));
        RefreshLineLabel();
        if (!LiveLocked)
        {
            _listSig = "";
            RebuildLines();
        }
    }

    private void StayLonger_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        _stayHold?.Down(1, sender as IInputElement);
    }

    private void StayShorter_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        _stayHold?.Down(-1, sender as IInputElement);
    }

    private void StayHold_Up(object sender, MouseEventArgs e)
    {
        _stayHold?.Up();
        FinishHoldRebuild();
    }

    private void LineEarlier_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        _lineHold?.Down(1, sender as IInputElement);
    }

    private void LineLater_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        _lineHold?.Down(-1, sender as IInputElement);
    }

    private void LineHold_Up(object sender, MouseEventArgs e)
    {
        _lineHold?.Up();
        FinishHoldRebuild();
    }

    private void FinishHoldRebuild()
    {
        _listSig = "";
        RebuildLines();
    }

    private void Align_Click(object sender, RoutedEventArgs e)
    {
        if (LstLines.SelectedItem is not LineRow row) return;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        var shift = (int)Math.Round(_lyricPos().TotalMilliseconds - row.Line.Time.TotalMilliseconds);
        shift = Math.Clamp(shift, LyricOffsetStore.MinMs, LyricOffsetStore.MaxMs);
        _apply(_getTiming().WithLineShift(row.Key, shift));
        _listSig = "";
        RebuildLines();
    }

    private void ResetLine_Click(object sender, RoutedEventArgs e)
    {
        if (LstLines.SelectedItem is not LineRow row) return;
        _apply(_getTiming().WithoutLine(row.Key));
        _listSig = "";
        RebuildLines();
    }

    private void Edit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyText_Click(sender, e);
        }
    }

    private void ApplyText_Click(object sender, RoutedEventArgs e)
    {
        if (LstLines.SelectedItem is not LineRow row) return;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        var text = (TxtEdit?.Text ?? "").Trim();
        var trans = EmptyToNull((TxtTrans?.Text ?? "").Trim());
        var t = _getTiming();
        if (LyricsService.IsAddedKey(row.Key))
        {
            var id = LyricsService.AddedId(row.Key);
            var cur = t.Added?.FirstOrDefault(a => a.Id == id) ?? new AddedLyric(
                (int)Math.Round(row.Line.Time.TotalMilliseconds), text, id, trans);
            if (string.IsNullOrWhiteSpace(text))
                _apply(t.WithoutLine(row.Key));
            else
                _apply(t.ReplaceAdded(id, cur with { Text = text, Trans = trans }));
        }
        else
        {
            var original = OriginalText(row);
            t = string.Equals(text, original, StringComparison.Ordinal)
                ? t.WithLineText(row.Key, null)
                : t.WithLineText(row.Key, text);
            // Empty box = keep the line's own translation (do not store "").
            t = t.WithLineTrans(row.Key, trans);
            _apply(t);
        }
        _listSig = "";
        RebuildLines();
    }

    private static string OriginalText(LineRow row)
    {
        var key = row.Key;
        var i = key.IndexOf('|');
        if (i < 0 || LyricsService.IsAddedKey(key)) return row.Line.Text;
        return key[(i + 1)..];
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        var text = (TxtEdit?.Text ?? "").Trim();
        var trans = EmptyToNull((TxtTrans?.Text ?? "").Trim());
        if (string.IsNullOrWhiteSpace(text) && LstLines.SelectedItem is LineRow row)
        {
            text = row.Line.Text;
            trans ??= EmptyToNull(row.Line.TranslatedText);
        }
        if (string.IsNullOrWhiteSpace(text)) text = "…";
        var at = (int)Math.Round(_lyricPos().TotalMilliseconds);
        if (at < 0) at = 0;
        var id = Guid.NewGuid().ToString("N")[..8];
        _apply(_getTiming().WithAdded(new AddedLyric(at, text, id, trans)));
        _listSig = "";
        RebuildLines();
        SelectKey(LyricsService.AddedKey(id));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (LstLines.SelectedItem is not LineRow row) return;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        if (LyricsService.IsAddedKey(row.Key))
            _apply(_getTiming().WithoutLine(row.Key));
        else
            _apply(_getTiming().WithLineText(row.Key, ""));
        _listSig = "";
        RebuildLines();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyAll();

    private void Paste_Click(object sender, RoutedEventArgs e) => PasteLines();

    private List<LrcLine> ShownSung()
        => (_lines() ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();

    private void DuplicateSelected()
    {
        if (LstLines.SelectedItem is not LineRow row) return;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        var lines = ShownSung();
        var idx = lines.FindIndex(l => LyricsService.LineKey(l) == row.Key);
        if (idx < 0) idx = 0;
        var t = _getTiming();
        var prev = LyricsService.TimeOf(row.Line, t.Lines);
        TimeSpan? next = idx + 1 < lines.Count ? LyricsService.TimeOf(lines[idx + 1], t.Lines) : null;
        var at = LyricsService.PlacementMs(prev, next, LyricsService.EffectiveMs(row.Line, t.Lines) + 1000);
        TryClipboard(LyricsService.FormatShownLrc([row.Line], t, headers: false));
        var nextTiming = LyricsService.DuplicateLine(t, row.Line, at);
        var newKey = nextTiming.Added is { Count: > 0 } added
            ? LyricsService.AddedKey(added[^1].Id)
            : row.Key;
        _apply(nextTiming);
        _listSig = "";
        RebuildLines();
        SelectKey(newKey);
    }

    private void MoveSelected(int delta)
    {
        if (LstLines.SelectedItem is not LineRow row) return;
        var lines = ShownSung();
        var from = lines.FindIndex(l => LyricsService.LineKey(l) == row.Key);
        if (from < 0) return;
        var insertAt = from + delta;
        if (insertAt < 0 || insertAt > lines.Count - 1) return;
        PlaceLine(lines, from, insertAt);
    }

    /// <param name="insertAt">Index in the list after <paramref name="from"/> is removed.</param>
    private void PlaceLine(List<LrcLine> lines, int from, int insertAt)
    {
        if (from < 0 || from >= lines.Count) return;
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        var moving = lines[from];
        var without = lines.Where((_, i) => i != from).ToList();
        insertAt = Math.Clamp(insertAt, 0, without.Count);
        if (without.Count == 0) return;
        var t = _getTiming();
        TimeSpan? prev = insertAt > 0 ? LyricsService.TimeOf(without[insertAt - 1], t.Lines) : null;
        TimeSpan? next = insertAt < without.Count ? LyricsService.TimeOf(without[insertAt], t.Lines) : null;
        var at = LyricsService.PlacementMs(prev, next, LyricsService.EffectiveMs(moving, t.Lines));
        _apply(LyricsService.SetEffectiveTime(t, moving, at));
        _listSig = "";
        RebuildLines();
        SelectKey(LyricsService.LineKey(moving));
    }

    private void CopyAll()
    {
        var t = _getTiming();
        TryClipboard(LyricsService.FormatShownLrc(ShownSung(), t));
    }

    private void CopySelectedText()
    {
        if (LstLines.SelectedItem is LineRow row)
            TryClipboard(LyricsService.FormatShownLrc([row.Line], _getTiming(), headers: false));
        else
            CopyAll();
    }

    private void PasteLines()
    {
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        string raw;
        try { raw = Clipboard.GetText() ?? ""; }
        catch { return; }
        var start = (int)Math.Round(_lyricPos().TotalMilliseconds);
        var parsed = LyricsService.ParseClipboardLyrics(raw, Math.Max(0, start));
        if (parsed.Count == 0) return;
        ApplyClipLyrics(parsed);
    }

    private void ApplyClipLyrics(List<LyricsService.ClipLyric> parsed)
    {
        var t = _getTiming();
        string? lastKey = null;
        foreach (var clip in parsed)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            t = t.WithAdded(new AddedLyric(clip.AtMs, clip.Text, id, clip.Trans));
            lastKey = LyricsService.AddedKey(id);
            if (clip.HoldMs != 0)
                t = t.WithLineHold(lastKey, clip.HoldMs);
        }
        _apply(t);
        _listSig = "";
        RebuildLines();
        if (lastKey != null) SelectKey(lastKey);
    }

    private void ReplaceClipLyrics(List<LyricsService.ClipLyric> parsed, int? offsetMs = null, double? rate = null)
    {
        var t = LyricsService.ReplaceShown(_getTiming(), _source() ?? [], parsed, offsetMs, rate);
        _apply(t);
        LoadFromTiming(t);
        _listSig = "";
        RebuildLines();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var t = LyricsService.RestoreLyrics(_getTiming());
        _apply(t);
        LoadFromTiming(t);
        _listSig = "";
        RebuildLines();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (ChkFollow != null) ChkFollow.IsChecked = false;
        var t = LyricsService.ClearShown(_getTiming(), _source() ?? []);
        _apply(t);
        _listSig = "";
        RebuildLines();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "LRC files|*.lrc|All files|*.*",
            FileName = $"{_title()}.lrc",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, LyricsService.FormatShownLrc(ShownSung(), _getTiming()),
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "LRC files|*.lrc;*.txt|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var raw = File.ReadAllText(dlg.FileName);
            var parsed = LyricsService.ParseClipboardLyrics(raw, 0);
            if (parsed.Count == 0) return;
            if (ChkFollow != null) ChkFollow.IsChecked = false;
            var (off, rate) = LyricsService.ParseTimingTags(raw);
            ReplaceClipLyrics(parsed, off, rate);
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    private static string? EmptyToNull(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void TryClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TxtEdit?.IsKeyboardFocused == true || TxtTrans?.IsKeyboardFocused == true) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (ctrl && shift && e.Key == Key.C)
        {
            e.Handled = true;
            CopyAll();
            return;
        }
        if (ctrl && e.Key == Key.C)
        {
            e.Handled = true;
            CopySelectedText();
            return;
        }
        if (ctrl && e.Key == Key.V)
        {
            e.Handled = true;
            PasteLines();
            return;
        }
        if (ctrl && e.Key == Key.D)
        {
            e.Handled = true;
            DuplicateSelected();
            return;
        }
        if (alt && e.Key == Key.Up)
        {
            e.Handled = true;
            MoveSelected(-1);
            return;
        }
        if (alt && e.Key == Key.Down)
        {
            e.Handled = true;
            MoveSelected(1);
        }
    }

    private void SelectKey(string key)
    {
        foreach (var item in LstLines.Items)
        {
            if (item is LineRow row && row.Key == key)
            {
                LstLines.SelectedItem = row;
                KeepRowInView(LstLines.SelectedIndex);
                FillEditBox();
                return;
            }
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _apply(TrackTiming.Default);
        LoadFromTiming(TrackTiming.Default);
    }

    private void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is Button or Slider or ListBox or ListBoxItem
            or TextBox or System.Windows.Controls.Primitives.Thumb)
            return;
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        RunLog.Write("timing-editor-closed");
        _timer.Stop();
        _lineHold?.Dispose();
        _lineHold = null;
        _rateHold?.Dispose();
        _rateHold = null;
        _stayHold?.Dispose();
        _stayHold = null;
        base.OnClosed(e);
    }

    private sealed record LineRow(LrcLine Line, string Key, string Display, string Translation)
    {
        public override string ToString() => Display;
    }
}
