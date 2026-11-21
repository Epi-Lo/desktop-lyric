using System.Collections.Frozen;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopLyric;

/// <summary>
/// Simplified → Hong Kong Traditional. OpenCC phrase + character tables first
/// (so 头发/里面/什么 actually convert). Windows LCMapStringEx is 1:1 and skips
/// one-to-many characters; it is only the fallback if dictionaries fail to load.
/// Lines with kana are left unchanged so Japanese lyrics are not rewritten.
/// </summary>
public static class S2TConverter
{
    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

    public static string Convert(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (LyricFonts.HasKana(input)) return input;

        var (st, hk) = Tables.Value;
        if (st.Map.Count == 0) return LcMap(input);

        var trad = Apply(input, st);
        return hk.Map.Count == 0 ? trad : Apply(trad, hk);
    }

    private readonly record struct Table(
        FrozenDictionary<string, string> Map,
        int MaxLen,
        HashSet<char> Starters);

    private static readonly Lazy<(Table st, Table hk)> Tables = new(LoadTables);

    private static (Table st, Table hk) LoadTables()
    {
        try
        {
            var asm = typeof(S2TConverter).Assembly;
            var names = asm.GetManifestResourceNames();
            string Find(string suffix) =>
                names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal))
                ?? throw new FileNotFoundException(suffix);

            // Characters first, phrases overwrite same keys so multi-char wins.
            var st = LoadTable(asm, Find("STCharacters.txt"), Find("STPhrases.txt"));
            var hk = LoadTable(asm, Find("HKVariants.txt"));
            return (st, hk);
        }
        catch
        {
            var empty = new Table(FrozenDictionary<string, string>.Empty, 1, []);
            return (empty, empty);
        }
    }

    private static Table LoadTable(Assembly asm, params string[] resourceNames)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var maxLen = 1;
        foreach (var name in resourceNames)
        {
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new FileNotFoundException(name);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var key = line[..tab];
                var rest = line[(tab + 1)..];
                var space = rest.IndexOf(' ');
                var value = space < 0 ? rest : rest[..space];
                if (key.Length == 0 || value.Length == 0) continue;
                map[key] = value;
                if (key.Length > maxLen) maxLen = key.Length;
            }
        }

        var starters = new HashSet<char>(map.Count);
        foreach (var key in map.Keys)
            starters.Add(key[0]);
        return new Table(map.ToFrozenDictionary(StringComparer.Ordinal), maxLen, starters);
    }

    private static string Apply(string input, Table table)
    {
        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length;)
        {
            if (!table.Starters.Contains(input[i]))
            {
                sb.Append(input[i]);
                i++;
                continue;
            }

            var max = Math.Min(table.MaxLen, input.Length - i);
            string? mapped = null;
            var take = 1;
            for (int len = max; len >= 1; len--)
            {
                if (table.Map.TryGetValue(input.Substring(i, len), out var value))
                {
                    mapped = value;
                    take = len;
                    break;
                }
            }

            if (mapped != null) sb.Append(mapped);
            else sb.Append(input[i]);
            i += take;
        }
        return sb.ToString();
    }

    private static string LcMap(string input)
    {
        try
        {
            var dest = new char[checked(input.Length * 2)];
            int n = LCMapStringEx(
                "zh-HK",
                LCMAP_TRADITIONAL_CHINESE,
                input,
                input.Length,
                dest,
                dest.Length,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (n > 0) return new string(dest, 0, n);
        }
        catch { }
        return input;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        [Out] char[] lpDestStr,
        int cchDest,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr sortHandle);
}
