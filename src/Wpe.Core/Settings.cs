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
    /// <summary>"freeze" pauses the video, "stop" closes mpv and shows a still frame.</summary>
    public string FullscreenMode { get; set; } = "stop";
    public bool Mute { get; set; } = true;
    /// <summary>"perMonitor" = one wallpaper per screen, "span" = one wallpaper across all screens.</summary>
    public string MonitorMode { get; set; } = "perMonitor";
    public string Hwdec { get; set; } = "auto-safe";
    /// <summary>"belowNormal" or "normal".</summary>
    public string MpvPriority { get; set; } = "belowNormal";
    /// <summary>Optional explicit path to mpv.exe.</summary>
    public string? MpvPath { get; set; }

    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".gif" };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize(File.ReadAllText(Paths.SettingsFile), WpeJson.Default.Settings) ?? new Settings();
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
