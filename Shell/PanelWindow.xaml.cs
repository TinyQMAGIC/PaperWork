using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Paperwork.Data;
using Paperwork.Diagnostics;
using Paperwork.Lifecycle;

namespace Paperwork.Shell;

/// <summary>
/// 主浮窗。M1 起承载真实内容：数据层、磁贴网格、两层导航、搜索。
/// 窗口外壳（样式位、置顶、单实例、热键）沿用 M0。
/// </summary>
public partial class PanelWindow : Window
{
    private const int SaveDebounceMs = 400;
    private static readonly TimeSpan ViewAnim = TimeSpan.FromMilliseconds(160);

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(PanelWindow),
            new PropertyMetadata(42.0));

    /// <summary>磁贴内图标边长。模板通过 RelativeSource 绑它，改这里即整屏重排。</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private readonly PerfProbe _probe = new();
    private readonly StateStore _store = new();
    private readonly NavStack _nav = new();
    private DispatcherTimer? _saveTimer;
    private HwndSource? _source;
    private uint _taskbarRestartMsg;
    private IconPump? _icons;
    private FolderWatcher? _watcher;
    private PanScroll? _pan;
    private FullscreenWatch? _fullscreen;
    private bool _suppressSave;
    private Board _board = BoardBuilder.Build(new StateStore(), NavFrame.Root);

    /// <summary>
    /// 界面上真正绑着的那一份磁贴列表。
    /// 用 <see cref="ObservableCollection{T}"/> 而不是每次换 <c>ItemsSource</c>：
    /// 换实例会让 WPF 整体重置容器，实测之后 UIA 就再也读不到磁贴了
    /// （屏幕阅读器和我们的自动化验证是同一类受害者），而且图标容器也白重建一遍。
    /// 拖拽排序要的就是 <c>Move</c> 这一条精确通知。
    /// </summary>
    private readonly ObservableCollection<TileVm> _shown = new();

    private string _query = string.Empty;

    public IntPtr Handle { get; private set; }

    public bool IsPanelVisible { get; private set; }

    public event EventHandler? ToggleRequested;

    /// <summary>设置里存的呼出快捷键，字符串形式。App 启动时解析并注册。</summary>
    internal string HotkeyPreference => _store.State.Settings.Hotkey;

    /// <summary>读盘时发现的麻烦（state.json 与备份都读不出来）。启动时由 App 弹托盘气泡。</summary>
    internal IReadOnlyList<string> DataProblems => _store.LoadProblems;

    internal bool AutorunPreference => _store.State.Settings.Autorun;

    /// <summary>App 自动退让到别的键位后回写，避免下次启动再抢同一个。</summary>
    internal void SetHotkeyPreference(string display)
    {
        _store.State.Settings.Hotkey = display;
        _store.Save();
    }
    public event EventHandler? TaskbarRestarted;

    /// <summary>呼出时以鼠标为中心（D22）。可在设置里改成记住位置。</summary>
    private bool FollowCursor => _store.State.Settings.Anchor == SummonAnchor.FollowCursor;

    public PanelWindow()
    {
        InitializeComponent();

        // X 走 Close() 而不是直接 HidePanel()，这样它和 Alt+F4 是同一套语义、
        // 由「点 X 收进托盘」这个开关统一决定收起还是退出。
        HideButton.Click += (_, _) => Close();
        Closing += OnClosing;

        // 齿轮图标：轮廓现场从系统字体取（Normal 字重），粗细再由 XAML 里的
        // StrokeThickness 微调。为什么不用字体自带的 Bold —— 见 FontGlyphPath 的注释。
        // 取不到就保持没有 Data，按钮仍可点（等于一个空图标按钮），不影响其它功能。
        SettingsGlyph.Data = FontGlyphPath.Build(
            "Segoe Fluent Icons, Segoe MDL2 Assets", "\uE713", emSize: 15);

        // 面包屑：ItemsControl 的条目直接放 Button，模板由 CrumbButton 样式提供
        _store.Load();
        Themes.Palette.Apply(_store.State.Settings.Accent, _store.State.Settings.Paper);
        IconSize = _store.State.Settings.IconSize;
        RestoreNav();
        _icons = new IconPump(Dispatcher);

        Tiles.ItemsSource = _shown;
        _pan = new PanScroll(Scroller, source => TileFrom(source) is null);

        _watcher = new FolderWatcher(Dispatcher);
        _watcher.Changed += OnWatchedFolderChanged;
        _watcher.Failed += msg => Status(msg);

        // 前台进了全屏就收起：浮窗常驻 TOPMOST，挡游戏和投屏是没有别的办法的
        _fullscreen = new FullscreenWatch();
        _fullscreen.EnteredFullscreen += OnEnteredFullscreen;
        Closed += (_, _) => _fullscreen?.Dispose();

        InitSettings();
        InitMemo();

        MouseLeftButtonUp += OnMouseUp;
        PreviewMouseRightButtonDown += OnRightDown;
        PreviewMouseLeftButtonDown += ReorderDown;
        PreviewMouseMove += ReorderMove;
        PreviewMouseLeftButtonUp += ReorderUp;
        MouseDoubleClick += OnDoubleClick;
        PreviewKeyDown += OnKeyDown;
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += OnDragOver;
        DragEnter += (_, _) =>
        {
            _dragDepth++;
            // 提示只在进来时给一次：OnDragOver 每帧都触发，在那儿改状态栏会刷屏
            if (_board.Kind == NavKind.Folder) Status(DropHintFolderLayer);
        };
        DragLeave += (_, _) =>
        {
            if (--_dragDepth <= 0) { _dragDepth = 0; SetDropGhost(false); }
        };

        // 搜索的接线全部在 InitSearch 里。初始态（点了搜索框还没打字）什么都不变：
        // 还是当前这一层的磁贴，不跳页、不改标题。
        InitSearch();
        SearchBox.GotKeyboardFocus += (_, _) => SearchHint.Visibility = Visibility.Collapsed;
        SearchBox.LostKeyboardFocus += (_, _) =>
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>防抖后的目录变更。只重建当前这一层，不碰导航栈也不写盘。</summary>
    private void OnWatchedFolderChanged()
    {
        if (_nav.Current.Kind != NavKind.Folder) return;
        Render(animate: false);
        ScheduleIcons();
        Status("文件夹已更新");
    }

    // ------------------------------------------------------------ 生命周期

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        Handle = new WindowInteropHelper(this).Handle;

        Native.ApplyToolWindowStyle(Handle);
        Native.SetTopmost(Handle);
        // 圆角 22px 由 XAML 自绘（AllowsTransparency），DWM 的 CORNER_PREFERENCE 对分层窗口无效

        _source = HwndSource.FromHwnd(Handle);
        _source?.AddHook(WndProc);   // 全进程只允许这一个 hook

        _taskbarRestartMsg = Native.RegisterWindowMessage("TaskbarCreated");

        ApplySavedBounds();
        Render(animate: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Dispatcher 队列里可能还压着 BeginInvoke（全屏自动隐藏、原生菜单回调都用它）。
        // 不在这里归位的话，它们事后跑到的 HidePanel() 会因为守卫失效而对一个已经 Close
        // 的 Window 调 Hide()，抛出 InvalidOperationException——虽然会被全局 handler 吞掉，
        // 但每次退出都在 error.log 里留一串毫无意义的异常，容易把真问题淹没掉。
        IsPanelVisible = false;

        _watcher?.Dispose();
        _pan?.Dispose();
        _icons?.Dispose();
        base.OnClosed(e);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)    {
        base.OnDpiChanged(oldDpi, newDpi);
        RequestSave();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 双击标题栏不要最大化。
        //
        // 这是常驻浮窗不是普通窗口：想放大的人会去拖边框，而双击顶栏多半只是连点了两下
        // （比如连点两次齿轮想开关设置页），结果整块面板突然铺满屏幕，非常突兀。
        //
        // WindowChrome 的 CaptionHeight=46 就是拖拽区，双击它会被 DefWindowProc 按
        // "HTCAPTION 上的双击 = 最大化/还原"处理掉。这里在 WndProc 层直接吃掉：
        // 拖拽本身不受影响——拖动走的是 WM_NCLBUTTONDOWN + WM_NCMOUSEMOVE，不是这条消息。
        if (msg == Native.WM_NCLBUTTONDBLCLK)
        {
            handled = true;
            return IntPtr.Zero;
        }

        // Win11 原生菜单的 owner-draw 条目靠这四条消息画自己，消息发给 hwndOwner（就是我们）。
        // 不转发的话「资源管理器菜单」弹出来一半条目是空白。
        if (NativeContextMenu.HandleMenuMessage((uint)msg, wParam, lParam, out IntPtr menuResult))
        {
            handled = true;
            return menuResult;
        }

        if ((msg == Native.WM_HOTKEY && wParam.ToInt32() == Native.HOTKEY_ID) || msg == Native.WM_SHOWTOGGLE)
        {
            // 改键进行中收到呼出请求：这次按到的正是当前注册的那个组合 ——
            // 它此刻的语义是"再确认一遍旧键"，不是"呼出/收起"。不拦的话面板会当场消失，
            // 而 WPF 那侧永远等不到这颗键（它被系统吞成了 WM_HOTKEY），
            // 结果就是"面板没了 + 呼回来还停在『按下新组合键…』"。
            if (CommitHotkeyFromHotkeyMessage())
            {
                handled = true;
                return IntPtr.Zero;
            }

            ToggleRequested?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        else if (_taskbarRestartMsg != 0 && msg == (int)_taskbarRestartMsg)
        {
            TaskbarRestarted?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------ 显示 / 收起

    public void Toggle()
    {
        if (IsPanelVisible) HidePanel();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        if (IsPanelVisible)
        {
            Native.RaiseWithinTopmost(Handle);
            Activate();
            return;
        }

        if (FollowCursor) PositionAtCursor();

        // §9.2：Show 之前不做任何 IO。图标与目录数据一律在显示之后异步补。
        _probe.ArmShowStart();
        CompositionTarget.Rendering += OnFirstFrame;

        Show();
        Activate();
        IsPanelVisible = true;

        OnAfterShow();
    }

    public void HidePanel()
    {
        if (!IsPanelVisible) return;

        OnBeforeHide();

        Hide();
        IsPanelVisible = false;
    }

    /// <summary>
    /// X 和 Alt+F4 都落到这里。<c>ShutdownMode=OnExplicitShutdown</c>，真把主窗口 Close 掉
    /// 只会得到"窗口没了、进程还活着、热键随 HWND 一起失效"的僵尸态——之前全库没有
    /// 任何 Closing 处理器，就是这个下场，而且「点 X 收进托盘」开关纯属装饰。
    /// 现在一律取消关闭：要么收起（顺带落盘），要么交给 <see cref="App.Quit"/> 正常收尾。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.IsExiting) return;   // App 正在收尾，这时候别再拦它

        e.Cancel = true;
        HidePanel();
        if (!_store.State.Settings.CloseToTray) App.Quit();
    }

    /// <summary>
    /// 前台窗口进入全屏 → 收起面板，<b>不</b>自动恢复（想再看按热键）。
    /// 回调是从原生 win-event 直接进来的，这时候同步改窗口状态会和 shell 自己的
    /// 前台切换抢；<c>TrackPopupMenu</c> 的模态循环期间更不能动窗口，所以两道都挡一下。
    /// </summary>
    private void OnEnteredFullscreen()
    {
        // D28：开关关着就不自动收起——全屏的 IDE / 远程桌面等「工作型全屏」会被这条误伤，
        // 用户拍板「藏不藏由我按热键决定」（与 D5 同一套哲学）。hook 照旧装着，
        // 只在这里短路——事件驱动零开销，"装着不用"没有成本，且开关一改立即生效。
        if (!_store.State.Settings.HideOnFullscreen) return;

        if (NativeContextMenu.IsLive) return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (NativeContextMenu.IsLive) return;
            HidePanel();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// §9.1 的 OnHide 契约。M1 能瘦的是失效探测任务；
    /// M2/M3 在这里追加：停 watcher、取消图标解码、把图标缓存压到隐藏期的像素预算。
    ///
    /// 瘦身的**顺序**是有讲究的：必须先把磁贴咬着的 ImageSource 松掉，再让 IconPump 裁缓存。
    /// 反过来做等于白裁——<see cref="IconPump.Trim"/> 只是把条目从 LRU 链表上摘下来，
    /// 位图本身还被 TileVm 指着，隐藏期间一块都回收不掉（这正是 D18 方案里没落地的那半句）。
    /// </summary>
    private void OnBeforeHide()
    {
        // 改键捕获也一样要收尾：面板都收了，捕获态留着只会在下次呼出后把点击全吃掉
        // （面板级的守卫会把它们当成"点空白退出改键"）。见 EndHotkeyCaptureIfAny。
        EndHotkeyCaptureIfAny();

        DropDragState();   // 拖拽幽灵窗口不跟着一起收掉的话，屏幕上会留一个孤儿图标
        ReleaseTileIcons();
        _icons?.Trim();
        _watcher?.Stop();
        _saveTimer?.Stop();
        FlushSave();

        // 备忘是去抖落盘的（400ms）。收面板 / 退出时计时器可能还没到点，
        // 不强制写一次的话，最后敲的那几个字就丢了。
        FlushNoteSave();
        _store.SaveNav(_nav.Frames);

        // 收完之后主动要一次 GC。面板收起后就没有任何分配压力了，GC 不会自己来，
        // 那些已经断掉引用的位图会在私有内存里继续挂几十分钟甚至到下次呼出。
        // 这和 D19 排除的 SetProcessWorkingSetSizeEx 不是一回事：那个只是把页面换出去
        // 给任务管理器看数字，这里是真的先断了可达性再回收。用 blocking:false 收尾，
        // 不卡 UI 线程。
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: true);
    }

    /// <summary>
    /// 把当前层的图标引用清成 null，让裁剪后的位图真的变成可回收（D18 落地）。
    ///
    /// 为什么不去清 <c>_shown</c>：那会把 ItemsControl 的容器一起拆掉，下次呼出要重建整棵
    /// 磁贴视觉树——而 UIA 在容器被整体换过之后实测就读不到磁贴了，屏幕阅读器和
    /// <c>tools/</c> 下的验证脚本是同一类受害者（见 <c>_shown</c> 字段的注释）。
    /// 这里只摘 ImageSource，容器结构原样保留。
    ///
    /// 为什么遍历 <c>_board.Tiles</c> 就够了：Render 之后它和 <c>_shown</c> 装的是同一批
    /// TileVm 实例，摘一遍两边同时松开。下次 Show 走 Render 会重建实例、图标由
    /// ScheduleIcons 重新排队列补回来，所以用户看不到任何中间态。
    /// </summary>
    private void ReleaseTileIcons()
    {
        foreach (var tile in _board.Tiles)
        {
            if (tile.Icon is not null) tile.Icon = null;
        }
    }

    /// <summary>§9.1 的 OnShow 契约。重扫可见层的失效状态，全部走后台。</summary>
    private void OnAfterShow()
    {
        Native.RaiseWithinTopmost(Handle);
        Render(animate: false);
        ScheduleIcons();

        // 上次落盘失败过，就在眼前这条最显眼的位置补一句。必须放在 Render 之后——
        // 状态栏会被 Render 里的 ApplyQuery 覆盖掉。
        if (_store.LastSaveFailure is { } fail)
            Status($"上次保存失败：{fail}（面板上的改动还没写进磁盘）");

        // 这里原来每次呼出都要写一行 startup.log（含进程资源快照）。M5 删掉了：
        // 它跑在 §13.3 那条 P95 < 30ms 的关键路径上，而"每次呼出延迟"这种量
        // 只在调性能时才要看，长驻写盘不值得。真要量的时候临时加回来即可。
    }

    /// <summary>
    /// 启动预热：第一次 Show 要把渲染路径的 JIT 和首次合成全走一遍（实测 86ms）。
    /// 在屏幕外静默 Show/Hide 一次把这一下付掉。
    /// </summary>
    public void WarmUp()
    {
        if (IsPanelVisible || Handle == IntPtr.Zero) return;

        Native.GetWindowRect(Handle, out var saved);
        const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;

        _suppressSave = true;
        Native.SetWindowPos(Handle, IntPtr.Zero, -32000, -32000, saved.Width, saved.Height, flags);
        Show();
        Hide();
        Native.SetWindowPos(Handle, IntPtr.Zero, saved.Left, saved.Top, saved.Width, saved.Height, flags);
        _suppressSave = false;
    }

    private void OnFirstFrame(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnFirstFrame;
        _probe.MarkFirstFrame();
    }

    // ------------------------------------------------------------ 渲染

    private void Render(bool animate)
    {
        _board = BoardBuilder.Build(_store, _nav.Current);
        BuildBreadcrumb();
        ApplyQuery(animate);
        _store.SaveNav(_nav.Frames);

        // 只盯当前展开的这一个文件夹；回到上层或退出文件夹时自动停掉
        _watcher?.Watch(_board.FolderPath);

        // 每次渲染都重建磁贴对象，图标和 Shell 显示名是异步填到新对象上的。
        // 这里漏掉的话，返回上一层就会出现"图标和名字全退回占位状态"。
        ScheduleIcons();

        // 文件夹层此刻只是个空壳，真内容交给后台
        if (_board.Kind == NavKind.Folder) BeginFolderLoad();
    }

    /// <summary>
    /// 后台枚举当前文件夹层，回来时用真内容替换 <see cref="BoardBuilder.LoadingFolder"/> 的空壳。
    ///
    /// 为什么必须后台：调用链里有 <c>OnAfterShow</c>（<b>每次热键呼出</b>）和
    /// <c>OnWatchedFolderChanged</c>。而 <c>Directory.Exists</c> /
    /// <c>EnumerateFileSystemInfos</c> 对掉线的网络共享、未下载的 OneDrive 占位文件能阻塞
    /// 数十秒——<see cref="StateStore.SafeIsDirectory"/> 的注释里写的就是这条。
    /// 同步读盘会发生在 <c>Show()</c> 之后、第一帧之前，既违反 §9.2「Show 之前不做任何 IO」，
    /// 也直接把 §13.3 的 P95 &lt; 30ms 作废；掉线共享上的表现是「按下热键，面板僵住十几秒」。
    ///
    /// 迟到的结果靠帧比对丢弃：枚举还没回来时用户可能已经上翻了、或者进了别的文件夹，
    /// 那种结果盖上来会变成「面包屑显示 A、内容却是 B」。
    /// <see cref="NavFrame"/> 是 record，这里走结构相等。
    /// </summary>
    private void BeginFolderLoad()
    {
        var requested = _nav.Current;

        _ = Task.Run(() =>
        {
            Board? board = null;
            try
            {
                board = BoardBuilder.BuildFolder(requested);
            }
            catch (Exception ex)
            {
                // BuildFolder 内部只兜住了 UnauthorizedAccess / IO / ArgumentException，
                // 剩下的（SecurityException 之类）在这里收掉。绝不能让异常从 Task.Run 漏出去：
                // 那是线程池线程，不受 DispatcherUnhandledException 保护。
                App.LogError($"读取文件夹失败 {requested.Path}：{ex.Message}");
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                // 已经不在请求的那一帧了，这份结果是过期的
                if (_nav.Current != requested) return;

                _board = board;
                ApplyQuery(animate: false);
                ScheduleIcons();
            }));
        });
    }

    private void BuildBreadcrumb()
    {
        // 搜索态：面包屑只有 Paperwork / Search，与备忘页那套完全同构（§2），不新造。
        // 位置就是首页标题的位置，所以从首页进搜索时左上角不会跳。
        if (_searching)
        {
            TitleText.Visibility = Visibility.Collapsed;
            Crumb.Visibility = Visibility.Visible;

            var home = new Button
            {
                Content = "Paperwork",
                Style = (Style)FindResource("CrumbButton")
            };
            WindowChrome.SetIsHitTestVisibleInChrome(home, true);
            home.Click += (_, _) => { SearchBox.Clear(); };   // 清词即退场，与 Esc 同一条路

            var leaf = new TextBlock
            {
                Text = "Search",
                FontFamily = (FontFamily)FindResource("BookFont"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Ink")
            };

            Crumb.ItemsSource = new object[] { home, Slash(), leaf };
            return;
        }

        var frames = _nav.Frames;
        var pieces = new List<object>(frames.Count * 2 - 1);

        // 只有一层时显示标题、收起面包屑。两者叠在同一列上，都可见就会重影。
        bool rooted = frames.Count <= 1;
        TitleText.Visibility = rooted ? Visibility.Visible : Visibility.Collapsed;
        Crumb.Visibility = rooted ? Visibility.Collapsed : Visibility.Visible;

        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0) pieces.Add(Slash());

            int index = i;
            var btn = new Button
            {
                Content = frames[i].Title,
                Style = (Style)FindResource("CrumbButton")
            };
            WindowChrome.SetIsHitTestVisibleInChrome(btn, true);

            if (i == frames.Count - 1)
            {
                btn.Foreground = (Brush)FindResource("Ink");
                btn.FontWeight = FontWeights.SemiBold;
                btn.IsEnabled = frames.Count > 1;
            }

            btn.Click += (_, _) => { _nav.PopTo(index); Render(animate: true); };
            pieces.Add(btn);
        }

        Crumb.ItemsSource = pieces;
    }

    /// <summary>面包屑的斜杠。抽出来是因为搜索态也要用同一个（首页 / 备忘页都是它）。</summary>
    private TextBlock Slash() => new()
    {
        Text = "/",
        FontSize = 11,
        Margin = new Thickness(7, 0, 7, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)FindResource("Ink3")
    };

    private void ApplyQuery(bool animate = true)
    {
        // 全局搜索上线后这里不再按查询过滤磁贴：搜索结果归 SearchPanel 那层管。
        // 两套过滤并存的话，清空搜索框时会出现"磁贴回来了、状态栏还是搜索口径"这种
        // 各说各话的中间态。这一层只负责把当前层原样铺出来。
        var list = _board.Tiles.ToList();

        _shown.Clear();
        foreach (var t in list) _shown.Add(t);

        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (list.Count == 0)
        {
            // 文件夹层此刻可能只有空壳（内容还在后台枚举）。这时候再说"把任意文件拖进来"
            // 是在骗人——用户会以为这是个空目录，然后把自己拖进去的文件当成丢了。
            bool loading = _board.Subtitle == BoardBuilder.FolderLoadingText;

            // 搜索的"没找到"不在这里说：搜索结果有自己的空态（SearchEmpty），
            // 磁贴区的空态只负责"这一层本来就是空的"。
            EmptyTitle.Text = loading ? BoardBuilder.FolderLoadingText : "把任意文件拖进来";
            EmptySub.Text = loading ? "稍等一下" : "程序、快捷方式、文件夹、文档、图片、压缩包…";
        }

        StatusText.Text = _board.Subtitle;

        // 搜索层背景是 Transparent（要透出右下角那两道斜线），主状态栏必须自己让位，
        // 否则"N 项 · M 个组合"和"0 项 · Esc 返回"两行字叠在左下角（实测重叠过）。
        StatusText.Visibility = _searching ? Visibility.Collapsed : Visibility.Visible;

        if (animate) PlayViewAnimation();
    }

    private void PlayViewAnimation()
    {
        if (Scroller.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            Scroller.RenderTransformOrigin = new Point(0.5, 0.5);
            Scroller.RenderTransform = scale;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(0.94, 1.0, ViewAnim) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        Scroller.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1.0, ViewAnim) { EasingFunction = ease });
    }

    /// <summary>
    /// 把可见磁贴交给后台去取图标 / 显示名 / 失效状态。
    /// 绝不在 UI 线程查 Shell —— 见 <see cref="IconPump"/> 的注释。
    /// </summary>
    private void ScheduleIcons() => _icons?.Enqueue(_board.Tiles, (int)Math.Round(IconSize * DpiScaleX));

    // ------------------------------------------------------------ 交互

    private static TileVm? TileFrom(object source)
    {
        // 用可空变量走循环：ParentOf 可能返回 null（到顶了），
        // 写成非空变量编译器会报 CS8600。
        DependencyObject? node = source as DependencyObject;
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.DataContext is TileVm vm) return vm;
            node = ParentOf(node);
        }
        return null;
    }

    /// <summary>
    /// 往上一层。<see cref="VisualTreeHelper.GetParent"/> 只吃 Visual / Visual3D，喂别的会抛
    /// <see cref="InvalidOperationException"/> —— 搜索结果行里那段高亮文案是 <c>Run</c>，
    /// 右键点到它上面直接崩过一次（见 error.log 07:11 那条，备忘页早就为同一件事加了
    /// <c>IsVisual</c> 守卫，这里换成通用版）。
    ///
    /// 不能直接"不是 Visual 就返回 null"：那样点在命中文字上就点不动结果行了，
    /// 而高亮处恰恰是最容易点的地方。改用逻辑树往上走——<c>Run</c> 的逻辑父级是
    /// <c>TextBlock</c>，从那儿又能接回视觉树。
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject node) =>
        IsVisual(node) ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Esc 取消拖拽后的那次抬手，不算点击
        if (_suppressClick)
        {
            _suppressClick = false;
            e.Handled = true;
            return;
        }

        // 改键捕获中：点空白即取消（与 Esc 同一语义，走同一个 CancelHotkeyCapture）。
        //
        // 必须排在下面所有分支之前 —— 设置页里的空白点击本来会一路掉进 D10 的"点空白返回"，
        // 把浮层<b>背后</b>那一层弹掉：用户在设置页里点了下空白，什么都没看见，却发现退出设置后
        // 面板停在了别的地方。捕获期间这一下先被这里吃掉，顺带把那个坑也盖住了。
        //
        // 点改键那一行不会误伤：那一行在 OnOptionRowClick 里已经把事件 Handled 掉，
        // 根本走不到这里来。
        if (IsCapturingHotkey)
        {
            CancelHotkeyCapture();
            e.Handled = true;
            return;
        }

        // 搜索态：点空白即退出搜索、回到进搜索前的那一层（D10 同一套手感）。
        // 点了结果行的那一路已经在 SearchList 的处理器里 Handled，所以这里剩下的只有空白和列表自己的零件
        // （滚动条不算空白——点在它上面不该把搜索关掉）。
        if (_searching)
        {
            if (!e.Handled && RowFrom(e.OriginalSource) is null && !IsSearchChrome(e.OriginalSource))
            {
                SearchBox.Clear();
                ExitSearch();
                e.Handled = true;
            }
            return;
        }

        var tile = TileFrom(e.OriginalSource);
        if (tile is null)
        {
            // D10 点空白返回。但同一块区域也承担 D9 的拖动滚动，
            // 拖动过就不能再当成点击，否则滑一下列表顺手就把这一层关掉了。
            if (!e.Handled && _nav.Frames.Count > 1 && _pan?.ConsumedDrag != true && IsBlankArea(e.OriginalSource))
            {
                _nav.Pop();
                Render(animate: true);
            }
            return;
        }

        // TileVm 实现了 INotifyPropertyChanged，改 Selected 会自动刷新模板，
        // 不要重设 ItemsSource —— 那会丢掉搜索过滤结果并让磁贴闪一下。
        foreach (var t in _board.Tiles) t.Selected = ReferenceEquals(t, tile);

        // 按草图的语义：组合栏和文件夹是"点一下就展开"，不需要双击。
        // 快捷方式反过来——单击只选中，双击才运行，避免手滑打开一堆程序。
        if (tile.Kind is TileKind.Group or TileKind.Folder or TileKind.More) Activate(tile);
        e.Handled = true;
    }

    private static bool IsBlankArea(object source) =>
        source is DependencyObject d && (d is System.Windows.Controls.Border || d is Grid || d is ScrollViewer);

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var tile = TileFrom(e.OriginalSource);
        if (tile is null) return;
        Activate(tile);
        e.Handled = true;
    }

    /// <summary>组合/文件夹：单击即钻入。快捷方式：双击才打开，避免误触。</summary>
    private void Activate(TileVm tile)
    {
        switch (tile.Kind)
        {
            case TileKind.Group when tile.GroupId is int gid:
                var g = _store.Group(gid);
                // Push 现在会告诉你到底压没压进去。压不进去就别刷新——
                // 否则界面闪一下又回到原样，看着像"点了没反应"
                if (g is not null && _nav.Push(new NavFrame(NavKind.Group, gid, null, g.Title)))
                {
                    Render(animate: true);
                    ScheduleIcons();
                }
                break;

            case TileKind.Folder:
                if (!StateStore.SafeIsDirectory(tile.Path))
                {
                    Status("该文件夹当前不可访问");
                    break;
                }

                // 已经到导航栈底了（两层封顶 D17）。
                // 这时候再点文件夹，原来是 Push 静默失败 + 照常播一遍动画 ——
                // 用户只看到"点了没反应"，比"明确告诉我不让进"更让人困惑。
                // 改成直接交给资源管理器打开，符合"再往下就该用资源管理器了"这个直觉。
                // 和本层的「在资源管理器中打开」收尾格（TileKind.More）走的是同一条路。
                if (_nav.AtMaxDepth)
                {
                    ShellOps.OpenInExplorer(tile.Path, select: null);
                    break;
                }

                if (_nav.Push(new NavFrame(NavKind.Folder, 0, tile.Path, System.IO.Path.GetFileName(tile.Path))))
                {
                    Render(animate: true);
                    ScheduleIcons();
                }
                break;

            case TileKind.More:
                ShellOps.OpenInExplorer(tile.Path, select: null);
                break;

            default:
                if (tile.IsDead) Status("路径已失效，可在右键菜单里移除");
                else if (!ShellOps.Open(tile.Path, elevated: false, out string err)) Status(err);
                break;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // 改键必须排在最前面：它只在设置页里发生，而下面那条覆盖页守卫会把它一起挡掉。
        if (TryCaptureHotkey(e)) return;

        // 覆盖页（设置 / 备忘录列表 / 备忘录编辑 / 搜索结果）开着的时候，键盘归那一层自己处理。
        // 不拦的话 Esc 会掉进下面的「收起面板」分支：面板收起来了，覆盖页却还留在上面，
        // 下次呼出直接停在那一层。三级链（编辑 → 列表 → 面板）本来就该一级一级退。
        // 搜索层同样在册：它可见时 Backspace 只该删字，不该触发"返回上一层"。
        // 它的 Esc 由搜索框自己的 PreviewKeyDown 先收走（那里比这里更早）。
        if (SettingsPanel.Visibility == Visibility.Visible
            || MemoPanel.Visibility == Visibility.Visible
            || MemoEditPanel.Visibility == Visibility.Visible
            || SearchPanel.Visibility == Visibility.Visible) return;

        // 拖拽中途按 Esc 只该取消拖拽，不能顺手把面板也收起来
        if (e.Key == Key.Escape && CancelDragIfAny())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_query.Length > 0) { SearchBox.Clear(); }
            else if (_nav.Frames.Count > 1) { _nav.Pop(); Render(animate: true); }
            else HidePanel();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            // 只响应"用户明确选中过"的磁贴。没有选中时什么都不做——
            // 否则刚呼出面板就按 Enter，会莫名其妙打开第一个条目。
            var target = _board.Tiles.FirstOrDefault(t => t.Selected);
            if (target is not null) { Activate(target); e.Handled = true; }
            return;
        }

        if (e.Key == Key.Back && _nav.Frames.Count > 1)
        {
            _nav.Pop();
            Render(animate: true);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ 拖放

    /// <summary>文件夹层拒绝拖放时的提示（D26）。语义见 <see cref="OnDragOver"/>。</summary>
    private const string DropHintFolderLayer =
        "文件夹层显示的是磁盘内容，请回主界面或组合里再拖";

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // 文件夹层不接受拖放（D26）：那一层显示的是**磁盘内容**，不是面板数据。
        // 接下来"我拖进了这个文件夹"和"我加了个快捷方式"是两件事，宁可明确拒绝也不偷偷落到主界面。
        bool folderLayer = _board.Kind == NavKind.Folder;
        var effects = !folderLayer && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Effects = effects;

        // 空面板已经有 EmptyHint 那一整块虚线区，再叠一格占位是重复表达
        SetDropGhost(effects == DragDropEffects.Copy && (_dropGhost is not null || _shown.Count > 0));
        e.Handled = true;
    }

    private int _dragDepth;
    private TileVm? _dropGhost;

    /// <summary>
    /// 外部拖放时在网格末尾放一格虚线占位。
    /// 进出用引用计数而不是布尔：OLE 在同一次拖放里会成对发多次 Enter/Leave
    /// （每跨一个子元素边界一对），用布尔这格会一闪一闪。
    /// 判"在不在"要看 _shown 的实际成员而不是字段是否为空——拖放途中目录监视器
    /// 触发 Render 会把 _shown 重建掉，那时字段还指着旧实例，占位格就再也加不回来了。
    /// </summary>
    private void SetDropGhost(bool on)
    {
        bool present = _dropGhost is not null && _shown.Contains(_dropGhost);
        if (on == present) return;

        if (on)
        {
            _dropGhost = new TileVm { Kind = TileKind.Ghost };
            _shown.Add(_dropGhost);
        }
        else
        {
            _shown.Remove(_dropGhost!);
            _dropGhost = null;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _dragDepth = 0;
        SetDropGhost(false);

        // 兜底：Effects=None 时 OLE 一般不会再送 Drop，但手动拖拽源可能不讲规矩
        if (_board.Kind == NavKind.Folder)
        {
            Status(DropHintFolderLayer);
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            Status("没收到文件路径");
            return;
        }

        // 两层封顶：已经在组合里，就不接受"拖一个组合进来"
        if (!_board.CanCreateGroup)
        {
            Status("组合内不能再放组合，已按普通条目加入");
        }

        int added = _store.AddPaths(paths, _board.GroupId);
        Render(animate: false);
        ScheduleIcons();
        Status(added == 0 ? "这些条目已经在这一层了" : $"已加入 {added} 项");
    }

    // ------------------------------------------------------------ 右键菜单

    /// <summary>
    /// 右键必须自己接管。窗口 <c>AllowDrop=true</c> 时 WPF 会把右键按下当成"准备发起拖放"，
    /// 于是 <c>ContextMenuOpening</c> 根本不触发——症状就是右键完全没菜单。
    /// 在 Preview 阶段截住并手动弹，既恢复菜单又不影响左键拖放。
    /// </summary>
    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        // 搜索态右键：结果行（备忘除外）给一套自己的菜单；点在列表空白/滚动条上则什么都不做，
        // 更不会冒泡成"点空白退出"——右键不是用来退场的。
        if (_searching)
        {
            var row = RowFrom(e.OriginalSource);
            if (row is null || row.Hit.IsNote) { e.Handled = true; return; }

            SearchList.SelectedItem = row;
            var smenu = BuildSearchMenu(row.Hit);
            smenu.PlacementTarget = this;
            smenu.Placement = PlacementMode.MousePoint;
            smenu.IsOpen = true;
            e.Handled = true;
            return;
        }

        var tile = TileFrom(e.OriginalSource);
        if (tile is null) return;

        foreach (var t in _board.Tiles) t.Selected = ReferenceEquals(t, tile);

        var menu = BuildMenu(tile);
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildMenu(TileVm tile)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("PaperContextMenu") };

        if (tile.Kind is TileKind.Group or TileKind.Folder)
            menu.Items.Add(MenuItem("打开", () => Activate(tile)));
        else if (tile.Kind == TileKind.More)
            menu.Items.Add(MenuItem("在资源管理器中打开", () => ShellOps.OpenInExplorer(tile.Path, null)));
        else
        {
            menu.Items.Add(MenuItem("打开", () => Activate(tile)));
            menu.Items.Add(MenuItem("以管理员身份运行", () =>
            {
                if (!ShellOps.Open(tile.Path, elevated: true, out string err)) Status(err);
            }));
        }

        // 组合是面板自造的概念，磁盘上没有对应物：给它弹"打开文件位置"会把 explorer
        // 领到一个空路径上（表现为莫名其妙打开"文档"），系统菜单同理。
        if (tile.Kind != TileKind.Group && !string.IsNullOrWhiteSpace(tile.Path))
        {
            menu.Items.Add(Sep());
            menu.Items.Add(MenuItem("打开文件位置", () => ShellOps.OpenInExplorer(null, tile.Path)));
            menu.Items.Add(MenuItem("资源管理器菜单", () => ShowNativeMenu(tile)));
        }

        if (tile.EntryId is int eid)
        {
            menu.Items.Add(Sep());
            // 统一叫「重命名」，不再分「恢复原名」：改回去只要把上面那行的完整路径里的
            // 本名再敲一遍（此时会当成"没有自定义"，标记自动清掉）。少一项菜单，少一层心智负担。
            menu.Items.Add(MenuItem("重命名", () => RenameEntry(eid)));
            menu.Items.Add(tile.CustomIcon is null
                ? MenuItem("更改图标…", () => ChangeIcon(eid, null))
                : MenuItem("恢复系统图标", () => ChangeIcon(eid, null, clear: true)));

            if (_board.CanCreateGroup)
            {
                menu.Items.Add(Sep());
                menu.Items.Add(MenuItem("新建组合…", () => CreateGroupFrom(eid)));
            }
            else if (_board.GroupId is int cur)
            {
                menu.Items.Add(MenuItem("移出本组合", () => { _store.MoveEntry(eid, null); Render(false); }));
            }

            menu.Items.Add(Sep());
            menu.Items.Add(MenuItem("从面板移除", () =>
            {
                _store.RemoveEntry(eid);
                Render(animate: false);
                Status("已从面板移除（未删除磁盘文件）");
            }, danger: true));
        }
        else if (tile.GroupId is int gid)
        {
            menu.Items.Add(Sep());
            menu.Items.Add(MenuItem("重命名", () => RenameGroup(gid)));
            menu.Items.Add(Sep());
            menu.Items.Add(MenuItem("删除组合（条目退回主界面）", () =>
            {
                _store.RemoveGroup(gid);
                if (_nav.Current.Kind == NavKind.Group && _nav.Current.Id == gid) _nav.PopTo(0);
                Render(animate: false);
            }, danger: true));
        }

        MarkMenuEdges(menu);
        return menu;
    }

    private MenuItem MenuItem(string text, Action act, bool danger = false)
    {
        // 危险项用独立样式：悬停底与字色都在它的 Style.Triggers 里，
        // 不再靠"模板里判断危险"——模板里的 DataTrigger 实测不生效（见 MenuLook 注释）。
        // 顺带修掉一件旧事：危险字色原来写死 #B8402E，夜空纸下该用提亮过的 #E08373。
        var style = (Style)FindResource(danger ? "PaperMenuItemDanger" : "PaperMenuItem");
        var item = new MenuItem { Header = text, Style = style };
        item.Click += (_, _) => act();
        return item;
    }

    /// <summary>分隔条要单独给样式，理由见 App.xaml 里「右键菜单：纸张化」那段注释。</summary>
    private Separator Sep() => new() { Style = (Style)FindResource("PaperSeparator") };

    /// <summary>
    /// 给菜单的<b>第一条和最后一条条目</b>打 Tag（first / last），
    /// 让模板把它们的高亮圆角换成跟着外弧走的同心圆角。
    ///
    /// 所有右键菜单（磁贴 / 备忘 / 搜索结果）都要过这一道，这是 UI 手册里的一条几何标准，
    /// 不是某一处菜单的临时补丁。分隔条不参与——它们不是"条目"。
    /// </summary>
    private static void MarkMenuEdges(ContextMenu menu)
    {
        var items = menu.Items.OfType<MenuItem>().ToList();
        if (items.Count == 0) return;

        // 同心圆角 = PanelRadius(22) − 菜单上下内边距(7) = 15。
        // 值写进附加属性、模板用普通绑定取；模板里的 DataTrigger 那条路实测走不通。
        var mid = new CornerRadius(8);

        // 只有一条时上下两角都要顺弧
        if (items.Count == 1)
        {
            MenuLook.SetCorner(items[0], new CornerRadius(15));
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var corner = i == 0 ? new CornerRadius(15, 15, 8, 8)
                       : i == items.Count - 1 ? new CornerRadius(8, 8, 15, 15)
                       : mid;
            MenuLook.SetCorner(items[i], corner);
        }
    }

    /// <summary>托盘菜单里的「退出」。由 App 接到真正的退出流程（要连托盘一起收掉）。</summary>
    internal event EventHandler? ExitRequested;

    /// <summary>
    /// 托盘右键菜单。<b>与磁贴菜单同一套样式</b>（<c>PaperContextMenu</c> + <c>MenuItem</c>），
    /// 所以 D27 的首末项同心圆角、危险色规则全部自动生效，不需要为新菜单写任何样式。
    ///
    /// 首项文字随面板状态在「呼出面板 / 收起面板」之间切换，右侧一列小字显示
    /// <b>当前真正生效的</b>热键（改过键、或者默认键被占用后退让过，这里都会跟着变）。
    /// </summary>
    internal ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("PaperContextMenu") };

        var toggle = MenuItem(IsPanelVisible ? "收起面板" : "呼出面板", () => Toggle());
        MenuLook.SetIcon(toggle, MenuIcons.Panel);
        MenuLook.SetHint(toggle, RegisteredHotkey?.Current?.Display);
        menu.Items.Add(toggle);

        var settings = MenuItem("设置", () =>
        {
            // 面板收起时，设置页是"看不见的"——它是面板里的覆盖层。
            // 只发一句"打开设置页"，用户看到的就是点了没反应；必须先弹面板再开设置页。
            if (!IsPanelVisible) ShowPanel();
            OpenSettings();
        });
        // 滑杆而不是齿轮：原来那颗"圆 + 八根穿过圆周的辐条"在 16px 下读作太阳，
        // 齿只在圈外的版本又太密，这个尺寸下最清楚的是三根线 + 错位旋钮
        MenuLook.SetIcon(settings, MenuIcons.Sliders);
        menu.Items.Add(settings);

        menu.Items.Add(Sep());

        // 退出用危险色：它会连托盘一起结束，是这个菜单里最重的动作
        var exit = MenuItem("退出", () => ExitRequested?.Invoke(this, EventArgs.Empty), danger: true);
        MenuLook.SetIcon(exit, MenuIcons.Power);
        menu.Items.Add(exit);

        MarkMenuEdges(menu);
        return menu;
    }

    private void ShowNativeMenu(TileVm tile)
    {
        if (string.IsNullOrWhiteSpace(tile.Path)) return;
        ShowNativeMenu(tile.Path);
    }

    /// <summary>按路径弹系统「资源管理器菜单」。搜索结果没有 TileVm，走这一个。</summary>
    private void ShowNativeMenu(string path)
    {
        // 此刻 WPF 的 ContextMenu 还在关闭动画里，同步弹原生菜单会和它抢前台、
        // 菜单一闪就没。记下坐标，等这一轮消息泵空了再弹。
        var cursor = System.Windows.Forms.Cursor.Position;
        int x = cursor.X, y = cursor.Y;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            bool ok = NativeContextMenu.TryShow(Handle, path, x, y);
            if (!ok && !string.IsNullOrEmpty(NativeContextMenu.LastFailure))
                Status("系统菜单不可用：" + NativeContextMenu.LastFailure);
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void RenameEntry(int id)
    {
        var entry = _store.Entry(id);
        if (entry is null) return;

        // 预填**面板上显示的那个名字**，而不是 entry.Label ?? entry.Path。
        // entry.Label 是"用户自定义名"，新拖进来的还没有，旧写法就整条路径塞进了输入框
        // —— 用户以为在改名字，其实框里是一条完整路径。
        // TileVm.Label 才是真的显示名：它已经在 BoardBuilder.FromEntry 里做过
        // 「自定义名 ?? 路径推导名」，之后还会被 Shell 显示名替换（IconPump 只改没自定义过的）。
        string initial = DeriveFromTile(id) ?? BoardBuilder.DeriveName(entry.Path, entry.IsDir);

        // 路径永远显示在标题下面：仅凭显示名分不清谁是谁（面板上三个同名文件是常事），
        // 而且刚才那条"和 initial 相同就不显示"的判断，恰好让新条目第一次打开时连路径都没有。
        string? typed = PromptText.Show(this, "重命名", initial, entry.Path);
        if (typed is null) return;

        // 敲的就是磁盘上的本名（或从本名推导出来的那个）时，等于没有"自定义"这回事：
        // 清掉标记，以后系统显示名改了它跟着走。
        // 注意这里比的是**推导名**而不是 tile.Label —— 后者在改过名之后就是用户自己那串字，
        // 拿它当基准会让"原样不动点确定"悄悄退化成恢复原名。
        bool sameAsDerived = string.Equals(typed, BoardBuilder.DeriveName(entry.Path, entry.IsDir),
                                           StringComparison.Ordinal);
        _store.RenameEntry(id, sameAsDerived ? null : typed);
        Render(animate: false);
    }

    /// <summary>组合名是面板自造的概念，磁盘上没有对应物，所以只改标题、不碰任何路径。</summary>
    private void RenameGroup(int id)
    {
        var group = _store.Group(id);
        if (group is null) return;

        // 不给第二行提示：组合名在磁盘上没有对应物，说"不改动任何文件"反而像在道歉
        string? typed = PromptText.Show(this, "重命名", group.Title);
        if (typed is null) return;

        _store.RenameGroup(id, typed);

        // 正在这一层时标题和面包屑都取自组合名，得一起换掉
        Render(animate: false);
        Status($"已重命名为「{typed}」");
    }

    private string? DeriveFromTile(int id) =>
        _board.Tiles.FirstOrDefault(t => t.EntryId == id)?.Label;

    private void ChangeIcon(int id, string? unused, bool clear = false)
    {
        if (clear)
        {
            _store.SetEntryIcon(id, null);
            Render(animate: false);
            ScheduleIcons();
            Status("已恢复系统图标");
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图标文件",
            Filter = "图标文件 (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != true) return;

        _store.SetEntryIcon(id, dlg.FileName);
        Render(animate: false);
        ScheduleIcons();
        Status("已更换图标");
    }

    private void CreateGroupFrom(int seedEntryId)
    {
        var selected = _board.Tiles.Where(t => t.Selected && t.EntryId is not null)
                                   .Select(t => t.EntryId!.Value).ToList();
        if (selected.Count == 0 && _board.Tiles.FirstOrDefault(t => t.EntryId == seedEntryId)?.EntryId is int s)
            selected.Add(s);

        if (selected.Count == 0) return;

        // M1 先自动编号命名；内联改名（F2）跟右键菜单一起在 M2 做
        string title = $"新组合 {_store.State.Groups.Count + 1}";
        var group = _store.CreateGroup(title, selected);
        Status(group is null ? "创建失败" : $"已创建「{group.Title}」，含 {selected.Count} 项");
        Render(animate: false);
    }

    // ------------------------------------------------------------ 位置（§10）

    private void ApplySavedBounds()
    {
        var saved = WindowStateStore.Load();
        if (saved is null) { CenterOnPrimaryWorkArea(); return; }

        int w = DipToPhys(saved.WidthDip), h = DipToPhys(saved.HeightDip);

        var anchor = new Native.POINT { X = saved.X + w / 2, Y = saved.Y + h / 2 };
        // 拔过副屏后保存的坐标可能落在已不存在的屏幕上，表现是"热键按了没反应"
        if (!Native.PointOnAnyMonitor(anchor)) { CenterOnPrimaryWorkArea(); return; }

        SetBounds(saved.X, saved.Y, w, h);
    }

    private void CenterOnPrimaryWorkArea()
    {
        var origin = new Native.POINT { X = 1, Y = 1 };
        var work = Native.TryGetWorkArea(origin, out var wa)
            ? wa
            : new Native.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 };

        int w = DipToPhys(Width), h = DipToPhys(Height);
        SetBounds(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 3, w, h);
    }

    /// <summary>当前窗口的 DPI 缩放系数。取不到时按 1.0 处理。</summary>
    private double DpiScaleX =>
        Handle == IntPtr.Zero ? 1.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;

    private int DipToPhys(double dip) => (int)Math.Round(dip * DpiScaleX);

    private void SetBounds(int x, int y, int w, int h)
    {
        _suppressSave = true;
        Native.SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _suppressSave = false;
        FlushSave();
    }

    private void PositionAtCursor()
    {
        if (!Native.GetCursorPos(out var cursor)) return;
        if (!Native.TryGetWorkArea(cursor, out var work)) return;
        if (!Native.GetWindowRect(Handle, out var current)) return;

        int w = current.Width, h = current.Height;
        int x = Math.Clamp(cursor.X - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        int y = Math.Clamp(cursor.Y - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));

        _suppressSave = true;
        Native.SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _suppressSave = false;
    }

    private void RequestSave()
    {
        if (_suppressSave || Handle == IntPtr.Zero) return;
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private DispatcherTimer CreateSaveTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDebounceMs) };
        t.Tick += (_, _) => { t.Stop(); FlushSave(); };
        return t;
    }

    private void FlushSave()
    {
        if (Handle == IntPtr.Zero) return;
        if (!Native.GetWindowRect(Handle, out var r)) return;
        // 位置取物理像素，尺寸取 WPF 的 DIP —— 见 PanelBounds 的注释
        WindowStateStore.Save(new PanelBounds(r.Left, r.Top, Width, Height));
    }

    private void RestoreNav()
    {
        var saved = _store.State.Nav;
        if (saved.Count > 0) _nav.ReplaceWith(saved);
    }

    private void Status(string text)
    {
        StatusText.Text = text;

        // 设置页（RowSpan=5）把主状态栏盖住了，而改键成败、开关切换这些提示
        // 大多发生在设置页里 —— 只写 StatusText 用户根本看不见。
        // 路由一份到设置页自己的状态行；备忘两个覆盖层（MemoStatus / MemoEditStatus）是同一套。
        if (SettingsPanel.Visibility == Visibility.Visible) SettingsStatus.Text = text;
    }
}

