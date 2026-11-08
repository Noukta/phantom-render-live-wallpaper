using System.Diagnostics;
using Wpe.Core;

namespace Wpe.Engine;

/// <summary>
/// Owns the hidden top-level window (tray, timers, broadcasts such as TaskbarCreated and WM_DISPLAYCHANGE),
/// the wallpaper hosts and the pause logic. Everything here runs on the UI thread.
/// </summary>
public sealed class Engine : Win32Window
{
    const nuint TimerPoll = 1, TimerDisplayChange = 2, TimerReattach = 3, TimerWallpaper = 4;
    const int CmdOpenUi = 6, CmdNext = 1, CmdPause = 2, CmdOpenFolder = 3, CmdReload = 4, CmdOpenSettings = 5, CmdExit = 9;

    static readonly uint s_taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

    readonly Random _rnd = new();
    readonly List<MonitorHost> _hosts = new();
    Settings _settings = Settings.Load();
    string? _mpvExe;
    Tray? _tray;
    ControlServer? _control;
    readonly RuleEngine _rules = new();
    bool _manualPause;
    TimeSpan _lastEngineCpu, _lastMpvCpu;
    DateTime _lastStatsAt;

    public void Start()
    {
        Log.Write($"engine {typeof(Engine).Assembly.GetName().Version} starting; folders={string.Join(";", _settings.Folders)} mode={_settings.FullscreenMode} monitors={_settings.MonitorMode}");
        _mpvExe = Paths.FindMpv(_settings);
        if (_mpvExe == null) Log.Write("mpv.exe not found; set mpvPath in " + Paths.SettingsFile);

        // Frame grabs from a previous run are no longer needed.
        try { if (Directory.Exists(Paths.FramesDir)) foreach (var f in Directory.GetFiles(Paths.FramesDir)) File.Delete(f); } catch { }

        // At sign-in the desktop may not exist yet: wait (up to 60 s) for the shell's icon layer.
        for (int i = 0; i < 120 && !Desktop.ShellReady(); i++) Thread.Sleep(500);
        Thread.Sleep(1500);

        // Hidden top-level popup (message-only windows don't receive broadcasts like TaskbarCreated).
        Create("WpeEngine", Native.WS_EX_TOOLWINDOW, Native.WS_POPUP, 0, 0, 0, 0, 0);
        _tray = new Tray(Handle);
        _control = new ControlServer(Handle);
        // Lock/unlock and display on/off arrive as messages, so these rules cost nothing between events.
        Native.WTSRegisterSessionNotification(Handle, 0);
        Guid display = Native.GUID_CONSOLE_DISPLAY_STATE;
        unsafe { Native.RegisterPowerSettingNotification(Handle, &display, 0); }
        BuildHosts();
        Native.SetTimer(Handle, TimerPoll, 1000, 0);
    }

    // ---------- hosts ----------

    void BuildHosts()
    {
        var previous = _hosts.ToDictionary(h => h.Id, h => h.Current);
        foreach (var h in _hosts) h.Dispose();
        _hosts.Clear();

        var monitors = Desktop.Monitors();
        if (monitors.Count == 0) { Log.Write("no monitors found"); return; }
        nint workerW = Desktop.GetWorkerW(true);

        if (_settings.MonitorMode == "span")
            _hosts.Add(new MonitorHost("span", Desktop.VirtualBounds(monitors), workerW));
        else
            foreach (var m in monitors) _hosts.Add(new MonitorHost(m.Id, m.Bounds, workerW));

        foreach (var h in _hosts)
        {
            Log.Write($"host {h.Id} at {h.Bounds} parent {workerW:X}");
            h.FrameReady += _ => Native.SetTimer(Handle, TimerWallpaper, 1500, 0);   // batch frames from all screens
            string? pinned = Pinned(h);
            string? file = pinned ?? (previous.TryGetValue(h.Id, out var prev) && prev != null && File.Exists(prev) && InSource(h, prev) ? prev : Pick(h));
            if (file == null) { Log.Write("no videos found"); continue; }
            if (_mpvExe != null) h.Play(_mpvExe, file, _settings);   // the next poll pauses it if a rule says so
            h.NextChange = NextChangeFor(h);
        }
        UpdateTooltip();
    }

