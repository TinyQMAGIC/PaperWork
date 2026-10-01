using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Paperwork.Data;

namespace Paperwork.Shell;

/// <summary>
/// 备忘录。列表页与编辑页都是<b>设置页同款的覆盖层</b> —— 不进 <c>NavStack</c>、
/// 不写 <c>state.json</c> 的 <c>Nav</c>。
/// 所以 D17「两层封顶」完全不用动，重启后也不会停在备忘页。
///
/// 备忘是数据不是文件，独立于 <see cref="EntryItem"/> 那条以 Path 为主键的链之外，
/// 因此拖放排序、组合收纳、图标队列全都天然不碰它。
/// </summary>
public partial class PanelWindow
{
    /// <summary>
    /// 当前正在编辑的备忘 id。
    /// <b>0 = 草稿</b>（点了「＋」但一个字都没写，存储里还没有它）或不在编辑态。
    /// 真正敲进第一个字时才建，见 <see cref="PushEdit"/>。
    /// </summary>
    private int _editingNoteId;

    /// <summary>
    /// 正在往编辑框里填内容（<see cref="OpenEditor"/> 的赋值阶段）。
    /// 那期间 <c>TextChanged</c> 触发的 <see cref="PushEdit"/> 必须整体忽略：
    /// 填标题的那一刻正文还是<b>上一条备忘</b>的字，照当时那个画面写进去会串味。
    /// 草稿态更要命 —— 那一下会被当成"用户敲了字"，凭空建出一条带着旧正文的备忘。
    /// </summary>
    private bool _fillingEditor;

    /// <summary>
    /// 备忘落盘去抖。实时保存意味着每敲一个键都会改内存，
    /// 若每次都 <c>Save()</c>，敲一行字就是几十次「整份序列化 + 原子写三份文件」——
    /// 正是之前专门修过的写放大。这里按 400ms 合并。
    /// </summary>
    private DispatcherTimer? _noteSaveTimer;

    private void InitMemo()
    {
        // 同上：备忘列表也是整块覆盖层，先进它就把搜索退掉，避免两层叠着
        MemoButton.Click += (_, _) => { ExitSearch(); OpenMemo(); };
        MemoClose.Click += (_, _) => CloseMemo();
        MemoCrumbHome.Click += (_, _) => CloseMemo();

        MemoEditClose.Click += (_, _) => CloseEditor();
        MemoEditCrumbHome.Click += (_, _) => CloseEditor();

        MemoAdd.Click += (_, _) => CreateNote();
        MemoEmpty.Click += (_, _) => CreateNote();
        MemoNewRow.Click += (_, _) => CreateNote();   // 列表末尾那行虚线入口，与 FAB / 空态同一个动作

        MemoList.PreviewMouseLeftButtonUp += OnNoteClick;
        MemoList.PreviewMouseRightButtonUp += OnNoteRightClick;

        // 点空白返回上一级（与主面板 D10 同一套手感）：列表空白 → 回主面板；编辑页空白 → 回列表。
        //
        // 无论是不是空白都必须吃掉这个事件：主面板在 Window 级也挂了一个「点空白返回上一层」，
        // 不吃掉的话，在备忘录里点空白会去动**下面**那个导航栈 —— 表面看是"返回了"，
        // 实际上把主界面的层级也一起改了。
        //
        // 编辑页多一条：落在表单区（MemoForm）里的点击不算空白。
        // 不然瞄准输入框时点歪到旁边的「标题」标签，整页就关了。
        MemoPanel.MouseLeftButtonUp += (_, e) =>
        {
            if (!InsideInteractive(e.OriginalSource)) CloseMemo();
            e.Handled = true;
        };
        MemoEditPanel.MouseLeftButtonUp += (_, e) =>
        {
            if (!InsideInteractive(e.OriginalSource) && !IsWithin(e.OriginalSource, MemoForm)) CloseEditor();
            e.Handled = true;
        };

        MemoPin.Click += (_, _) =>
        {
            if (_editingNoteId == 0) return;
            _store.SetNotePinned(_editingNoteId, MemoPin.IsChecked == true);
        };

        MemoDelete.Click += (_, _) => DeleteEditing();

        // 实时保存：内容一变就推回存储，落盘交给去抖计时器
        MemoTitle.TextChanged += (_, _) => PushEdit();
        MemoBody.TextChanged += (_, _) => PushEdit();

        _noteSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _noteSaveTimer.Tick += (_, _) => { _noteSaveTimer.Stop(); _store.Save(); };

        // Esc 要在备忘自己的层级里先被吃掉，否则会掉进「返回上一层 / 收起面板」
        PreviewKeyDown += OnMemoKeyDown;
    }

