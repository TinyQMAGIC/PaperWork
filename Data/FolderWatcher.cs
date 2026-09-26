using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace Paperwork.Data;

/// <summary>
/// 只盯"当前展开的那一个文件夹"（D18：隐藏时全部停掉）。
///
/// 三条约束：
/// <list type="bullet">
///   <item>只响应 Created / Deleted / Renamed。LastWrite 会被正在写入的文件高频触发，
///         拿它刷新列表等于自己给自己做轮询。</item>
///   <item>事件在线程池线程上回调，必须转回 Dispatcher 再碰 UI。</item>
///   <item>网络路径上的 <see cref="FileSystemWatcher"/> 不可靠（可能静默失效），
///         出错时停掉监听并在状态栏说明，而不是反复重试。</item>
/// </list>
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    /// <summary>合并突发事件的窗口。复制一个文件夹进去会连着抛几十条。</summary>
    private const int DebounceMs = 450;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _watcher;

    public string? Path { get; private set; }

    public event Action? Changed;

    /// <summary>监听中断（目录被删、网络断开、超出内部缓冲区且无法恢复）。</summary>
    public event Action<string>? Failed;

    public FolderWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Changed?.Invoke(); };
    }

    /// <summary>切到新的监听目标。传 null 或同一路径表示不动 / 停止。</summary>
    public void Watch(string? path)
    {
        if (string.Equals(path, Path, StringComparison.OrdinalIgnoreCase)) return;

        Stop();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (!Directory.Exists(path)) return;

            var w = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                InternalBufferSize = 16 * 1024
            };
            w.Created += OnSignal;
            w.Deleted += OnSignal;
            w.Renamed += OnSignal;
            w.Error += OnError;
            w.EnableRaisingEvents = true;

            _watcher = w;
            Path = path;
        }
        catch (Exception ex)
        {
            // 无权限、路径过长、UNC 不可达——都不该让面板崩掉
            Failed?.Invoke(ex.GetType().Name);
        }
    }

    private void OnSignal(object sender, FileSystemEventArgs e) => Schedule();

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Error 和 Created/Deleted/Renamed 一样在线程池线程上回调，而这里要做的两件事都有
        // UI 线程亲和性：Stop() 会碰 _debounce（DispatcherTimer），Failed 的订阅方直接操作 UI。
        // 所以必须跨回去，理由同 Schedule()：用 BeginInvoke 而不是阻塞的 Invoke，
        // 并且包 try —— Dispatcher 已停时会抛，而线程池线程不受
        // DispatcherUnhandledException 保护，那种异常会把进程带走。
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                Stop();
                Failed?.Invoke("监听已中断，重新进入该文件夹可恢复");
            });
        }
        catch (Exception)
        {
            // 走到这里说明正在退出；此时没有补救的价值，也不该再抛出去打断收尾
        }
    }

    /// <summary>
    /// 合并突发事件的去抖。必须从线程池线程跨回 UI 线程——<c>_debounce</c> 是 DispatcherTimer，
    /// 有线程亲和性。
    ///
    /// 这里<b>必须</b>是 <c>BeginInvoke</c> 而不是阻塞的 <c>Invoke</c>：
    /// <c>NativeContextMenu.TryShow</c> 的 <c>TrackPopupMenu</c> 是一个原生模态循环，
    /// 期间 UI 线程不处理 Dispatcher 队列。用 <c>Invoke</c> 的话，回调所在的线程池线程会
    /// 全部堆在等待里，FileSystemWatcher 的 <c>InternalBufferSize</c>（16KB）随之溢出，
    /// 触发 <c>Error</c> → <c>Stop()</c> —— 监听器从此永久静默失效，只在状态栏留一句话。
    ///
    /// 外面还要包一层 try：Dispatcher 已 shutdown 时 <c>BeginInvoke</c> 本身会抛，
    /// 而这里是线程池线程，<b>不在</b> <c>DispatcherUnhandledException</c> 的保护范围内
    /// （那种异常会直接把进程带走）。
    /// </summary>
    private void Schedule()
    {
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                _debounce.Stop();
                _debounce.Start();
            });
        }
        catch (Exception)
        {
            // 走到这里说明正在退出（或 Dispatcher 已经停了），此时没有补救的价值，也不该再抛出去
        }
    }

    public void Stop()
    {
        _debounce.Stop();
        if (_watcher is null) { Path = null; return; }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnSignal;
        _watcher.Deleted -= OnSignal;
        _watcher.Renamed -= OnSignal;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _watcher = null;
        Path = null;
    }

    public void Dispose() => Stop();
}
