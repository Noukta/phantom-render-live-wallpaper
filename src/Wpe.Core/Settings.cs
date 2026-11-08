using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wpe.Core;

public sealed class Settings
{
    public int Version { get; set; } = 1;
    /// <summary>Folders scanned (recursively) for wallpapers.</summary>
    public List<string> Folders { get; set; } = new();
    public int IntervalMinutes { get; set; } = 10;
    public bool Shuffle { get; set; } = true;
    /// <summary>Action while a fullscreen app/game is in front: "stop", "freeze" or "none".</summary>
    public string FullscreenMode { get; set; } = "stop";
    /// <summary>The other pause rules.</summary>
    public PauseRules Rules { get; set; } = new();
    public bool Mute { get; set; } = true;
    /// <summary>"perMonitor" = one wallpaper per screen, "span" = one wallpaper across all screens.</summary>
    public string MonitorMode { get; set; } = "perMonitor";
    public string Hwdec { get; set; } = "auto-safe";
    /// <summary>"belowNormal" or "normal".</summary>
    public string MpvPriority { get; set; } = "belowNormal";
    /// <summary>
    /// Keep the regular Windows wallpaper set to a frame of the playing video, so closing the app (or anything
    /// else that stops it) shows a picture from the same video.
    /// </summary>
    public bool SyncWindowsWallpaper { get; set; } = true;
    /// <summary>Optional explicit path to mpv.exe.</summary>
    public string? MpvPath { get; set; }
    public List<Playlist> Playlists { get; set; } = new();
    /// <summary>Per-screen choices, keyed by monitor id (e.g. \\.\DISPLAY1) or "span".</summary>
    public Dictionary<string, ScreenAssignment> Screens { get; set; } = new();

    public Playlist? FindPlaylist(string? name) =>
        name == null ? null : Playlists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public ScreenAssignment? ScreenFor(string id) => Screens.TryGetValue(id, out var a) ? a : null;

    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".gif" };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                string text = File.ReadAllText(Paths.SettingsFile);
                var loaded = JsonSerializer.Deserialize(text, WpeJson.Default.Settings) ?? new Settings();
                // Write back so options added in newer versions show up in the file with their defaults.
                if (JsonSerializer.Serialize(loaded, WpeJson.Default.Settings) != text) loaded.Save();
                return loaded;
            }
        }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        var s = ImportLegacy() ?? new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.SettingsFile)!);
        string tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, WpeJson.Default.Settings));
        File.Move(tmp, Paths.SettingsFile, true);
    }

    /// <summary>Imports config.ini from the first version of the engine, if present.</summary>
    static Settings? ImportLegacy()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "config.ini"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WallpaperEngine", "config.ini"),
        };
        string? ini = candidates.FirstOrDefault(File.Exists);
        if (ini == null) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(ini))
        {
            int i = line.IndexOf('=');
            if (i > 0 && !line.TrimStart().StartsWith('#')) d[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        var s = new Settings();
        if (d.TryGetValue("folder", out var f)) s.Folders.Add(f);
        if (d.TryGetValue("interval_minutes", out var m) && int.TryParse(m, out int mi) && mi > 0) s.IntervalMinutes = mi;
        if (d.TryGetValue("fullscreen_mode", out var fm)) s.FullscreenMode = fm.ToLowerInvariant() == "freeze" ? "freeze" : "stop";
        if (d.TryGetValue("mute", out var mu)) s.Mute = mu.ToLowerInvariant() != "false";
        Log.Write("imported legacy settings from " + ini);
        return s;
    }

    public List<string> ScanVideos()
    {
        var result = new List<string>();
        foreach (string folder in Folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                result.AddRange(Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(p => VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())));
            }
            catch (Exception ex) { Log.Write("scan failed for " + folder + ": " + ex.Message); }
        }
        return result;
    }
}

public sealed class Playlist
{
    public string Name { get; set; } = "";
    public List<string> Items { get; set; } = new();
    public bool Shuffle { get; set; } = true;
    /// <summary>Minutes between changes; null uses the global interval.</summary>
    public int? IntervalMinutes { get; set; }
}

public sealed class ScreenAssignment
{
    /// <summary>Playlist to rotate through; null means every video in the library folders.</summary>
    public string? Playlist { get; set; }
    /// <summary>A pinned wallpaper: shown permanently, no rotation.</summary>
    public string? Pinned { get; set; }
}

/// <summary>
/// Pause rules. Every action is "none", "freeze" (pause the video) or "stop" (close mpv, show a still frame);
/// when several rules match, the strictest action wins.
/// </summary>
public sealed class PauseRules
{
    /// <summary>"all" pauses every screen; "monitor" pauses only the screen the fullscreen/maximized window is on.</summary>
    public string Scope { get; set; } = "all";
    /// <summary>A maximized window is focused (the wallpaper is hidden anyway).</summary>
    public string Maximized { get; set; } = "freeze";
    public string OnBattery { get; set; } = "stop";
    public string Locked { get; set; } = "stop";
    public string DisplayOff { get; set; } = "stop";
    public List<AppRule> Apps { get; set; } = new();
    public GpuRule Gpu { get; set; } = new();
    /// <summary>Testing aid: behave as if on battery.</summary>
    public bool DebugOnBattery { get; set; }
}

public sealed class AppRule
{
    /// <summary>Executable name, e.g. "VALORANT-Win64-Shipping.exe" (".exe" optional, case-insensitive).</summary>
    public string Exe { get; set; } = "";
    /// <summary>"running" (anywhere) or "focused" (in front).</summary>
    public string When { get; set; } = "running";
    public string Action { get; set; } = "stop";
}

public sealed class GpuRule
{
    public bool Enabled { get; set; }
    /// <summary>3D GPU use by other programs, in percent, that triggers the rule (sustained for ~6 s).</summary>
    public int Threshold { get; set; } = 85;
    public string Action { get; set; } = "freeze";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
[JsonSerializable(typeof(ControlEvent))]
public partial class WpeJson : JsonSerializerContext { }

/// <summary>Single-line JSON for the control pipe.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
[JsonSerializable(typeof(ControlEvent))]
public partial class WpeWire : JsonSerializerContext { }
