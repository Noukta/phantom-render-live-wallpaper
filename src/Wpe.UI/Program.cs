using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Velopack;
using Wpe.Core;

namespace Wpe.UI;

/// <summary>
/// Entry point. Velopack runs first: during install, update and uninstall it calls the hooks below and exits
/// without showing any window. A normal start then opens the control panel.
/// </summary>
public static class Program
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "WallpaperEngine";

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUpdateFastCallback(_ => StopEngine())       // files must not be in use while they are replaced
            .OnBeforeUninstallFastCallback(_ => Uninstall())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    public static string EngineExe => Path.Combine(AppContext.BaseDirectory, Paths.EngineExe);

    public static bool EngineRunning() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.EngineExe)).Length > 0;

    public static void StartEngine()
    {
        if (EngineRunning() || !File.Exists(EngineExe)) return;
        Process.Start(new ProcessStartInfo(EngineExe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
    }

    /// <summary>Stops the engine and its video players and waits for them to exit.</summary>
    public static void StopEngine()
    {
        foreach (string name in new[] { Path.GetFileNameWithoutExtension(Paths.EngineExe), "mpv" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    // Only our own mpv: the one next to the engine or in our data folder.
                    if (name == "mpv")
                    {
                        string? path = p.MainModule?.FileName;
                        if (path == null || !(path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
                                              || path.StartsWith(Paths.LocalDir, StringComparison.OrdinalIgnoreCase))) continue;
                    }
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
                finally { p.Dispose(); }
            }
    }

    /// <summary>The engine path stored in the startup entry, or null if Phantom Render doesn't start with Windows.</summary>
    public static string? StartupEntry() => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunValue) as string;

    public static void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, "\"" + EngineExe + "\"");
        else key.DeleteValue(RunValue, false);
    }

    /// <summary>
    /// Leaves the PC as it was: engine stopped, startup entry gone, the user's own Windows wallpaper back,
    /// caches removed. Settings are kept in case the user reinstalls.
    /// </summary>
    static void Uninstall()
    {
        StopEngine();
        try { SetStartup(false); } catch { }
        try { WindowsWallpaper.RestoreOriginal(); } catch { }
        foreach (string dir in new[] { Paths.ThumbsDir, Paths.FramesDir, Path.Combine(Paths.LocalDir, "mpv") })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        try { File.Delete(WindowsWallpaper.FramePath); } catch { }
    }
}
