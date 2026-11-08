namespace Wpe.Core;

/// <summary>
/// Messages on the engine's control pipe (\\.\pipe\wpe_control). One JSON object per line.
/// Requests carry an id; the engine answers with a response of the same id. Events have no id.
/// </summary>
public static class Control
{
    public const string PipeName = "wpe_control";

    public const string GetState = "getState";
    public const string Next = "next";
    public const string SetWallpaper = "setWallpaper";
    public const string Pause = "pause";
    public const string ReloadSettings = "reloadSettings";
    public const string Stats = "stats";
}

public sealed class ControlRequest
{
    public int Id { get; set; }
    public string Cmd { get; set; } = "";
    /// <summary>Monitor id (e.g. \\.\DISPLAY1); null means all monitors.</summary>
    public string? Monitor { get; set; }
    public string? Path { get; set; }
    public bool? Paused { get; set; }
}

public sealed class ControlResponse
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public EngineState? State { get; set; }
    public EngineStats? Stats { get; set; }
}

public sealed class ControlEvent
{
    public string Event { get; set; } = "";
    public string? Monitor { get; set; }
    public string? Path { get; set; }
    public string? Reason { get; set; }
}

public sealed class EngineState
{
    public bool ManualPause { get; set; }
    /// <summary>Why the wallpaper is paused right now (null if playing).</summary>
    public string? PauseReason { get; set; }
    public string MonitorMode { get; set; } = "";
    /// <summary>Other programs' 3D GPU use in percent (only when the GPU rule is enabled).</summary>
    public double? GpuLoad { get; set; }
    public List<MonitorState> Monitors { get; set; } = new();
}

public sealed class MonitorState
{
    public string Id { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Current { get; set; }
    /// <summary>"playing", "frozen", "stopping", "stopped" or "empty".</summary>
    public string Status { get; set; } = "";
    /// <summary>Why this screen is paused (null if playing).</summary>
    public string? PauseReason { get; set; }
}

public sealed class EngineStats
{
    public double EngineMemoryMB { get; set; }
    public double EngineCpuPercent { get; set; }
    public double MpvMemoryMB { get; set; }
    public double MpvCpuPercent { get; set; }
    public int MpvProcesses { get; set; }
}
