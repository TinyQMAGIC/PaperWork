using System;
using System.Runtime.InteropServices;

namespace Paperwork.Shell;

/// <summary>
/// M0 用到的全部 Win32 互操作。集中一个文件，避免签名散落。
/// 仅支持 x64（csproj 里 PlatformTarget=x64）；若要支持 x86，
/// GetWindowStyle/SetWindowStyle 需要换成 GetWindowLong 的回退分支。
/// </summary>
internal static class Native
{
    // ---------------- 样式位 ----------------
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const long WS_EX_TRANSPARENT = 0x0020;
    public const long WS_EX_TOPMOST = 0x0008;
    public const long WS_EX_TOOLWINDOW = 0x0080;
    public const long WS_EX_APPWINDOW = 0x0004_0000;
    /// <summary>不抢前台。拖拽幽灵窗口必须带它，否则每动一下都会把焦点从搜索框上夺走。</summary>
    public const long WS_EX_NOACTIVATE = 0x0800_0000;

    // ---------------- SetWindowPos ----------------
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    // ---------------- 消息 ----------------
    public const int WM_HOTKEY = 0x0312;
    public const int WM_APP = 0x8000;
    public const int WM_SHOWTOGGLE = WM_APP + 1;   // 二次启动时发给已存在的实例

    // ---------------- 热键 ----------------
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;       // 不加会按住连发
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    public const int HOTKEY_ID = 0xB001;

    // ---------------- DWM ----------------
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public const int DWMWCP_ROUND = 2;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    // ---------------- 显示器 ----------------
    public const uint MONITOR_DEFAULTTONULL = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ---------------- 函数 ----------------
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static long GetExStyle(IntPtr hWnd) => GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();

    public static void SetExStyle(IntPtr hWnd, long style) => _ = SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(style));

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    /// <summary>Explorer 的 Shell 窗口。是个真实顶层窗口，适合当诊断用的临时 owner。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    // ---------------- 便捷封装 ----------------

    /// <summary>加 WS_EX_TOOLWINDOW、去 WS_EX_APPWINDOW：让窗口不进 Alt-Tab。</summary>
    public static void ApplyToolWindowStyle(IntPtr hWnd)
    {
        var ex = GetExStyle(hWnd);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        SetExStyle(hWnd, ex);
    }

    public static void SetTopmost(IntPtr hWnd) =>
        _ = SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>只调 TOPMOST 组内的 Z 序，不改置顶标记（见方案 §5）。</summary>
    public static void RaiseWithinTopmost(IntPtr hWnd) =>
        _ = SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static void ApplyRoundedCorners(IntPtr hWnd)
    {
        var round = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        // 干掉 Win11 那条 1px 系统边框，我们的纸张边框自绘，否则会出现双层边
        var noBorder = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    /// <summary>点是否仍落在某块显示器内。用于 §10.3 的位置归属校验。</summary>
    public static bool PointOnAnyMonitor(POINT pt) => MonitorFromPoint(pt, MONITOR_DEFAULTTONULL) != IntPtr.Zero;

    public static bool TryGetWorkArea(POINT pt, out RECT work)
    {
        work = default;
        var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONULL);
        if (hMon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return false;
        work = mi.rcWork;
        return true;
    }
}
