namespace DesktopLyric;

/// <summary>
/// Estimates playback position when SMTC's timeline is frozen during play
/// (common on Tidal, NetEase, some Spotify builds). Pause / seek reports
/// are trusted; while playing we interpolate with a monotonic clock.
/// </summary>
public sealed class PlaybackClock
{
    public static readonly TimeSpan SeekThreshold = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan FrozenEpsilon = TimeSpan.FromMilliseconds(250);

    private readonly Func<TimeSpan> _now;
    private TimeSpan _basePos;
    private TimeSpan _anchorMono;
    private TimeSpan _lastSmtcPos;
    private double _rate = 1.0;
    private bool _playing;
    private bool _hasAnchor;

    public PlaybackClock() : this(new StopwatchNow()) { }

    internal PlaybackClock(Func<TimeSpan> now) => _now = now;

    private PlaybackClock(StopwatchNow sw) : this(sw.Elapsed) { }

    public bool IsPlaying => _playing;

    /// <summary>
    /// Stop interpolating at the current estimated position (player gone /
    /// SMTC session died). Does not rewind.
    /// </summary>
    public void Freeze()
    {
        if (!_hasAnchor || !_playing) return;
        _basePos = Position;
        _playing = false;
    }

    public TimeSpan Position
    {
        get
        {
            if (!_hasAnchor) return TimeSpan.Zero;
            if (!_playing) return _basePos;
            var elapsed = TimeSpan.FromTicks((long)((_now() - _anchorMono).Ticks * _rate));
            var pos = _basePos + elapsed;
            return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
        }
    }

    /// <param name="smtcPosition">TimelineProperties.Position</param>
    /// <param name="playing">PlaybackStatus == Playing</param>
    /// <param name="rate">PlaybackRate; 0 or negative is treated as 1 while playing</param>
    public void Apply(TimeSpan smtcPosition, bool playing, double rate = 1.0)
    {
        if (smtcPosition < TimeSpan.Zero) smtcPosition = TimeSpan.Zero;
        if (playing)
        {
            if (rate <= 0) rate = 1.0;
        }
        else
        {
            // Paused: SMTC position is the source of truth on almost every player.
            _basePos = smtcPosition;
            _rate = rate > 0 ? rate : 1.0;
            _playing = false;
            _lastSmtcPos = smtcPosition;
            _hasAnchor = true;
            return;
        }

        if (!_hasAnchor || !_playing)
        {
            Anchor(smtcPosition, rate);
            _lastSmtcPos = smtcPosition;
            return;
        }

        var smtcAdvance = smtcPosition - _lastSmtcPos;
        var drift = smtcPosition - Position;

        if (smtcAdvance.Duration() < FrozenEpsilon)
        {
            // Same (or nearly same) SMTC reading as last time: frozen. Keep interpolating.
        }
        else if (drift.Duration() >= SeekThreshold)
        {
            // SMTC moved and disagrees with the interpolator — user seeked.
            Anchor(smtcPosition, rate);
        }
        else
        {
            _rate = rate;
        }

        _lastSmtcPos = smtcPosition;
    }

    private void Anchor(TimeSpan pos, double rate)
    {
        _basePos = pos;
        _rate = rate;
        _anchorMono = _now();
        _playing = true;
        _hasAnchor = true;
    }

    private sealed class StopwatchNow
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public TimeSpan Elapsed() => _sw.Elapsed;
    }
}