/// <summary>Shell 动作。M1 只需要这三件，M2 会扩到完整右键菜单。</summary>
internal static class ShellOps
{
    public static bool Open(string path, bool elevated, out string error)
    {
        error = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = WorkingDirOf(path)
            };
            // runas 拉起的是目标程序，本进程始终是普通权限（D14）
            if (elevated) psi.Verb = "runas";

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = ex is System.ComponentModel.Win32Exception && ex.HResult == unchecked((int)0x800704C7)
                ? "已取消管理员授权"
                : $"无法打开：{ex.Message}";
            return false;
        }
    }

    public static void OpenInExplorer(string? folder, string? select)
    {
        try
        {
            // Windows 的文件名允许含半角引号。把路径拼进命令行的话，一个名字里带引号的
            // 目录会让 explorer.exe 收到额外的参数（参数注入）——路径本身来自拖放和
            // state.json，属于半可信输入。
            //
            // 改走 ArgumentList：由框架负责转义，整个 "/select,<路径>" 仍然作为**一个**
            // 参数投递给 explorer，所以 /select, 那套语义完全不变。
            string arg = select is not null ? "/select," + select : folder ?? string.Empty;
            if (arg.Length == 0) return;

            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }
        catch (Exception) { /* explorer 拒绝就什么也不做，别弹错误框 */ }
    }

    private static string? WorkingDirOf(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) return path;
            string? dir = System.IO.Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception) { return null; }
    }
}
