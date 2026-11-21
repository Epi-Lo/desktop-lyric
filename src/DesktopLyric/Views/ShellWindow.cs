using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopLyric.Views;

/// <summary>
/// Taskbar membership without toggling WPF ShowInTaskbar at runtime.
/// Changing that DP on a layered window re-registers the HWND and leaves
/// ghost buttons; auto-hide taskbar then shows several identical icons.
/// </summary>
internal static class ShellWindow
{
    private const int GwlExStyle = -20;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;

    public static void NeverInTaskbar(Window w)
    {
        w.ShowInTaskbar = false;
        w.Loaded -= OnNeverLoaded;
        w.Loaded += OnNeverLoaded;
    }

    private static void OnNeverLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window w)
            Apply(w, exclude: true);
    }

    public static void Unpin(Window w) => Apply(w, exclude: true);

    public static void Pin(Window w) => Apply(w, exclude: false);

    /// <summary>Receive clicks without becoming the foreground window.</summary>
    public static void NoActivate(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;
            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)(ex | WsExNoActivate));
        }
        catch { }
    }

    /// <summary>When true, mouse clicks pass through to windows below.</summary>
    public static void ClickThrough(Window w, bool enable)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            var next = enable ? (ex | WsExTransparent) : (ex & ~WsExTransparent);
            if (next == ex) return;
            SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)next);
        }
        catch { }
    }

    private static void Apply(Window w, bool exclude)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;
            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            if (exclude)
                ex = (ex | WsExToolWindow) & ~WsExAppWindow;
            else
                ex = (ex | WsExAppWindow) & ~WsExToolWindow;
            SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)ex);
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
