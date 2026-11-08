using Wpe.Core;

namespace Wpe.Engine;

public enum HostStatus { Empty, Playing, Frozen, Stopping, Stopped }

/// <summary>
/// One wallpaper surface: a child window of the desktop WorkerW covering a monitor (or all of them in span mode),
/// with an mpv process rendering into it.
/// </summary>
public sealed unsafe class MonitorHost : Win32Window
{
    public string Id { get; }
    public RECT Bounds { get; private set; }
    public string? Current { get; private set; }
    public HostStatus Status { get; private set; } = HostStatus.Empty;
    public DateTime NextChange { get; set; }
    public MpvClient Mpv { get; } = new();

    nint _backdrop;
    string? _backdropFile;
    string? _pendingFrame;
    DateTime _stopDeadline;
    static int s_frameCounter;

    public MonitorHost(string id, RECT bounds, nint workerW)
    {
        Id = id;
        Bounds = bounds;
        Create("WpeHost", Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW,
            Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            0, 0, bounds.Width, bounds.Height, workerW);
        Place(workerW);
    }

    /// <summary>Parents the window to the given WorkerW and positions it over its monitor.</summary>
    public void Place(nint workerW)
    {
        if (Native.GetParent(Handle) != workerW) Native.SetParent(Handle, workerW);
        Native.GetWindowRect(workerW, out RECT pr);
        Native.SetWindowPos(Handle, Native.HWND_BOTTOM, Bounds.Left - pr.Left, Bounds.Top - pr.Top, Bounds.Width, Bounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public bool IsAttachedTo(nint workerW) => Native.GetParent(Handle) == workerW;

    public void Play(string mpvExe, string file, Settings s)
    {
        Current = file;
        if (Status == HostStatus.Playing && Mpv.Alive)
        {
            Mpv.Command("loadfile", file, "replace");
            return;
        }
        Mpv.Start(mpvExe, Handle, file, s);
        Status = HostStatus.Playing;
    }

    /// <summary>Remembers a new wallpaper without starting it (used while paused).</summary>
    public void SetPendingFile(string file)
    {
        Current = file;
        // mpv keeps the pause property across files, so the new one loads paused.
        if (Status == HostStatus.Frozen && Mpv.Alive) Mpv.Command("loadfile", file, "replace");
    }

    public void Freeze()
    {
        if (Status != HostStatus.Playing) return;
        Mpv.Command("set_property", "pause", "true");
        Status = HostStatus.Frozen;
    }

    /// <summary>Asks mpv for a still frame, then (on a later Tick) shows it and closes mpv to free the GPU.</summary>
    public void BeginStop()
    {
        if (Status is HostStatus.Stopped or HostStatus.Stopping or HostStatus.Empty) return;
        if (!Mpv.Alive) { FinishStop(); return; }
        Directory.CreateDirectory(Paths.FramesDir);
        _pendingFrame = Path.Combine(Paths.FramesDir, $"frame_{Environment.ProcessId}_{Interlocked.Increment(ref s_frameCounter)}.png");
        Mpv.Command("screenshot-to-file", _pendingFrame, "video");
        _stopDeadline = DateTime.UtcNow.AddSeconds(2);
        Status = HostStatus.Stopping;
    }

    /// <summary>Called every poll; completes a pending stop without blocking the UI thread.</summary>
    public void Tick()
    {
        if (Status != HostStatus.Stopping) return;
        if (_pendingFrame != null && File.Exists(_pendingFrame))
        {
            nint img = GdiPlus.Load(_pendingFrame);
            if (img != 0) { SetBackdrop(img, _pendingFrame); _pendingFrame = null; FinishStop(); return; }
        }
        if (DateTime.UtcNow > _stopDeadline) FinishStop();
    }

    void FinishStop()
    {
        Mpv.Stop();
        Status = HostStatus.Stopped;
        if (_pendingFrame != null) { TryDelete(_pendingFrame); _pendingFrame = null; }
    }

    public void Resume(string mpvExe, Settings s)
    {
        if (Current == null) return;
        if (Status == HostStatus.Frozen && Mpv.Alive) { Mpv.Command("set_property", "pause", "false"); Status = HostStatus.Playing; return; }
        if (Status == HostStatus.Stopping) FinishStop();
        Status = HostStatus.Empty;
        Play(mpvExe, Current, s);
    }

    /// <summary>Restarts mpv if it exited on its own while it should be playing.</summary>
    public void EnsureRunning(string mpvExe, Settings s)
    {
        if (Status is HostStatus.Playing or HostStatus.Frozen && !Mpv.Alive && Current != null)
        {
            Log.Write($"{Id}: mpv not running, restarting");
            Status = HostStatus.Empty;
            Play(mpvExe, Current, s);
        }
    }

    void SetBackdrop(nint img, string? file)
    {
        nint old = _backdrop;
        string? oldFile = _backdropFile;
        _backdrop = img;
        _backdropFile = file;
        Native.InvalidateRect(Handle, 0, 1);
        GdiPlus.Dispose(old);
        if (oldFile != null) TryDelete(oldFile);
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }

    protected override nint WndProc(nint h, uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case Native.WM_ERASEBKGND:
                return 1;
            case Native.WM_PAINT:
                PAINTSTRUCT ps;
                nint hdc = Native.BeginPaint(h, &ps);
                Native.GetClientRect(h, out RECT rc);
                Native.FillRect(hdc, &rc, Native.GetStockObject(4 /* BLACK_BRUSH */));
                if (_backdrop != 0) GdiPlus.DrawCover(hdc, _backdrop, rc.Width, rc.Height);
                Native.EndPaint(h, &ps);
                return 0;
        }
        return Native.DefWindowProc(h, msg, w, l);
    }

    public override void Dispose()
    {
        Mpv.Dispose();
        SetBackdrop(0, null);
        base.Dispose();
    }
}
