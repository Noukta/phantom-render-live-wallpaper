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

    /// <summary>
    /// Pixel width images are decoded at: the card's width times the display scaling, set by the window
    /// (232 px at 100 %, 348 px at 150 %). Decoding at the real size keeps cards sharp without wasting memory.
    /// </summary>
    public static int DecodeWidth { get; set; } = 232;

    static BitmapImage? Load(string file)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;   // read now, don't keep the file open
            img.DecodePixelWidth = DecodeWidth;
            img.UriSource = new Uri(file);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ animated previews

    /// <summary>
    /// Hover previews are a short strip of JPEG frames (3 s at 16 fps, 480 px wide, sharp up to 200 % display
    /// scaling) rather than a video file: no codec or player needed in the control panel, small on disk.
    /// </summary>
    public const int PreviewFrames = 48, PreviewFps = 16, PreviewWidth = 480;
    readonly SemaphoreSlim _previewOne = new(1, 1);

    // The suffix names the preview format, so previews made with older settings are simply made again.
    const string PreviewSuffix = "_anim16";

    public static string PreviewDir(string video) => Path.ChangeExtension(CachePath(video), null) + PreviewSuffix;

    /// <summary>Deletes previews in older formats (8 fps / 360 px).</summary>
    public static void RemoveOldPreviews()
    {
        try
        {
            if (!Directory.Exists(Paths.ThumbsDir)) return;
            foreach (var d in Directory.GetDirectories(Paths.ThumbsDir, "*_anim*"))
                if (!d.EndsWith(PreviewSuffix, StringComparison.Ordinal)) Directory.Delete(d, true);
        }
        catch { }
    }

    static List<string>? CachedPreview(string video)
    {
        string dir = PreviewDir(video);
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToList();
        return files.Count > 0 ? files : null;
    }

    public static bool HasPreview(string video) => CachedPreview(video) != null;

    /// <summary>
    /// Returns the preview frame files, generating them if needed. <paramref name="urgent"/> (the user is
    /// hovering) skips the game-mode wait; background generation waits like thumbnails do.
    /// </summary>
    public async Task<List<string>?> GetPreviewAsync(string video, string? mpvExe, bool urgent, CancellationToken token)
    {
        var cached = CachedPreview(video);
        if (cached != null || mpvExe == null) return cached;
        await _previewOne.WaitAsync(token);
        try
        {
            if (!urgent) while (!MayWork()) await Task.Delay(2000, token);
            cached = CachedPreview(video);
            if (cached != null) return cached;
            string tmp = Path.Combine(Path.GetTempPath(), "phantom-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var psi = new ProcessStartInfo(mpvExe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    Arguments = $"--no-config --no-audio --no-sub --hwdec=no --start=15% --frames={PreviewFrames} " +
                                $"--vf=fps={PreviewFps},scale={PreviewWidth}:-2 --vo=image --vo-image-format=jpg --vo-image-jpeg-quality=80 " +
                                $"--vo-image-outdir=\"{tmp}\" --really-quiet --no-terminal \"{video}\"",
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                try { p.PriorityClass = urgent ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Idle; } catch { }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { try { p.Kill(); } catch { } throw; }
                if (!Directory.EnumerateFiles(tmp, "*.jpg").Any()) return null;
                string dir = PreviewDir(video);
                Directory.CreateDirectory(Paths.ThumbsDir);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(tmp, dir);
                return CachedPreview(video);
            }
            finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
        }
        catch (OperationCanceledException) { return null; }
        catch (IOException) { return null; }
        finally { _previewOne.Release(); }
    }

    /// <summary>Loads preview frames small and frozen so they can be built off the UI thread.</summary>
    public static List<BitmapImage> LoadFrames(IEnumerable<string> files) =>
        files.Select(Load).Where(b => b != null).Select(b => b!).ToList();

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
