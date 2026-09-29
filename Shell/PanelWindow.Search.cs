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
        };

        // 键盘全在搜索框这一层处理：焦点始终留在框里（列表不可聚焦），
        // 这样连着打字不会被 ListBox 抢走。
        SearchBox.PreviewKeyDown += OnSearchKeyDown;

        // 点一条结果就执行。用 handledEventsToo=true：ListBoxItem 会把 MouseUp 吃掉。
        SearchList.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnSearchClick), true);

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearch(); };
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

        if (_rows.Count > 0) SearchList.SelectedIndex = 0;

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
                if (SearchList.SelectedItem is SearchRowVm row) OpenHit(row.Hit);
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

    private void OnSearchClick(object sender, MouseButtonEventArgs e)
    {
        if (!_searching) return;

        if (RowFrom(e.OriginalSource) is { } row)
        {
            OpenHit(row.Hit);
            e.Handled = true;
        }
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
