using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Paperwork.Data;

namespace Paperwork.Shell;

/// <summary>
/// 组合磁贴的「更改预设图标」选择页（2026-10-14）。
///
/// 入口有两个（用户拍的 R1 + R3）：
/// <list type="bullet">
///   <item>右键菜单「更改磁贴预设图标」—— 本条链路的正门（<c>BuildMenu</c> 的组合分支）</item>
///   <item>状态栏右侧的「恢复默认图标」—— 只在当前组合<b>确实换过图标</b>时才出现</item>
/// </list>
///
/// 面板本身照设置页那套做（<c>Border</c> + <c>Grid.RowSpan=5</c> + <c>Visibility</c>），
/// **标题栏由浮层自己画**：打开时窗口那三枚按钮被整块盖住，所以"不要备忘录与设置入口"这条
/// 不用额外写代码；禁用那三枚只为挡 ToolTip（焦点路径会漏气泡），理由与 <c>OpenSettings</c> 完全一样。
///
/// 60 个格子**在代码里建**而不是写 XAML：图标族来自生成物
/// <see cref="Glyphs.Group"/>（tools/make-glyphs.py 产出），数量与分区都会随它变 ——
/// 手抄进 XAML 就等于给同一份数据留了第二个源，迟早对不上。
/// 格子的几何、笔宽、状态全部照抄磁贴模板（§4.4 / §5），不另立一套。
/// </summary>
public partial class PanelWindow
{
    /// <summary>正在改哪个组合。null = 这一趟改的不是组合。</summary>
    private int? _pickerGroupId;

    /// <summary>
    /// 正在改哪个**条目**（文件 / 文件夹 / 程序 / 快捷方式）。null = 这一趟改的不是条目。
    /// 与 <see cref="_pickerGroupId"/> 二选一：两个都是 null 时面板没开。
    /// </summary>
    private int? _pickerEntryId;

    /// <summary>当前选中项的 key（null = 还没换过，磁贴画自己的那张：组合是 2×2、条目是类型线稿 / 真图标）。</summary>
    private string? _pickerSelected;

    private readonly List<PickerCell> _pickerCells = new();

    /// <summary>一格的三件套：整格（挂悬停/点击）、图标格（换框色）、名字（换字色/字重）。</summary>
    private sealed record PickerCell(string Key, Border Root, Border Box, TextBlock Name);

    private void InitIconPicker()
    {
        IconPickerClose.Click += (_, _) => CloseIconPicker();
        IconPickerReset.Click += (_, _) => ApplyIcon(null);
        PreviewKeyDown += OnIconPickerKeyDown;

        BuildIconPickerCells();
    }

    /// <summary>
    /// 建一次就够：图标族是编译期常量，面板每次打开只换高亮，不重建视觉树。
    /// 顺序就是 <see cref="Glyphs.Group.Zones"/> 的顺序（分区标题 → 该区格子）。
    /// </summary>
    private void BuildIconPickerCells()
    {
        IconPickerRoot.Children.Clear();
        _pickerCells.Clear();

        foreach (var (title, keys) in Glyphs.Group.Zones)
        {
            var head = new TextBlock
            {
                Text = title,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");

            var rule = new Border { Height = 1 };
            rule.SetResourceReference(Border.BackgroundProperty, "Rule");

            // 分区标题 + 底部发丝线：与手册 §7 的分区小标题同一支笔（10.5 / 600 / 字距靠系统默认）
            var headWrap = new StackPanel { Margin = new Thickness(0, 14, 0, 4) };
            headWrap.Children.Add(head);
            headWrap.Children.Add(rule);
            IconPickerRoot.Children.Add(headWrap);

            // 8 列：与稿子里算的一屏 32 格对齐（524 宽的内容区 496 / 8 = 62 一格）
            var row = new UniformGrid { Columns = 8 };
            foreach (string key in keys) row.Children.Add(BuildPickerCell(key));
            IconPickerRoot.Children.Add(row);
        }
    }

    private UIElement BuildPickerCell(string key)
    {
        // 图标格：42 / R11 / 底 Paper2 / 框 1px Rule（§4.4）
        var box = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true
        };
        box.SetResourceReference(Border.BackgroundProperty, "Paper2");
        box.SetResourceReference(Border.BorderBrushProperty, "Rule");

        // 图形：与磁贴模板逐条对齐（24 视框 + Margin 12 撑成 48 设计格 + Viewbox 缩到格子的 50%）。
        // 这里**不写死渲染尺寸**：交给 42 的图标格去定，和主视图里的线稿磁贴走同一条缩放链。
        var art = new Path
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(12),
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Data = Glyphs.Group.Find(key)
        };
        art.SetResourceReference(Shape.StrokeProperty, "Ink2");
        box.Child = new Viewbox { Stretch = Stretch.Uniform, Child = art };

