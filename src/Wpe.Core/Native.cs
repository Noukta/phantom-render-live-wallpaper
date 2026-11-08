using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DisableRuntimeMarshalling]

namespace Wpe.Core;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public override readonly string ToString() => $"{Left},{Top} {Width}x{Height}";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MONITORINFOEXW
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    public fixed char szDevice[32];
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WNDCLASSEXW
{
    public uint cbSize; public uint style; public nint lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
    public nint hInstance; public nint hIcon; public nint hCursor; public nint hbrBackground;
    public char* lpszMenuName; public char* lpszClassName; public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PAINTSTRUCT
{
    public nint hdc; public int fErase; public RECT rcPaint; public int fRestore; public int fIncUpdate;
    public fixed byte rgbReserved[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NOTIFYICONDATAW
{
    public uint cbSize; public nint hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public nint hIcon;
    public fixed char szTip[128];
    public uint dwState; public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_POWER_STATUS
{
    public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
    public uint BatteryLifeTime, BatteryFullLifeTime;
}

[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags;
    public nuint MinimumWorkingSetSize; public nuint MaximumWorkingSetSize; public uint ActiveProcessLimit;
    public nuint Affinity; public uint PriorityClass; public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
public struct IO_COUNTERS
{
    public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
    public nuint ProcessMemoryLimit; public nuint JobMemoryLimit; public nuint PeakProcessMemoryUsed; public nuint PeakJobMemoryUsed;
}

public static unsafe partial class Native
{
    public const int GWL_STYLE = -16;
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOZORDER = 0x0004;
    public const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_TIMER = 0x0113, WM_COMMAND = 0x0111;
    public const uint WM_DISPLAYCHANGE = 0x007E, WM_APP = 0x8000, WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8, MF_GRAYED = 0x1;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    public static readonly nint HWND_BOTTOM = 1;

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? cls, string? title);
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowEx(nint parent, nint after, string? cls, string? title);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    public static partial nint SendMessageTimeout(nint h, uint msg, nint w, nint l, uint flags, uint timeout, out nint result);
    [LibraryImport("user32.dll")]
    public static partial int EnumWindows(delegate* unmanaged<nint, nint, int> proc, nint lParam);
    [LibraryImport("user32.dll")]
    public static partial nint SetParent(nint child, nint parent);
    [LibraryImport("user32.dll")]
    public static partial nint GetParent(nint h);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint h, int i);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint h, int i, nint v);
    [LibraryImport("user32.dll")]
    public static partial int SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    public static partial int GetWindowRect(nint h, out RECT r);
    [LibraryImport("user32.dll")]
    public static partial int GetClientRect(nint h, out RECT r);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint h, char* buf, int n);
    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint h, out uint pid);
    [LibraryImport("user32.dll")]
    public static partial int IsWindow(nint h);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string s);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint h, uint msg, nint w, nint l);
    [LibraryImport("user32.dll")]
    public static partial int DestroyWindow(nint h);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG m, nint h, uint min, uint max);
    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(in MSG m);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in MSG m);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    public static partial int PostMessage(nint h, uint msg, nint w, nint l);
    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint h, nuint id, uint ms, nint proc);
    [LibraryImport("user32.dll")]
    public static partial int KillTimer(nint h, nuint id);
    [LibraryImport("user32.dll")]
    public static partial int EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> proc, nint data);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    public static partial int GetMonitorInfo(nint mon, MONITORINFOEXW* mi);
    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint h, uint flags);
    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int AppendMenu(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint h, nint tpm);
    [LibraryImport("user32.dll")]
    public static partial int DestroyMenu(nint menu);
    [LibraryImport("user32.dll")]
    public static partial int SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")]
    public static partial int GetCursorPos(out POINT p);
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    public static partial nint LoadIcon(nint inst, nint id);
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")]
    public static partial nint LoadImage(nint inst, nint name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")]
    public static partial int InvalidateRect(nint h, nint rect, int erase);
    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint h, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")]
    public static partial int EndPaint(nint h, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint hdc, RECT* r, nint brush);
    [LibraryImport("gdi32.dll")]
    public static partial nint GetStockObject(int i);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    public static partial int Shell_NotifyIcon(uint msg, NOTIFYICONDATAW* data);
    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial nint GetModuleHandle(nint name);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW")]
    public static partial nint CreateJobObject(nint attrs, nint name);
    [LibraryImport("kernel32.dll")]
    public static partial int SetInformationJobObject(nint job, int cls, void* info, uint len);
    [LibraryImport("kernel32.dll")]
    public static partial int AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("user32.dll")]
    public static partial int IsZoomed(nint h);
    [LibraryImport("user32.dll")]
    public static partial int IsWindowVisible(nint h);
    [LibraryImport("user32.dll")]
    public static partial nint RegisterPowerSettingNotification(nint h, Guid* setting, uint flags);
    [LibraryImport("wtsapi32.dll")]
    public static partial int WTSRegisterSessionNotification(nint h, uint flags);
    [LibraryImport("wtsapi32.dll")]
    public static partial int WTSUnRegisterSessionNotification(nint h);
    [LibraryImport("kernel32.dll")]
    public static partial int GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [LibraryImport("kernel32.dll")]
    public static partial nint OpenProcess(uint access, int inherit, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    public static partial int QueryFullProcessImageName(nint process, uint flags, char* buf, ref uint size);
    [LibraryImport("kernel32.dll")]
    public static partial int CloseHandle(nint h);

    public const uint WM_WTSSESSION_CHANGE = 0x02B1, WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    /// <summary>File name (e.g. "game.exe") of a process, or null.</summary>
    public static string? ProcessExeName(uint pid)
    {
        nint h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, 0, pid);
        if (h == 0) return null;
        try
        {
            char* buf = stackalloc char[1024];
            uint size = 1024;
            if (QueryFullProcessImageName(h, 0, buf, ref size) == 0) return null;
            return Path.GetFileName(new string(buf, 0, (int)size));
        }
        finally { CloseHandle(h); }
    }

    public static string ClassName(nint h)
    {
        char* buf = stackalloc char[128];
        int n = GetClassName(h, buf, 128);
        return n > 0 ? new string(buf, 0, n) : "";
    }
}
