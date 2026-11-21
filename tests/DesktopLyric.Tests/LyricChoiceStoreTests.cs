using DesktopLyric.Services;
using Xunit;

namespace DesktopLyric.Tests;

[Collection("LyricChoiceStore")]
public class LyricChoiceStoreTests : IDisposable
{
    private readonly string _path;

    public LyricChoiceStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "DesktopLyric-tests-" + Guid.NewGuid().ToString("N") + ".json");
        LyricChoiceStore.ResetForTests(_path);
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { }
        LyricChoiceStore.ResetForTests(_path + ".done");
    }

    [Fact]
    public void remembers_exact_pick()
    {
        LyricChoiceStore.Set("七里香", "周杰倫", "ncm:1");
        Assert.Equal("ncm:1", LyricChoiceStore.Get("七里香", "周杰倫"));
    }

    [Fact]
    public void simplified_and_traditional_title_share_a_key()
    {
        LyricChoiceStore.Set("爱", "周杰伦", "ncm:2");
        Assert.Equal("ncm:2", LyricChoiceStore.Get("愛", "周杰倫"));
    }

    [Fact]
    public void empty_artist_still_finds_unique_title()
    {
        LyricChoiceStore.Set("コントラスト", "hatsuboshi gakuen - topic", "ncm:3");
        Assert.Equal("ncm:3", LyricChoiceStore.Get("コントラスト", ""));
        Assert.Equal("ncm:3", LyricChoiceStore.Get("コントラスト", "hatsuboshi gakuen"));
    }

    [Fact]
    public void strips_youtube_topic_and_track_number()
    {
        LyricChoiceStore.Set("01. 花痕 -shirushi- (hanaato -shirushi-)", "calico calico - topic", "ncm:4");
        Assert.Equal("ncm:4", LyricChoiceStore.Get("花痕 -shirushi-", "calico calico"));
    }

    [Fact]
    public void ambiguous_same_title_requires_artist()
    {
        LyricChoiceStore.Set("同名", "A", "ncm:a");
        LyricChoiceStore.Set("同名", "B", "ncm:b");
        Assert.Equal("ncm:a", LyricChoiceStore.Get("同名", "A"));
        Assert.Equal("ncm:b", LyricChoiceStore.Get("同名", "B"));
        Assert.Null(LyricChoiceStore.Get("同名", ""));
        Assert.Null(LyricChoiceStore.Get("同名", "C"));
    }

    [Fact]
    public void different_youtube_channel_still_hits_unique_title()
    {
        LyricChoiceStore.Set("level5 -judgelight-", "nbcuniversal anime/music", "ncm:5");
        Assert.Equal("ncm:5", LyricChoiceStore.Get("level5-judgelight-", "fripside"));
    }

    const string RailgunOp =
        "TVアニメ「とある科学の超電磁砲」後期OP映像（ LEVEL5 -judgelight-／ fripSide）【NBCユニバーサルAnime✕Music30周年記念OP/ED毎日投稿企画】";

    [Fact]
    public void extracts_quoted_mv_title_and_strips_topic_artist()
    {
        Assert.Equal("星をあつめて", LyricChoiceStore.SearchTitle(
            "fhána「星をあつめて」（劇場版『SHIROBAKO』主題歌）MUSIC VIDEO"));
        Assert.Equal("ユビオリ", LyricChoiceStore.SearchTitle("大原ゆい子「ユビオリ」 Live Ver."));
        Assert.Equal("Kana Hanazawa", LyricChoiceStore.SearchArtist("雪に咲く花", "Kana Hanazawa - Topic"));
    }

    [Fact]
    public void extracts_song_from_youtube_anime_op_dump()
    {
        Assert.Equal("level5-judgelight-", LyricChoiceStore.ExtractParenSong(RailgunOp));
        Assert.Equal("fripside", LyricChoiceStore.ExtractParenArtist(RailgunOp));
        Assert.Equal("level5-judgelight- TVサイズ", LyricChoiceStore.SearchTitle(RailgunOp));
        Assert.Equal("fripside", LyricChoiceStore.SearchArtist(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC"));
    }

    [Fact]
    public void remembers_youtube_dump_as_the_inner_song()
    {
        LyricChoiceStore.Set(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC", "ncm:676207");
        Assert.Equal("ncm:676207", LyricChoiceStore.Get(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC"));
        Assert.Equal("ncm:676207", LyricChoiceStore.Get(RailgunOp, ""));
        Assert.Equal("ncm:676207", LyricChoiceStore.Get("LEVEL5 -judgelight-", "fripSide"));
        Assert.Equal("ncm:676207", LyricChoiceStore.Get("LEVEL5 -judgelight-", "NBCUniversal Anime✕Music"));
    }

    [Fact]
    public void finds_existing_stripped_and_song_keys_from_full_youtube_title()
    {
        File.WriteAllText(_path, """
            {
              "nbcuniversal anime|tvアニメ「とある科学の超電磁砲」後期op映像": "ncm:676207",
              "|level5 -judgelight-": "ncm:676207"
            }
            """);
        LyricChoiceStore.ResetForTests(_path);
        Assert.Equal("ncm:676207", LyricChoiceStore.Get(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC"));
        Assert.Equal("ncm:676207", LyricChoiceStore.Get(RailgunOp, "fripSide"));
        Assert.Equal("ncm:676207", LyricChoiceStore.Get("LEVEL5 -judgelight-", ""));
    }

    [Fact]
    public void list_all_groups_aliases_of_one_candidate()
    {
        LyricChoiceStore.Set(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC", "ncm:676207");
        LyricChoiceStore.Set("七里香", "周杰倫", "ncm:1");
        var list = LyricChoiceStore.ListAll();
        Assert.Equal(2, list.Count);
        var railgun = Assert.Single(list, c => c.CandidateKey == "ncm:676207");
        Assert.Contains("judgelight", railgun.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("網易雲", railgun.SourceLabel);
        Assert.True(railgun.Keys.Count >= 2);
    }

    [Fact]
    public void remove_candidate_drops_all_aliases()
    {
        LyricChoiceStore.Set(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC", "ncm:676207");
        Assert.True(LyricChoiceStore.RemoveCandidate("ncm:676207"));
        Assert.Null(LyricChoiceStore.Get("LEVEL5 -judgelight-", "fripSide"));
        Assert.Empty(LyricChoiceStore.ListAll());
    }

    [Fact]
    public void later_pick_replaces_overlapping_old_candidate()
    {
        LyricChoiceStore.Set(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC", "ncm:676207");
        LyricChoiceStore.Set(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC", "ncm:tv-size");
        Assert.Equal("ncm:tv-size", LyricChoiceStore.Get(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC"));
        Assert.Equal("ncm:tv-size", LyricChoiceStore.Get("LEVEL5 -judgelight-", "fripSide"));
        Assert.DoesNotContain(LyricChoiceStore.ListAll(), c => c.CandidateKey == "ncm:676207");
        Assert.Equal("ncm:tv-size", Assert.Single(LyricChoiceStore.ListAll()).CandidateKey);
    }

    [Fact]
    public void retarget_moves_every_alias_to_the_new_candidate()
    {
        LyricChoiceStore.Set(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC", "ncm:676207");
        LyricChoiceStore.Retarget("ncm:676207", "ncm:999");
        Assert.Equal("ncm:999", LyricChoiceStore.Get(RailgunOp, "NBCUNIVERSAL ANIME/MUSIC"));
        Assert.Equal("ncm:999", LyricChoiceStore.Get("LEVEL5 -judgelight-", "fripSide"));
        Assert.Single(LyricChoiceStore.ListAll());
        Assert.Equal("ncm:999", LyricChoiceStore.ListAll()[0].CandidateKey);
    }
}
