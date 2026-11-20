namespace DesktopLyric.Services;

/// <summary>
/// Tracks which track the lyric search results belong to.
///
/// The SMTC poll is async and can overlap: a search for the previous track may
/// finish after the user has already skipped to a new one. LyricsService.Cancel
/// only makes that old SearchAsync return null, and null is also what "no
/// lyrics found" looks like, so the caller cannot tell the two apart. This
/// tracker closes that gap: only the request opened for the current title is
/// allowed to touch the lyric state.
///
/// Call Start and IsCurrent from the UI thread (the WPF dispatcher), which is
/// where PollNowPlaying's continuations run.
/// </summary>
public sealed class LyricSearchTracker
{
    private int _generation;
    private string _title = "";

    /// <summary>
    /// Opens a request for <paramref name="title"/>. Returns null when the
    /// title has not changed, so the polling loop does not restart a search.
    /// </summary>
    public LyricSearchRequest? Start(string title)
    {
        if (title == _title) return null;
        _title = title;
        return new LyricSearchRequest(title, ++_generation);
    }

    /// <summary>
    /// True when no newer title has been started since <paramref name="request"/>
    /// was opened. A false result means the request is stale and must be dropped.
    /// </summary>
    public bool IsCurrent(LyricSearchRequest request)
        => request.Generation == _generation;
}

/// <summary>Identifies one lyric search for one title.</summary>
public readonly record struct LyricSearchRequest(string Title, int Generation);
