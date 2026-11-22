using System.Text.Json;
using DesktopLyric.Services;
using Xunit;

namespace DesktopLyric.Tests;

public class QqRequestTests
{
    [Fact]
    public void backslash_in_title_still_gives_valid_json()
    {
        // 選歌 list for a title like AC\DC came back empty, qq got broken json
        var body = LyricsService.QqSearchBody(@"AC\DC", "Back In Black", 20);

        using var doc = JsonDocument.Parse(body);
        var query = doc.RootElement.GetProperty("req").GetProperty("param").GetProperty("query").GetString();
        Assert.Equal(@"AC\DC Back In Black", query);
    }

    [Fact]
    public void quote_in_title_is_kept_in_the_query()
    {
        // the quote used to be cut out to protect the hand made json, auto search never cut it
        var body = LyricsService.QqSearchBody("\"Heroes\"", "David Bowie", 20);

        using var doc = JsonDocument.Parse(body);
        var query = doc.RootElement.GetProperty("req").GetProperty("param").GetProperty("query").GetString();
        Assert.Equal("\"Heroes\" David Bowie", query);
    }
}
