using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

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
        new MainWindow().Show();
    }
}
