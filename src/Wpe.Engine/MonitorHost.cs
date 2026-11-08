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
    DateTime _freeBackdropAt;
    DateTime _stopDeadline;
    static int s_frameCounter;

    // Frame grabs: mpv writes a JPEG of the current video frame; we use it once the file has stopped growing.
    string? _shotFile;          // file mpv is writing
    long _shotSize = -1;        // its size at the previous tick
    DateTime _shotDue = DateTime.MaxValue;   // when to request a desktop snapshot of the current video

    /// <summary>The latest frame grabbed from the current video (JPEG), or null.</summary>
    public string? LastFrame { get; private set; }
    /// <summary>Raised when a new frame of the playing video is available (used for the Windows wallpaper).</summary>
    public event Action<MonitorHost>? FrameReady;

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
        bool newVideo = !string.Equals(file, Current, StringComparison.OrdinalIgnoreCase) || LastFrame == null;
        Current = file;
        if (newVideo) _shotDue = DateTime.UtcNow.AddSeconds(3);   // grab a frame once the video is running
        if (Status == HostStatus.Playing && Mpv.Alive)
        {
            Mpv.Command("loadfile", file, "replace");
            return;
        }
        Mpv.Start(mpvExe, Handle, file, s);
        Status = HostStatus.Playing;
    }

    string NewFramePath()
    {
        Directory.CreateDirectory(Paths.FramesDir);
        return Path.Combine(Paths.FramesDir, $"frame_{Environment.ProcessId}_{Interlocked.Increment(ref s_frameCounter)}.jpg");
    }

    void RequestShot()
    {
        if (_shotFile != null) return;   // one at a time
        _shotFile = NewFramePath();
        _shotSize = -1;
        Mpv.Command("screenshot-to-file", _shotFile, "video");
    }

    /// <summary>True once the requested frame file exists and did not grow since the last tick.</summary>
    bool ShotComplete()
    {
        if (_shotFile == null) return false;
        long size;
        try { var fi = new FileInfo(_shotFile); size = fi.Exists ? fi.Length : -1; } catch { size = -1; }
        bool stable = size > 0 && size == _shotSize;
        _shotSize = size;
        return stable;
    }

    void AcceptShot()
    {
        string? old = LastFrame;
        LastFrame = _shotFile;
        _shotFile = null;
        if (old != null && old != LastFrame) TryDelete(old);
    }

    /// <summary>Remembers a new wallpaper without starting it (used while paused).</summary>
    public void SetPendingFile(string file)
    {
        if (!string.Equals(file, Current, StringComparison.OrdinalIgnoreCase)) _shotDue = DateTime.UtcNow.AddSeconds(3);
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

    /// <summary>
    /// Asks mpv for the frame on screen right now; on a later Tick that frame becomes the still image and mpv
    /// is closed to free the GPU. If the grab fails, the last frame of the same video is used instead.
    /// </summary>
    public void BeginStop()
    {
        if (Status is HostStatus.Stopped or HostStatus.Stopping or HostStatus.Empty) return;
        if (!Mpv.Alive) { FinishStop(); return; }
        if (_shotFile != null) { TryDelete(_shotFile); _shotFile = null; }   // a pending desktop grab is superseded
        RequestShot();
        _stopDeadline = DateTime.UtcNow.AddSeconds(5);
        Status = HostStatus.Stopping;
    }

    /// <summary>Called every poll (1 s); finishes frame grabs and pending stops without blocking.</summary>
    public void Tick()
    {
        if (Status == HostStatus.Stopping)
        {
            if (ShotComplete()) { AcceptShot(); FinishStop(); }
            else if (DateTime.UtcNow > _stopDeadline) { Log.Write($"{Id}: frame grab timed out"); FinishStop(); }
            return;
        }
        if (Status == HostStatus.Playing && _backdrop != 0 && DateTime.UtcNow > _freeBackdropAt) SetBackdrop(0);   // video covers it again
        if (Status == HostStatus.Playing && Mpv.Alive)
        {
            if (_shotFile == null && DateTime.UtcNow >= _shotDue) { _shotDue = DateTime.MaxValue; RequestShot(); }
            else if (ShotComplete()) { AcceptShot(); FrameReady?.Invoke(this); }
        }
    }

    void FinishStop()
    {
        Mpv.Stop();
        Status = HostStatus.Stopped;
        if (_shotFile != null) { TryDelete(_shotFile); _shotFile = null; }
        // Show the frame we stopped on (or the latest one from this video). Scaled to the window: no 4K buffers.
        nint img = LastFrame != null ? GdiPlus.LoadScaled(LastFrame, Bounds.Width, Bounds.Height) : 0;
        if (img == 0 && LastFrame != null) Log.Write($"{Id}: could not load still frame {LastFrame}");
        SetBackdrop(img);
    }

    /// <summary>The verdict currently applied to this host.</summary>
    public Verdict Applied { get; private set; } = Verdict.Play;

    /// <summary>Moves the host to the state the verdict asks for. Returns true if the verdict changed.</summary>
    public bool Apply(Verdict v, string? mpvExe, Settings s)
    {
        bool changed = v.Action != Applied.Action || v.Reason != Applied.Reason;
        Applied = v;
        switch (v.Action)
        {
            case PauseAction.None:
                if (Status != HostStatus.Playing && mpvExe != null) Resume(mpvExe, s);
                break;
            case PauseAction.Freeze:
                if (Status == HostStatus.Playing) Freeze();   // a stopped host stays stopped: cheaper
                break;
            case PauseAction.Stop:
                if (Status is HostStatus.Playing or HostStatus.Frozen) BeginStop();
                break;
        }
        return changed;
    }

    public void Resume(string mpvExe, Settings s)
    {
        if (Current == null) return;
        if (Status == HostStatus.Frozen && Mpv.Alive) { Mpv.Command("set_property", "pause", "false"); Status = HostStatus.Playing; return; }
        if (Status == HostStatus.Stopping) FinishStop();
        Status = HostStatus.Empty;
        // Keep the still frame a moment so there is no black flash while mpv starts, then free it.
        _freeBackdropAt = DateTime.UtcNow.AddSeconds(3);
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

    void SetBackdrop(nint img)
    {
        nint old = _backdrop;
        _backdrop = img;
        if (Handle != 0) Native.InvalidateRect(Handle, 0, 1);
        GdiPlus.Dispose(old);
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
                if (_backdrop != 0) GdiPlus.DrawAt(hdc, _backdrop, rc.Width, rc.Height);
                Native.EndPaint(h, &ps);
                return 0;
        }
        return Native.DefWindowProc(h, msg, w, l);
    }

    public override void Dispose()
    {
        Mpv.Dispose();
        SetBackdrop(0);
        if (_shotFile != null) TryDelete(_shotFile);
        base.Dispose();
    }
}
