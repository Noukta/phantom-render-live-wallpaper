using System.Diagnostics;
using Wpe.Core;

namespace Wpe.Engine;

public enum PauseAction { None = 0, Freeze = 1, Stop = 2 }

/// <summary>An action plus the human-readable reason it was chosen.</summary>
public readonly record struct Verdict(PauseAction Action, string? Reason)
{
    public static readonly Verdict Play = new(PauseAction.None, null);
    public Verdict Max(Verdict o) => o.Action > Action ? o : this;
}

/// <summary>
/// Decides, once per poll, whether each wallpaper should play, freeze or stop.
/// Lock and display state arrive as window messages (no polling); everything else is a few cheap calls.
/// </summary>
public sealed class RuleEngine : IDisposable
{
    readonly uint _ownPid = (uint)Environment.ProcessId;
    readonly Dictionary<uint, string?> _exeCache = new();
    HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    DateTime _nextProcessScan;
    GpuLoad? _gpu;
    DateTime _nextGpuSample;
    int _gpuHigh, _gpuLow;
    bool _gpuActive;

    public bool Locked { get; set; }
    public bool DisplayOff { get; set; }
    /// <summary>Last GPU reading (other programs' 3D use), for diagnostics.</summary>
    public double? LastGpu { get; private set; }

    public static PauseAction Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "stop" => PauseAction.Stop,
        "freeze" => PauseAction.Freeze,
        _ => PauseAction.None,
    };

    static string Normalize(string exe)
    {
        exe = Path.GetFileName(exe.Trim());
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";
    }

    /// <summary>Returns a verdict per host id.</summary>
    public Dictionary<string, Verdict> Evaluate(Settings s, IReadOnlyList<MonitorHost> hosts, bool manualPause)
    {
        var rules = s.Rules;
        Verdict global = Verdict.Play;
        if (manualPause) global = global.Max(new(PauseAction.Stop, "paused from tray"));
        if (Locked) global = global.Max(new(Parse(rules.Locked), "screen locked"));
        if (DisplayOff) global = global.Max(new(Parse(rules.DisplayOff), "display off"));
        if (rules.DebugOnBattery || OnBattery()) global = global.Max(new(Parse(rules.OnBattery), "on battery"));

        // Focused window: fullscreen, maximized, or a listed app.
        var fg = Desktop.Foreground(_ownPid);
        Verdict local = Verdict.Play;   // applies to the foreground window's monitor (or everything, by scope)
        if (fg.Fullscreen) local = local.Max(new(Parse(s.FullscreenMode), "fullscreen app"));
        if (fg.Maximized) local = local.Max(new(Parse(rules.Maximized), "maximized window"));

        string? fgExe = null;
        foreach (var app in rules.Apps)
        {
            if (string.IsNullOrWhiteSpace(app.Exe)) continue;
            string name = Normalize(app.Exe);
            if (app.When == "focused")
            {
                fgExe ??= fg.Pid != 0 ? ExeName(fg.Pid) : null;
                if (fgExe != null && string.Equals(fgExe, name, StringComparison.OrdinalIgnoreCase))
                    local = local.Max(new(Parse(app.Action), name + " focused"));
            }
            else if (RunningSet(rules).Contains(name))
                global = global.Max(new(Parse(app.Action), name + " running"));
        }

        global = global.Max(GpuVerdict(rules.Gpu, hosts));

        var result = new Dictionary<string, Verdict>();
        bool perMonitor = rules.Scope == "monitor" && fg.MonitorId != null;
        foreach (var h in hosts)
        {
            bool affected = !perMonitor || h.Id == "span" || string.Equals(h.Id, fg.MonitorId, StringComparison.OrdinalIgnoreCase);
            result[h.Id] = affected ? global.Max(local) : global;
        }
        return result;
    }

    static bool OnBattery() => Native.GetSystemPowerStatus(out var ps) != 0 && ps.ACLineStatus == 0;

    string? ExeName(uint pid)
    {
        if (_exeCache.TryGetValue(pid, out var n)) return n;
        if (_exeCache.Count > 256) _exeCache.Clear();
        return _exeCache[pid] = Native.ProcessExeName(pid);
    }

    /// <summary>Names of running processes, refreshed every 5 s and only if a "running" rule exists.</summary>
    HashSet<string> RunningSet(PauseRules rules)
    {
        if (DateTime.UtcNow < _nextProcessScan) return _running;
        _nextProcessScan = DateTime.UtcNow.AddSeconds(5);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { set.Add(p.ProcessName + ".exe"); } catch { }
            p.Dispose();
        }
        return _running = set;
    }

    Verdict GpuVerdict(GpuRule rule, IReadOnlyList<MonitorHost> hosts)
    {
        if (!rule.Enabled)
        {
            if (_gpu != null) { _gpu.Dispose(); _gpu = null; _gpuActive = false; LastGpu = null; }
            return Verdict.Play;
        }
        _gpu ??= new GpuLoad();
        if (DateTime.UtcNow >= _nextGpuSample)
        {
            _nextGpuSample = DateTime.UtcNow.AddSeconds(2);
            var exclude = new HashSet<int>();
            foreach (var h in hosts) { try { if (h.Mpv.Process is { } p && h.Mpv.Alive) exclude.Add(p.Id); } catch { } }
            exclude.Add(Environment.ProcessId);
            LastGpu = _gpu.Sample(exclude);
            if (LastGpu is double g)
            {
                // Hysteresis: 3 samples (~6 s) above the threshold to trigger, 3 below threshold-10 to release.
                if (g >= rule.Threshold) { _gpuHigh++; _gpuLow = 0; } else if (g < rule.Threshold - 10) { _gpuLow++; _gpuHigh = 0; }
                if (!_gpuActive && _gpuHigh >= 3) _gpuActive = true;
                if (_gpuActive && _gpuLow >= 3) _gpuActive = false;
            }
        }
        return _gpuActive ? new(Parse(rule.Action), "high GPU load") : Verdict.Play;
    }

    public void Dispose() => _gpu?.Dispose();
}
