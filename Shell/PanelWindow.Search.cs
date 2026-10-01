using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Paperwork.Data;

namespace Paperwork.Shell;

/// <summary>
/// 全局搜索：条目（含组合里的）+ 组合 + 备忘（标题与正文）。
///
/// <b>零 IO</b>：三份数据全在 <c>AppState</c> 的内存副本里，一次查询几十微秒，
/// 所以不需要索引、不需要后台线程、不需要取消 token —— 防抖只是防手速，不是等磁盘。
/// <b>不搜文件夹内部</b>：那是产品决定（见 <see cref="SearchService"/> 的注释）。
///
/// 交互上刻意做得很轻：初始态（点了搜索框还没打字）界面<b>完全不变</b>，
/// 输入后只在内容区出结果 —— 搜索框、发丝线、右上角按钮都在原位，
/// 左上角变成 <c>Paperwork / Search</c>，位置与首页标题一致，所以进搜索不跳。
/// </summary>
public partial class PanelWindow
{
    /// <summary>备忘编辑页"从哪来、回哪去"。从搜索进的，退出来要回搜索，不能凭空蹦出备忘列表。</summary>
    private enum MemoReturn { List, Search }

    /// <summary>防抖 120ms。查的是内存，这个值只为防手速。</summary>
    private DispatcherTimer? _searchTimer;

    private bool _searching;

    /// <summary>当前结果。用 ObservableCollection 是为了换查询时列表自己跟着变，不用重建 ItemsSource。</summary>
    private readonly ObservableCollection<SearchRowVm> _rows = new();

    /// <summary>
    /// 给"程序/自定义图标"条目挂的<b>一次性探针磁贴</b>。
    /// <see cref="IconPump"/> 只认 TileVm，而它的价值恰好在缓存：首页已经取过的图标，
    /// 搜索里直接命中，不用再查一遍 Shell。键 = 探针，值 = 结果行。
    /// </summary>
    private readonly Dictionary<TileVm, SearchRowVm> _iconProbes = new();

    private MemoReturn _memoReturn = MemoReturn.List;

