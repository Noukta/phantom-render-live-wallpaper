using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wpe.Core;

namespace Wpe.UI;

/// <summary>
/// Downloads mpv (the video player Phantom Render drives) from the Windows builds linked on mpv.io, because it
/// is distributed under its own license and isn't bundled. It goes to %LOCALAPPDATA%\WallpaperEngine\mpv, which
/// survives app updates.
/// </summary>
public static class MpvDownloader
{
    const string ReleasesApi = "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest";
    public static string TargetDir => Path.Combine(Paths.LocalDir, "mpv");
    public static string TargetExe => Path.Combine(TargetDir, "mpv.exe");

    public static async Task<string> DownloadAsync(IProgress<(string text, double fraction)> progress, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhantomRender/" + typeof(MpvDownloader).Assembly.GetName().Version);

        progress.Report(("Finding the latest mpv build...", 0));
        using var doc = JsonDocument.Parse(await http.GetStringAsync(ReleasesApi, token));
        // The plain x86_64 build runs on every 64-bit CPU (the "v3" builds need AVX2).
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => Regex.IsMatch(a.GetProperty("name").GetString() ?? "", @"^mpv-x86_64-\d{8}-git-[0-9a-f]+\.7z$"));
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("No mpv build found in the latest release.");
        string url = asset.GetProperty("browser_download_url").GetString()!;
        long size = asset.GetProperty("size").GetInt64();

        string tmp = Path.Combine(Path.GetTempPath(), "phantom-mpv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string archive = Path.Combine(tmp, "mpv.7z");
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(token);
                await using var dst = File.Create(archive);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, token)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), token);
                    done += n;
                    progress.Report(($"Downloading mpv... {done / 1048576} of {size / 1048576} MB", size > 0 ? 0.9 * done / size : 0));
                }
            }

            progress.Report(("Unpacking...", 0.92));
            // Windows' own tar (libarchive) reads .7z archives.
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"), $"-xf \"{archive}\" -C \"{tmp}\" mpv.exe")
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)!) await p.WaitForExitAsync(token);
            string exe = Path.Combine(tmp, "mpv.exe");
            if (!File.Exists(exe)) throw new InvalidOperationException("Could not unpack mpv.exe from the download.");

            Directory.CreateDirectory(TargetDir);
            File.Move(exe, TargetExe, true);
            progress.Report(("mpv is ready.", 1));
            return TargetExe;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
