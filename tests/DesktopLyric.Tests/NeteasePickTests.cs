using System.Text.Json;
using DesktopLyric.Services;
using Xunit;

namespace DesktopLyric.Tests;

public class NeteasePickTests
{
    [Fact]
    public void traditional_youtube_title_still_picks_the_simplified_netease_song()
    {
        // YouTube says 記得 / 林俊傑, Netease answers 记得 / 林俊杰
        using var doc = JsonDocument.Parse("""
            [
              { "id": 1, "name": "记得", "ar": [ { "name": "林俊杰" } ], "dt": 287000 }
            ]
            """);

        var id = LyricsService.PickBest(doc.RootElement, "記得 Remember", "林俊傑 JJ LIn");

        Assert.Equal(1, id);
    }
}
