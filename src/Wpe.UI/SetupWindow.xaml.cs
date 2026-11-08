using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wpe.Core;

namespace Wpe.UI;

/// <summary>First-run setup: video folder, what to do while gaming, startup, and getting mpv.</summary>
public partial class SetupWindow : Window
{
    readonly Settings _settings;
    readonly CancellationTokenSource _cts = new();

    public SetupWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        FolderBox.Text = settings.Folders.FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        StopRadio.IsChecked = settings.FullscreenMode != "freeze";
        FreezeRadio.IsChecked = settings.FullscreenMode == "freeze";
        MpvInfo.Text = Paths.FindMpv(settings) is string mpv
            ? "mpv is installed: " + mpv
            : "Phantom Render plays videos with mpv, a free open-source player. It will be downloaded once (about 33 MB) from the official Windows builds linked on mpv.io.";
        Closed += (_, _) => _cts.Cancel();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folder with your wallpaper videos" };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    void FolderBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string f = FolderBox.Text.Trim();
        if (!Directory.Exists(f)) { FolderInfo.Text = "This folder doesn't exist."; return; }
        int n = new Settings { Folders = { f } }.ScanVideos().Count;
        FolderInfo.Text = n == 0 ? "No videos found here yet (you can add some later)." : $"{n} video{(n == 1 ? "" : "s")} found.";
    }

    void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        string folder = FolderBox.Text.Trim();
        if (!Directory.Exists(folder)) { ShowError("Choose a folder that exists."); return; }
        StartButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            if (Paths.FindMpv(_settings) == null)
            {
                MpvProgress.Visibility = Visibility.Visible;
                var progress = new Progress<(string text, double fraction)>(p => { MpvInfo.Text = p.text; MpvProgress.Value = p.fraction; });
                await MpvDownloader.DownloadAsync(progress, _cts.Token);
            }
            _settings.Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            _settings.Folders.Insert(0, folder);
            _settings.FullscreenMode = FreezeRadio.IsChecked == true ? "freeze" : "stop";
            _settings.SetupDone = true;
            _settings.Save();
            Program.SetStartup(StartupCheck.IsChecked == true);
            Program.StartEngine();
            DialogResult = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowError("Setup could not finish: " + ex.Message + "\nCheck your internet connection and try again.");
            StartButton.IsEnabled = true;
        }
    }

    void ShowError(string text) { ErrorText.Text = text; ErrorText.Visibility = Visibility.Visible; }
}
