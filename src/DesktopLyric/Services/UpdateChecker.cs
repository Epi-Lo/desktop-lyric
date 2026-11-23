using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace DesktopLyric.Services;

public static class UpdateChecker
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    public const string CurrentVersion = "1.2.0"; // bump this when releasing
    private const string Repo = "Epi-Lo/desktop-lyric";

    public record UpdateInfo(string Version, string Url, string Notes);

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("DesktopLyric/0.9");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var ver = tag.TrimStart('v');
            var body = doc.RootElement.GetProperty("body").GetString() ?? "";
            var url = doc.RootElement.GetProperty("html_url").GetString() ?? "";

            if (IsNewer(ver, CurrentVersion))
                return new UpdateInfo(ver, url, body);
            return null;
        }
        catch { return null; }
    }

    private static bool IsNewer(string remote, string local)
    {
        try { return Version.Parse(remote) > Version.Parse(local); }
        catch { return false; }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
