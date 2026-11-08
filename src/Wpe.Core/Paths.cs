namespace Wpe.Core;

public static class Paths
{
    public static string RoamingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WallpaperEngine");
    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallpaperEngine");
    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public static string LogFile => Path.Combine(LocalDir, "engine.log");
    public static string FramesDir => Path.Combine(LocalDir, "frames");
    public static string ThumbsDir => Path.Combine(LocalDir, "thumbs");

    public const string EngineExe = "WallpaperEngine.exe";
    public const string UiExe = "PhantomRender.exe";

    /// <summary>mpv.exe: explicit setting, then next to the app, then an mpv sub-folder.</summary>
    public static string? FindMpv(Settings s)
    {
        string?[] candidates =
        {
            s.MpvPath,
            Path.Combine(AppContext.BaseDirectory, "mpv.exe"),
            Path.Combine(AppContext.BaseDirectory, "mpv", "mpv.exe"),
            Path.Combine(LocalDir, "mpv", "mpv.exe"),
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }
}

public static class Log
{
    static readonly object s_lock = new();

    public static void Write(string msg)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(Paths.LocalDir);
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(Paths.LogFile, Paths.LogFile + ".old", true);
                File.AppendAllText(Paths.LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine);
            }
        }
        catch { }
    }
}
