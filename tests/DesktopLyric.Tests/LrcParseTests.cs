using DesktopLyric.Services;
using Xunit;

namespace DesktopLyric.Tests;

public class LrcParseTests
{
    [Fact]
    public void splits_jp_cn_packed_into_one_line()
    {
        var (orig, trans) = LyricsService.SplitBilingual("夕暮れ 駆け抜けた在黃昏中奔馳而過");
        Assert.Equal("夕暮れ 駆け抜けた", orig);
        Assert.Equal("在黃昏中奔馳而過", trans);
    }

    [Fact]
    public void splits_jp_cn_slash_line()
    {
        var (orig, trans) = LyricsService.SplitBilingual("きみの声 まだ残る / 你的聲音仍迴盪著");
        Assert.Equal("きみの声 まだ残る", orig);
        Assert.Equal("你的聲音仍迴盪著", trans);
    }

    [Fact]
    public void pairs_same_timestamp_jp_then_cn()
    {
        var lines = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(12), "夕暮れ 駆け抜けた"),
            new(TimeSpan.FromSeconds(12), "在黃昏中奔馳而過"),
            new(TimeSpan.FromSeconds(15), "きみの声 まだ残る"),
        };
        LyricsService.SplitMixedLyrics(lines);
        Assert.Equal(2, lines.Count);
        Assert.Equal("夕暮れ 駆け抜けた", lines[0].Text);
        Assert.Equal("在黃昏中奔馳而過", lines[0].TranslatedText);
    }

    [Fact]
    public void resolved_translation_uses_nearby_chinese_line()
    {
        var jp = new LrcLine(TimeSpan.FromSeconds(12), "夕暮れ 駆け抜けた");
        var cn = new LrcLine(TimeSpan.FromMilliseconds(12_200), "在黃昏中奔馳而過");
        var lines = new List<LrcLine> { jp, cn };
        Assert.Equal("在黃昏中奔馳而過", LyricsService.ResolvedTranslation(lines, jp));
        Assert.Null(LyricsService.ResolvedTranslation(lines, cn));
    }

    [Fact]
    public void karaoke_words_drop_trailing_chinese()
    {
        var line = new LrcLine(TimeSpan.FromSeconds(1), "夕暮れ駆け抜けた在黃昏奔馳");
        line.WordTimings =
        [
            new(0, 100, "夕"), new(100, 100, "暮"), new(200, 100, "れ"),
            new(300, 100, "駆"), new(400, 100, "け"), new(500, 100, "抜"),
            new(600, 100, "け"), new(700, 100, "た"),
            new(800, 100, "在"), new(900, 100, "黃"), new(1000, 100, "昏"),
            new(1100, 100, "奔"), new(1200, 100, "馳"),
        ];
        var list = new List<LrcLine> { line };
        LyricsService.SplitMixedLyrics(list);
        Assert.Equal("夕暮れ駆け抜けた", list[0].Text);
        Assert.Equal("在黃昏奔馳", list[0].TranslatedText);
        Assert.Equal(8, list[0].WordTimings!.Count);
        Assert.DoesNotContain(list[0].WordTimings, w => w.Text is "在" or "黃");
    }

    [Fact]
    public void yrc_absolute_word_times_become_relative_to_line()
    {
        var yrc = "[14726,1200](14726,240,0)何(14966,240,0)か(15206,400,0)を";
        var lines = LyricsService.ParseYrcLines(yrc);
        Assert.Single(lines);
        Assert.Equal(14726, lines[0].startMs);
        Assert.Equal(1200, lines[0].durMs);
        Assert.Equal(3, lines[0].words.Count);
        Assert.Equal(0, lines[0].words[0].StartMs);
        Assert.Equal(240, lines[0].words[1].StartMs);
        Assert.Equal(480, lines[0].words[2].StartMs);
        Assert.Equal("何", lines[0].words[0].Text);
    }

    [Fact]
    public void yrc_already_relative_word_times_stay_relative()
    {
        var yrc = "[14726,1200](0,240,0)何(240,240,0)か(480,400,0)を";
        var lines = LyricsService.ParseYrcLines(yrc);
        Assert.Single(lines);
        Assert.Equal(0, lines[0].words[0].StartMs);
        Assert.Equal(240, lines[0].words[1].StartMs);
        Assert.Equal(480, lines[0].words[2].StartMs);
    }

    [Fact]
    public void parses_standard_lrc_timestamps()
    {
        var lrc = "[00:12.34]hello world\n[00:15.67]second line";
        var lines = LyricsService.ParseLrc(lrc);

        Assert.Equal(2, lines.Count);
        Assert.Equal("hello world", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(12340), lines[0].Time);
        Assert.Equal("second line", lines[1].Text);
    }

    [Fact]
    public void handles_3_digit_milliseconds()
    {
        // yoasobi - idol has these
        var lrc = "[01:05.123]three digits\n[01:10.456]another";
        var lines = LyricsService.ParseLrc(lrc);

        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(65123), lines[0].Time);
    }

    [Fact]
    public void keeps_empty_timestamps_as_gap_markers()
    {
        var lrc = "[ti:Song Title]\n[ar:Artist]\n[00:05.00]\n[00:10.00]actual lyric\n[00:15.00]  ";
        var lines = LyricsService.ParseLrc(lrc);

        Assert.Equal(3, lines.Count);
        Assert.Equal("", lines[0].Text);
        Assert.Equal("actual lyric", lines[1].Text);
        Assert.Equal("", lines[2].Text);
    }

    [Fact]
    public void gap_after_karaoke_clears_only_after_a_long_hold()
    {
        var lines = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(10), "verse end")
            {
                WordTimings = [new(0, 800, "verse"), new(800, 400, "end")],
            },
            new(TimeSpan.FromSeconds(50), "next verse"),
        };
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(10.5)));
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(24)));
        Assert.False(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(25)));
        Assert.True(LyricsService.LineIsActive(lines, 1, TimeSpan.FromSeconds(50.2)));
    }

    [Fact]
    public void empty_timestamp_between_nearby_lines_does_not_cut()
    {
        var lines = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(5), "hello"),
            new(TimeSpan.FromSeconds(8), ""),
            new(TimeSpan.FromSeconds(12), "later"),
        };
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(9)));
        Assert.Equal(TimeSpan.FromSeconds(12), LyricsService.LineDisplayEnd(lines, 0));
        Assert.False(LyricsService.LineIsActive(lines, 1, TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void consecutive_lines_hold_until_the_next_stamp()
    {
        var lines = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(10), "a"),
            new(TimeSpan.FromSeconds(13), "b"),
        };
        Assert.Equal(TimeSpan.FromSeconds(13), LyricsService.LineDisplayEnd(lines, 0));
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(12.5)));
    }

    [Fact]
    public void stretched_consecutive_singing_does_not_blank_before_next()
    {
        var lines = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(10), "held"),
            new(TimeSpan.FromSeconds(20), "next"),
        };
        Assert.Equal(TimeSpan.FromSeconds(20), LyricsService.LineDisplayEnd(lines, 0));
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(17)));
        Assert.False(LyricsService.LineIsActive(lines, 1, TimeSpan.FromSeconds(17)));
        Assert.True(LyricsService.LineIsActive(lines, 1, TimeSpan.FromSeconds(20.2)));
    }

    [Fact]
    public void chorus_gap_still_clears_after_default_hold()
    {
        var lines = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(10), "verse end"),
            new(TimeSpan.FromSeconds(40), "chorus"),
        };
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(24)));
        Assert.False(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(25)));
        Assert.False(LyricsService.LineIsActive(lines, 1, TimeSpan.FromSeconds(25)));
    }

    [Fact]
    public void delayed_chinese_stamp_does_not_cut_last_chorus_line()
    {
        var lines = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(10), "最後のサビ"),
            new(TimeSpan.FromSeconds(12), "最後副歌"),
            new(TimeSpan.FromSeconds(40), "次のAメロ"),
        };
        Assert.Equal(2, LyricsService.NextSungIndex(lines, 0));
        Assert.False(LyricsService.LineIsActive(lines, 1, TimeSpan.FromSeconds(12.5)));
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(12.5)));
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(16.5)));
        Assert.Equal("最後副歌", LyricsService.ResolvedTranslation(lines, lines[0]));
    }

    [Fact]
    public void extra_hold_keeps_line_past_the_next_stamp()
    {
        var lines = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(10), "held"),
            new(TimeSpan.FromSeconds(13), "next"),
        };
        var key = LyricsService.LineKey(lines[0]);
        var holds = new Dictionary<string, int> { [key] = 4000 };
        Assert.Equal(TimeSpan.FromSeconds(17), LyricsService.LineDisplayEnd(lines, 0, holds: holds));
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(15), holds: holds));
        Assert.False(LyricsService.LineIsActive(lines, 1, TimeSpan.FromSeconds(15), holds: holds));
        Assert.True(LyricsService.LineIsActive(lines, 1, TimeSpan.FromSeconds(17.2), holds: holds));
    }

    [Fact]
    public void extra_hold_extends_a_long_gap()
    {
        var lines = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(10), "end"),
            new(TimeSpan.FromSeconds(50), "later"),
        };
        var holds = new Dictionary<string, int> { [LyricsService.LineKey(lines[0])] = 3000 };
        Assert.True(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(27), holds: holds));
        Assert.False(LyricsService.LineIsActive(lines, 0, TimeSpan.FromSeconds(28), holds: holds));
    }

    [Fact]
    public void overlay_freezes_after_last_word_and_on_plain_lines()
    {
        Assert.True(KaraokeWordTiming.OverlayFrozen(null, 0));
        Assert.True(KaraokeWordTiming.OverlayFrozen([], 9000));
        var words = new List<KaraokeWordTiming> { new(0, 400, "a"), new(400, 500, "b") };
        Assert.False(KaraokeWordTiming.OverlayFrozen(words, 100));
        Assert.False(KaraokeWordTiming.OverlayFrozen(words, 899));
        Assert.True(KaraokeWordTiming.OverlayFrozen(words, 900));
        Assert.True(KaraokeWordTiming.OverlayFrozen(words, 14_500));
    }

    [Fact]
    public void apply_edits_replaces_hides_and_inserts()
    {
        var src = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(1), "studio") { WordTimings = [new(0, 200, "studio")] },
            new(TimeSpan.FromSeconds(2), "skip me"),
            new(TimeSpan.FromSeconds(4), "keep"),
        };
        var timing = TrackTiming.Default
            .WithLineText(LyricsService.LineKey(src[0]), "live words")
            .WithLineText(LyricsService.LineKey(src[1]), "")
            .WithAdded(new AddedLyric(2500, "ad-lib", "ab12"));
        var shown = LyricsService.ApplyEdits(src, timing);
        Assert.Equal(3, shown.Count);
        Assert.Equal("live words", shown[0].Text);
        Assert.Equal(LyricsService.LineKey(src[0]), shown[0].SourceKey);
        Assert.Equal("ad-lib", shown[1].Text);
        Assert.Equal("add|ab12", LyricsService.LineKey(shown[1]));
        Assert.Equal("keep", shown[2].Text);
        Assert.Null(shown[0].WordTimings);
    }

    [Fact]
    public void apply_edits_keeps_translation_unless_overridden()
    {
        var src = new LrcLine(TimeSpan.FromSeconds(1), "夕暮れ") { TranslatedText = "黃昏" };
        var key = LyricsService.LineKey(src);
        Assert.Equal("黃昏", LyricsService.ApplyEdits([src], TrackTiming.Default)[0].TranslatedText);
        Assert.Null(LyricsService.ApplyEdits([src], TrackTiming.Default.WithLineTrans(key, ""))[0].TranslatedText);
        Assert.Equal("黃昏啊", LyricsService.ApplyEdits([src], TrackTiming.Default.WithLineTrans(key, "黃昏啊"))[0].TranslatedText);
    }

    [Fact]
    public void replace_shown_does_not_stack_on_originals()
    {
        var src = new List<LrcLine>
        {
            new(TimeSpan.FromSeconds(1), "studio") { TranslatedText = "錄音室" },
            new(TimeSpan.FromSeconds(2), "keep"),
        };
        var t = new TrackTiming(800, 1.03);
        t = LyricsService.ReplaceShown(t, src,
        [
            new LyricsService.ClipLyric(1000, "live", "現場"),
            new LyricsService.ClipLyric(2000, "hey"),
        ]);
        Assert.Equal(800, t.OffsetMs);
        Assert.Equal(1.03, t.Rate, 3);
        var shown = LyricsService.ApplyEdits(src, t);
        Assert.Equal(2, shown.Count);
        Assert.Equal("live", shown[0].Text);
        Assert.Equal("現場", shown[0].TranslatedText);
        Assert.Equal("hey", shown[1].Text);
        Assert.DoesNotContain(shown, l => l.Text is "studio" or "keep");

        var again = LyricsService.ReplaceShown(t, src,
            [new LyricsService.ClipLyric(3000, "once")]);
        shown = LyricsService.ApplyEdits(src, again);
        Assert.Single(shown);
        Assert.Equal("once", shown[0].Text);
    }

    [Fact]
    public void restore_lyrics_keeps_clock_and_clear_blanks_overlay()
    {
        var src = new List<LrcLine> { new(TimeSpan.FromSeconds(1), "hello") };
        var t = new TrackTiming(500, 1.02)
            .WithLineShift(LyricsService.LineKey(src[0]), 100)
            .WithAdded(new AddedLyric(2000, "extra", "x1"));
        var cleared = LyricsService.ClearShown(t, src);
        Assert.Empty(LyricsService.ApplyEdits(src, cleared));
        Assert.Equal(500, cleared.OffsetMs);

        var restored = LyricsService.RestoreLyrics(t);
        var shown = LyricsService.ApplyEdits(src, restored);
        Assert.Single(shown);
        Assert.Equal("hello", shown[0].Text);
        Assert.Equal(500, restored.OffsetMs);
        Assert.Equal(1.02, restored.Rate, 3);
        Assert.Null(restored.Added);
        Assert.Null(restored.Lines);
    }

    [Fact]
    public void placement_ms_splits_the_gap()
    {
        Assert.Equal(11_500, LyricsService.PlacementMs(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(13), 0));
        Assert.Equal(14_000, LyricsService.PlacementMs(TimeSpan.FromSeconds(13), null, 0));
        Assert.Equal(9_500, LyricsService.PlacementMs(null, TimeSpan.FromSeconds(10), 0));
    }

    [Fact]
    public void set_effective_time_shifts_original_and_rewrites_added()
    {
        var orig = new LrcLine(TimeSpan.FromSeconds(10), "hello");
        var t = LyricsService.SetEffectiveTime(TrackTiming.Default, orig, 12_000);
        Assert.Equal(2000, t.Lines![LyricsService.LineKey(orig)]);
        Assert.Equal(TimeSpan.FromSeconds(12), LyricsService.TimeOf(orig, t.Lines));

        var added = new LrcLine(TimeSpan.FromSeconds(5), "ad") { SourceKey = "add|ab" };
        t = TrackTiming.Default.WithAdded(new AddedLyric(5_000, "ad", "ab"));
        t = LyricsService.SetEffectiveTime(t, added, 8_000);
        Assert.Equal(8_000, t.Added![0].AtMs);
        Assert.True(t.Lines == null || t.Lines.Count == 0);
    }

    [Fact]
    public void duplicate_line_inserts_a_copy()
    {
        var line = new LrcLine(TimeSpan.FromSeconds(10), "chorus");
        var t = LyricsService.DuplicateLine(TrackTiming.Default, line, 15_000);
        Assert.NotNull(t.Added);
        Assert.Single(t.Added);
        Assert.Equal("chorus", t.Added[0].Text);
        Assert.Equal(15_000, t.Added[0].AtMs);
        var shown = LyricsService.ApplyEdits([line], t);
        Assert.Equal(2, shown.Count);
        Assert.Equal("chorus", shown[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(15), shown[1].Time);
    }

    [Fact]
    public void duplicate_line_keeps_chinese_translation()
    {
        var line = new LrcLine(TimeSpan.FromSeconds(10), "夕暮れ") { TranslatedText = "黃昏" };
        var t = LyricsService.DuplicateLine(TrackTiming.Default, line, 20_000);
        Assert.Equal("黃昏", t.Added![0].Trans);
        var shown = LyricsService.ApplyEdits([line], t);
        Assert.Equal("黃昏", shown[1].TranslatedText);
    }

    [Fact]
    public void clipboard_plain_and_lrc()
    {
        var plain = LyricsService.ParseClipboardLyrics("hello\nworld", 3_000);
        Assert.Equal(2, plain.Count);
        Assert.Equal(3000, plain[0].AtMs);
        Assert.Equal("hello", plain[0].Text);
        Assert.Equal(4000, plain[1].AtMs);
        Assert.Equal("world", plain[1].Text);

        var lrc = LyricsService.ParseClipboardLyrics("[00:10.00]a\n[00:12.50]b", 0);
        Assert.Equal(2, lrc.Count);
        Assert.Equal(10_000, lrc[0].AtMs);
        Assert.Equal("a", lrc[0].Text);
        Assert.Equal(12_500, lrc[1].AtMs);
    }

    [Fact]
    public void clipboard_pairs_jp_then_cn_as_translation()
    {
        var lrc = LyricsService.ParseClipboardLyrics(
            "[00:10.00]夕暮れ 駆け抜けた\n[00:10.00]在黃昏中奔馳而過\n[00:15.00]きみの声\n[00:15.00]你的聲音", 0);
        Assert.Equal(2, lrc.Count);
        Assert.Equal("夕暮れ 駆け抜けた", lrc[0].Text);
        Assert.Equal("在黃昏中奔馳而過", lrc[0].Trans);
        Assert.Equal("きみの声", lrc[1].Text);
        Assert.Equal("你的聲音", lrc[1].Trans);

        var plain = LyricsService.ParseClipboardLyrics("きみの声\n你的聲音", 1000);
        Assert.Single(plain);
        Assert.Equal("きみの声", plain[0].Text);
        Assert.Equal("你的聲音", plain[0].Trans);
    }

    [Fact]
    public void format_shown_lrc_uses_effective_time()
    {
        var line = new LrcLine(TimeSpan.FromSeconds(10), "hello");
        var shifts = new Dictionary<string, int> { [LyricsService.LineKey(line)] = 1500 };
        var text = LyricsService.FormatShownLrc([line], shifts);
        Assert.Contains("[0:11.50]hello", text);
    }

    [Fact]
    public void format_shown_lrc_writes_translation_on_same_stamp()
    {
        var line = new LrcLine(TimeSpan.FromSeconds(12), "夕暮れ 駆け抜けた")
        {
            TranslatedText = "在黃昏中奔馳而過",
        };
        var text = LyricsService.FormatShownLrc([line], null);
        Assert.Equal(
            "[0:12.00]夕暮れ 駆け抜けた\n[0:12.00]在黃昏中奔馳而過\n",
            text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void format_shown_lrc_writes_offset_and_rate()
    {
        var line = new LrcLine(TimeSpan.FromSeconds(10), "hello");
        var t = new TrackTiming(2000, 1.03);
        var text = LyricsService.FormatShownLrc([line], t);
        Assert.Contains("[offset:2000]", text);
        Assert.Contains("[dl_rate:1.030]", text);
        Assert.Contains("[0:10.00]hello", text);
    }

    [Fact]
    public void format_and_parse_per_line_hold()
    {
        var line = new LrcLine(TimeSpan.FromSeconds(10), "held");
        var key = LyricsService.LineKey(line);
        var t = new TrackTiming(0, 1.0).WithLineHold(key, 2500);
        var text = LyricsService.FormatShownLrc([line], t);
        Assert.Contains("[dl_hold:2500]", text);

        var clips = LyricsService.ParseClipboardLyrics(text, 0);
        Assert.Single(clips);
        Assert.Equal(2500, clips[0].HoldMs);

        var src = new List<LrcLine> { new(TimeSpan.FromSeconds(1), "old") };
        var imported = LyricsService.ReplaceShown(TrackTiming.Default, src, clips);
        Assert.NotNull(imported.Holds);
        Assert.Contains(imported.Holds, kv => kv.Value == 2500);
        var shown = LyricsService.ApplyEdits(src, imported);
        Assert.Single(shown);
        Assert.Equal(TimeSpan.FromSeconds(27),
            LyricsService.LineDisplayEnd(shown, 0, imported.Lines, imported.Holds));
    }

    [Fact]
    public void parse_timing_tags_and_replace_keeps_them()
    {
        var raw = "[offset:-1500]\n[dl_rate:1.015]\n[00:10.00]きみの声\n[00:10.00]你的聲音\n";
        var (off, rate) = LyricsService.ParseTimingTags(raw);
        Assert.Equal(-1500, off);
        Assert.Equal(1.015, rate!.Value, 3);
        var clips = LyricsService.ParseClipboardLyrics(raw, 0);
        var src = new List<LrcLine> { new(TimeSpan.FromSeconds(1), "old") };
        var t = LyricsService.ReplaceShown(new TrackTiming(0, 1.0), src, clips, off, rate);
        Assert.Equal(-1500, t.OffsetMs);
        Assert.Equal(1.015, t.Rate, 3);
        var shown = LyricsService.ApplyEdits(src, t);
        Assert.Single(shown);
        Assert.Equal("きみの声", shown[0].Text);
        Assert.Equal("你的聲音", shown[0].TranslatedText);
        Assert.Equal(TimeSpan.FromSeconds(10), shown[0].Time);
    }

    [Fact]
    public void lines_sorted_by_time()
    {
        var lrc = "[00:30.00]late\n[00:05.00]early\n[00:15.00]middle";
        var lines = LyricsService.ParseLrc(lrc);

        Assert.Equal("early", lines[0].Text);
        Assert.Equal("middle", lines[1].Text);
        Assert.Equal("late", lines[2].Text);
    }

    [Fact]
    public void long_youtube_duration_is_not_used_to_score_lyrics()
    {
        Assert.Null(LyricsService.UsableTrackDuration(TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromMinutes(4), LyricsService.UsableTrackDuration(TimeSpan.FromMinutes(4)));
        Assert.Null(LyricsService.UsableTrackDuration(TimeSpan.FromSeconds(8)));
    }
}