    string? Pinned(MonitorHost h)
    {
        string? p = _settings.ScreenFor(h.Id)?.Pinned;
        return p != null && File.Exists(p) ? p : null;
    }

    Playlist? PlaylistFor(MonitorHost h) => _settings.FindPlaylist(_settings.ScreenFor(h.Id)?.Playlist);

    /// <summary>Videos this screen rotates through: its playlist, or the whole library.</summary>
    List<string> Source(MonitorHost h)
    {
        var pl = PlaylistFor(h);
        if (pl != null)
        {
            var items = pl.Items.Where(File.Exists).ToList();
            if (items.Count > 0) return items;
        }
        return _settings.ScanVideos();
    }

    bool InSource(MonitorHost h, string file) => Source(h).Contains(file, StringComparer.OrdinalIgnoreCase);

    DateTime NextChangeFor(MonitorHost h) =>
        Pinned(h) != null ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(Math.Max(1, PlaylistFor(h)?.IntervalMinutes ?? _settings.IntervalMinutes));

    string? Pick(MonitorHost host)
    {
        var all = Source(host);
        if (all.Count == 0) return null;
        bool shuffle = PlaylistFor(host)?.Shuffle ?? _settings.Shuffle;
        if (!shuffle)
        {
            // Playlists keep their own order; the library goes alphabetically.
            if (PlaylistFor(host) == null) all.Sort(StringComparer.OrdinalIgnoreCase);
            int i = host.Current == null ? -1 : all.FindIndex(f => string.Equals(f, host.Current, StringComparison.OrdinalIgnoreCase));
            return all[(i + 1) % all.Count];
        }
        var inUse = _hosts.Where(h => h != host && h.Current != null).Select(h => h.Current!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = all.Where(f => !string.Equals(f, host.Current, StringComparison.OrdinalIgnoreCase) && !inUse.Contains(f)).ToList();
        if (candidates.Count == 0) candidates = all.Where(f => !string.Equals(f, host.Current, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) candidates = all;
        return candidates[_rnd.Next(candidates.Count)];
    }

    void SetWallpaper(MonitorHost h, string file)
    {
        if (h.Applied.Action != PauseAction.None || _mpvExe == null) h.SetPendingFile(file);
        else h.Play(_mpvExe, file, _settings);
        h.NextChange = NextChangeFor(h);
        Log.Write($"{h.Id}: {file}");
        _control?.Broadcast(new ControlEvent { Event = "wallpaperChanged", Monitor = h.Id, Path = file });
    }

    void Next(MonitorHost h)
    {
        string? f = Pick(h);
        if (f != null) SetWallpaper(h, f);
    }

    // ---------- polling ----------

    void Poll()
    {
        // The shell can recreate its desktop windows (e.g. right after sign-in); follow it.
        nint want = Desktop.GetWorkerW(false);
        foreach (var h in _hosts)
            if (want != 0 && !h.IsAttachedTo(want)) { Log.Write($"{h.Id}: desktop window changed, re-attaching"); h.Place(want); }

        ApplyRules();

        foreach (var h in _hosts)
        {
            h.Tick();
            if (h.Applied.Action != PauseAction.None || _mpvExe == null) continue;
            h.EnsureRunning(_mpvExe, _settings);
            if (DateTime.UtcNow >= h.NextChange) Next(h);
        }
    }

    void ApplyRules()
    {
        var verdicts = _rules.Evaluate(_settings, _hosts, _manualPause);
        bool any = false;
        foreach (var h in _hosts)
        {
            var v = verdicts.TryGetValue(h.Id, out var x) ? x : Verdict.Play;
            if (!h.Apply(v, _mpvExe, _settings)) continue;
            any = true;
            string what = v.Action == PauseAction.None ? "playing" : v.Action.ToString().ToLowerInvariant() + ": " + v.Reason;
            Log.Write($"{h.Id}: {what}");
            _control?.Broadcast(new ControlEvent
            {
                Event = v.Action == PauseAction.None ? "resumed" : "paused", Monitor = h.Id, Reason = v.Reason,
            });
        }
        if (any) UpdateTooltip();
    }

    void UpdateTooltip()
    {
        var paused = _hosts.Where(h => h.Applied.Action != PauseAction.None).Select(h => h.Applied.Reason).Distinct().ToList();
        string state = paused.Count == 0 ? "playing" : "paused (" + string.Join(", ", paused) + ")";
        _tray?.SetTooltip("Phantom Render - " + state);
    }

    // ---------- control pipe ----------

    ControlResponse HandleRequest(ControlRequest r)
    {
        switch (r.Cmd)
        {
            case Control.GetState:
                return new ControlResponse { Ok = true, State = State() };
            case Control.Next:
                foreach (var h in Targets(r.Monitor)) Next(h);
                return new ControlResponse { Ok = true, State = State() };
            case Control.SetWallpaper:
                if (string.IsNullOrEmpty(r.Path) || !File.Exists(r.Path)) return Fail("file not found");
                var targets = Targets(r.Monitor).ToList();
                if (targets.Count == 0) return Fail("unknown monitor");
                foreach (var h in targets) SetWallpaper(h, r.Path);
                return new ControlResponse { Ok = true, State = State() };
            case Control.Pause:
                _manualPause = r.Paused ?? !_manualPause;
                ApplyRules();
                return new ControlResponse { Ok = true, State = State() };
            case Control.ReloadSettings:
                Reload();
                return new ControlResponse { Ok = true, State = State() };
            case Control.Stats:
                return new ControlResponse { Ok = true, Stats = Stats() };
            default:
                return Fail("unknown command " + r.Cmd);
        }
    }

    static ControlResponse Fail(string e) => new() { Ok = false, Error = e };

    IEnumerable<MonitorHost> Targets(string? monitor) =>
        monitor == null ? _hosts : _hosts.Where(h => string.Equals(h.Id, monitor, StringComparison.OrdinalIgnoreCase));

    EngineState State() => new()
    {
        ManualPause = _manualPause,
        PauseReason = _hosts.Select(h => h.Applied.Reason).FirstOrDefault(r => r != null),
        MonitorMode = _settings.MonitorMode,
        GpuLoad = _rules.LastGpu,
        Monitors = _hosts.Select(h => new MonitorState
        {
            Id = h.Id, X = h.Bounds.Left, Y = h.Bounds.Top, Width = h.Bounds.Width, Height = h.Bounds.Height,
            Current = h.Current, Status = h.Status.ToString().ToLowerInvariant(), PauseReason = h.Applied.Reason,
        }).ToList(),
    };

    EngineStats Stats()
    {
        var now = DateTime.UtcNow;
        double elapsed = _lastStatsAt == default ? 0 : (now - _lastStatsAt).TotalMilliseconds * Environment.ProcessorCount;
        using var self = Process.GetCurrentProcess();
        TimeSpan engineCpu = self.TotalProcessorTime, mpvCpu = TimeSpan.Zero;
        double mpvMem = 0;
        int count = 0;
        foreach (var h in _hosts)
        {
            var p = h.Mpv.Process;
            if (p == null || !h.Mpv.Alive) continue;
            try { p.Refresh(); mpvCpu += p.TotalProcessorTime; mpvMem += p.WorkingSet64; count++; } catch { }
        }
        var s = new EngineStats
        {
            EngineMemoryMB = Math.Round(self.WorkingSet64 / 1048576.0, 1),
            MpvMemoryMB = Math.Round(mpvMem / 1048576.0, 1),
            MpvProcesses = count,
            EngineCpuPercent = elapsed > 0 ? Math.Round((engineCpu - _lastEngineCpu).TotalMilliseconds / elapsed * 100, 2) : 0,
            MpvCpuPercent = elapsed > 0 ? Math.Round(Math.Max(0, (mpvCpu - _lastMpvCpu).TotalMilliseconds) / elapsed * 100, 2) : 0,
        };
        _lastEngineCpu = engineCpu; _lastMpvCpu = mpvCpu; _lastStatsAt = now;
        return s;
    }

    /// <summary>
    /// Applies changed settings. Only changes that affect how mpv runs or the screen layout restart the
    /// wallpapers; playlist, pin and interval changes are applied in place (no flicker).
    /// </summary>
    void Reload()
    {
        var old = _settings;
        var oldSig = _hosts.ToDictionary(h => h.Id, ScreenSignature);
        _settings = Settings.Load();
        _mpvExe = Paths.FindMpv(_settings);
        bool rebuild = old.MonitorMode != _settings.MonitorMode || old.Hwdec != _settings.Hwdec || old.Mute != _settings.Mute
            || old.MpvPriority != _settings.MpvPriority || old.MpvPath != _settings.MpvPath || _hosts.Count == 0;
        Log.Write("settings reloaded" + (rebuild ? " (restarting wallpapers)" : ""));
        if (rebuild) { BuildHosts(); return; }

        foreach (var h in _hosts)
        {
            if (oldSig.TryGetValue(h.Id, out var sig) && sig == ScreenSignature(h)) continue;
            string? pinned = Pinned(h);
            if (pinned != null) { if (!string.Equals(pinned, h.Current, StringComparison.OrdinalIgnoreCase)) SetWallpaper(h, pinned); }
            else if (h.Current == null || !InSource(h, h.Current)) Next(h);
            h.NextChange = NextChangeFor(h);
        }
    }

    /// <summary>Everything about a screen's rotation that a settings change could alter.</summary>
    string ScreenSignature(MonitorHost h)
    {
        var a = _settings.ScreenFor(h.Id);
        var pl = PlaylistFor(h);
        return string.Join("|", a?.Pinned, a?.Playlist, pl?.Shuffle, pl?.IntervalMinutes, pl == null ? "" : string.Join(";", pl.Items),
            _settings.IntervalMinutes, _settings.Shuffle, string.Join(";", _settings.Folders));
    }

    // ---------- window messages ----------

    void ShowTrayMenu()
    {
        int cmd = _tray!.ShowMenu(new (int, string, bool)[]
        {
            (CmdOpenUi, "Open Phantom Render", false),
            (0, "", false),
            (CmdNext, "Next wallpaper", false),
            (CmdPause, "Pause", _manualPause),
            (0, "", false),
            (CmdOpenFolder, "Open wallpaper folder", false),
            (CmdOpenSettings, "Open settings file", false),
            (CmdReload, "Reload settings", false),
            (0, "", false),
            (CmdExit, "Exit", false),
        });
        switch (cmd)
        {
            case CmdOpenUi: OpenUi(); break;
            case CmdNext: foreach (var h in _hosts) Next(h); break;
            case CmdPause: _manualPause = !_manualPause; ApplyRules(); break;
            case CmdOpenFolder: if (_settings.Folders.Count > 0) Open(_settings.Folders[0]); break;
            case CmdOpenSettings: Open(Paths.SettingsFile); break;
            case CmdReload: Reload(); break;
            case CmdExit: Native.PostQuitMessage(0); break;
        }
    }

    /// <summary>
    /// Sets the Windows wallpaper to the latest frame of each screen's video, laid out like the screens, so
    /// anything that stops the live wallpaper (closing the app, a crash, sign-out) still shows the same video.
    /// Runs only when a new video has started playing, never during a game.
    /// </summary>
    void SyncWindowsWallpaper()
    {
        if (!_settings.SyncWindowsWallpaper) return;
        var parts = _hosts.Where(h => h.LastFrame != null && File.Exists(h.LastFrame)).ToList();
        if (parts.Count == 0) return;
        var monitors = Desktop.Monitors();
        if (monitors.Count == 0) return;
        RECT v = Desktop.VirtualBounds(monitors);
        var layout = parts.Select(h => (h.LastFrame!, new RECT(h.Bounds.Left - v.Left, h.Bounds.Top - v.Top, h.Bounds.Right - v.Left, h.Bounds.Bottom - v.Top)));
        var sw = Stopwatch.StartNew();
        if (!GdiPlus.ComposeJpeg(WindowsWallpaper.FramePath, v.Width, v.Height, layout)) { Log.Write("windows wallpaper: compose failed"); return; }
        bool ok = WindowsWallpaper.Set(WindowsWallpaper.FramePath);
        Log.Write($"windows wallpaper {(ok ? "set" : "NOT set")} to current frame ({sw.ElapsedMilliseconds} ms)");
    }

    /// <summary>Starts the control panel (a separate process that exits when its window closes).</summary>
    static void OpenUi()
    {
        string ui = Path.Combine(AppContext.BaseDirectory, Paths.UiExe);
        if (File.Exists(ui)) Open(ui);
        else { Log.Write("control panel not found: " + ui); Open(Paths.SettingsFile); }
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Log.Write("open: " + ex.Message); }
    }

    protected override nint WndProc(nint h, uint msg, nint w, nint l)
    {
        if (msg == Native.WM_TIMER)
        {
            switch ((nuint)w)
            {
                case TimerPoll: Poll(); break;
                case TimerDisplayChange: Native.KillTimer(h, TimerDisplayChange); Log.Write("display layout changed"); BuildHosts(); break;
                case TimerWallpaper: Native.KillTimer(h, TimerWallpaper); SyncWindowsWallpaper(); break;
                case TimerReattach:
                    Native.KillTimer(h, TimerReattach);
                    nint wk = Desktop.GetWorkerW(true);
                    foreach (var host in _hosts) host.Place(wk);
                    break;
            }
            return 0;
        }
        if (msg == Tray.CallbackMessage)
        {
            uint ev = (uint)(l & 0xFFFF);
            if (ev is Native.WM_CONTEXTMENU or Native.WM_RBUTTONUP) ShowTrayMenu();
            else if (ev == Native.WM_LBUTTONDBLCLK) OpenUi();
            return 0;
        }
        if (msg == ControlServer.WakeMessage) { _control?.ProcessQueue(HandleRequest); return 0; }
        if (msg == Native.WM_DISPLAYCHANGE) { Native.SetTimer(h, TimerDisplayChange, 1500, 0); return 0; }
        if (msg == Native.WM_WTSSESSION_CHANGE)
        {
            // 7 = WTS_SESSION_LOCK, 8 = WTS_SESSION_UNLOCK
            if (w == 7 || w == 8) { _rules.Locked = w == 7; Log.Write(_rules.Locked ? "session locked" : "session unlocked"); ApplyRules(); }
            return 0;
        }
        if (msg == Native.WM_POWERBROADCAST && w == (nint)Native.PBT_POWERSETTINGCHANGE && l != 0)
        {
            unsafe
            {
                // POWERBROADCAST_SETTING: GUID, DWORD length, then the data (0 = off, 1 = on, 2 = dimmed).
                byte* p = (byte*)l;
                if (*(Guid*)p == Native.GUID_CONSOLE_DISPLAY_STATE)
                {
                    _rules.DisplayOff = *(uint*)(p + 20) == 0;
                    Log.Write(_rules.DisplayOff ? "display off" : "display on");
                    ApplyRules();
                }
            }
            return 1;
        }
        if (msg == s_taskbarCreated && s_taskbarCreated != 0)
        {
            Log.Write("explorer restarted");
            _tray?.Add();
            Native.SetTimer(h, TimerReattach, 1500, 0);
            return 0;
        }
        return Native.DefWindowProc(h, msg, w, l);
    }

    public override void Dispose()
    {
        if (Handle != 0) Native.WTSUnRegisterSessionNotification(Handle);
        _rules.Dispose();
        _control?.Dispose();
        foreach (var h in _hosts) h.Dispose();
        _hosts.Clear();
        _tray?.Dispose();
        try { WindowsWallpaper.Refresh(); } catch { }   // show the video's frame where the live wallpaper was
        base.Dispose();
    }
}
