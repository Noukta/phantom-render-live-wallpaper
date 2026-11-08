using System.Runtime.InteropServices;
using Wpe.Core;

namespace Wpe.Engine;

/// <summary>Minimal Win32 window base: registers a class once and routes messages to an instance.</summary>
public abstract unsafe class Win32Window : IDisposable
{
    static readonly Dictionary<nint, Win32Window> s_windows = new();
    static readonly HashSet<string> s_registered = new();
    static Win32Window? s_creating;

    public nint Handle { get; private set; }

    protected void Create(string className, uint exStyle, uint style, int x, int y, int w, int h, nint parent)
    {
        nint inst = Native.GetModuleHandle(0);
        if (s_registered.Add(className))
        {
            fixed (char* name = className)
            {
                WNDCLASSEXW wc = new()
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&StaticWndProc,
                    hInstance = inst,
                    lpszClassName = name,
                };
                Native.RegisterClassEx(&wc);
            }
        }
        s_creating = this;
        Handle = Native.CreateWindowEx(exStyle, className, null, style, x, y, w, h, parent, 0, inst, 0);
        s_creating = null;
        if (Handle == 0) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastPInvokeError());
        s_windows[Handle] = this;
    }

    protected abstract nint WndProc(nint h, uint msg, nint w, nint l);

    [UnmanagedCallersOnly]
    static nint StaticWndProc(nint h, uint msg, nint w, nint l)
    {
        try
        {
            if (!s_windows.TryGetValue(h, out var win))
            {
                win = s_creating;
                if (win != null) { win.Handle = h; s_windows[h] = win; }
            }
            if (win != null) return win.WndProc(h, msg, w, l);
        }
        catch (Exception ex) { Log.Write("wndproc error: " + ex); }
        return Native.DefWindowProc(h, msg, w, l);
    }

    public virtual void Dispose() => DestroyHandle();

    /// <summary>Destroys the window (a no-op for a handle that is already gone) and forgets it.</summary>
    protected void DestroyHandle()
    {
        if (Handle != 0)
        {
            nint h = Handle;
            Handle = 0;
            Native.DestroyWindow(h);
            s_windows.Remove(h);
        }
    }

    public static void RunMessageLoop()
    {
        while (Native.GetMessage(out MSG m, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(m);
            Native.DispatchMessage(m);
        }
    }
}
