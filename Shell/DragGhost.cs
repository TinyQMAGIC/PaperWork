using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // UniformGrid
using System.Windows.Interop;
using System.Windows.Media;
using Paperwork.Data;

namespace Paperwork.Shell;

/// <summary>幽灵窗口的三种状态。</summary>
internal enum GhostMode
{
    /// <summary>普通拖拽：跟着光标。</summary>
    Normal,

    /// <summary>压到了某块磁贴的正中心：准备和它并成一个新组合。</summary>
    Merge,

    /// <summary>光标已经拖出面板：松手就是取消。</summary>
    Cancel
}

/// <summary>
/// 拖拽时跟在鼠标上的"虚拟图标"。
///
/// <b>为什么是独立窗口而不是面板里盖一层 Canvas。</b>
/// 面板是 <c>ClipToBounds</c> + 22px 圆角的，鼠标一旦移出面板，盖层里的图标会被圆角裁掉；
/// 而"拖出面板 = 取消"恰恰是最需要视觉反馈的那条路径。开一个分层小窗口就没有这个边界，
/// 顺便还能盖在其他窗口上面。
///
/// <b>坐标一律走物理像素。</b><see cref="Native.GetCursorPos"/> 拿光标、
/// <see cref="Native.SetWindowPos"/> 摆窗口，两边同一个坐标系。
/// 不用 <c>Window.Left/Top</c>——那是 DIP，光标跑到另一块不同 DPI 的屏上就会偏。
///
/// <b>三个扩展样式缺一不可：</b>
/// <list type="bullet">
///   <item><c>WS_EX_TRANSPARENT</c>：鼠标穿透。虽然拖拽期间鼠标已被 <c>Tiles</c> 捕获、
///         消息本来就不会落到这个窗口上，但"两个图标叠到一起"时幽灵正好压着落点，
///         不穿透的话任何基于真实窗口的命中测试都会先撞到它。</item>
///   <item><c>WS_EX_NOACTIVATE</c>：不抢前台。<c>ShowActivated=false</c> 单独用不够，
///         首次 <c>Show()</c> 仍可能激活，表现是"一开拖，搜索框的光标就没了"。</item>
///   <item><c>WS_EX_TOOLWINDOW</c>：不进任务栏与 Alt+Tab。</item>
/// </list>
/// 设置时只加自己这几位、只清 <c>APPWINDOW</c>——<c>AllowsTransparency</c> 依赖的
/// <c>WS_EX_LAYERED</c> 绝不能被整份覆盖掉。
/// </summary>
internal sealed class DragGhost : Window
{
    /// <summary>图标比面板里的大一档，看起来才像"被拎起来了"。</summary>
    private const double IconScale = 1.15;

    /// <summary>
    /// 卡片外框比图标格多出来的宽度：左右内边距 9+9，再加描边。
    /// 描边按<b>合并态的 2.5</b> 算——三态里它最粗，窗口宽度钉死后要给最粗的那一态留够，
    /// 否则合并时图标会被挤掉两像素。
    /// </summary>
    private const double ShellChrome = 9 + 9 + 2.5 + 2.5;

    private readonly Image _art;
    private readonly TextBlock _glyph;
    private readonly UniformGrid _mini;
    private readonly Grid _iconHost;
    private readonly TextBlock _label;
    private readonly Border _shell;

    private IntPtr _hwnd;
    private GhostMode _mode = GhostMode.Normal;

    public DragGhost()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;
        Focusable = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei");

