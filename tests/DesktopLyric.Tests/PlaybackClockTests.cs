using Xunit;

namespace DesktopLyric.Tests;

public class PlaybackClockTests
{
    [Fact]
    public void frozen_smtc_during_play_keeps_interpolating()
    {
        var t = TimeSpan.Zero;
        var clock = new PlaybackClock(() => t);

        clock.Apply(TimeSpan.FromSeconds(10), playing: true);
        t = TimeSpan.FromSeconds(2);
        // SMTC still reports 10s — the original bug reset the clock here
        clock.Apply(TimeSpan.FromSeconds(10), playing: true);
        t = TimeSpan.FromSeconds(4);

        Assert.Equal(TimeSpan.FromSeconds(14), clock.Position);
    }

    [Fact]
    public void pause_snaps_to_smtc_position()
    {
        var t = TimeSpan.Zero;
        var clock = new PlaybackClock(() => t);

        clock.Apply(TimeSpan.FromSeconds(5), playing: true);
        t = TimeSpan.FromSeconds(3);
        clock.Apply(TimeSpan.FromSeconds(90), playing: false);

        Assert.False(clock.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(90), clock.Position);
    }

    [Fact]
    public void resume_anchors_at_paused_position_then_interpolates()
    {
        var t = TimeSpan.Zero;
        var clock = new PlaybackClock(() => t);

        clock.Apply(TimeSpan.FromSeconds(90), playing: false);
        t = TimeSpan.FromSeconds(10);
        clock.Apply(TimeSpan.FromSeconds(90), playing: true);
        t = TimeSpan.FromSeconds(12);

        Assert.True(clock.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(92), clock.Position);
    }

    [Fact]
    public void seek_while_playing_snaps_forward()
    {
        var t = TimeSpan.Zero;
        var clock = new PlaybackClock(() => t);

        clock.Apply(TimeSpan.FromSeconds(10), playing: true);
        t = TimeSpan.FromSeconds(1);
        clock.Apply(TimeSpan.FromSeconds(60), playing: true);

        Assert.Equal(TimeSpan.FromSeconds(60), clock.Position);
    }

    [Fact]
    public void healthy_smtc_updates_do_not_rewind()
    {
        var t = TimeSpan.Zero;
        var clock = new PlaybackClock(() => t);

        clock.Apply(TimeSpan.FromSeconds(10), playing: true);
        t = TimeSpan.FromSeconds(2);
        clock.Apply(TimeSpan.FromSeconds(12), playing: true);
        t = TimeSpan.FromSeconds(4);

        Assert.Equal(TimeSpan.FromSeconds(14), clock.Position);
    }

    [Fact]
    public void freeze_stops_interpolation_without_rewind()
    {
        var t = TimeSpan.Zero;
        var clock = new PlaybackClock(() => t);

        clock.Apply(TimeSpan.FromSeconds(10), playing: true);
        t = TimeSpan.FromSeconds(4);
        clock.Freeze();
        t = TimeSpan.FromSeconds(20);

        Assert.False(clock.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(14), clock.Position);
    }

    [Fact]
    public void playback_rate_scales_interpolation()
    {
        var t = TimeSpan.Zero;
        var clock = new PlaybackClock(() => t);

        clock.Apply(TimeSpan.FromSeconds(10), playing: true, rate: 2.0);
        t = TimeSpan.FromSeconds(3);

        Assert.Equal(TimeSpan.FromSeconds(16), clock.Position);
    }
}
