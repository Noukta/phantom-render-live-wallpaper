using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Wpe.Core;

namespace Wpe.UI;

public partial class App : Application
{
    static Mutex? s_mutex;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint h, int cmd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // One control panel at a time: a second launch brings the first one forward.
        s_mutex = new Mutex(true, "PhantomRender_UI_single_instance", out bool created);
        if (!created)
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == 0) continue;
                ShowWindow(p.MainWindowHandle, 9 /* SW_RESTORE */);
                SetForegroundWindow(p.MainWindowHandle);
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);

        // The first time (or after a fresh install) ask the three setup questions; after that just make
        // sure the engine is running (e.g. after an update restarted the app) and open the panel.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = Settings.Load();
        if (!settings.SetupDone)
        {
            if (new SetupWindow(settings).ShowDialog() != true) { Shutdown(); return; }
        }
        else Program.StartEngine();

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}