        var name = new TextBlock
        {
            Text = Glyphs.Group.Label(key),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(box);
        stack.Children.Add(name);

        // 整格：必须给 Transparent 背景，否则空白处不吃命中，悬停与点击都只在图标/文字上生效
        var root = new Border
        {
            Padding = new Thickness(1, 3, 1, 4),
            CornerRadius = new CornerRadius(11),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = stack
        };

        var cell = new PickerCell(key, root, box, name);
        _pickerCells.Add(cell);

        root.MouseEnter += (_, _) => PaintPickerCell(cell, hover: true);
        root.MouseLeave += (_, _) => PaintPickerCell(cell, hover: false);
        root.MouseLeftButtonUp += (_, _) => ApplyIcon(key);
        return root;
    }

    /// <summary>
    /// 三种状态只改底色/描边/字色，**不改尺寸**（§0 硬约束①）：
    /// 常态 = 图标格 Paper2 + 框 Rule；悬停 = 整格 Paper2 + 框 AccentLine；选中 = 整格 AccentSoft + 框 Accent + 名字 Accent 600。
    /// 颜色一律走 SetResourceReference，换纸换主题自动跟随（D15）。
    /// </summary>
    private void PaintPickerCell(PickerCell cell, bool hover)
    {
        bool selected = cell.Key == _pickerSelected;

        cell.Box.SetResourceReference(Border.BackgroundProperty, "Paper2");

        if (selected)
        {
            cell.Root.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            cell.Box.SetResourceReference(Border.BorderBrushProperty, "Accent");
            cell.Name.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            cell.Name.FontWeight = FontWeights.SemiBold;
            return;
        }

        if (hover) cell.Root.SetResourceReference(Border.BackgroundProperty, "Paper2");
        else cell.Root.Background = Brushes.Transparent;

        cell.Box.SetResourceReference(Border.BorderBrushProperty, hover ? "AccentLine" : "Rule");
        cell.Name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        cell.Name.FontWeight = FontWeights.Normal;
    }

    /// <summary>改<b>组合</b>磁贴的预设图标。</summary>
    internal void OpenGroupIconPicker(int groupId) => ShowIconPicker(null, groupId);

    /// <summary>
    /// 改<b>条目</b>磁贴（文件 / 文件夹 / 程序 / 快捷方式）的预设图标。
    /// 与组合走同一张选择页、同一套 60 格 —— 只是写入口不同（<c>SetEntryPresetIcon</c>）。
    /// </summary>
    internal void OpenEntryIconPicker(int entryId) => ShowIconPicker(entryId, null);

    private void ShowIconPicker(int? entryId, int? groupId)
    {
        if (entryId is null && groupId is null) return;
        if (groupId is int gid0 && _store.Group(gid0) is null) return;
        if (entryId is int eid0 && _store.Entry(eid0) is null) return;

        _pickerEntryId = entryId;
        _pickerGroupId = groupId;

        // 与设置页同一条理由：整块覆盖层压在搜索结果上面的话，关掉它会掉回一堆
        // "已经不属于当前视野"的结果里。
        ExitSearch();

        // 点击右键菜单后焦点还留在面板上；不清掉的话，焦点框画在窗口级 AdornerLayer，
        // 按一下 Alt 就穿透盖层现形（设置页踩过同一脚）。
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);

        // 三枚标题栏按钮一起禁：浮层盖住了它们的视觉，禁掉是为了不弹 ToolTip、也不响应误触。
        MemoButton.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        HideButton.IsEnabled = false;

        RefreshPicker();

        IconPickerPanel.Visibility = Visibility.Visible;

