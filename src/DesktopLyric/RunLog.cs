using System.IO;
using System.Text;

namespace DesktopLyric;

/// <summary>
/// One-line lifecycle log. Native heap/Finalize crashes never reach error.log;
/// last hb/trace line is the last known-alive moment and last action.
/// </summary>
internal static class RunLog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> LastTrace = new();
    private static readonly string PathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopLyric", "run.log");

    public static string FilePath => PathFile;

    public static void Write(string msg) => WriteCore(msg, throttleKey: null);

    /// <summary>Same as Write, but at most once per 400ms per key (prefix before space).</summary>
    public static void Trace(string msg)
    {
        var key = msg;
        var sp = msg.IndexOf(' ');
        if (sp > 0) key = msg[..sp];
        WriteCore(msg, key);
    }

    private static void WriteCore(string msg, string? throttleKey)
    {
        try
        {
            lock (Gate)
            {
                var now = Environment.TickCount64;
                if (throttleKey != null)
                {
                    if (LastTrace.TryGetValue(throttleKey, out var prev) && now - prev < 400)
                        return;
                    LastTrace[throttleKey] = now;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
                var line = DateTime.Now.ToString("s") + "." + DateTime.Now.ToString("fff")
                    + " pid=" + Environment.ProcessId
                    + " " + msg
                    + Environment.NewLine;
                using var fs = new FileStream(PathFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var w = new StreamWriter(fs, Encoding.UTF8);
                w.Write(line);
                w.Flush();
                fs.Flush(flushToDisk: true);
                TrimIfHuge();
            }
        }
        catch { }
    }

    private static void TrimIfHuge()
    {
        try
        {
            var info = new FileInfo(PathFile);
            if (!info.Exists || info.Length < 400_000) return;
            var text = File.ReadAllText(PathFile, Encoding.UTF8);
            var keep = text.Length > 120_000 ? text[^120_000..] : text;
            File.WriteAllText(PathFile, keep, Encoding.UTF8);
        }
        catch { }
    }
}
