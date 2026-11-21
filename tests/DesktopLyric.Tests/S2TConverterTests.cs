using Xunit;

namespace DesktopLyric.Tests;

public class S2TConverterTests
{
    [Fact]
    public void converts_common_chars()
    {
        var input = "我爱你";
        var result = S2TConverter.Convert(input);
        Assert.Equal("我愛你", result);
    }

    [Fact]
    public void leaves_already_traditional_alone()
    {
        var input = "這是繁體";
        var result = S2TConverter.Convert(input);
        Assert.Equal("這是繁體", result);
    }

    [Fact]
    public void handles_mixed_content()
    {
        var input = "I love 音乐 and 梦想";
        var result = S2TConverter.Convert(input);
        Assert.Contains("樂", result);
        Assert.Contains("夢", result);
        Assert.Contains("I love", result);
    }

    [Fact]
    public void empty_input_returns_empty()
    {
        Assert.Equal("", S2TConverter.Convert(""));
        Assert.Null(S2TConverter.Convert(null!));
    }

    [Fact]
    public void jay_chou_lyrics_convert_correctly()
    {
        // 七里香 chorus
        var input = "雨下整夜 我的爱溢出就像雨水";
        var result = S2TConverter.Convert(input);
        Assert.Contains("愛", result);
        Assert.Contains("溢", result); // this one stays the same
    }

    [Fact]
    public void overlay_sizes_leave_room_for_japanese_when_chinese_trans_is_large()
    {
        var s = LyricFonts.FitOverlaySizes(
            area: 120,
            hasTrans: true,
            hasNext: true,
            originalIsJapanese: true,
            originalScale: 1.0,
            translationScale: 1.8);
        var used = s.CurrentFont * 1.28 + s.TransMaxHeight + s.NextMaxHeight;
        Assert.True(used <= 120 + 0.5, $"used {used} > 120");
        Assert.True(s.CurrentFont >= 18, $"original too small: {s.CurrentFont}");
        Assert.True(s.TransFont > s.CurrentFont, "Chinese translation should be larger than JP original at default-ish scales");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(140)]
    public void original_scale_up_increases_current_font(double area)
    {
        var a = LyricFonts.FitOverlaySizes(area, true, true, true, 1.0, 1.15);
        var b = LyricFonts.FitOverlaySizes(area, true, true, true, 1.0 + LyricFonts.ScaleStep * 4, 1.15);
        Assert.True(b.CurrentFont > a.CurrentFont + 2,
            $"area {area}: {a.CurrentFont} -> {b.CurrentFont}");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(140)]
    public void translation_scale_up_increases_trans_font_and_maxheight(double area)
    {
        var a = LyricFonts.FitOverlaySizes(area, true, true, true, 1.0, 1.15);
        var b = LyricFonts.FitOverlaySizes(area, true, true, true, 1.0, 1.15 + LyricFonts.ScaleStep * 4);
        Assert.True(b.TransFont > a.TransFont + 2,
            $"area {area} font: {a.TransFont} -> {b.TransFont}");
        Assert.True(b.TransMaxHeight > a.TransMaxHeight + 2,
            $"area {area} maxH: {a.TransMaxHeight} -> {b.TransMaxHeight}");
    }

    [Fact]
    public void original_scale_down_decreases_current_font()
    {
        var a = LyricFonts.FitOverlaySizes(100, true, true, true, 1.0, 1.15);
        var b = LyricFonts.FitOverlaySizes(100, true, true, true, 0.55, 1.15);
        Assert.True(b.CurrentFont < a.CurrentFont - 2, $"{a.CurrentFont} -> {b.CurrentFont}");
    }

    [Fact]
    public void japanese_line_kanji_piece_still_counts_as_japanese()
    {
        Assert.True(LyricFonts.IsJapaneseLine("駆", "夕暮れ駆け抜けた"));
        Assert.False(LyricFonts.IsJapaneseLine("在黃昏中奔馳", "在黃昏中奔馳"));
    }

    [Fact]
    public void has_kana_detects_japanese_not_chinese()
    {
        Assert.True(LyricFonts.HasKana("君の知らない物語"));
        Assert.True(LyricFonts.HasKana("ありがとう"));
        Assert.False(LyricFonts.HasKana("雨下整夜 我的愛溢出就像雨水"));
        Assert.False(LyricFonts.HasKana("hello"));
    }

    [Fact]
    public void converts_chars_missing_from_the_old_hand_map()
    {
        var result = S2TConverter.Convert("歌词颜帅无处");
        Assert.Equal("歌詞顏帥無處", result);
    }

    [Theory]
    [InlineData("头发", "頭髮")]
    [InlineData("发丝", "髮絲")]
    [InlineData("发型", "髮型")]
    [InlineData("里面", "裏面")]
    [InlineData("这里", "這裏")]
    [InlineData("心里", "心裏")]
    [InlineData("什么", "什麼")]
    [InlineData("怎么", "怎麼")]
    [InlineData("那么", "那麼")]
    [InlineData("这么", "這麼")]
    [InlineData("干净", "乾淨")]
    [InlineData("干杯", "乾杯")]
    [InlineData("干活", "幹活")]
    [InlineData("后面", "後面")]
    [InlineData("落后", "落後")]
    [InlineData("杰作", "傑作")]
    [InlineData("忧郁", "憂鬱")]
    [InlineData("云端", "雲端")]
    [InlineData("一只猫", "一隻貓")]
    [InlineData("伙伴", "夥伴")]
    [InlineData("于是", "於是")]
    [InlineData("剩余", "剩餘")]
    [InlineData("发现", "發現")]
    [InlineData("关系", "關係")]
    public void converts_phrases_windows_lcmap_skips(string simplified, string traditional)
    {
        Assert.Equal(traditional, S2TConverter.Convert(simplified));
    }

    [Fact]
    public void does_not_mangle_ambiguous_words()
    {
        Assert.Equal("皇后", S2TConverter.Convert("皇后"));
        Assert.Equal("公里", S2TConverter.Convert("公里"));
        Assert.Equal("只是", S2TConverter.Convert("只是"));
    }

    [Fact]
    public void uses_hong_kong_variants()
    {
        Assert.Equal("為", S2TConverter.Convert("为"));
        Assert.Equal("台灣", S2TConverter.Convert("台湾"));
    }

    [Fact]
    public void leaves_japanese_lines_alone()
    {
        Assert.Equal("君の知らない物語", S2TConverter.Convert("君の知らない物語"));
        Assert.Equal("夕暮れ 駆け抜けた", S2TConverter.Convert("夕暮れ 駆け抜けた"));
    }

    [Fact]
    public void converts_chinese_karaoke_word_but_not_kana_word()
    {
        Assert.Equal("在", S2TConverter.Convert("在"));
        Assert.Equal("黃昏", S2TConverter.Convert("黄昏"));
        Assert.Equal("け", S2TConverter.Convert("け"));
    }
}
