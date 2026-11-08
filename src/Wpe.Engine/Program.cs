using Wpe.Core;

namespace Wpe.Engine;

static class Program
{
    [STAThread]
    static int Main()
    {
        using var mutex = new Mutex(true, "WallpaperEngine_single_instance", out bool created);
        if (!created) return 0;
        try
        {
            using var engine = new Engine();
            engine.Start();
            Win32Window.RunMessageLoop();
            Log.Write("engine exiting");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("fatal: " + ex);
            return 1;
        }
    }
}
