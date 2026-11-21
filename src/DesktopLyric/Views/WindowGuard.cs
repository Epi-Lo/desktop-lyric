using System.Windows;
using System.Windows.Media;

namespace DesktopLyric.Views;

internal static class WindowGuard
{
    public static bool CanTouch(Window? w)
    {
        if (w == null) return false;
        try
        {
            if (!w.IsLoaded) return false;
            if (w.Dispatcher.HasShutdownStarted) return false;
            return PresentationSource.FromVisual(w) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetFontSize(LyricLineView view, double fs)
    {
        if (double.IsNaN(fs) || double.IsInfinity(fs) || fs < 8) fs = 8;
        fs = Math.Clamp(fs, 8, 160);
        if (Math.Abs(view.FontSize - fs) < 0.04) return;
        view.FontSize = fs;
    }

    public static void SetMaxHeight(FrameworkElement el, double h)
    {
        if (h <= 1 || double.IsNaN(h) || double.IsInfinity(h))
        {
            if (!double.IsPositiveInfinity(el.MaxHeight))
                el.MaxHeight = double.PositiveInfinity;
            return;
        }
        if (Math.Abs(el.MaxHeight - h) < 0.5) return;
        el.MaxHeight = h;
    }
}