        // 清完焦点必须再交出去，否则这一层一个可聚焦的东西都没有 ——
        // WPF 没有键盘事件的目标，Esc 关面板会失效（设置页那段注释记的是同一个坑）。
        Keyboard.Focus(IconPickerRoot);
    }

    private void CloseIconPicker()
    {
        IconPickerPanel.Visibility = Visibility.Collapsed;
        _pickerGroupId = null;
        _pickerEntryId = null;
        MemoButton.IsEnabled = true;
        SettingsButton.IsEnabled = true;
        HideButton.IsEnabled = true;
    }

    /// <summary>把 60 格的选中态、右下角链接的可见性、状态栏文案一起刷成当前值。</summary>
    private void RefreshPicker()
    {
        _pickerSelected = _pickerGroupId is int gid ? _store.Group(gid)?.Icon
                        : _pickerEntryId is int eid ? _store.Entry(eid)?.PresetIcon
                        : null;

        foreach (var cell in _pickerCells) PaintPickerCell(cell, hover: false);

        // 「恢复默认图标」只在**确实换过**时出现：没换过时它是个空动作，摆着只会让人误点
        // （与组合那条同一个规矩）。
        IconPickerReset.Visibility = _pickerSelected is null ? Visibility.Collapsed : Visibility.Visible;

        // 走格式串：中英文语序不一样，插值字符串会把语序焊死
        IconPickerStatus.Text = _pickerSelected is null
            ? string.Format(Str.IconStatusDefault, _pickerCells.Count, DefaultIconName())
            : string.Format(Str.IconStatusDefault, _pickerCells.Count, Glyphs.Group.Label(_pickerSelected));
    }

    /// <summary>
    /// 点一格就立刻落盘并重绘磁贴。**面板不关** —— 底部状态栏会跟着变成"当前 xxx"，
    /// 想连试几个不用重新开面板；关掉面板就能看到磁贴已经换了脸。
    /// </summary>
    private void ApplyIcon(string? key)
    {
        if (_pickerGroupId is int gid)
        {
            _store.SetGroupIcon(gid, key);
            Render(animate: false);
            RefreshPicker();
            return;
        }

        if (_pickerEntryId is not int eid) return;

        // 「恢复默认图标」= **一次剥到第 1 层**：连自定义图标那一层一起清，
        // 与右键菜单那条同一个口径（2026-10-14 用户定：不做两套逻辑）。
        // 只清预设的话，改过自定义图标的磁贴点这里会露出第 2 层 —— 那是"回到上一层"，不是"恢复默认"。
        if (key is null) _store.RestoreEntryDefaultIcon(eid);
        else _store.SetEntryPresetIcon(eid, key);
        Render(animate: false);
        // 剥掉预设之后，程序 / 快捷方式那一类的 WantsShellIcon 从 false 变回 true，
        // 真图标得重新排一次队才取得回来 —— 少了这句就一直停在类型线稿上，直到下次重绘。
        ScheduleIcons();
        RefreshPicker();
    }

    /// <summary>
    /// 没选预设时状态栏那一栏叫什么。组合是 2×2；条目是"磁贴自己的图标"——
    /// 程序/快捷方式是真 Shell 图标，其余是类型线稿。写清楚是为了让"恢复默认会回到什么"这件事
    /// 在点之前就看得见（2026-10-14 定：「恢复默认」一律回到最底那一层）。
    /// </summary>
    private string DefaultIconName()
    {
        if (_pickerEntryId is int eid && _store.Entry(eid) is { } e)
            return Glyphs.IsProgram(e.Path) ? Str.IconDefaultSystemIcon : Str.IconDefaultTypeGlyph;
        return Str.IconDefaultGroup;
    }

    /// <summary>Esc 关面板。先于「返回上一层 / 收起面板」那条链（本处理器比它早注册不了，
    /// 但它在 <c>OnPreviewKeyDown</c> 的覆盖层判定里也在册，见 PanelWindow.xaml.cs）。</summary>
    private void OnIconPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (IconPickerPanel.Visibility != Visibility.Visible) return;

        CloseIconPicker();
        e.Handled = true;
    }

    /// <summary>面板开着 = 覆盖层之一（搜索结果、设置、备忘那几层同册）。</summary>
    private bool IconPickerVisible => IconPickerPanel.Visibility == Visibility.Visible;
}
