using System;
using System.Runtime.InteropServices;

namespace Paperwork.Shell;

/// <summary>
/// 前台窗口进入"铺满整块显示器"状态时通知宿主收起面板。
///
/// 判定基准是<b>整块监视器</b>矩形而不是工作区：普通最大化窗口只等于工作区（不含任务栏），
/// 只有真全屏——独占全屏游戏、F11 浏览器——才会把任务栏也盖住。这样不误伤日常最大化。
///
/// 只在「非全屏 → 全屏」的跳变上触发。否则你把面板呼到全屏视频上面、随手点一下视频，
/// 面板就自己消失了，很难看。
///
/// 用 <c>SetWinEventHook</c> 而不是定时器轮询：前台切换是事件驱动的，零空闲开销，
/// 也不会出现"进了全屏还没藏上"的那一帧延迟。
/// </summary>
internal sealed class FullscreenWatch : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>铺满整屏但不算全屏应用的窗口：桌面本身、任务栏（含副屏的任务栏）。</summary>
    private static readonly string[] NotAnApp = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    public event Action? EnteredFullscreen;

    /// <summary>
    /// 回调委托必须由字段持有。原生侧只存一个函数指针，委托一旦被 GC 回收，
    /// 下一次事件就是往已释放的 thunk 里跳——典型的"偶发崩溃且复现不了"。
    /// </summary>
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;

    /// <summary>
    /// 不用 WINEVENT_SKIPOWNPROCESS：面板自己成为前台时也必须收到事件，
    /// 否则"离开全屏"这个状态没人记录，用户点回全屏应用会立刻又被隐藏。
    /// </summary>
    private readonly int _ownPid = Environment.ProcessId;
    private bool _wasFullscreen;

    public FullscreenWatch()
    {
        _callback = OnWinEvent;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                                IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                            int idObject, int idChild, uint dwEventThread, uint msidEventTime)
    {
        // idObject/idChild 非零是子元素拿焦点，不是窗口切换
        if (idObject != 0 || idChild != 0) return;

        bool now = IsFullscreen(hwnd);

        // 只在「非全屏 → 全屏」的跳变上动手
        if (now && !_wasFullscreen)
        {
            // 不能直接去 HidePanel：正处在原生回调栈里，同步改窗口状态容易撞上
            // shell 自己那套前台切换。交给宿主异步派发。
            EnteredFullscreen?.Invoke();
        }
        _wasFullscreen = now;
    }

    private bool IsFullscreen(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == _ownPid) return false;         // 我们自己的窗口/对话框不算

        if (!Native.GetWindowRect(hwnd, out Native.RECT r)) return false;
        if (r.Width <= 0 || r.Height <= 0) return false;

        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi)) return false;

        Native.RECT m = mi.rcMonitor;
        if (m.Width <= 0 || m.Height <= 0) return false;

        // 四条边都不缩进监视器范围内才算全屏。差 1px 常见于无边框窗口的阴影补偿，
        // 所以只要求"盖过"，不要求严格相等。
        if (r.Left > m.Left || r.Top > m.Top || r.Right < m.Right || r.Bottom < m.Bottom) return false;

        return !IsShellWindow(hwnd);
    }

    /// <summary>桌面和任务栏本来就铺满整屏，不能算"用户进了全屏"。</summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var buffer = new char[64];
        int len = GetClassName(hwnd, buffer, buffer.Length);
        if (len <= 0) return false;
        return Array.IndexOf(NotAnApp, new string(buffer, 0, len)) >= 0;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        _ = UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                                           int idObject, int idChild, uint dwEventThread, uint msidEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr hwnd, char[] className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
