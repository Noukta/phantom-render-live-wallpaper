using Wpe.Core;

namespace Wpe.Engine;

/// <summary>Notification-area icon with a right-click menu.</summary>
public sealed unsafe class Tray : IDisposable
{
    public const uint CallbackMessage = Native.WM_APP + 10;
    readonly nint _owner;
    bool _added;

    public Tray(nint owner) { _owner = owner; Add(); }

    public void Add()
    {
        NOTIFYICONDATAW d = Data();
        d.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        d.uCallbackMessage = CallbackMessage;
        d.hIcon = Native.LoadIcon(0, 32512 /* IDI_APPLICATION */);
        SetTip(ref d, "Wallpaper Engine");
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, &d) != 0;
        d.uVersion = 4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, &d);
    }

    public void SetTooltip(string text)
    {
        NOTIFYICONDATAW d = Data();
        d.uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP;
        SetTip(ref d, text);
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, &d);
    }

    NOTIFYICONDATAW Data() => new() { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _owner, uID = 1 };

    static void SetTip(ref NOTIFYICONDATAW d, string text)
    {
        int n = Math.Min(text.Length, 127);
        for (int i = 0; i < n; i++) d.szTip[i] = text[i];
        d.szTip[n] = '\0';
    }

    /// <summary>Shows a menu at the cursor and returns the chosen command id (0 if none).</summary>
    public int ShowMenu(IEnumerable<(int id, string text, bool check)> items)
    {
        nint menu = Native.CreatePopupMenu();
        foreach (var (id, text, check) in items)
        {
            if (id == 0) Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
            else Native.AppendMenu(menu, Native.MF_STRING | (check ? Native.MF_CHECKED : 0), (nuint)id, text);
        }
        Native.GetCursorPos(out POINT p);
        Native.SetForegroundWindow(_owner);
        int cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY, p.X, p.Y, _owner, 0);
        Native.DestroyMenu(menu);
        return cmd;
    }

    public void Dispose()
    {
        if (!_added) return;
        NOTIFYICONDATAW d = Data();
        Native.Shell_NotifyIcon(Native.NIM_DELETE, &d);
        _added = false;
    }
}
