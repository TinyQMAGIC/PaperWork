using System;
using System.Runtime.InteropServices;
using Paperwork.Shell;

namespace Paperwork.Hotkeys;

internal enum HotkeyStatus { Ok, Conflict, Failed }

/// <summary>注册结果。Conflict 必须显式提示用户，静默失效是这类工具最难被发现的 bug。</summary>
internal sealed record HotkeyResult(HotkeyStatus Status, int Win32Error = 0)
{
    public bool Success => Status == HotkeyStatus.Ok;
    public static readonly HotkeyResult Ok = new(HotkeyStatus.Ok);
    public static readonly HotkeyResult Conflict = new(HotkeyStatus.Conflict, Native.ERROR_HOTKEY_ALREADY_REGISTERED);
    public static HotkeyResult Error(int err) => new(HotkeyStatus.Failed, err);

    public string Message => Status switch
    {
        HotkeyStatus.Ok => "已生效",
        HotkeyStatus.Conflict => "该热键已被其他程序占用，请换一个",
        _ => $"注册失败（Win32 {Win32Error}）"
    };
}

/// <summary>
/// 全局热键（方案 §8）。注册目标窗口是主浮窗的 HWND——它在启动时就 EnsureHandle 过，
/// 隐藏期间句柄不销毁，所以 WM_HOTKEY 一直收得到。若将来改成"销毁式收起"，这里必须跟着重注册。
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private readonly IntPtr _hwnd;
    private HotkeyChord? _current;

    public GlobalHotkey(IntPtr hwnd) => _hwnd = hwnd;

    public HotkeyChord? Current => _current;

    public HotkeyResult Register(HotkeyChord chord)
    {
        if (_current is not null) _ = UnregisterInternal();
        return Commit(chord);
    }

    /// <summary>
    /// 改键的唯一正确顺序：先注册新的，成功才提交；失败立刻把旧的注册回去。
    /// 反过来做（先解绑再注册）一旦新键被占用，用户就被留在"一个热键都没有"的状态，只能重启。
    /// </summary>
    public HotkeyResult TryRebind(HotkeyChord next)
    {
        var previous = _current;
        _ = UnregisterInternal();

        var result = CommitRaw(next);
        if (result.Success)
        {
            _current = next;
            return result;
        }

        if (previous is not null)
            _ = CommitRaw(previous.Value);   // 回滚
        _current = previous;
        return result;
    }

    private HotkeyResult Commit(HotkeyChord chord)
    {
        var result = CommitRaw(chord);
        _current = result.Success ? chord : null;
        return result;
    }

    private HotkeyResult CommitRaw(HotkeyChord chord)
    {
        if (Native.RegisterHotKey(_hwnd, Native.HOTKEY_ID, chord.ToModifierFlags(), chord.VirtualKey))
            return HotkeyResult.Ok;

        int err = Marshal.GetLastWin32Error();
        return err == Native.ERROR_HOTKEY_ALREADY_REGISTERED ? HotkeyResult.Conflict : HotkeyResult.Error(err);
    }

    private bool UnregisterInternal()
    {
        if (_current is null) return true;
        bool ok = Native.UnregisterHotKey(_hwnd, Native.HOTKEY_ID);
        _current = null;
        return ok;
    }

    public void Dispose() => _ = UnregisterInternal();
}
