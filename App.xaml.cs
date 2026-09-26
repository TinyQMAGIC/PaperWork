using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Paperwork.Hotkeys;
using Paperwork.Lifecycle;
using Paperwork.Shell;
using Paperwork.Tray;

namespace Paperwork;

/// <summary>
/// 启动编排。顺序是有原因的：
/// 单实例 → 建窗口但不显示（EnsureHandle 拿到 HWND）→ 托盘 → 注册热键 → 发布 HWND。
/// 热键和二次唤醒都需要一个已存在的 HWND，而"隐藏"比"未创建"更容易被忽略。
/// </summary>
public partial class App : Application
{
    private readonly SingleInstance _instance = new();
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private PanelWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandled;

        // 自检模式：不建窗口不弹菜单，只验证 Shell 管线，供脚本调用。
        // 放在单实例检查之前，探针不该把已有实例唤起来。
        if (e.Args.Length >= 2 && e.Args[0] == "--probe")
        {
            Probe(e.Args[1]);
            return;
        }

        if (!_instance.TryAcquireOrSignalExisting())
        {
            // 已有实例，唤醒动作在 SingleInstance 里做完了，这里直接退场
            Shutdown();
            return;
        }

        bool elevated = IsElevated();

        var window = new PanelWindow();
        _window = window;

        // 只建句柄，不显示：冷启动后应当只有托盘图标
        new WindowInteropHelper(window).EnsureHandle();

        window.ToggleRequested += (_, _) => window.Toggle();
        window.TaskbarRestarted += (_, _) => _tray?.ReRegister();

        var tray = new TrayIcon();
        _tray = tray;
        tray.ToggleRequested += (_, _) => window.Toggle();
        tray.ExitRequested += (_, _) => ExitApp();

        if (elevated)
            tray.Balloon("不要用管理员身份运行 Paperwork",
                "Windows 的 UIPI 会阻止资源管理器向提权进程拖放文件，" +
                "「把文件拖进来」这个核心功能会完全失效。请以普通权限启动。");

        // 数据没读出来必须当场说：这时候面板是空的，用户随手一拖就会 Save() 覆盖原文件。
        // 坏文件在 StateStore.Load 里已经另存成 state.json.corrupt-<时间戳> 了。
        if (window.DataProblems.Count > 0)
            tray.Balloon("面板数据没能读出来", window.DataProblems[0]);

        var hotkey = new GlobalHotkey(window.Handle);
        _hotkey = hotkey;
        window.RegisteredHotkey = hotkey;

        // 用设置里记的键位；没存过或解析不出来才退回默认
        var chosen = HotkeyChord.TryParse(window.HotkeyPreference, out var saved) ? saved : HotkeyChord.Default;
        var result = hotkey.Register(chosen);
        if (!result.Success)
        {
            // 本机实测：默认键位被在跑的 Rolan 占着（1409）。首发撞车不能让用户面对"按了没反应"，
            // 自动退到第一个能注册的候选，并把实际键位告诉用户。
            foreach (var candidate in FallbackChords)
            {
                if (!HotkeyChord.TryParse(candidate, out var alt)) continue;
                var r = hotkey.Register(alt);
                if (r.Success) { chosen = alt; result = r; break; }
            }

            if (result.Success)
                tray.Balloon("Paperwork 已启动", $"默认热键被占用，已改用 {chosen}");
            else
                tray.Balloon("呼出热键未生效", result.Message + "；可在设置里改键");
        }

        // 把最终生效的键位回写，免得下次启动又去抢一个已被占用的
        if (result.Success && chosen.Display != window.HotkeyPreference)
            window.SetHotkeyPreference(chosen.Display);

        Lifecycle.Autorun.Apply(window.AutorunPreference);

        _instance.PublishWindowHandle(window.Handle);

        Log($"started. elevated={elevated} hwnd=0x{window.Handle:x} hotkey={chosen} -> {result.Status} (win32={result.Win32Error})");

