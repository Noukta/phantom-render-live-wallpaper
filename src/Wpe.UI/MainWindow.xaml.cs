using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpe.Core;
using Control = Wpe.Core.Control;

namespace Wpe.UI;

public partial class MainWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "WallpaperEngine";

    static readonly Opt[] Actions =
    {
        new("Stop video (saves the most)", "stop"),
        new("Freeze video", "freeze"),
        new("Don't pause", "none"),
    };

    readonly EngineClient _engine = new();
    readonly Thumbnails _thumbs = new();
    readonly ObservableCollection<VideoItem> _videos = new();
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly CancellationTokenSource _closing = new();
    Settings _settings = Settings.Load();
    EngineState? _state;
    bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        VideoList.ItemsSource = _videos;
        _thumbs.MayWork = ThumbnailsMayWork;

        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveNowAsync(); };
        _stateTimer.Tick += async (_, _) => await RefreshStateAsync();
        _statsTimer.Tick += async (_, _) => await RefreshStatsAsync();
        _previewTimer.Tick += (_, _) => PreviewTick();
        _engine.ConnectionChanged += async c => { UpdateStatus(); if (c) await RefreshStateAsync(); };
        _engine.EventReceived += async _ => await RefreshStateAsync();

        VersionText.Text = $"Phantom Render {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · settings: {Paths.SettingsFile}";
        LoadSettingsIntoUi();
        LoadLibrary();
        _stateTimer.Start();
        Activated += async (_, _) => await RefreshStateAsync();
        Closed += (_, _) => { _closing.Cancel(); _engine.Dispose(); };
    }

    // ---------------------------------------------------------------- settings <-> UI

    void LoadSettingsIntoUi()
    {
        _loading = true;
        Fill(RuleFullscreen, Actions, _settings.FullscreenMode);
        Fill(RuleMaximized, Actions, _settings.Rules.Maximized);
        Fill(RuleBattery, Actions, _settings.Rules.OnBattery);
        Fill(RuleLocked, Actions, _settings.Rules.Locked);
        Fill(RuleDisplayOff, Actions, _settings.Rules.DisplayOff);
        Fill(RuleScope, new Opt[] { new("Every screen", "all"), new("Only the screen the game is on", "monitor") }, _settings.Rules.Scope);
        Fill(GpuAction, Actions.Where(a => a.Value != "none").ToArray(), _settings.Rules.Gpu.Action);
        GpuEnabled.IsChecked = _settings.Rules.Gpu.Enabled;
        GpuThreshold.Value = Math.Clamp(_settings.Rules.Gpu.Threshold, 30, 99);
        GpuThresholdText.Text = $"{GpuThreshold.Value:0} %";
        Fill(MonitorModeCombo, new Opt[] { new("A wallpaper on each screen", "perMonitor"), new("One wallpaper across all screens", "span") }, _settings.MonitorMode);
        Fill(HwdecCombo, new Opt[]
        {
            new("Automatic (recommended)", "auto-safe"), new("Direct3D 11", "d3d11va"), new("NVIDIA NVDEC", "nvdec"), new("Off (decode on the CPU)", "no"),
        }, _settings.Hwdec);
        Fill(PriorityCombo, new Opt[] { new("Below normal (recommended)", "belowNormal"), new("Normal", "normal") }, _settings.MpvPriority);
        MuteSwitch.IsChecked = _settings.Mute;
        ShuffleSwitch.IsChecked = _settings.Shuffle;
        SyncWallpaperSwitch.IsChecked = _settings.SyncWindowsWallpaper;
        RestoreWallpaperButton.IsEnabled = WindowsWallpaper.HasOriginal;
        IntervalBox.Text = _settings.IntervalMinutes.ToString();
        StartupSwitch.IsChecked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunValue) != null;
        FoldersList.ItemsSource = _settings.Folders.ToList();
        RebuildApps();
        RefreshPlaylists();
        _loading = false;
    }

    static void Fill(ComboBox combo, Opt[] options, string? value)
    {
        combo.ItemsSource = options;
        combo.SelectedItem = options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase)) ?? options[0];
    }

    static string Value(ComboBox c) => (c.SelectedItem as Opt)?.Value ?? "";

    /// <summary>Copies every simple control back into the settings, then saves shortly after.</summary>
    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.FullscreenMode = Value(RuleFullscreen);
        _settings.Rules.Maximized = Value(RuleMaximized);
        _settings.Rules.OnBattery = Value(RuleBattery);
        _settings.Rules.Locked = Value(RuleLocked);
        _settings.Rules.DisplayOff = Value(RuleDisplayOff);
        _settings.Rules.Scope = Value(RuleScope);
        _settings.Rules.Gpu.Enabled = GpuEnabled.IsChecked == true;
        _settings.Rules.Gpu.Threshold = (int)GpuThreshold.Value;
        _settings.Rules.Gpu.Action = Value(GpuAction);
        _settings.MonitorMode = Value(MonitorModeCombo);
        _settings.Hwdec = Value(HwdecCombo);
        _settings.MpvPriority = Value(PriorityCombo);
        _settings.Mute = MuteSwitch.IsChecked == true;
        _settings.Shuffle = ShuffleSwitch.IsChecked == true;
        _settings.SyncWindowsWallpaper = SyncWallpaperSwitch.IsChecked == true;
        if (int.TryParse(IntervalBox.Text, out int m) && m >= 1 && m <= 1440) _settings.IntervalMinutes = m;
        else IntervalBox.Text = _settings.IntervalMinutes.ToString();
        SaveSoon();
    }

    void SaveSoon() { _saveTimer.Stop(); _saveTimer.Start(); }

    async Task SaveNowAsync()
    {
        try { _settings.Save(); }
        catch (Exception ex) { MessageBox.Show(this, "Could not save settings: " + ex.Message, "Phantom Render"); return; }
        await _engine.SendAsync(Control.ReloadSettings);
        await RefreshStateAsync();
    }

    // ---------------------------------------------------------------- engine state

    async Task RefreshStateAsync()
    {
        if (!_engine.Connected) { _state = null; UpdateStatus(); return; }
        var r = await _engine.SendAsync(Control.GetState);
        if (r?.State != null) _state = r.State;
        UpdateStatus();
        UpdatePlayingBadges();
        RebuildTargets();
        if (Tabs.SelectedIndex == 2) RebuildScreens();
    }

    void UpdateStatus()
    {
        bool connected = _engine.Connected && _state != null;
        StartEngineButton.Visibility = _engine.Connected ? Visibility.Collapsed : Visibility.Visible;
        PauseButton.IsEnabled = NextButton.IsEnabled = connected;
        if (!_engine.Connected)
        {
            StatusDot.Fill = (Brush)FindResource("Muted");
            StatusText.Text = "Engine not running";
            StatusDetail.Text = "Wallpapers are off.";
            return;
        }
        if (_state == null) return;
        var reasons = _state.Monitors.Select(m => m.PauseReason).Where(r => r != null).Distinct().ToList();
        bool paused = reasons.Count > 0;
        StatusDot.Fill = (Brush)FindResource(paused ? "Warn" : "Good");
        StatusText.Text = paused ? "Paused" : "Playing";
        StatusDetail.Text = paused ? string.Join(", ", reasons) : $"{_state.Monitors.Count} screen{(_state.Monitors.Count == 1 ? "" : "s")}";
        PauseButton.Content = _state.ManualPause ? "Resume" : "Pause";
    }

    void UpdatePlayingBadges()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (_state != null)
            for (int i = 0; i < _state.Monitors.Count; i++)
                if (_state.Monitors[i].Current is string cur)
                {
                    if (!map.TryGetValue(cur, out var l)) map[cur] = l = new List<string>();
                    l.Add(ScreenLabel(_state.Monitors[i], i));
                }
        bool single = _state?.Monitors.Count == 1;
        foreach (var v in _videos)
            v.PlayingOn = map.TryGetValue(v.Path, out var on) ? (single ? "Now playing" : "On " + string.Join(", ", on)) : null;
    }

    static string ScreenLabel(MonitorState m, int index) => m.Id == "span" ? "all screens" : "Screen " + (index + 1);

    bool ThumbnailsMayWork()
    {
        // Don't spend CPU on thumbnails while a game (or anything that triggered a game rule) is running.
        var reasons = _state?.Monitors.Select(m => m.PauseReason).Where(r => r != null) ?? Enumerable.Empty<string?>();
        return !reasons.Any(r => r!.Contains("fullscreen") || r.Contains("running") || r.Contains("focused") || r.Contains("GPU") || r.Contains("battery"));
    }

    void RebuildTargets()
    {
        var opts = new List<Opt> { new("All screens", "") };
        if (_state != null && _state.Monitors.Count > 1)
            for (int i = 0; i < _state.Monitors.Count; i++) opts.Add(new(ScreenLabel(_state.Monitors[i], i), _state.Monitors[i].Id));
        string? keep = (TargetCombo.SelectedItem as Opt)?.Value;
        if (TargetCombo.ItemsSource is Opt[] cur && cur.Select(o => o.Value).SequenceEqual(opts.Select(o => o.Value))) return;
        TargetCombo.ItemsSource = opts.ToArray();
        TargetCombo.SelectedItem = opts.FirstOrDefault(o => o.Value == keep) ?? opts[0];
    }

    string? TargetMonitor() => (TargetCombo.SelectedItem as Opt)?.Value is { Length: > 0 } v ? v : null;

    async void Pause_Click(object sender, RoutedEventArgs e)
    {
        await _engine.SendAsync(Control.Pause, paused: !(_state?.ManualPause ?? false));
        await RefreshStateAsync();
    }

    async void NextAll_Click(object sender, RoutedEventArgs e)
    {
        await _engine.SendAsync(Control.Next);
        await RefreshStateAsync();
    }

    void StartEngine_Click(object sender, RoutedEventArgs e)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, Paths.EngineExe);
        if (!File.Exists(exe)) { MessageBox.Show(this, "Engine not found: " + exe, "Phantom Render"); return; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
    }

    // ---------------------------------------------------------------- library

    void LoadLibrary()
    {
        StopPreview();
        var files = _settings.ScanVideos().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        _videos.Clear();
        foreach (var f in files) _videos.Add(new VideoItem(f) { Thumb = Thumbnails.LoadCached(f) });
        LibraryEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlayingBadges();
        _ = GenerateThumbnailsAsync(_videos.Where(v => v.Thumb == null).ToList());
    }

    async Task GenerateThumbnailsAsync(List<VideoItem> items)
    {
        string? mpv = Paths.FindMpv(_settings);
        foreach (var v in items)
        {
            if (_closing.IsCancellationRequested) return;
            var img = await Task.Run(() => _thumbs.GetAsync(v.Path, mpv, _closing.Token));
            if (img != null) v.Thumb = img;
        }
        // Then prepare hover previews in the background (idle priority, paused while a game rule is active).
        foreach (var v in _videos.ToList())
        {
            if (_closing.IsCancellationRequested) return;
            if (!Thumbnails.HasPreview(v.Path)) await Task.Run(() => _thumbs.GetPreviewAsync(v.Path, mpv, false, _closing.Token));
        }
    }

    // ---------------------------------------------------------------- hover previews

    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / Thumbnails.PreviewFps) };
    CancellationTokenSource? _hoverCts;
    VideoItem? _hovered;
    List<System.Windows.Media.Imaging.BitmapImage>? _frames;
    int _frameIndex;

    async void VideoCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VideoItem v) return;
        StopPreview();
        _hovered = v;
        var cts = _hoverCts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        try
        {
            // Small delay so sweeping the mouse across the grid doesn't start work for every card.
            await Task.Delay(180, cts.Token);
            v.LoadingPreview = !Thumbnails.HasPreview(v.Path);
            string? mpv = Paths.FindMpv(_settings);
            var files = await Task.Run(() => _thumbs.GetPreviewAsync(v.Path, mpv, true, cts.Token));
            if (files == null || cts.IsCancellationRequested) return;
            var frames = await Task.Run(() => Thumbnails.LoadFrames(files), cts.Token);
            if (cts.IsCancellationRequested || frames.Count == 0 || _hovered != v) return;
            _frames = frames;
            _frameIndex = 0;
            v.Frame = frames[0];
            _previewTimer.Start();
        }
        catch (OperationCanceledException) { }
        finally { v.LoadingPreview = false; }
    }

    void VideoCard_MouseLeave(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VideoItem v && v == _hovered) StopPreview();
    }

    void PreviewTick()
    {
        if (_hovered == null || _frames == null || _frames.Count == 0) { _previewTimer.Stop(); return; }
        _frameIndex = (_frameIndex + 1) % _frames.Count;
        _hovered.Frame = _frames[_frameIndex];
    }

    void StopPreview()
    {
        _previewTimer.Stop();
        _hoverCts?.Cancel();
        _hoverCts = null;
        if (_hovered != null) { _hovered.Frame = null; _hovered.LoadingPreview = false; }
        _hovered = null;
        _frames = null;   // frames of one video only, released when the mouse leaves
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder with video wallpapers" };
        if (dlg.ShowDialog(this) != true) return;
        if (!_settings.Folders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) _settings.Folders.Add(dlg.FolderName);
        FoldersList.ItemsSource = _settings.Folders.ToList();
        LoadLibrary();
        SaveSoon();
    }

    void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string folder) return;
        _settings.Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        FoldersList.ItemsSource = _settings.Folders.ToList();
        LoadLibrary();
        SaveSoon();
    }

    void Rescan_Click(object sender, RoutedEventArgs e) => LoadLibrary();

    async void VideoList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VideoList.SelectedItem is VideoItem v) await ShowNowAsync(v.Path, TargetMonitor());
    }

    async Task ShowNowAsync(string path, string? monitor)
    {
        var r = await _engine.SendAsync(Control.SetWallpaper, monitor: monitor, path: path);
        if (r != null && !r.Ok) MessageBox.Show(this, r.Error ?? "Failed", "Phantom Render");
        await RefreshStateAsync();
    }

    void VideoList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = VideoList.ContextMenu!;
        menu.Items.Clear();
        if (VideoList.SelectedItem is not VideoItem v) { e.Handled = true; return; }
        var screens = _state?.Monitors ?? new List<MonitorState>();

        menu.Items.Add(Item("Show now on all screens", async () => await ShowNowAsync(v.Path, null)));
        if (screens.Count > 1)
            for (int i = 0; i < screens.Count; i++)
            {
                var id = screens[i].Id;
                menu.Items.Add(Item("Show now on " + ScreenLabel(screens[i], i), async () => await ShowNowAsync(v.Path, id)));
            }
        menu.Items.Add(new Separator());
        for (int i = 0; i < Math.Max(1, screens.Count); i++)
        {
            string id = screens.Count > 0 ? screens[i].Id : "";
            string label = screens.Count > 1 ? "Pin to " + ScreenLabel(screens[i], i) : "Pin (stop rotating)";
            if (id.Length > 0) menu.Items.Add(Item(label, () => { Assign(id).Pinned = v.Path; SaveSoon(); }));
        }
        var add = new MenuItem { Header = "Add to playlist" };
        foreach (var pl in _settings.Playlists)
        {
            var p = pl;
            add.Items.Add(Item(p.Name, () => { if (!p.Items.Contains(v.Path, StringComparer.OrdinalIgnoreCase)) p.Items.Add(v.Path); RefreshPlaylists(); SaveSoon(); }));
        }
        add.Items.Add(Item("New playlist...", () =>
        {
            var p = new Playlist { Name = UniquePlaylistName() };
            p.Items.Add(v.Path);
            _settings.Playlists.Add(p);
            RefreshPlaylists(p);
            SaveSoon();
        }));
        menu.Items.Add(add);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show in Explorer", () => Process.Start("explorer.exe", $"/select,\"{v.Path}\"")));
    }

    static MenuItem Item(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    ScreenAssignment Assign(string id)
    {
        if (!_settings.Screens.TryGetValue(id, out var a)) _settings.Screens[id] = a = new ScreenAssignment();
        return a;
    }

    // ---------------------------------------------------------------- playlists

    Playlist? SelectedPlaylist => PlaylistList.SelectedItem as Playlist;

    void RefreshPlaylists(Playlist? select = null)
    {
        var keep = select ?? SelectedPlaylist;
        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = _settings.Playlists;
        PlaylistList.SelectedItem = keep != null && _settings.Playlists.Contains(keep) ? keep : _settings.Playlists.FirstOrDefault();
        ShowPlaylist();
    }

    void ShowPlaylist()
    {
        var p = SelectedPlaylist;
        PlaylistEditor.IsEnabled = p != null;
        bool was = _loading;
        _loading = true;
        PlaylistName.Text = p?.Name ?? "";
        PlaylistShuffle.IsChecked = p?.Shuffle ?? true;
        PlaylistInterval.Text = p?.IntervalMinutes?.ToString() ?? "";
        PlaylistItems.ItemsSource = p?.Items.Select(i => new PlaylistEntry(i)).ToList();
        _loading = was;
    }

    void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPlaylist();

    string UniquePlaylistName()
    {
        for (int i = 1; ; i++)
        {
            string n = i == 1 ? "My playlist" : "My playlist " + i;
            if (_settings.FindPlaylist(n) == null) return n;
        }
    }

    void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var p = new Playlist { Name = UniquePlaylistName() };
        _settings.Playlists.Add(p);
        RefreshPlaylists(p);
        PlaylistName.Focus();
        PlaylistName.SelectAll();
        SaveSoon();
    }

    void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlaylist is not Playlist p) return;
        if (MessageBox.Show(this, $"Delete the playlist \"{p.Name}\"? The videos themselves are not deleted.", "Phantom Render",
                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _settings.Playlists.Remove(p);
        foreach (var a in _settings.Screens.Values)
            if (string.Equals(a.Playlist, p.Name, StringComparison.OrdinalIgnoreCase)) a.Playlist = null;
        RefreshPlaylists();
        SaveSoon();
    }

    void PlaylistName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PlaylistName_Changed(sender, e);
    }

    void PlaylistName_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || SelectedPlaylist is not Playlist p) return;
        string name = PlaylistName.Text.Trim();
        if (name.Length == 0 || name == p.Name) { PlaylistName.Text = p.Name; return; }
        if (_settings.FindPlaylist(name) is Playlist other && other != p) { MessageBox.Show(this, "A playlist with that name already exists.", "Phantom Render"); return; }
        foreach (var a in _settings.Screens.Values)
            if (string.Equals(a.Playlist, p.Name, StringComparison.OrdinalIgnoreCase)) a.Playlist = name;
        p.Name = name;
        RefreshPlaylists(p);
        SaveSoon();
    }

    void PlaylistSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || SelectedPlaylist is not Playlist p) return;
        p.Shuffle = PlaylistShuffle.IsChecked == true;
        string t = PlaylistInterval.Text.Trim();
        if (t.Length == 0) p.IntervalMinutes = null;
        else if (int.TryParse(t, out int m) && m >= 1 && m <= 1440) p.IntervalMinutes = m;
        else PlaylistInterval.Text = p.IntervalMinutes?.ToString() ?? "";
        SaveSoon();
    }

    void MoveSelected(int delta)
    {
        if (SelectedPlaylist is not Playlist p || PlaylistItems.SelectedIndex < 0) return;
        int i = PlaylistItems.SelectedIndex, j = i + delta;
        if (delta == 0) p.Items.RemoveAt(i);
        else if (j >= 0 && j < p.Items.Count) (p.Items[i], p.Items[j]) = (p.Items[j], p.Items[i]);
        else return;
        ShowPlaylist();
        PlaylistItems.SelectedIndex = delta == 0 ? Math.Min(i, p.Items.Count - 1) : j;
        SaveSoon();
    }

    void PlaylistUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    void PlaylistDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    void PlaylistRemove_Click(object sender, RoutedEventArgs e) => MoveSelected(0);

    // ---------------------------------------------------------------- screens

    string? _screensSig;

    void RebuildScreens()
    {
        // Rebuild only when something shown changed, so an open drop-down isn't closed by the refresh timer.
        string sig = _state == null ? "" : string.Join("|", _state.Monitors.Select(m => m.Id + m.Current + m.Status + m.PauseReason + m.Width + m.Height))
            + string.Join("|", _settings.Playlists.Select(p => p.Name)) + string.Join("|", _settings.Screens.Select(kv => kv.Key + kv.Value.Playlist + kv.Value.Pinned));
        if (sig == _screensSig && ScreensPanel.Children.Count > 0) return;
        _screensSig = sig;
        ScreensPanel.Children.Clear();
        if (_state == null)
        {
            ScreensPanel.Children.Add(new TextBlock { Text = "Start the engine to see your screens.", Style = (Style)FindResource("Hint") });
            return;
        }
        var playlistOpts = new List<Opt> { new("Whole library", "") };
        playlistOpts.AddRange(_settings.Playlists.Select(p => new Opt(p.Name, p.Name)));

        for (int i = 0; i < _state.Monitors.Count; i++)
        {
            var m = _state.Monitors[i];
            var a = _settings.ScreenFor(m.Id);
            var card = new Border { Style = (Style)FindResource("Card") };
            var stack = new StackPanel();
            card.Child = stack;

            stack.Children.Add(new TextBlock
            {
                Text = $"{(m.Id == "span" ? "All screens (spanned)" : "Screen " + (i + 1))}   {m.Width}×{m.Height}",
                Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 4),
            });
            string status = m.PauseReason != null ? $"{m.Status} — {m.PauseReason}" : m.Status;
            stack.Children.Add(new TextBlock { Text = $"Now: {(m.Current == null ? "nothing" : Path.GetFileNameWithoutExtension(m.Current))}  ·  {status}", Style = (Style)FindResource("Hint") });

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            row.Children.Add(new TextBlock { Text = "Rotate through", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            var combo = new ComboBox { DisplayMemberPath = "Label", MinWidth = 220 };
            Fill(combo, playlistOpts.ToArray(), a?.Playlist ?? "");
            string id = m.Id;
            combo.SelectionChanged += (_, _) =>
            {
                string v = Value(combo);
                Assign(id).Playlist = v.Length == 0 ? null : v;
                SaveSoon();
            };
            row.Children.Add(combo);
            var next = new Button { Content = "Next", Margin = new Thickness(10, 0, 0, 0) };
            next.Click += async (_, _) => { await _engine.SendAsync(Control.Next, monitor: id); await RefreshStateAsync(); };
            row.Children.Add(next);
            stack.Children.Add(row);

            if (a?.Pinned != null)
            {
                var pinRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                pinRow.Children.Add(new TextBlock { Text = "Pinned: " + Path.GetFileNameWithoutExtension(a.Pinned), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                var unpin = new Button { Content = "Unpin" };
                unpin.Click += (_, _) => { Assign(id).Pinned = null; SaveSoon(); };
                pinRow.Children.Add(unpin);
                stack.Children.Add(pinRow);
            }
            ScreensPanel.Children.Add(card);
        }
    }

    // ---------------------------------------------------------------- apps rule

    void RebuildApps()
    {
        AppsPanel.Children.Clear();
        AppsEmpty.Visibility = _settings.Rules.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var app in _settings.Rules.Apps)
        {
            var rule = app;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) => { _settings.Rules.Apps.Remove(rule); RebuildApps(); SaveSoon(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var action = new ComboBox { DisplayMemberPath = "Label", MinWidth = 200, Margin = new Thickness(8, 0, 0, 0) };
            Fill(action, Actions.Where(x => x.Value != "none").ToArray(), rule.Action);
            action.SelectionChanged += (_, _) => { rule.Action = Value(action); SaveSoon(); };
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);

            var when = new ComboBox { DisplayMemberPath = "Label", MinWidth = 160, Margin = new Thickness(8, 0, 0, 0) };
            Fill(when, new Opt[] { new("while running", "running"), new("while in front", "focused") }, rule.When);
            when.SelectionChanged += (_, _) => { rule.When = Value(when); SaveSoon(); };
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);

            row.Children.Add(new TextBlock { Text = rule.Exe, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            AppsPanel.Children.Add(row);
        }
    }

    void RunningApps_DropDownOpened(object? sender, EventArgs e)
    {
        // Programs with a window, which is where games are; read only when the list is opened.
        var names = Process.GetProcesses()
            .Where(p => { try { return p.MainWindowHandle != 0 && p.Id != Environment.ProcessId; } catch { return false; } })
            .Select(p => p.ProcessName + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RunningApps.ItemsSource = names;
    }

    void AddApp(string exe)
    {
        exe = Path.GetFileName(exe);
        if (exe.Length == 0 || _settings.Rules.Apps.Any(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase))) return;
        _settings.Rules.Apps.Add(new AppRule { Exe = exe, When = "running", Action = "stop" });
        RebuildApps();
        SaveSoon();
    }

    void AddRunningApp_Click(object sender, RoutedEventArgs e)
    {
        if (RunningApps.SelectedItem is string exe) AddApp(exe);
    }

    void BrowseApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose a game or app" };
        if (dlg.ShowDialog(this) == true) AddApp(dlg.FileName);
    }

    void GpuThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GpuThresholdText != null) GpuThresholdText.Text = $"{e.NewValue:0} %";
    }

    // ---------------------------------------------------------------- performance

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        if (Tabs.SelectedIndex == 2) RebuildScreens();
        if (Tabs.SelectedIndex == 4) { _ = RefreshStatsAsync(); _statsTimer.Start(); }
        else _statsTimer.Stop();
    }

    async Task RefreshStatsAsync()
    {
        var r = await _engine.SendAsync(Control.Stats);
        var s = r?.Stats;
        if (s == null) { StatEngineRam.Text = StatEngineCpu.Text = StatMpvRam.Text = StatMpvCpu.Text = "-"; StatNote.Text = "Engine not running."; return; }
        StatEngineRam.Text = $"{s.EngineMemoryMB:0.0} MB";
        StatEngineCpu.Text = $"{s.EngineCpuPercent:0.0} %";
        StatMpvRam.Text = s.MpvProcesses == 0 ? "0 MB" : $"{s.MpvMemoryMB:0} MB";
        StatMpvCpu.Text = $"{s.MpvCpuPercent:0.0} %";
        string gpu = _state?.GpuLoad is double g ? $" · Other programs use {g:0} % of the GPU." : "";
        StatNote.Text = (s.MpvProcesses == 0 ? "The video player is closed right now (stopped by a rule), so it uses nothing." : $"{s.MpvProcesses} video player process(es) running.") + gpu;
    }

    // ---------------------------------------------------------------- general

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartupSwitch.IsChecked == true) key.SetValue(RunValue, "\"" + Path.Combine(AppContext.BaseDirectory, Paths.EngineExe) + "\"");
        else key.DeleteValue(RunValue, false);
    }

    void RestoreWallpaper_Click(object sender, RoutedEventArgs e)
    {
        // Stop syncing first, otherwise the next video would replace the restored wallpaper again.
        if (SyncWallpaperSwitch.IsChecked == true) SyncWallpaperSwitch.IsChecked = false;
        bool ok = WindowsWallpaper.RestoreOriginal();
        RestoreWallpaperButton.IsEnabled = WindowsWallpaper.HasOriginal;
        MessageBox.Show(this, ok ? "Your previous Windows wallpaper is back. It shows whenever Phantom Render is closed."
                                 : "Could not restore the previous wallpaper.", "Phantom Render");
    }

    void OpenLog_Click(object sender, RoutedEventArgs e) => OpenPath(Paths.LogFile);
    void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenPath(Paths.SettingsFile);

    void ClearThumbs_Click(object sender, RoutedEventArgs e)
    {
        try { if (Directory.Exists(Paths.ThumbsDir)) Directory.Delete(Paths.ThumbsDir, true); } catch { }
        LoadLibrary();
    }

    static void OpenPath(string p)
    {
        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { }
    }
}
