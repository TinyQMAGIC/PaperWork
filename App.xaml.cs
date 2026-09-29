using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Paperwork.Data;
using Paperwork.Hotkeys;
using Paperwork.Lifecycle;
using Paperwork.Shell;
using Paperwork.Tray;

// Native 在 Paperwork.Shell 命名空间下，托盘菜单要用它的前台窗口读写
using Native = Paperwork.Shell.Native;

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
        tray.MenuRequested += (_, _) => ShowTrayMenu();
        // 退出的唯一入口是纸张菜单里那一项（菜单 → 窗口 ExitRequested → 这里）
        window.ExitRequested += (_, _) => ExitApp();

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

        // 这里原来有一行 startup.log（提权状态 / hwnd / 实际键位）。M5 决定去掉：
        // 它每次启动都写、每次呼出还要再写一行，长期运行只增不减，而真正要看的
        // 只有"异常"和"数据没落盘"两类——那两类走 LogError。
        // 实际生效的键位不需要日志也能拿到：上面第 98-100 行已经把它回写进
        // state.json 的 settings.hotkey，脚本和设置页都从那儿读。

        // 手动启动（双击 / 命令行 / 快捷方式）→ 直接把面板弹出来；
        // 开机自启那一次 → 只留托盘。没有这个区分的话只能二选一：
        // 要么手动双击也不弹（现状），要么每次开机脸上都糊一块面板。
        bool manualLaunch = !HasArg(e.Args, Autorun.StartupArg);

        // 兜底：旧版本写进 Run 键的值没有 --startup 标记，升级之后第一次开机就是这种情形。
        // 判据 = "自启确实开着" + "开机后 60 秒内启动"；上面第 103 行的 Autorun.Apply 每次
        // 启动都会把标记重写回去，所以这种兜底最多用到一次。
        if (manualLaunch && window.AutorunPreference && Environment.TickCount64 < BootGraceMs)
            manualLaunch = false;

        // 等启动路径走完再显示，别和冷启动抢 CPU
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 手动启动直接弹面板——这一下同时把预热那件事做了：
            // 真的 Show 一样会走 JIT 与首次合成，不需要再屏外 Show/Hide 一次。
            if (manualLaunch) window.ShowPanel();
            else window.WarmUp();
        }), DispatcherPriority.ContextIdle);
    }

    /// <summary>开机后这么久之内启动，视为开机自启（只用于旧版遗留的 Run 值没有标记那一次）。</summary>
    private const long BootGraceMs = 60_000;

    private static bool HasArg(string[] args, string name) =>
        Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 托盘右键 → 弹纸张菜单。
    ///
    /// <b>必须设 PlacementTarget</b>：不设的话弹出层拿不到焦点，ContextMenu 的自动关闭机制
    /// 就不工作 —— 实测打开之后 Esc 关不掉、点别处也关不掉，菜单就卡在屏幕上。
    /// 位置仍然用鼠标点（<see cref="PlacementMode.MousePoint"/>），所以菜单出现在光标处，
    /// 不受窗口位置影响；面板隐藏时照样弹得出来（Popup 是独立的 HWND）。
    /// </summary>
    private TrayMenuHost? _menuHost;

    private void ShowTrayMenu()
    {
        var menu = _window?.BuildTrayMenu();
        if (menu is null) return;

        // 记住弹菜单之前的前台窗口，关掉之后要还回去 ——
        // 不还的话，用户正在用的那个程序会被我们这个透明宿主抢走焦点
        IntPtr previous = Native.GetForegroundWindow();

        // 必须延到这一轮消息之后再开：右键是落在<b>托盘</b>上的，那是 explorer 的窗口，
        // 此刻鼠标捕获还在它手里。立刻开菜单的话 Popup 拿不到捕获。
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            // 宿主用**专用的隐形窗口**，不能用面板：面板收起时它不是可见窗口，
            // 菜单就没有可激活的宿主 —— Esc 关不掉、点别处也关不掉（实测卡死）。
            _menuHost ??= new TrayMenuHost();
            _menuHost.ActivateForMenu();

            menu.PlacementTarget = _menuHost;
            menu.Placement = PlacementMode.MousePoint;

            menu.Closed += (_, _) =>
            {
                // 焦点还回去。面板自己就是前台时不用动（它本来就该接着持有焦点）
                if (previous != IntPtr.Zero && previous != WindowHandleOf(_window))
                    Native.SetForegroundWindow(previous);
            };

            menu.IsOpen = true;
        }), DispatcherPriority.Background);
    }

    private static IntPtr WindowHandleOf(PanelWindow? window) =>
        window is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(window).Handle;

    /// <summary>首选热键被占时的退让顺序。</summary>
    private static readonly string[] FallbackChords =
    [
        "Ctrl+Alt+P", "Ctrl+Alt+B", "Ctrl+Alt+M", "Ctrl+Shift+F12"
    ];

    /// <summary>
    /// 唯一的日志：异常，以及"数据没能落盘"。
    /// 后者尤其不能吞：磁盘满、杀软锁文件、APPDATA 被同步盘撞车，
    /// 表现都是"我明明加了，重启又没了"。写失败的一侧在 StateStore.Save 里调它。
    ///
    /// 平时一个字节都不写——只有真出事才写。这正是 M5 定下来的取舍：
    /// 原来那个 startup.log（每次启动 + 每次呼出各一行）已删除，它只增不减，
    /// 而它承载的信息（实际生效的热键）state.json 里本来就有。
    /// </summary>
    internal static void LogError(string message) =>
        Append(Path.Combine(DataDir, "error.log"), $"{DateTime.Now:O}  {message}");

    /// <summary>
    /// 自检模式 <c>--probe &lt;路径&gt;</c> 的输出。单独一个文件，是因为它**只在有人显式敲
    /// 这个开关时**才产生，正常运行一个字节都不写——和"去掉常驻日志"不冲突。
    /// 它原来写 startup.log，那个文件 M5 已删除；而探针没有输出就完全没有意义，
    /// 所以给它留一条专用通道，而不是让它悄悄变哑。
    /// </summary>
    private static void LogProbe(string message) =>
        Append(Path.Combine(DataDir, "probe.txt"), $"{DateTime.Now:O}  {message}");

    // 与 state.json / window.json 同一个目录，由 AppPaths 一处决定。
    // 日志跟着数据走，不然又变成两个地方存东西。
    private static string DataDir => AppPaths.DataDir;

    /// <summary>日志体积上限。error.log 只在异常时写、平时不动，
    /// 但真出了反复崩溃的情况照样会涨，所以留一道轮转。</summary>
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
        LogProbe($"PROBE menu  path='{path}' hwnd=0x{hwnd:x} ok={ok} verbs={verbs} fail='{failure}'");

        var pending = ShellIcons.Query(path, 48);
        var image = ShellIcons.Materialize(pending) as System.Windows.Media.Imaging.BitmapSource;
        LogProbe($"PROBE icon  path='{path}' exists={pending.Exists} name='{pending.DisplayName}' " +
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
