using System.Runtime.InteropServices;

namespace Wpe.Core;

public sealed record MonitorInfo(string Id, RECT Bounds, bool Primary);

/// <summary>Finds the window that sits between the desktop background and the icons.</summary>
public static unsafe class Desktop
{
    static nint s_found;

    /// <summary>
    /// Returns the WorkerW to parent wallpaper windows to. With poke=true, asks Progman to create it first
    /// (message 0x052C). Falls back to Progman itself if no WorkerW exists.
    /// </summary>
    public static nint GetWorkerW(bool poke)
    {
        nint progman = Native.FindWindow("Progman", null);
        if (poke && progman != 0)
            Native.SendMessageTimeout(progman, 0x052C, 0, 0, 0, 1000, out _);

        // Windows 11 24H2+: the WorkerW is a child of Progman.
        nint w = Native.FindWindowEx(progman, 0, "WorkerW", null);
        if (w != 0) return w;

        // Older layout: the WorkerW is the top-level sibling after the one that holds SHELLDLL_DefView.
        s_found = 0;
        Native.EnumWindows(&EnumProc, 0);
        return s_found != 0 ? s_found : progman;
    }

    [UnmanagedCallersOnly]
    static int EnumProc(nint h, nint l)
    {
        if (Native.FindWindowEx(h, 0, "SHELLDLL_DefView", null) != 0)
            s_found = Native.FindWindowEx(0, h, "WorkerW", null);
        return 1;
    }

    /// <summary>True once the shell has created its desktop icon layer (used to wait at sign-in).</summary>
    public static bool ShellReady()
    {
        nint pm = Native.FindWindow("Progman", null);
        if (pm == 0) return false;
        return Native.FindWindowEx(pm, 0, "SHELLDLL_DefView", null) != 0
            || Native.FindWindowEx(0, 0, "WorkerW", null) != 0;
    }

    static List<MonitorInfo>? s_monitors;

    public static List<MonitorInfo> Monitors()
    {
        s_monitors = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(0, 0, &MonitorProc, 0);
        var list = s_monitors;
        s_monitors = null;
        // Stable order: primary first, then left-to-right.
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    [UnmanagedCallersOnly]
    static int MonitorProc(nint mon, nint hdc, RECT* r, nint data)
    {
        MONITORINFOEXW mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (Native.GetMonitorInfo(mon, &mi) != 0)
        {
            string id = new string(mi.szDevice);
            s_monitors!.Add(new MonitorInfo(id, mi.rcMonitor, (mi.dwFlags & 1) != 0));
        }
        return 1;
    }

    public static RECT VirtualBounds(IEnumerable<MonitorInfo> monitors)
    {
        var all = monitors.ToList();
        return new RECT(all.Min(m => m.Bounds.Left), all.Min(m => m.Bounds.Top), all.Max(m => m.Bounds.Right), all.Max(m => m.Bounds.Bottom));
    }

    /// <summary>What is in front right now. Pid is 0 when the shell/desktop itself is focused.</summary>
    public readonly record struct ForegroundInfo(uint Pid, string? MonitorId, bool Fullscreen, bool Maximized);

    /// <summary>
    /// Describes the focused window: whether it is a fullscreen app or game (exclusive or borderless),
    /// whether it is maximized, and which monitor it is on.
    /// </summary>
    public static ForegroundInfo Foreground(uint ownPid)
    {
        bool shellSaysBusy = Native.SHQueryUserNotificationState(out int s) == 0 && (s == 2 || s == 3 || s == 4);
        nint fg = Native.GetForegroundWindow();
        if (fg == 0) return new(0, null, shellSaysBusy, false);
        string cls = Native.ClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return new(0, null, shellSaysBusy, false);
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == ownPid) return new(0, null, false, false);

        nint mon = Native.MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */);
        MONITORINFOEXW mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (Native.GetMonitorInfo(mon, &mi) == 0) return new(pid, null, shellSaysBusy, false);
        string id = new string(mi.szDevice);
        bool covers = Native.GetWindowRect(fg, out RECT r) != 0 && r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        bool maximized = Native.IsZoomed(fg) != 0 && Native.IsWindowVisible(fg) != 0;
        return new(pid, id, shellSaysBusy || covers, maximized);
    }
}
