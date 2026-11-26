using System.Windows.Media;
using DesktopLyric.Views;
using Xunit;

namespace DesktopLyric.Tests;

public class KaraokeRampTests
{
    [Fact]
    public void ramp_starts_from_the_theme_unsung_colour_not_fixed_grey()
    {
        // before this the ramp always start from #586878, white unsung word go dark first then turn cyan
        var unsung = Colors.White;
        var accent = Color.FromRgb(0x00, 0xd4, 0xff);

        var first = LyricLineView.RampColor(unsung, accent, 1 / 8.0);

        Assert.True(first.R > 0xD0, $"first step R {first.R:X2} should still be close to white");
        Assert.True(first.G > 0xF0, $"first step G {first.G:X2} should still be close to white");
    }
}
