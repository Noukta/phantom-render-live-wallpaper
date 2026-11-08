using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wpe.Core;

/// <summary>
/// The regular Windows desktop wallpaper. Phantom Render sets it to a frame of the video that is playing,
/// so whatever stops the live wallpaper (closing the app, a crash, sign-out) leaves a picture from the same
/// video instead of an old unrelated one. The user's previous wallpaper is remembered so it can be restored.
/// </summary>
public static unsafe partial class WindowsWallpaper
{
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static partial int SystemParametersInfo(uint action, uint param, char* value, uint flags);

    const uint SPI_GETDESKWALLPAPER = 0x0073, SPI_SETDESKWALLPAPER = 0x0014, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;
    const string DesktopKey = @"Control Panel\Desktop";

    public static string FramePath => Path.Combine(Paths.LocalDir, "desktop.jpg");
    static string OriginalFile => Path.Combine(Paths.LocalDir, "original-wallpaper.txt");

    public static string Current()
    {
        char* buf = stackalloc char[1024];
        buf[0] = '\0';
        SystemParametersInfo(SPI_GETDESKWALLPAPER, 1024, buf, 0);
        return new string(buf);
    }

    /// <summary>Sets the desktop wallpaper to <paramref name="file"/>, spanned across all screens.</summary>
    public static bool Set(string file)
    {
        RememberOriginal();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKey, true);
            key?.SetValue("WallpaperStyle", "22");   // span: one picture across the whole virtual screen
            key?.SetValue("TileWallpaper", "0");
        }
        catch (Exception ex) { Log.Write("wallpaper style: " + ex.Message); }
        fixed (char* p = file)
            return SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, p, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) != 0;
    }

    /// <summary>
    /// Saves the user's own wallpaper (a copy of the picture, plus style and tile) the first time we replace it.
    /// The picture is copied because Windows' own cached copy (TranscodedWallpaper) gets overwritten.
    /// </summary>
    static void RememberOriginal()
    {
        if (File.Exists(OriginalFile)) return;
        string cur = Current();
        if (string.Equals(cur, FramePath, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(cur))
        {
            try
            {
                string copy = Path.Combine(Paths.LocalDir, "original-wallpaper" + ImageExtension(cur));
                Directory.CreateDirectory(Paths.LocalDir);
                File.Copy(cur, copy, true);
                cur = copy;
            }
            catch (Exception ex) { Log.Write("could not copy original wallpaper: " + ex.Message); }
        }
        string style = "", tile = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
            style = key?.GetValue("WallpaperStyle") as string ?? "";
            tile = key?.GetValue("TileWallpaper") as string ?? "";
        }
        catch { }
        Directory.CreateDirectory(Paths.LocalDir);
        File.WriteAllLines(OriginalFile, new[] { cur, style, tile });
        Log.Write("remembered original wallpaper: " + cur);
    }

    /// <summary>File extension from the image's first bytes (TranscodedWallpaper has none).</summary>
    static string ImageExtension(string file)
    {
        string ext = Path.GetExtension(file);
        if (ext.Length > 0) return ext;
        try
        {
            using var fs = File.OpenRead(file);
            Span<byte> b = stackalloc byte[4];
            int n = fs.Read(b);
            if (n >= 2 && b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
            if (n >= 4 && b[0] == 0x89 && b[1] == (byte)'P') return ".png";
            if (n >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return ".bmp";
        }
        catch { }
        return ".jpg";
    }

    public static bool HasOriginal => File.Exists(OriginalFile);

    /// <summary>
    /// Called when the engine exits normally: if the Windows wallpaper is our frame, re-apply it so the desktop
    /// is redrawn with it (otherwise it can stay black where the live wallpaper was).
    /// </summary>
    public static void Refresh()
    {
        string cur = Current();
        if (string.Equals(cur, FramePath, StringComparison.OrdinalIgnoreCase) && File.Exists(cur))
            fixed (char* p = cur) SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, p, SPIF_SENDCHANGE);
    }

    /// <summary>Puts the user's previous wallpaper back (used by the control panel and on uninstall).</summary>
    public static bool RestoreOriginal()
    {
        if (!File.Exists(OriginalFile)) return false;
        var lines = File.ReadAllLines(OriginalFile);
        string path = lines.Length > 0 ? lines[0] : "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKey, true);
            if (lines.Length > 1 && lines[1].Length > 0) key?.SetValue("WallpaperStyle", lines[1]);
            if (lines.Length > 2 && lines[2].Length > 0) key?.SetValue("TileWallpaper", lines[2]);
        }
        catch { }
        bool ok;
        fixed (char* p = path) ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, p, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) != 0;
        if (ok) File.Delete(OriginalFile);
        return ok;
    }
}
