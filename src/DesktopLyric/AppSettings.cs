using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopLyric;

public class AppSettings
{
    public bool BoldLyrics { get; set; } = true;
    public bool HideTranslation { get; set; } = false;
    public bool ForceTraditional { get; set; } = true;
    public bool ShowRomaji { get; set; } = false;
    public int GlobalOffsetMs { get; set; } = 0; // negative = lyrics show earlier
    public double OverlayOpacity { get; set; } = 85;
    public string AccentColor { get; set; } = "#00d4ff"; // might add color picker later
    public string FontFamily { get; set; } = "Chiron GoRound TC";
    public bool OverlayTopmost { get; set; } = true;
    /// <summary>1 = default. Japanese original is a bit smaller at 1.0.</summary>
    public double OverlayOriginalScale { get; set; } = 1.0;
    /// <summary>1.15 = Chinese translation a bit larger than the Japanese original.</summary>
    public double OverlayTranslationScale { get; set; } = 1.15;
    public bool FullscreenAlbumLayout { get; set; } = true;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopLyric", "settings.json");

    public static string FolderPath => Path.GetDirectoryName(SettingsPath)!;
    public static string FilePath => SettingsPath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
                if (loaded.OverlayOriginalScale <= 0) loaded.OverlayOriginalScale = 1.0;
                if (loaded.OverlayTranslationScale <= 0) loaded.OverlayTranslationScale = 1.15;
                return loaded;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
