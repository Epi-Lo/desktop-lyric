using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace DesktopLyric;

internal static class FontLoader
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string fileName, uint fl, IntPtr pdv);

    public static Uri? FontsUri { get; private set; }
    public static FontFamily? BundledChiron { get; private set; }

    public static void Load()
    {
        var dir = FindFontsDir();
        if (dir == null) return;

        foreach (var file in Directory.EnumerateFiles(dir, "*.ttf"))
            AddFontResourceEx(file, 0, IntPtr.Zero);

        FontsUri = new Uri(dir + Path.DirectorySeparatorChar);
        BundledChiron = new FontFamily(FontsUri, "./#Chiron GoRound TC");
    }

    private static string? FindFontsDir()
    {
        string[] roots =
        [
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath) ?? "",
        ];
        foreach (var root in roots.Distinct())
        {
            var dir = Path.Combine(root, "Fonts");
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*Chiron*.ttf").Any())
                return Path.GetFullPath(dir);
        }
        return null;
    }
}
