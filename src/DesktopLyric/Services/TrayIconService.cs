using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace DesktopLyric.Services;

internal sealed class TrayIconService : IDisposable
{
    private WinForms.NotifyIcon? _notify;

    public event Action? ShowMainRequested;
    public event Action? ShowOverlayRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public void Start()
    {
        if (_notify != null) return;
        try
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("顯示主視窗", null, (_, _) => Dispatch(ShowMainRequested));
            menu.Items.Add("顯示 Overlay", null, (_, _) => Dispatch(ShowOverlayRequested));
            menu.Items.Add("設定", null, (_, _) => Dispatch(SettingsRequested));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("結束", null, (_, _) => Dispatch(ExitRequested));

            _notify = new WinForms.NotifyIcon
            {
                Text = "Desktop Lyric",
                Visible = true,
                Icon = LoadIcon(),
                ContextMenuStrip = menu,
            };
            _notify.MouseUp += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                    Dispatch(ShowMainRequested);
            };
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    public void Dispose()
    {
        if (_notify == null) return;
        try
        {
            _notify.Visible = false;
            _notify.Dispose();
        }
        catch { }
        _notify = null;
    }

    private static void Dispatch(Action? handler)
    {
        if (handler == null) return;
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null) { handler(); return; }
        if (d.CheckAccess()) handler();
        else d.BeginInvoke(handler);
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted != null) return extracted;
            }
        }
        catch { }
        return SystemIcons.Application;
    }
}
