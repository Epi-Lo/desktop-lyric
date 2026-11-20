using DesktopLyric.Services;
using Xunit;

namespace DesktopLyric.Tests;

public class LyricSearchTrackerTests
{
    [Fact]
    public void same_title_does_not_start_a_second_search()
    {
        var tracker = new LyricSearchTracker();

        Assert.NotNull(tracker.Start("Song"));
        Assert.Null(tracker.Start("Song"));
    }

    [Fact]
    public void only_the_latest_track_is_current()
    {
        var tracker = new LyricSearchTracker();

        var a = tracker.Start("Track A")!.Value;
        var b = tracker.Start("Track B")!.Value;

        Assert.False(tracker.IsCurrent(a));
        Assert.True(tracker.IsCurrent(b));
    }

    [Fact]
    public void a_late_result_for_the_previous_track_is_rejected()
    {
        var tracker = new LyricSearchTracker();
        var a = tracker.Start("Track A")!.Value;
        var b = tracker.Start("Track B")!.Value;
        var applied = new List<string>();

        // B's search finishes first and is applied.
        Apply(tracker, b, "B lyrics", applied);
        // A's cancelled search (SearchAsync returned null) finishes later.
        Apply(tracker, a, null, applied);

        Assert.Equal(new[] { "B lyrics" }, applied);
    }

    [Fact]
    public void replaying_a_previous_track_starts_a_fresh_request()
    {
        var tracker = new LyricSearchTracker();

        var firstA = tracker.Start("Track A")!.Value;
        tracker.Start("Track B");
        var secondA = tracker.Start("Track A")!.Value;

        Assert.False(tracker.IsCurrent(firstA));
        Assert.True(tracker.IsCurrent(secondA));
    }

    // Mirrors the decision in MainWindow.PollNowPlaying: only a current
    // request may replace the visible lyric state.
    private static void Apply(LyricSearchTracker tracker, LyricSearchRequest request,
        string? lyrics, List<string> applied)
    {
        if (!tracker.IsCurrent(request)) return;
        applied.Add(lyrics ?? "no lyrics found");
    }
}