    private void InitSearch()
    {
        SearchList.ItemsSource = _rows;

        SearchBox.TextChanged += (_, _) =>
        {
            _query = SearchBox.Text?.Trim() ?? string.Empty;
            QueueSearch();
            UpdateSearchHint();      // 清空之后占位文案要立刻回来，见下面那段注释
        };

        // 键盘全在搜索框这一层处理：焦点始终留在框里（列表不可聚焦），
        // 这样连着打字不会被 ListBox 抢走。
        SearchBox.PreviewKeyDown += OnSearchKeyDown;

        // 单击只选中（ListBox 自己会选），**双击才执行** —— 与首页同一口径
        // （TileVm.NeedsDoubleClick）。用 handledEventsToo=true：ListBoxItem 会把 MouseUp 吃掉。
        SearchList.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnSearchClick), true);

        // 选中与执行都挂在 **MouseLeftButtonDown** 上（见 OnSearchRowDown）：
        //   · 不用 MouseUp —— WPF 里 MouseUp 的 ClickCount 不足以当双击判据
        //     （正因为它不可靠，WPF 才另有 MouseDoubleClick 这个事件）；
        //   · 也不用 MouseDoubleClick —— 它是 Control 上的 **Direct** 路由事件，
        //     双击命中的是 ListBoxItem，挂在 ListBox 上收不到（Direct 不往上冒）。
        // MouseDown 的 ClickCount 才是可靠的那个（WPF 的双击判定本身发生在按下那一下）。
        // handledEventsToo：ListBoxItem 会把这个事件吃掉。
        SearchList.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnSearchRowDown), true);

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearch(); };

        // 点搜索框以外的地方 → 把焦点从框里移开（光标不再闪、占位文案回来）
        PreviewMouseLeftButtonDown += OnSearchBlurClick;
    }

    /// <summary>
    /// 点搜索框<b>以外</b>的地方就把焦点从搜索框上移开：输入光标消失、「搜索…」占位文案回来，
    /// 也就是回到"点搜索框之前"的样子。
    ///
    /// 为什么要单独做这一步：面板背景是 Border / Grid，它们<b>不可聚焦</b>，点上去 WPF
    /// 没有理由把焦点从 TextBox 拿走 —— 于是光标一直闪。这里主动把焦点交给主面板根容器
    /// （和呼出时用的是同一个落点），而不是 <c>Keyboard.ClearFocus()</c>：
    /// 焦点必须有个真实去处，否则 WPF 连键盘事件都没有目标（设置页那段注释记过这个教训）。
    ///
    /// 刻意<b>不设</b> <c>e.Handled</c>：这一下点击还得照常交给磁贴 / 结果行 / 拖拽那些处理器。
    /// </summary>
    private void OnSearchBlurClick(object sender, MouseButtonEventArgs e)
    {
        if (!SearchBox.IsKeyboardFocusWithin) return;      // 框本来就没焦点，什么都不用做
        if (IsInsideTextInput(e.OriginalSource)) return;   // 点回搜索框（或别的输入框）别抢

        BlurSearchBox();
    }

    /// <summary>
    /// 把焦点从搜索框交还主面板根容器。两条路共用它：
    /// ① 客户区点击（<see cref="OnSearchBlurClick"/>）；
    /// ② <b>非客户区</b>点击 —— 标题栏拖拽区（CaptionHeight=46）与拉伸边框走 WM_NCLBUTTONDOWN，
    ///    压根不产生 WPF 鼠标事件，所以由 <c>PanelWindow.WndProc</c> 调这里（见那边的注释）。
    /// </summary>
    internal void BlurSearchBox()
    {
        if (SearchBox.IsKeyboardFocusWithin) Keyboard.Focus(MainRoot);
    }

    /// <summary>
    /// 占位文案「搜索…」只在<b>框里没字、且没有键盘焦点</b>时显示。
    ///
    /// 不能只在 <c>GotKeyboardFocus</c> / <c>LostKeyboardFocus</c> 里算一次：
    /// "先失焦、再被清空"是真实存在的一条路（点空白退出搜索就是它）——
    /// 失焦那一刻框里还有字，于是判成"不显示"；紧接着文本被清空，却没人再算一遍，
    /// 结果是一个空框、没有提示、也没有光标，看着像坏了。所以文本一变就重算。
    /// </summary>
    private void UpdateSearchHint() =>
        SearchHint.Visibility = !SearchBox.IsKeyboardFocusWithin && string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>沿着上层找，判断这一下有没有落在文本输入上 —— 命中就不该抢它的焦点。</summary>
    private static bool IsInsideTextInput(object source)
    {
        DependencyObject? node = source as DependencyObject;
        while (node is not null)
        {
            if (node is TextBoxBase) return true;
            node = ParentOf(node);
        }
        return false;
    }

    private void QueueSearch()
    {
        if (_searchTimer is null) return;

        // 清空要立刻退场（不然收尾还挂着 120ms），有内容才排队
        if (_query.Length == 0)
        {
            _searchTimer.Stop();
            ExitSearch();
            return;
        }

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void RunSearch()
    {
        var (hits, total) = SearchService.Query(
            _store.State.Entries, _store.State.Groups, _store.Notes(), _query);

        bool first = !_searching;
        _searching = true;

        // 主状态栏让位。可见性开关虽然也写在 ApplyQuery 里，但**进搜索这条路不经过它**：
        // 这里的 StatusText.Visible 是上一次 ApplyQuery 留下的，没人动它就一直是 Visible，
        // 和搜索自己的状态行叠在左下角（实测重叠过两次，第二次才找到这个时序）。
        StatusText.Visibility = Visibility.Collapsed;

        DetachIconProbes();
        _rows.Clear();
        var probes = new List<TileVm>();

        foreach (var h in hits)
        {
            var row = new SearchRowVm(h);
            _rows.Add(row);

            // 程序 / 自定义图标：挂探针走 IconPump。文件夹和普通文档不挂——
            // 它们用的是预设线稿，跟磁贴同一支笔，换了真图标反而乱（UI 手册 §0 第 3 条）。
            if (h.Kind == SearchHitKind.Entry && h.WantsShellIcon && h.EntryId is int id)
            {
                var entry = _store.Entry(id);
                var probe = new TileVm
                {
                    EntryId = id,
                    Kind = h.IsDir ? TileKind.Folder : TileKind.Shortcut,
                    Path = h.Path ?? string.Empty,
                    Label = h.Name.Prefix + h.Name.Match + h.Name.Suffix,
                    // 防止 pump 用 Shell 显示名覆盖 —— 搜索行显示的是我们自己的名字
                    HasCustomLabel = true,
                    CustomIcon = entry?.Icon
                };
                probe.PropertyChanged += OnProbeIcon;
                _iconProbes[probe] = row;
                probes.Add(probe);
            }
        }

        // 目标像素与磁贴用同一档：缓存键含尺寸，同档才能命中首页已经取过的那一份
        if (probes.Count > 0)
            _icons?.Enqueue(probes, (int)Math.Round(IconSize * DpiScaleX));

        // 磁贴区与它的空态都让位：两者都在第 4 行，叠着会出现"结果上面压着一圈虚线"
        Scroller.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Visible;
        SearchEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 默认**不预选**（2026-10-01）：高亮只该由鼠标悬停、用户点击或 ↑↓ 产生。
        // 原来这里写的是 `if (_rows.Count > 0) SelectedIndex = 0;` —— 每出一批结果就
        // 强行选中第一条，于是"还没操作就有东西亮着"；而悬停态与选中态又是同一套颜色
        // （AccentSoft + AccentLine），鼠标一划过去就更分不清是谁亮。
        // 集合重建本来就会清掉选中，这里显式写出来：哪天换成别的填充方式（比如逐条 Insert），
        // 也不会又冒出一个"凭空亮着"的第一条。
        // 回车那条快路径没丢 —— 见 OnSearchKeyDown 里"没选中就打开第一条"。
        SearchList.SelectedIndex = -1;

        // 状态行只留两样：几项、Esc 返回。快捷键提示是多余的——用户要么已经会用，
        // 要么看一遍也不会记住，常年挂在角落里只是噪音。
        string more = total > hits.Count ? $"（还有 {total - hits.Count} 项）" : string.Empty;
        SearchStatus.Text = _rows.Count == 0 ? "0 项 · Esc 返回" : $"{total} 项{more} · Esc 返回";

        // 左上角换成 Paperwork / Search（位置与首页标题一致）
        BuildBreadcrumb();

        // 首次进入时把焦点钉回搜索框：点结果之后焦点可能落在别处
        if (first && !SearchBox.IsFocused) SearchBox.Focus();
    }

    private void ExitSearch()
    {
        if (!_searching) return;
        _searching = false;

        SearchPanel.Visibility = Visibility.Collapsed;
        DetachIconProbes();
        _rows.Clear();
        Scroller.Visibility = Visibility.Visible;

        // 磁贴与状态栏恢复原样（EmptyHint 的可见性也在里面算）
        ApplyQuery(animate: false);
        BuildBreadcrumb();
    }

    /// <summary>探针的图标到了就转给结果行。探针是一次性的，行也没了就解绑，不留悬挂引用。</summary>
    private void OnProbeIcon(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TileVm.Icon)) return;
        if (sender is TileVm probe && probe.Icon is not null && _iconProbes.TryGetValue(probe, out var row))
            row.Icon = probe.Icon;
    }

    private void DetachIconProbes()
    {
        foreach (var probe in _iconProbes.Keys) probe.PropertyChanged -= OnProbeIcon;
        _iconProbes.Clear();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (!_searching || _rows.Count == 0)
        {
            // 没在搜索时什么都不做：Esc 由窗口那条阶梯（清词 → 返回上层 → 收面板）处理。
            // 空态下也只有 Esc 有意义，同样交给下面。
            if (e.Key == Key.Escape && _searching) { SearchBox.Clear(); ExitSearch(); e.Handled = true; }
            return;
        }

        switch (e.Key)
        {
            case Key.Down: MoveSelection(1); e.Handled = true; break;
            case Key.Up: MoveSelection(-1); e.Handled = true; break;

            case Key.Enter:
                // 默认不预选（见 ApplyResults），但"打字 → 回车"这条快路径不能一起丢掉：
                // 没有选中项时打开**第一条**；用 ↑↓ 选过之后就按选中的那条走。
                // 走到这里 _rows.Count 一定 > 0（本方法开头就挡掉了空结果）。
                var enter = SearchList.SelectedItem as SearchRowVm
                            ?? (_rows.Count > 0 ? _rows[0] : null);
                if (enter is not null) OpenHit(enter.Hit);
                e.Handled = true;
                break;

            case Key.Escape:
                // 必须在这里收：窗口那条覆盖页守卫会先拦下 Esc，
                // 不放这儿就成了"按 Esc 没反应，而搜索框里还留着字"。
                SearchBox.Clear();
                ExitSearch();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int n = _rows.Count;
        int i = SearchList.SelectedIndex;
        int next = i < 0
            ? (delta > 0 ? 0 : n - 1)
            : Math.Min(n - 1, Math.Max(0, i + delta));

        SearchList.SelectedIndex = next;
        SearchList.ScrollIntoView(SearchList.SelectedItem);
    }

    /// <summary>
    /// 搜索结果上的<b>单击抬起</b>：只管"单击即执行"的那些行 —— 组合与备忘
    /// （它们的 <see cref="SearchHit.NeedsDoubleClick"/> 为 false）。文件 / 文件夹不在这里执行。
    /// 选中已经在<b>按下</b>那一下做掉了，见 <see cref="OnSearchRowDown"/>。
    ///
    /// 仍要 <c>e.Handled = true</c>：窗口那条 <c>OnMouseUp</c> 在搜索态是"点空白即退出搜索"，
    /// 虽然它也判了 <c>RowFrom</c>，但把 Handled 留住才是明说的意图 ——
    /// 不吃掉就等于把这一下交给"算不算空白"去猜。
    /// </summary>
    private void OnSearchClick(object sender, MouseButtonEventArgs e)
    {
        if (!_searching) return;

        if (RowFrom(e.OriginalSource) is { } row)
        {
            if (!row.Hit.NeedsDoubleClick) OpenHit(row.Hit);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 搜索结果行的<b>按下</b>：先选中（单击 = 高亮常亮），再判双击是否执行。
    ///
    /// <b>为什么必须自己设 <c>SelectedItem</c></b>（2026-10-01 修的）：列表项设了
    /// <c>Focusable="False"</c>（为了让点完结果还能接着在搜索框里打字），
    /// 而 WPF 的 <c>ListBoxItem</c> <b>在不可聚焦时压根不响应鼠标选中</b>。
    /// 探针实测：同样的容器样式，`Focusable=false` 时合成一次 MouseDown → <c>SelectedIndex</c>
    /// 仍是 -1、<c>IsSelected</c> 仍是 false；改成 `true` 立刻变成 0 / true。
    /// 触发器本身没问题（手动置 `IsSelected` 后底色如期变色）。
    /// 这也解释了右键菜单那条为什么要手动设 —— <c>SearchList.SelectedItem = row;</c>，
    /// 当初撞的就是同一个坑。
    ///
    /// 双击按 <c>ClickCount</c> 判（MouseDown 的 ClickCount 才是可靠的那个），
    /// 规则与首页一致：条目（文件 / 文件夹）双击，组合与备忘单击。
    /// </summary>
    private void OnSearchRowDown(object sender, MouseButtonEventArgs e)
    {
        if (!_searching) return;
        if (RowFrom(e.OriginalSource) is not { } row) return;

        SearchList.SelectedItem = row;      // 单击 = 选中并常亮（不依赖 ListBoxItem 自己）

        if (e.ClickCount < 2) return;
        if (!row.Hit.NeedsDoubleClick) return;

        OpenHit(row.Hit);
        e.Handled = true;
    }

    /// <summary>
    /// 命中的那一行。点到的大多是行内部的 TextBlock / <c>Run</c>，得往上找到 ListBoxItem。
    /// 必须用 <see cref="ParentOf"/> 而不是 <see cref="VisualTreeHelper.GetParent"/>：
    /// <c>Run</c> 不是 Visual，直接喂进去会抛（右键那段注释里有完整来历）。
    /// </summary>
    private SearchRowVm? RowFrom(object source)
    {
        DependencyObject? node = source as DependencyObject;
        while (node is not null && node is not ListBoxItem) node = ParentOf(node);
        return (node as ListBoxItem)?.Content as SearchRowVm;
    }

    /// <summary>
    /// 点在搜索列表自己的零件上 —— **只认滚动条**。这类点<b>不算空白</b>：
    /// 拖滚动条拖到一半把搜索关掉，是最让人恼火的一类误触。
    ///
    /// <b>千万别把 ListBox 本身算进来</b>：它铺满整个内容区，点在最后一行下面的空白处时，
    /// 命中的正是它模板里那层透明边框 —— 算成零件的话，"点空白退出"就永远不生效了
    /// （第一次就是这么写坏的）。
    /// </summary>
    private bool IsSearchChrome(object source)
    {
        DependencyObject? node = source as DependencyObject;
        while (node is not null)
        {
            if (node is ScrollBar) return true;
            node = ParentOf(node);
        }
        return false;
    }

    /// <summary>
    /// 搜索结果行的右键菜单。<b>备忘不给菜单</b>（它的入口就是编辑页，右键没有可做的事），
    /// 其余给四件：以管理员运行 / 打开磁贴位置 / 打开文件位置 / 资源管理器菜单。
    /// </summary>
    private ContextMenu BuildSearchMenu(SearchHit hit)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("PaperContextMenu") };

        // 文件夹没有"运行"这回事；组合是面板自造的概念，磁盘上没有对应物
        if (hit.Kind == SearchHitKind.Entry && !hit.IsDir && hit.Path is string p)
        {
            menu.Items.Add(MenuItem("以管理员身份运行", () =>
            {
                if (!ShellOps.Open(p, elevated: true, out string err)) Status(err);
            }));
        }

        menu.Items.Add(MenuItem("打开磁贴位置", () => RevealTile(hit)));

        if (hit.Kind == SearchHitKind.Entry && !string.IsNullOrWhiteSpace(hit.Path))
        {
            menu.Items.Add(Sep());
            menu.Items.Add(MenuItem("打开文件位置", () => ShellOps.OpenInExplorer(null, hit.Path!)));
            menu.Items.Add(MenuItem("资源管理器菜单", () => ShowNativeMenu(hit.Path!)));
        }

        MarkMenuEdges(menu);
        return menu;
    }

    /// <summary>
    /// 「打开磁贴位置」：跳到这条磁贴真正待的那一层——在组合里就进那个组合，在主界面就回首页，
    /// 并且把那一块选中（否则"跳过去了但看不出是哪个"等于没跳）。
    /// </summary>
    private void RevealTile(SearchHit hit)
    {
        // 先清空搜索栏：它触发 TextChanged → 立即 ExitSearch（搜索层收起、面包屑复原）。
        // 不清的话跳转之后框里还留着刚搜的词，下次呼出面板看着像"还停在搜索里"。
        // 下面那句 ExitSearch 只是兜底——万一 Clear 没引起文本变化（比如本来就是空的）。
        SearchBox.Clear();
        ExitSearch();

        int? entryId = hit.Kind == SearchHitKind.Entry ? hit.EntryId : null;
        int? groupId = hit.Kind == SearchHitKind.Group ? hit.GroupId : null;

        if (entryId is int eid)
        {
            // 条目自己所在的层 = 它的 GroupId；null 就是在主界面
            groupId = _store.Entry(eid)?.GroupId;
        }

        // 先回首页再压目标组合：Push 不检查距离，直接压可能压成一个不相干的三层栈
        _nav.PopTo(0);

        if (groupId is int gid)
        {
            var g = _store.Group(gid);
            if (g is not null) _nav.Push(new NavFrame(NavKind.Group, gid, null, g.Title));
        }

        Render(animate: true);
        ScheduleIcons();
        SelectTileAfterReveal(entryId, groupId);
    }

    /// <summary>
    /// 跳转之后把那一块选中。<c>TileVm.Selected</c> 有模板里的视觉反馈，
    /// 直接改它而不是重设 ItemsSource（后者会让磁贴整体闪一下，见 OnMouseUp 那段注释）。
    /// </summary>
    private void SelectTileAfterReveal(int? entryId, int? groupId)
    {
        TileVm? target = null;
        foreach (var t in _board.Tiles)
        {
            bool match = entryId is int e ? t.EntryId == e : t.GroupId == groupId;
            if (match) target = t;
        }

        foreach (var t in _board.Tiles) t.Selected = ReferenceEquals(t, target);
    }

    /// <summary>执行一条结果。条目/组合/备忘各走各的路，见下面分支里的说明。</summary>
    private void OpenHit(SearchHit hit)
    {
        switch (hit.Kind)
        {
            case SearchHitKind.Group when hit.GroupId is int gid:
                var g = _store.Group(gid);
                if (g is not null && _nav.Push(new NavFrame(NavKind.Group, gid, null, g.Title)))
                {
                    InvalidateDragForLayer();   // 换层 = 拖拽上下文作废（见那个方法的注释）

                    // 进层了就退搜索：搜索结果已经不属于这一层。
                    // 清空搜索栏同 RevealTile —— 留着旧词，下次呼出看着像还停在搜索里。
                    SearchBox.Clear();
                    ExitSearch();
                    Render(animate: true);
                    ScheduleIcons();
                }
                else Say("已经到最里一层了");
                break;

            case SearchHitKind.Entry when hit.Path is string p:
                if (hit.IsDir)
                {
                    // 文件夹磁贴：和首页点它一样是钻进去；到栈底了交给资源管理器
                    if (!StateStore.SafeIsDirectory(p)) { Say("该文件夹当前不可访问"); break; }

                    if (_nav.AtMaxDepth) { ShellOps.OpenInExplorer(p, select: null); break; }

                    string leaf = Path.GetFileName(p.TrimEnd('\\', '/'));
                    if (string.IsNullOrEmpty(leaf)) leaf = p;

                    if (_nav.Push(new NavFrame(NavKind.Folder, 0, p, leaf)))
                    {
                        InvalidateDragForLayer();   // 换层 = 拖拽上下文作废（见那个方法的注释）

                        // 钻进文件夹：同样清空搜索栏。跳到新的一层之后，
                        // 框里还留着刚才的词就是"半个搜索态"——界面不认，用户会以为还要搜
                        SearchBox.Clear();
                        ExitSearch();
                        Render(animate: true);
                        ScheduleIcons();
                    }
                    break;
                }

                // 文件 / 程序：直接按路径打开，不绕"先切到那一层再点一下"——
                // 结果条目可能在别的组合里，绕过去等于搜索白做。
                // 结果列表**故意不关**：连着打开好几个东西时不用重新搜一遍。
                if (!ShellOps.Open(p, elevated: false, out string err)) Say(err);
                break;

            case SearchHitKind.Note when hit.NoteId is int nid:
                // 编辑页会盖住整个面板，退出来要回到搜索（见 MemoReturn）
                OpenEditor(nid, MemoReturn.Search);
                break;
        }
    }

    /// <summary>
    /// 搜索态下说话要说在<b>搜索面板自己的状态行</b>上：
    /// 主面板那条状态栏此刻被搜索层盖着，写它等于对空气说话。
    /// </summary>
    private void Say(string text)
    {
        if (_searching) SearchStatus.Text = text;
        else Status(text);
    }
}

/// <summary>
/// 搜索结果一行的视图模型。包着不可变的 <see cref="SearchHit"/>，
/// 额外承担两件只有 UI 才知道的事：程序真图标（异步到了才显示，没到就用预设线稿），
/// 以及按项目惯例预先算好的 Visibility（参照 <see cref="NoteVm"/>，省掉转换器）。
/// </summary>
internal sealed class SearchRowVm : INotifyPropertyChanged
{
    public SearchHit Hit { get; }

    public SearchRowVm(SearchHit hit) => Hit = hit;

    public MatchText Name => Hit.Name;
    public MatchText Sub => Hit.Sub;
    public SearchHitKind Kind => Hit.Kind;
    public bool IsNote => Hit.IsNote;

    /// <summary>预设线稿。真图标到了就让位（与磁贴 <c>HasGlyphData</c> 同一个约定）。</summary>
    public Geometry? Glyph => Hit.Glyph;

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnChanged();
            OnChanged(nameof(IconVisibility));
            OnChanged(nameof(GlyphVisibility));
        }
    }

    public Visibility IconVisibility => _icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
