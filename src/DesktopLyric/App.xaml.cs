using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopLyric.Services;
using DesktopLyric.Views;

namespace DesktopLyric;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static bool _ownsMutex;
    internal TrayIconService? Tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        try { SetCurrentProcessExplicitAppUserModelID("DesktopLyric.App"); }
        catch { }
        _mutex = new Mutex(true, "DesktopLyric_SingleInstance", out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            try
            {
                _ownsMutex = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Previous instance crashed without releasing. Take over.
                _ownsMutex = true;
            }
            if (!_ownsMutex)
            {
                RunLog.Write("already-running");
                MessageBox.Show("already running", "Desktop Lyric");
                Shutdown();
                return;
            }
        }
        try { System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2); }
        catch { }
        FontLoader.Load();
        ErrorLog.Attach(this);
        // Layered AllowsTransparency windows + GPU geometry/effects have been
        // killing the process with no dump. Software render is slower to look at
        // but the 100ms lyric clock does not depend on the GPU.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        RunLog.Write("start " + (Environment.ProcessPath ?? ""));
        RunLog.Write("sw-render");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { RunLog.Write("process-exit"); } catch { }
        };
        Tray = new TrayIconService();
        Tray.Start();
        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RunLog.Write("exit code=" + e.ApplicationExitCode);
        try { Tray?.Dispose(); } catch { }
        Tray = null;
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _ownsMutex = false;
        }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
        base.OnExit(e);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
