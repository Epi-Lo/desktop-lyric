using DesktopLyric.Services;
using Xunit;

namespace DesktopLyric.Tests;

[Collection("LyricOffsetStore")]
public class LyricOffsetStoreTests : IDisposable
{
    private readonly string _path;

    public LyricOffsetStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "DesktopLyric-off-" + Guid.NewGuid().ToString("N") + ".json");
        LyricOffsetStore.ResetForTests(_path);
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { }
        LyricOffsetStore.ResetForTests(_path + ".done");
    }

    [Fact]
    public void remembers_offset_for_a_track()
    {
        LyricOffsetStore.SetMs("七里香", "周杰倫", 200);
        Assert.Equal(200, LyricOffsetStore.GetMs("七里香", "周杰倫"));
    }

    [Fact]
    public void zero_clears_offset()
    {
        LyricOffsetStore.SetMs("七里香", "周杰倫", 200);
        LyricOffsetStore.SetMs("七里香", "周杰倫", 0);
        Assert.Equal(0, LyricOffsetStore.GetMs("七里香", "周杰倫"));
    }

    [Fact]
    public void youtube_dump_and_extracted_song_share_offset()
    {
        const string dump =
            "TVアニメ「とある科学の超電磁砲」後期OP映像（ LEVEL5 -judgelight-／ fripSide）【NBCユニバーサルAnime✕Music30周年記念OP/ED毎日投稿企画】";
        LyricOffsetStore.SetMs(dump, "NBCUNIVERSAL ANIME/MUSIC", -150);
        Assert.Equal(-150, LyricOffsetStore.GetMs(dump, "NBCUNIVERSAL ANIME/MUSIC"));
        Assert.Equal(-150, LyricOffsetStore.GetMs("LEVEL5 -judgelight-", "fripSide"));
    }

    [Fact]
    public void nudge_steps_and_clamps()
    {
        Assert.Equal(50, LyricOffsetStore.Nudge("a", "b", 50));
        Assert.Equal(100, LyricOffsetStore.Nudge("a", "b", 50));
        Assert.Equal(LyricOffsetStore.MaxMs, LyricOffsetStore.Nudge("a", "b", 999_000));
    }

    [Fact]
    public void allows_offsets_beyond_ten_seconds()
    {
        LyricOffsetStore.SetMs("a", "b", 15_000);
        Assert.Equal(15_000, LyricOffsetStore.GetMs("a", "b"));
        LyricOffsetStore.SetMs("a", "b", -90_000);
        Assert.Equal(-90_000, LyricOffsetStore.GetMs("a", "b"));
    }

    [Fact]
    public void hold_step_accelerates()
    {
        Assert.Equal(0, LyricOffsetStore.StepForHoldMs(0));
        Assert.Equal(0, LyricOffsetStore.StepForHoldMs(LyricOffsetStore.HoldDelayMs - 1));
        Assert.Equal(LyricOffsetStore.StepMs, LyricOffsetStore.StepForHoldMs(LyricOffsetStore.HoldDelayMs));
        Assert.Equal(LyricOffsetStore.MediumStepMs, LyricOffsetStore.StepForHoldMs(LyricOffsetStore.HoldAccelMs));
        Assert.Equal(LyricOffsetStore.FastStepMs, LyricOffsetStore.StepForHoldMs(LyricOffsetStore.HoldFastMs));
    }

    [Fact]
    public void remembers_rate_and_offset_together()
    {
        LyricOffsetStore.SetTiming("live", "yt", new TrackTiming(1500, 1.03));
        var t = LyricOffsetStore.GetTiming("live", "yt");
        Assert.Equal(1500, t.OffsetMs);
        Assert.Equal(1.03, t.Rate, 3);
    }

    [Fact]
    public void set_ms_keeps_existing_rate()
    {
        LyricOffsetStore.SetTiming("a", "b", new TrackTiming(100, 1.04));
        LyricOffsetStore.SetMs("a", "b", 200);
        var t = LyricOffsetStore.GetTiming("a", "b");
        Assert.Equal(200, t.OffsetMs);
        Assert.Equal(1.04, t.Rate, 3);
    }

    [Fact]
    public void migrates_legacy_int_offsets()
    {
        File.WriteAllText(_path, """{"bob|song":250}""");
        LyricOffsetStore.ResetForTests(_path);
        Assert.Equal(250, LyricOffsetStore.GetMs("song", "bob"));
        Assert.Equal(1.0, LyricOffsetStore.GetTiming("song", "bob").Rate);
    }

    [Fact]
    public void remembers_per_line_shift()
    {
        var t = TrackTiming.Default.WithLineShift("1000|hello", 400);
        LyricOffsetStore.SetTiming("live", "yt", t);
        var got = LyricOffsetStore.GetTiming("live", "yt");
        Assert.NotNull(got.Lines);
        Assert.Equal(400, got.Lines!["1000|hello"]);
        Assert.Equal(TimeSpan.FromMilliseconds(1400),
            LyricsService.TimeOf(new LrcLine(TimeSpan.FromMilliseconds(1000), "hello"), got.Lines));
    }

    [Fact]
    public void remembers_hold_and_text_override()
    {
        var key = "1000|hello";
        var t = TrackTiming.Default
            .WithLineHold(key, 2500)
            .WithLineText(key, "live hello");
        LyricOffsetStore.SetTiming("live", "yt", t);
        var got = LyricOffsetStore.GetTiming("live", "yt");
        Assert.Equal(2500, got.Holds![key]);
        Assert.Equal("live hello", got.Texts![key]);
    }

    [Fact]
    public void remembers_inserted_line()
    {
        var t = TrackTiming.Default.WithAdded(new AddedLyric(12_000, "hey", "x1"));
        LyricOffsetStore.SetTiming("live", "yt", t);
        var got = LyricOffsetStore.GetTiming("live", "yt");
        Assert.Single(got.Added!);
        Assert.Equal(12_000, got.Added![0].AtMs);
        Assert.Equal("hey", got.Added[0].Text);
        Assert.Equal("x1", got.Added[0].Id);
    }

    [Fact]
    public void remembers_translation_override_and_added_trans()
    {
        var key = "1000|hello";
        var t = TrackTiming.Default
            .WithLineTrans(key, "你好")
            .WithAdded(new AddedLyric(2000, "hey", "z1", "嘿"));
        LyricOffsetStore.SetTiming("live", "yt", t);
        var got = LyricOffsetStore.GetTiming("live", "yt");
        Assert.Equal("你好", got.Trans![key]);
        Assert.Equal("嘿", got.Added![0].Trans);
    }

    [Fact]
    public void without_line_clears_shift_hold_and_text()
    {
        var key = "1000|hello";
        var t = TrackTiming.Default
            .WithLineShift(key, 400)
            .WithLineHold(key, 2000)
            .WithLineText(key, "x")
            .WithLineTrans(key, "中文");
        t = t.WithoutLine(key);
        Assert.True(t.IsIdentity);
    }

    [Fact]
    public void format_shows_seconds()
    {
        Assert.Equal("±0.00s", LyricOffsetStore.Format(0));
        Assert.Equal("+0.20s", LyricOffsetStore.Format(200));
        Assert.Equal("−0.05s", LyricOffsetStore.Format(-50));
        Assert.Equal("+15.50s", LyricOffsetStore.Format(15_500));
        Assert.Equal("−90.00s", LyricOffsetStore.Format(-90_000));
    }
}