    /// <summary>
    /// Esc 三级链：<b>关编辑页 → 关列表页 → 掉进原有的关闭链</b>。
    /// 只处理自己这两级，剩下的原样交回 <c>OnKeyDown</c>，不去动它。
    /// </summary>
    private void OnMemoKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (MemoEditPanel.Visibility == Visibility.Visible)
        {
            CloseEditor();
            e.Handled = true;
            return;
        }

        if (MemoPanel.Visibility == Visibility.Visible)
        {
            CloseMemo();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ 列表

    private void OpenMemo()
    {
        RenderMemoList();
        MemoPanel.Visibility = Visibility.Visible;
        MemoEditPanel.Visibility = Visibility.Collapsed;
    }

    private void CloseMemo()
    {
        FlushNoteSave();
        MemoPanel.Visibility = Visibility.Collapsed;
    }

    private void RenderMemoList()
    {
        var notes = _store.Notes();
        var vms = new List<NoteVm>(notes.Count);
        foreach (var n in notes) vms.Add(NoteVm.From(n));

        MemoList.ItemsSource = vms;
        MemoEmpty.Visibility = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 与空态互斥：只有**有条目**时，列表末尾才给那行虚线新建入口
        MemoNewRow.Visibility = vms.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MemoStatus.Text = $"{vms.Count} 条备忘 · 按最近更新";
    }

    private void OnNoteClick(object sender, MouseButtonEventArgs e)
    {
        if (FindNoteId(e.OriginalSource) is not int id) return;
        OpenEditor(id);
        e.Handled = true;
    }

    private void OnNoteRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindNoteId(e.OriginalSource) is not int id) return;

        var n = _store.Note(id);
        if (n is null) return;

        var menu = new ContextMenu { Style = (Style)FindResource("PaperContextMenu") };
        menu.Items.Add(MenuItem("编辑", () => OpenEditor(id)));
        menu.Items.Add(MenuItem(n.Pinned ? "取消置顶" : "置顶", () =>
        {
            _store.SetNotePinned(id, !n.Pinned);
            RenderMemoList();
        }));
        menu.Items.Add(Sep());
        // danger: 红字红图标。菜单里唯一的危险动作就是它，标出来免得误点
        menu.Items.Add(MenuItem("删除", () =>
        {
            _store.DeleteNote(id);
            RenderMemoList();

            // 删掉的正好是正在编辑的那条：编辑页必须一起退掉，
            // 否则会停在一个已经不存在的备忘上
            if (_editingNoteId == id)
            {
                _editingNoteId = 0;
                MemoEditPanel.Visibility = Visibility.Collapsed;
                MemoPanel.Visibility = Visibility.Visible;
            }
        }, danger: true));

