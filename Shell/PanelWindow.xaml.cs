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

        SearchBox.TextChanged += (_, _) => ApplyQuery();
        SearchBox.GotKeyboardFocus += (_, _) => SearchHint.Visibility = Visibility.Collapsed;
        SearchBox.LostKeyboardFocus += (_, _) =>
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>防抖后的目录变更。只重建当前这一层，不碰导航栈也不写盘。</summary>
    private void OnWatchedFolderChanged()
    {
        if (_nav.Current.Kind != NavKind.Folder) return;
        App.Log($"watcher fired: {_nav.Current.Path}");
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
        // Win11 原生菜单的 owner-draw 条目靠这四条消息画自己，消息发给 hwndOwner（就是我们）。
        // 不转发的话「资源管理器菜单」弹出来一半条目是空白。
        if (NativeContextMenu.HandleMenuMessage((uint)msg, wParam, lParam, out IntPtr menuResult))
        {
            handled = true;
            return menuResult;
        }

        if ((msg == Native.WM_HOTKEY && wParam.ToInt32() == Native.HOTKEY_ID) || msg == Native.WM_SHOWTOGGLE)
        {
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

#if DEBUG
        // 进程资源快照要走一次内核查询，调试时留着有用
        App.Log($"show #{_probe.ShowCount + 1} last={_probe.LastMs:F1}ms :: {PerfProbe.SnapshotResource()}");
#else
        // Release 下这句话跑在 §13.3 那条 P95 < 30ms 的关键路径上，
        // 资源快照只是给调试看的，省掉它。
        App.Log($"show #{_probe.ShowCount + 1} last={_probe.LastMs:F1}ms");
#endif
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
        var frames = _nav.Frames;
        var pieces = new List<object>(frames.Count * 2 - 1);

        // 只有一层时显示标题、收起面包屑。两者叠在同一列上，都可见就会重影。
        bool rooted = frames.Count <= 1;
        TitleText.Visibility = rooted ? Visibility.Visible : Visibility.Collapsed;
        Crumb.Visibility = rooted ? Visibility.Collapsed : Visibility.Visible;

        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0)
            {
                pieces.Add(new TextBlock
                {
                    Text = "/",
                    FontSize = 11,
                    Margin = new Thickness(7, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("Ink3")
                });
            }

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

    private void ApplyQuery(bool animate = true)
    {
        _query = SearchBox.Text?.Trim() ?? string.Empty;

        IEnumerable<TileVm> tiles = _board.Tiles;
        if (_query.Length > 0)
            tiles = tiles.Where(t => t.Label.Contains(_query, StringComparison.CurrentCultureIgnoreCase));

        var list = tiles.ToList();
        foreach (var t in list) t.Highlighted = _query.Length > 0;

        _shown.Clear();
        foreach (var t in list) _shown.Add(t);

        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (list.Count == 0)
        {
            // 文件夹层此刻可能只有空壳（内容还在后台枚举）。这时候再说"把任意文件拖进来"
            // 是在骗人——用户会以为这是个空目录，然后把自己拖进去的文件当成丢了。
            bool loading = _board.Subtitle == BoardBuilder.FolderLoadingText;

            EmptyTitle.Text = _query.Length > 0
                ? $"没有匹配「{_query}」的条目"
                : loading ? BoardBuilder.FolderLoadingText : "把任意文件拖进来";
            EmptySub.Text = _query.Length > 0
                ? "只搜当前这一层，不跨组合"
                : loading ? "稍等一下" : "程序、快捷方式、文件夹、文档、图片、压缩包…";
        }

        StatusText.Text = _query.Length > 0
            ? $"{list.Count} / {_board.Tiles.Count} 项"
            : _board.Subtitle;

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
        if (source is not DependencyObject node) return null;
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.DataContext is TileVm vm) return vm;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Esc 取消拖拽后的那次抬手，不算点击
        if (_suppressClick)
        {
            _suppressClick = false;
            e.Handled = true;
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
        if (TryCaptureHotkey(e)) return;

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
            menu.Items.Add(MenuItem(tile.HasCustomLabel ? "恢复原名" : "重命名…", () => RenameEntry(eid)));
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
            menu.Items.Add(MenuItem("重命名…", () => RenameGroup(gid)));
            menu.Items.Add(Sep());
            menu.Items.Add(MenuItem("删除组合（条目退回主界面）", () =>
            {
                _store.RemoveGroup(gid);
                if (_nav.Current.Kind == NavKind.Group && _nav.Current.Id == gid) _nav.PopTo(0);
                Render(animate: false);
            }, danger: true));
        }

        return menu;
    }

    private MenuItem MenuItem(string text, Action act, bool danger = false)
    {
        var item = new MenuItem { Header = text, Style = (Style)FindResource("PaperMenuItem") };
        if (danger) item.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x40, 0x2E));
        item.Click += (_, _) => act();
        return item;
    }

    /// <summary>分隔条要单独给样式，理由见 App.xaml 里「右键菜单：纸张化」那段注释。</summary>
    private Separator Sep() => new() { Style = (Style)FindResource("PaperSeparator") };

    private void ShowNativeMenu(TileVm tile)
    {
        if (string.IsNullOrWhiteSpace(tile.Path)) return;

        // 此刻 WPF 的 ContextMenu 还在关闭动画里，同步弹原生菜单会和它抢前台、
        // 菜单一闪就没。记下坐标，等这一轮消息泵空了再弹。
        var cursor = System.Windows.Forms.Cursor.Position;
        int x = cursor.X, y = cursor.Y;
        string path = tile.Path;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            bool ok = NativeContextMenu.TryShow(Handle, path, x, y);
            App.Log($"native menu ok={ok} verbs={NativeContextMenu.LastVerbCount} " +
                    $"fail='{NativeContextMenu.LastFailure}'");
            if (!ok && !string.IsNullOrEmpty(NativeContextMenu.LastFailure))
                Status("系统菜单不可用：" + NativeContextMenu.LastFailure);
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void RenameEntry(int id)
    {
        var entry = _store.Entry(id);
        if (entry is null) return;

        string? typed = PromptText.Show(this, "重命名", entry.Label ?? entry.Path);
        if (typed is null) return;

        // 和 Shell 显示名一致就等于没改，把自定义标记清掉，免得以后图标改名不跟随
        bool sameAsShell = string.Equals(typed, DeriveFromTile(id), StringComparison.Ordinal);
        _store.RenameEntry(id, sameAsShell ? null : typed);
        Render(animate: false);
    }

    /// <summary>组合名是面板自造的概念，磁盘上没有对应物，所以只改标题、不碰任何路径。</summary>
    private void RenameGroup(int id)
    {
        var group = _store.Group(id);
        if (group is null) return;

        string? typed = PromptText.Show(this, "重命名组合", group.Title);
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

    private void Status(string text) => StatusText.Text = text;
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
