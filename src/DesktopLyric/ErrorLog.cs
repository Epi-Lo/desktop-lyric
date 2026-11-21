using System.IO;
using System.Text;
using System.Windows;

namespace DesktopLyric;

internal static class ErrorLog
{
    private static readonly object Gate = new();
    private static readonly string PathFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopLyric", "error.log");

    public static void Attach(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write(e.Exception);
            RunLog.Write("ui-exception " + e.Exception.GetType().Name + " " + OneLine(e.Exception));
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Write(ex);
                RunLog.Write("unhandled " + ex.GetType().Name
                    + (e.IsTerminating ? " terminating" : "")
                    + " " + OneLine(ex));
            }
            else
                RunLog.Write("unhandled " + e.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception);
            RunLog.Write("unobserved-task " + e.Exception.GetType().Name);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            var ex = e.Exception;
            if (ex is AccessViolationException or System.Runtime.InteropServices.SEHException
                or OutOfMemoryException)
                RunLog.Write("first-chance " + ex.GetType().Name + " " + (ex.Message ?? ""));
        };
    }

    public static void Write(Exception ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
                var sb = new StringBuilder();
                sb.AppendLine("==== " + DateTime.Now.ToString("s") + " ====");
                sb.AppendLine(ex.GetType().FullName);
                sb.AppendLine(ex.Message);
                sb.AppendLine(ex.StackTrace);
                if (ex.InnerException != null)
                {
                    sb.AppendLine("inner: " + ex.InnerException.GetType().FullName);
                    sb.AppendLine(ex.InnerException.Message);
                }
                sb.AppendLine();
                File.AppendAllText(PathFile, sb.ToString());
            }
        }
        catch { }
    }

    private static string OneLine(Exception ex)
    {
        var msg = (ex.Message ?? "").Replace('\n', ' ').Replace('\r', ' ');
        if (msg.Length > 160) msg = msg[..160];
        var st = ex.StackTrace ?? "";
        var n = st.IndexOf('\n');
        if (n > 0) st = st[..n];
        st = st.Trim();
        if (st.Length > 180) st = st[..180];
        return msg + " | " + st;
    }
}