        // 首末项的悬停圆角要跟着菜单外弧走（UI 手册的菜单几何标准，三处菜单一致）
        MarkMenuEdges(menu);

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>从命中元素往上找备忘卡片，取它 Tag 里的 id。</summary>
    private static int? FindNoteId(object source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is Button { Tag: int id }) return id;
            if (!IsVisual(node)) return null;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>
    /// 命中元素往上找，判断它是否落在可交互控件里（按钮 / 输入框 / 复选框）。
    /// 不落在里面就算空白处。
    /// 输入框模板内部的 Border、ScrollViewer 也算可交互 —— 从它们继续往上走会碰到那个 TextBox。
    /// </summary>
    private static bool InsideInteractive(object source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is Button or TextBox or CheckBox) return true;
            if (!IsVisual(node)) return false;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>命中元素是否落在某个容器里。</summary>
    private static bool IsWithin(object source, DependencyObject container)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, container)) return true;
            if (!IsVisual(node)) return false;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>
    /// VisualTreeHelper.GetParent 只吃 Visual / Visual3D，喂别的会抛。
    /// e.OriginalSource 未必是 Visual（比如 TextBlock 里的 Run），所以每次都先判一下。
    /// </summary>
    private static bool IsVisual(DependencyObject node) =>
        node is Visual || node is System.Windows.Media.Media3D.Visual3D;

    // ------------------------------------------------------------------ 编辑

    private void CreateNote()
    {
        // 不再一进来就建：AddNote 内部直接落盘，于是"点 ＋ 什么都没写就退出"
        // 会在内存和磁盘上都留下一条空备忘（2026-10-01 修）。
        // 改成打开**草稿**（id = 0）：真正敲进第一个字时才建，见 PushEdit。
        OpenEditor(0);
    }

    /// <param name="id">备忘 id。<b>0 = 草稿</b>（新建、还没写任何东西，存储里没有它）。</param>
    /// <param name="back">退出来回哪。默认回备忘列表；从搜索进来时传 <see cref="MemoReturn.Search"/>。</param>
    private void OpenEditor(int id, MemoReturn back = MemoReturn.List)
    {
        var n = id == 0 ? null : _store.Note(id);
        if (id != 0 && n is null) return;

        _editingNoteId = id;
        _memoReturn = back;

        // 赋值会触发 TextChanged → PushEdit：填标题的那一刻正文还是上一条备忘的字，
        // 草稿态更会被当成"敲了字"凭空建出一条。所以填的整个过程屏蔽掉（见 _fillingEditor）。
        _fillingEditor = true;
        try
        {
            MemoTitle.Text = n?.Title ?? string.Empty;
            MemoBody.Text = n?.Body ?? string.Empty;
            MemoPin.IsChecked = n?.Pinned ?? false;
        }
        finally { _fillingEditor = false; }

        MemoPanel.Visibility = Visibility.Collapsed;
        MemoEditPanel.Visibility = Visibility.Visible;
        UpdateEditStatus();

        MemoBody.Focus();
        MemoBody.CaretIndex = MemoBody.Text.Length;
    }

    private void CloseEditor()
    {
        PushEdit();

        // 「空备忘不存在」（2026-10-01）：
        //   · 草稿（id = 0）→ 压根没建过，什么都不用做；
        //   · 已有的备忘被把标题和正文都删空了再退出 → 一并删掉。
        //     不这么做的话，同一个问题换个入口（清空旧备忘）又冒出来一条空条目。
        if (_editingNoteId != 0 && IsEditorBlank())
        {
            _noteSaveTimer?.Stop();             // 没有待写的内容了，别白写一次
            _store.DeleteNote(_editingNoteId);  // 内部 Touch() 落盘
        }
        else
        {
            FlushNoteSave();
        }

        _editingNoteId = 0;
        MemoEditPanel.Visibility = Visibility.Collapsed;

        // 从哪来回哪去。搜索来的就回搜索，并且**重跑一次查询**——
        // 内容刚改过，结果里的标题/片段可能是旧的。
        if (_memoReturn == MemoReturn.Search && SearchPanel.Visibility == Visibility.Visible)
        {
            MemoPanel.Visibility = Visibility.Collapsed;
            _memoReturn = MemoReturn.List;
            RunSearch();
            SearchBox.Focus();
            return;
        }

        // 回到列表：内容可能刚变过，顺带刷新一次
        _memoReturn = MemoReturn.List;
        RenderMemoList();
        MemoPanel.Visibility = Visibility.Visible;
    }

    private void DeleteEditing()
    {
        // 草稿态没有可删的东西：这一下等同于"丢弃草稿退出"。
        // 按钮刻意不置灰 —— 按下去就该有反应，而不是让人怀疑自己点歪了。
        if (_editingNoteId == 0)
        {
            CloseEditor();
            return;
        }

        _store.DeleteNote(_editingNoteId);
        _editingNoteId = 0;

        MemoEditPanel.Visibility = Visibility.Collapsed;

        // 删除后同样按来源回落（删除走的不是 CloseEditor，所以这里要单独判一次）
        if (_memoReturn == MemoReturn.Search && SearchPanel.Visibility == Visibility.Visible)
        {
            MemoPanel.Visibility = Visibility.Collapsed;
            _memoReturn = MemoReturn.List;
            RunSearch();
            SearchBox.Focus();
            return;
        }

        _memoReturn = MemoReturn.List;
        RenderMemoList();
        MemoPanel.Visibility = Visibility.Visible;
    }

    private void PushEdit()
    {
        if (_fillingEditor) return;

        if (_editingNoteId == 0)
        {
            // 草稿：一个字都没写就不建 —— 什么都没写的备忘不该存在（见 CloseEditor）。
            // 只写标题也算写了：那是用户明确留下的东西，照样建。
            if (IsEditorBlank()) return;

            // 真正敲进第一个字，此刻才落盘（AddNote 内部 Touch）—— 低频，不必去抖
            _editingNoteId = _store.AddNote().Id;
        }

        _store.UpdateNote(_editingNoteId, MemoTitle.Text, MemoBody.Text);
        UpdateEditStatus();
        RequestNoteSave();
    }

    private void UpdateEditStatus()
    {
        // 草稿：还没有这条备忘，自然没有字数和时间可报 —— 清空。
        // 否则会留着上一条备忘的「N 字 · 时间」，指着一条不存在的备忘报数。
        if (_editingNoteId == 0) { MemoEditStatus.Text = string.Empty; return; }

        var n = _store.Note(_editingNoteId);
        if (n is null) return;

        MemoEditStatus.Text = $"{n.Body.Length} 字 · {NoteVm.FormatTime(n.UpdatedAt)}";
    }

    /// <summary>
    /// 编辑框里是不是"什么都没写" —— 标题与正文都空（Trim 后）。
    /// 只写标题也算写了：那是用户明确留下的东西，不能当空处理。
    /// </summary>
    private bool IsEditorBlank() =>
        string.IsNullOrWhiteSpace(MemoTitle.Text) && string.IsNullOrWhiteSpace(MemoBody.Text);

    private void RequestNoteSave()
    {
        if (_noteSaveTimer is null) return;
        _noteSaveTimer.Stop();
        _noteSaveTimer.Start();
    }

    /// <summary>立刻把待写的备忘落盘（关面板 / 关编辑页 / 退出时调用）。</summary>
    private void FlushNoteSave()
    {
        if (_noteSaveTimer is null) return;
        if (!_noteSaveTimer.IsEnabled) return;   // 没有待写的，别白写一次

        _noteSaveTimer.Stop();
        _store.Save();
    }
}

/// <summary>
/// 列表里一张横条卡片的数据。刻意做成扁平的：
/// 图钉的 <c>Visibility</c> 直接算好给 XAML，省掉一个 BoolToVisibility 转换器。
/// </summary>
internal sealed class NoteVm
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;
    public bool Pinned { get; init; }

    public Visibility PinVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;

    public static NoteVm From(NoteItem n) => new()
    {
        Id = n.Id,
        Title = string.IsNullOrWhiteSpace(n.Title) ? "（无标题）" : n.Title,
        // 预览折成一行：卡片只给两行高度，换行符会把版式顶乱
        Preview = n.Body.Replace('\r', ' ').Replace('\n', ' '),
        TimeText = FormatTime(n.UpdatedAt),
        Pinned = n.Pinned,
    };

    /// <summary>时间只给到"今天几点 / 昨天 / 几月几日"三档，卡片上不需要精确到分。</summary>
    public static string FormatTime(long ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        var now = DateTimeOffset.Now;

        if (t.Date == now.Date) return t.ToString("HH:mm");
        if (t.Date == now.Date.AddDays(-1)) return "昨天";
        return t.ToString("M/d");
    }
}
