using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Wpe.Core;

namespace Wpe.UI;

/// <summary>
/// Makes one small JPEG per video with mpv, at idle priority, one at a time, cached on disk.
/// Generation waits while the engine reports a game-related pause, so it never competes with a game.
/// </summary>
public sealed class Thumbnails
{
    readonly SemaphoreSlim _one = new(1, 1);
    public Func<bool> MayWork { get; set; } = () => true;

    public static string CachePath(string video)
    {
        var fi = new FileInfo(video);
        string key = video.ToLowerInvariant() + "|" + (fi.Exists ? fi.Length + "|" + fi.LastWriteTimeUtc.Ticks : "");
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20];
        return Path.Combine(Paths.ThumbsDir, hash + ".jpg");
    }

    public static BitmapImage? LoadCached(string video)
    {
        string p = CachePath(video);
        return File.Exists(p) ? Load(p) : null;
    }

    static BitmapImage? Load(string file)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;   // read now, don't keep the file open
            img.DecodePixelWidth = 320;
            img.UriSource = new Uri(file);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>Returns the thumbnail, creating it if needed. Null if mpv is missing or the video can't be read.</summary>
    public async Task<BitmapImage?> GetAsync(string video, string? mpvExe, CancellationToken token)
    {
        string target = CachePath(video);
        if (File.Exists(target)) return Load(target);
        if (mpvExe == null) return null;

        await _one.WaitAsync(token);
        try
        {
            while (!MayWork()) await Task.Delay(2000, token);
            if (File.Exists(target)) return Load(target);
            string tmp = Path.Combine(Path.GetTempPath(), "phantom-thumb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var psi = new ProcessStartInfo(mpvExe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    Arguments = "--no-config --no-audio --no-sub --hwdec=no --frames=1 --start=15% --hr-seek=no " +
                                "--vf=scale=480:-2 --vo=image --vo-image-format=jpg --vo-image-jpeg-quality=85 " +
                                $"--vo-image-outdir=\"{tmp}\" --really-quiet --no-terminal \"{video}\"",
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { try { p.Kill(); } catch { } throw; }
                string? frame = Directory.EnumerateFiles(tmp, "*.jpg").FirstOrDefault();
                if (frame == null) return null;
                Directory.CreateDirectory(Paths.ThumbsDir);
                File.Move(frame, target, true);
                return Load(target);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
        catch (OperationCanceledException) { return null; }
        finally { _one.Release(); }
    }
}
