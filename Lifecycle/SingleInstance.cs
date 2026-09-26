using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Paperwork.Lifecycle;

/// <summary>
/// 单实例（方案 §6）。
///
/// 两个必须记住的坑：
/// 1) Mutex 必须用静态字段持有且永不主动 ReleaseMutex。
///    若用局部变量，JIT 会提前判死 → finalizer 线程里释放 → 抛 ApplicationException，
///    而且更糟的是互斥量被提前释放会让第二个实例误判"自己是首个实例"。
/// 2) 不用 FindWindow 找已有实例。WPF 的窗口类名是 HwndWrapper[...;guid] 且标题可变，
///    靠类名/标题匹配都不稳。改为把 HWND 发布到一个命名 MMF，二次启动直接读。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Paperwork.SingleInstance";
    private const string HwndMapName = @"Local\Paperwork.Hwnd";
    private const int MapSize = 8;

    private static Mutex? _mutex;          // 静态持有，见坑 1
    private MemoryMappedFile? _map;
    private bool _isFirstInstance;

    public bool IsFirstInstance => _isFirstInstance;

    /// <summary>尝试成为首个实例。若不是，会顺手唤醒已有实例。</summary>
    public bool TryAcquireOrSignalExisting()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        _isFirstInstance = createdNew;

        if (_isFirstInstance)
        {
            // 留一个持有者，进程退出时由 OS 回收互斥量，不主动释放
            _map = MemoryMappedFile.CreateOrOpen(HwndMapName, MapSize);
            return true;
        }

        WakeExistingInstance();
        return false;
    }

    /// <summary>首个实例在窗口 HWND 建好后调用，把句柄发布出去供后续实例唤醒。</summary>
    public void PublishWindowHandle(IntPtr hWnd)
    {
        if (_map is null || hWnd == IntPtr.Zero) return;
        using var view = _map.CreateViewStream(0, MapSize, MemoryMappedFileAccess.Write);
        using var w = new BinaryWriter(view);
        w.Write(hWnd.ToInt64());
    }

    private void WakeExistingInstance()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(HwndMapName);
            using var view = map.CreateViewStream(0, MapSize, MemoryMappedFileAccess.Read);
            using var r = new BinaryReader(view);
            var raw = r.ReadInt64();

            // 旧实例可能已经僵死但 OS 还没回收映射：PostMessage 失败就走超时退出
            if (raw != 0 && Shell.Native.PostMessage(new IntPtr(raw), (uint)Shell.Native.WM_SHOWTOGGLE, IntPtr.Zero, IntPtr.Zero))
                return;
        }
        catch (Exception)
        {
            // 映射不存在或读失败：当作没有实例，让调用方继续启动
        }

        // 唤醒失败 → 认定旧实例已死，本实例接管
        _isFirstInstance = true;
    }

    public void Dispose()
    {
        _map?.Dispose();
        _map = null;
        // 故意不调用 _mutex.ReleaseMutex()，见坑 1
    }
}
