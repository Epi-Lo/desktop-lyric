using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopLyric.Services;

namespace DesktopLyric.Views;

/// <summary>
/// One immediate step on press, then accelerating repeats while held.
/// </summary>
internal sealed class HoldRepeat : IDisposable
{
    private readonly Action<int> _step;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _sw = new();
    private int _sign;

    public bool IsHeld => _timer.IsEnabled;

    public HoldRepeat(Action<int> step)
    {
        _step = step;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) =>
        {
            try
            {
                var ms = LyricOffsetStore.StepForHoldMs(_sw.Elapsed.TotalMilliseconds);
                if (ms != 0) _step(_sign * ms);
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        };
    }

    public void Down(int sign, IInputElement? capture)
    {
        Up();
        _sign = sign < 0 ? -1 : 1;
        _sw.Restart();
        try { _step(_sign * LyricOffsetStore.StepMs); }
        catch (Exception ex) { ErrorLog.Write(ex); }
        if (capture != null)
        {
            try { Mouse.Capture(capture); } catch { }
        }
        _timer.Start();
    }

    public void Up()
    {
        _timer.Stop();
        _sw.Reset();
        try { Mouse.Capture(null); } catch { }
    }

    public void Dispose() => Up();
}