        // 等启动路径走完再预热，别和冷启动抢 CPU
        Dispatcher.BeginInvoke(new Action(window.WarmUp), DispatcherPriority.ContextIdle);
    }

    /// <summary>首选热键被占时的退让顺序。</summary>
    private static readonly string[] FallbackChords =
    [
        "Ctrl+Alt+P", "Ctrl+Alt+B", "Ctrl+Alt+M", "Ctrl+Shift+F12"
    ];

    /// <summary>启动与呼出诊断日志。M0 用来排查热键注册、提权状态和呼出延迟；M5 决定去留。</summary>
    internal static void Log(string message) =>
        Append(Path.Combine(DataDir, "startup.log"), $"{DateTime.Now:HH:mm:ss.fff}  {message}");

    /// <summary>
    /// 异常与"数据没能落盘"。后者尤其不能吞：磁盘满、杀软锁文件、APPDATA 被同步盘撞车，
    /// 表现都是"我明明加了，重启又没了"。写失败的一侧在 StateStore.Save 里调它。
    /// </summary>
    internal static void LogError(string message) =>
        Append(Path.Combine(DataDir, "error.log"), $"{DateTime.Now:O}  {message}");

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Paperwork");

    /// <summary>日志体积上限。startup.log 每次呼出都写一行，长期运行会一直长下去。</summary>
    private const long MaxLogBytes = 512 * 1024;

    /// <summary>每个文件本次进程是否已经轮转过。日志是慢变量，没必要每条都去问文件系统。</summary>
    private static readonly HashSet<string> RotatedLogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RotateGate = new();

    private static void Append(string file, string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            RotateOnce(file);

            // 只记类型 + 消息定位不到现场（一堆异常消息长得一模一样），所以带上堆栈
            File.AppendAllText(file, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // 日志写不了就算了，不能再因为写日志把进程带崩
        }
    }

    /// <summary>
    /// 超过上限就把旧日志挪成 .old，让 Append 重新起一个新文件。
    /// 只在本次进程的第一条日志上问一次文件系统，之后走 HashSet 短路。
    /// 加锁是因为 <see cref="App.Log"/> 会从多个线程进来（UI 线程 + IconPump 的后台线程）。
    /// </summary>
    private static void RotateOnce(string file)
    {
        try
        {
            lock (RotateGate) if (!RotatedLogs.Add(file)) return;

            if (new FileInfo(file) is { Exists: true, Length: > MaxLogBytes } info)
                File.Move(file, file + ".old", overwrite: true);
        }
        catch (Exception)
        {
            // 轮转失败绝不许影响真正要记的那条日志
        }
    }

    /// <summary>
    /// Shell 管线自检。UI 线程本身就是 STA，可以直接同步调 COM；
    /// 图标句柄也在这里就地转成 BitmapSource，省掉跨线程那一段。
    /// </summary>
    private void Probe(string path)
    {
        // 探针不能建 PanelWindow——它一渲染就会把导航写进 state.json，
        // 诊断路径不该动用户数据。owner 句柄用 Explorer 的 Shell 窗口，够用且无副作用。
        var hwnd = Native.GetShellWindow();

        var (ok, verbs, failure) = NativeContextMenu.Probe(path, hwnd);
        Log($"PROBE menu  path='{path}' hwnd=0x{hwnd:x} ok={ok} verbs={verbs} fail='{failure}'");

        var pending = ShellIcons.Query(path, 48);
        var image = ShellIcons.Materialize(pending) as System.Windows.Media.Imaging.BitmapSource;
        Log($"PROBE icon  path='{path}' exists={pending.Exists} name='{pending.DisplayName}' " +
            $"size={(image is null ? "null" : $"{image.PixelWidth}x{image.PixelHeight}")} " +
            $"type='{pending.TypeName}'");

        Shutdown();
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        // 不在这里 Dispose 热键和托盘：统一交给 OnExit，免得释放两次。
        // 也不 _window.Close()——Closing 处理器会把它取消掉，Shutdown() 才是唯一出口。
        Shutdown();
    }

    /// <summary>退出中标志。<see cref="Shell.PanelWindow.OnClosing"/> 靠它决定还要不要拦下关闭。</summary>
    internal static bool IsExiting => Current is App app && app._exiting;

    private bool _exiting;

    /// <summary>让窗口侧的"我要退出"走到同一个出口（X 按钮在关掉「收进托盘」时用）。</summary>
    internal static void Quit() => ((App)Current!).ExitApp();

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 崩溃不能静默：留一行日志，并让托盘弹出来。
        LogError(e.Exception.ToString());

        _tray?.Balloon("Paperwork 出了个问题", "已记录到 %APPDATA%\\Paperwork\\error.log");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _instance.Dispose();
        base.OnExit(e);
    }
}