        _art = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);

        // 图标还没解码出来时退回 M1 的占位字形，不能是一个空框
        _glyph = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI Mono"),
            FontSize = 11,
            Visibility = Visibility.Collapsed
        };

        _label = new TextBlock
        {
            FontSize = 11,
            LineHeight = 14,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 30,
            Margin = new Thickness(0, 5, 0, 0)
        };

        // 组合栏既没有 Icon 也没有占位字形（它在面板里是 2x2 迷你格），
        // 不做这一版的话拖组合栏时幽灵是个空框。格子配色与磁贴模板那一份保持一致。
        _mini = new UniformGrid
        {
            Rows = 2, Columns = 2, Width = 26, Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        for (int i = 0; i < 4; i++)
        {
            bool accent = i is 0 or 3;
            var cell = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(1.5)
            };
            cell.SetResourceReference(Border.BackgroundProperty, accent ? "AccentSoft" : "Paper");
            cell.SetResourceReference(Border.BorderBrushProperty, accent ? "AccentLine" : "Rule");
            _mini.Children.Add(cell);
        }

        // 图标座是个**固定正方形**，边长只由图标档决定（见 ShowFor）。
        // 三种内容（真图标 / 占位字形 / 组合迷你格）都居中塞进这一格里，
        // 所以卡片宽度与"这次拖到的图标解码出来没有""拖的是不是组合"无关。
        _iconHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        _iconHost.Children.Add(_art);
        _iconHost.Children.Add(_glyph);
        _iconHost.Children.Add(_mini);

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(_iconHost);
        stack.Children.Add(_label);

        _shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(9, 8, 9, 7),
            Child = stack
        };
        Content = _shell;

        // 色键走应用级资源，和面板/菜单同一套，换主题时幽灵也跟着变
        _shell.SetResourceReference(Border.BackgroundProperty, "Paper");
        _shell.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
        _label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        var ex = Native.GetExStyle(_hwnd);
        ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        ex &= ~Native.WS_EX_APPWINDOW;
        Native.SetExStyle(_hwnd, ex);
    }

    /// <summary>
    /// 按被拖的磁贴铺内容并显示。只在拖拽开始那一刻调一次，之后只动位置。
    ///
    /// <b>卡片宽度只由图标档决定</b>：图标座是 <c>side×side</c> 的固定格，三种内容
    /// （真图标 / 占位字形 / 组合迷你格）都居中塞进同一格，标签的 <c>MaxWidth</c> 也是
    /// <c>side</c>（长名字换行、两行后省略号，而不是把卡片撑宽），最后把 <c>side+23</c>
    /// 一起钉到窗口的 <c>MinWidth/MaxWidth</c> 上。少了最后这一步还是会飘：
    /// 实测 <c>_shell.DesiredSize</c> 已经是正确的 70，<c>Window.Width</c> 却停在上一张
    /// 卡片的 136 不动，要等到标签换成两行、<b>高度</b>先变了，宽度才跟着回来。
    /// </summary>
    public void ShowFor(TileVm tile, double iconDip)
    {
        double side = Math.Round(iconDip * IconScale);
        _iconHost.Width = side;
        _iconHost.Height = side;
        _label.MaxWidth = side;

        // 只钉内容不够，窗口这一层也得钉——原因见上面那段。
        double outer = side + ShellChrome;
        MinWidth = outer;
        MaxWidth = outer;

        _art.Width = side;
        _art.Height = side;
        _art.Source = tile.Icon;

        bool mini = tile.IsGroupTile && tile.Icon is null;
        _art.Visibility = !mini && tile.Icon is not null ? Visibility.Visible : Visibility.Collapsed;
        _mini.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;

        _glyph.Text = tile.DisplayGlyph;
        _glyph.Visibility = !mini && tile.Icon is null && tile.DisplayGlyph.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _label.Text = tile.Label;

        SetMode(GhostMode.Normal);
        if (!IsVisible) Show();

        // 尺寸是这一轮才定的，而 CenterOn 要拿窗口真实宽高做居中：
        // 不先走一遍布局，第一帧就会按上一张卡片的尺寸摆，看着像偏了半格。
        UpdateLayout();
        Follow();
    }

    /// <summary>
    /// 贴到光标上：**图标的中心就是光标**，不再挂在光标右下角——
    /// 挂在角上离鼠标有一段距离，看不出"我正拎着它"。
    /// </summary>
    public void Follow()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetCursorPos(out var cursor)) return;
        CenterOn(cursor.X, cursor.Y);
    }

    /// <summary>让幽灵的中心对准给定的屏幕物理坐标（合并态下吸附到目标磁贴中心）。</summary>
    public void CenterOn(int physX, int physY)
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var rect)) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, physX - rect.Width / 2, physY - rect.Height / 2, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 三态的不透明度是一个序列：<b>普通 0.78 → 合并 0.92 → 取消 0.45</b>。
    /// 普通态必须是半透明的——它就是"我正拎着个东西"的那个"虚"，实心卡片会让人以为
    /// 面板里多了一块真磁贴。合并态反而要更实（落点已经认准了），取消态最虚。
    /// 所以普通态不能往 0.9 以上调，否则和合并态糊成同一个状态。
    /// </summary>
    public void SetMode(GhostMode mode)
    {
        if (_mode == mode && IsVisible) return;
        _mode = mode;

        switch (mode)
        {
            case GhostMode.Merge:
                _shell.SetResourceReference(Border.BorderBrushProperty, "Accent");
                _shell.BorderThickness = new Thickness(2.5);
                Opacity = 0.92;
                break;

            case GhostMode.Cancel:
                _shell.SetResourceReference(Border.BorderBrushProperty, "Ink3");
                _shell.BorderThickness = new Thickness(1.5);
                Opacity = 0.45;
                break;

            default:
                _shell.SetResourceReference(Border.BorderBrushProperty, "Accent");
                _shell.BorderThickness = new Thickness(1.5);
                Opacity = 0.78;
                break;
        }
    }

    public void HideGhost()
    {
        if (IsVisible) Hide();
    }
}
