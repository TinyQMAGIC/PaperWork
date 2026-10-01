using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // UniformGrid
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;                 // Path / Shape（预设线稿）
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
    /// 卡片外框比图标座多出来的部分：内边距 9+9，再加描边 —— <b>宽高同一个值</b>
    /// （四边等宽的内边距 + 恒定 1.5 描边，所以卡片是正方形）。
    ///
    /// 描边恒为 1.5，三态之间只改颜色与不透明度：既不改 <c>BorderThickness</c>
    /// （改厚度会让窗口尺寸跳 2px，而 <see cref="CenterOn"/> 是按 <c>rect.Width/2</c> 居中的，
    /// 尺寸一跳中心就偏 1px），也不在里面再垫一圈（那会变成"框里叠框"）。
    /// </summary>
    private const double ShellChrome = 9 + 9 + 1.5 + 1.5;

    /// <summary>
    /// "吸过去"那一下的过渡时长（毫秒）。**只有进入方向有过渡**，解除一律瞬移。
    ///
    /// 70ms ≈ 4 帧：看得出是"贴过去"而不是硬切，又不至于让人觉得迟钝。
    /// 试过 130ms 的对称插值（进入 + 解除都插值），用户反馈"油腻不跟手" ——
    /// 跟手是底线，能省的只有"进入"这一下。
    /// </summary>
    private const double EnterGlideMs = 70;

    private readonly Image _art;
    private readonly TextBlock _glyph;
    private readonly UniformGrid _mini;
    private readonly Viewbox _preset;
    private readonly Path _presetPath;
    private readonly Grid _iconHost;
    private readonly Border _shell;
    private readonly System.Windows.Threading.DispatcherTimer _glide;

    private IntPtr _hwnd;
    private GhostMode _mode = GhostMode.Normal;

    private bool _gliding;
    private int _glideStart;              // Environment.TickCount 起点
    private double _gx0, _gy0;            // 起点（窗口中心，物理像素）
    private double _gx1, _gy1;            // 目标

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
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI Mono, Microsoft YaHei"),
            FontSize = 11,
            Visibility = Visibility.Collapsed
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

        // 预设线稿：文件夹、.txt / .png / .pdf 这些**自绘图标**在面板里画的就是它。
        // **与磁贴模板里的 <c>presetArt</c> 逐字同款**（PanelWindow.xaml 那个 Viewbox + Path）：
        // 24×24 的图形配四边各 12 的 Margin = 48 的设计格，被 Viewbox 缩放后恒为
        // **图标座的 50%**（磁贴那边 40 → 20，这边 side → side/2），笔宽的有效占比也一样
        // （两边都是 3.3%）。所以这里一个尺寸都不用另算，照抄就对齐。
        //
        // 2026-10-01 之前**没有这一档**，症状是：拖文件夹是个空框（它的 DisplayGlyph 是空串，
        // 三个元素全 Collapsed）；拖 .txt / .png 显示的是 "TXT" / "PNG" 文字，
        // 而不是面板上那张线稿。判定漏一档的根子在于"幽灵自己重推了一遍条件"——
        // 现在一律照抄 TileVm 的旗标，见 ShowFor。
        _presetPath = new Path
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(12),
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        _preset = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = _presetPath,
            Visibility = Visibility.Collapsed
        };

        // 图标座是个**固定正方形**，边长只由图标档决定（见 ShowFor）。
        // 四种内容（真图标 / 预设线稿 / 占位字形 / 组合迷你格）都居中塞进这一格里，
        // 所以卡片宽度与"这次拖到的图标解码出来没有""拖的是不是组合"无关。
        // 加入顺序照抄磁贴模板（art → 文字 → 迷你格 → 预设线稿），虽然四态互斥、
        // 谁在上层都一样，但保持同序才好在两处之间对照着读。
        _iconHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        _iconHost.Children.Add(_art);
        _iconHost.Children.Add(_glyph);
        _iconHost.Children.Add(_mini);
        _iconHost.Children.Add(_preset);


        // 描边恒为 1.5：三态之间**只改颜色与不透明度**，绝不改厚度 ——
        // 改厚度会让窗口尺寸跳 2px，而定位是按 rect.Width/2 居中的，尺寸一跳中心就偏。
        // 也不再往里面垫"合并态内环"：那会和外框叠成两圈（2026-09-30 用户反馈"框里叠一个框"）。
        _shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.5),
            // 四边同宽：卡片里只有图标了（B 方案去掉标签），上下不再需要不对称的留白
            Padding = new Thickness(9, 9, 9, 9),
            Child = _iconHost
        };
        Content = _shell;

        _glide = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render)
        { Interval = TimeSpan.FromMilliseconds(16) };
        _glide.Tick += OnGlideTick;

        // 色键走应用级资源，和面板/菜单同一套，换主题时幽灵也跟着变
        _shell.SetResourceReference(Border.BackgroundProperty, "Paper");
        _shell.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
        // 线稿与磁贴模板里那条同一个色（Ink2）—— 换纸换色自动跟随（D15）
        _presetPath.SetResourceReference(Shape.StrokeProperty, "Ink2");

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
    /// <b>卡片是固定正方形，尺寸只由图标档决定</b>：图标座是 <c>side×side</c> 的固定格，
    /// 三种内容（真图标 / 占位字形 / 组合迷你格）都居中塞进同一格，卡片 = <c>side + ShellChrome</c>。
    ///
    /// <b>B 方案（2026-09-30）：幽灵不再显示名字。</b>
    /// 去掉标签有三个理由：① 卡片尺寸与名字彻底无关（原来高度随标签行数在 85↔99 之间跳，
    /// 因为宽度钉了、<b>高度漏了</b>，而窗口是 <c>SizeToContent=WidthAndHeight</c>）；
    /// ② 长名字不会再从词中间断成"DeepSee / k Harne…"；
    /// ③ 拖拽时"我拎着哪个"本来就由<b>源磁贴压暗</b> + 光标位置表达，标签是冗余信息，
    /// 去掉后卡片更小、更不容易挡住落点。代价：拖两个长得像的图标时分不清 —— 可接受。
    /// </summary>
    public void ShowFor(TileVm tile, double iconDip)
    {
        double side = Math.Round(iconDip * IconScale);
        _iconHost.Width = side;
        _iconHost.Height = side;

        // 宽高**都要钉**：窗口是 SizeToContent=WidthAndHeight，只钉一边另边仍会跟着内容走。
        // 历史上踩过两次：先是宽度会停在上一张卡片的尺寸（见下面 Follow 前的 UpdateLayout），
        // 再是高度随标签行数变。现在两个方向一起钉。
        double outer = side + ShellChrome;
        MinWidth = outer;
        MaxWidth = outer;
        MinHeight = outer;
        MaxHeight = outer;

        _art.Width = side;
        _art.Height = side;
        _art.Source = tile.Icon;

        // 图标四态与磁贴模板一一对应，优先级：组合迷你格 → 真图标 → 预设线稿 → 扩展名文字。
        //
        // **判定一律用 TileVm 自己的旗标**，不要在这儿重新推条件：
        // <c>HasGlyphData</c> / <c>HasGlyphText</c> 里都已经含了"真图标到了就让位"这条约定，
        // 手写一遍条件就会漏档 —— 2026-10-01 漏掉预设线稿那一档就是这么来的
        // （文件夹拖起来是空框、.txt 显示成 "TXT" 文字）。
        bool mini = tile.IsGroupTile && tile.Icon is null;

        _art.Visibility = !mini && tile.HasIcon ? Visibility.Visible : Visibility.Collapsed;

        _presetPath.Data = tile.GlyphData;
        _preset.Visibility = !mini && tile.HasGlyphData ? Visibility.Visible : Visibility.Collapsed;

        // 扩展名文字这一档现在**基本是死路**：HasGlyphText 要求 GlyphData 为 null，
        // 而 Glyphs.ForPath 从不返回 null（只有组合格满足，而组合显示的是迷你格）。
        // 保留它作兜底，别拿它当"没图标时该长什么样"的基准。
        _glyph.Text = tile.DisplayGlyph;
        _glyph.Visibility = !mini && tile.HasGlyphText ? Visibility.Visible : Visibility.Collapsed;

        _mini.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;

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
    ///
    /// 一律<b>瞬移</b>，不做插值：跟手优先。
    /// （2026-09-30 试过"吸附时朝目标中心插值、解除后再飘回光标"：解除那一下幽灵要慢慢追光标，
    ///   手感立刻从"干脆"变"油腻"。那一版插值是为了掩盖判定翻转造成的瞬移，
    ///   而翻转已经由判定侧的迟滞 + 锁定根治了 —— 不该让幽灵的跟手来背这个锅。）
    /// </summary>
    public void Follow()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetCursorPos(out var cursor)) return;

        // **解除即跟手**：正在"吸过去"的路上被叫回光标，就当场打断插值、直接贴上去。
        // 这一条是整个方案的关键 —— 上一版插值就是因为"解除后还飘着追光标"被否掉的。
        if (_gliding) { _gliding = false; _glide.Stop(); }

        CenterOn(cursor.X, cursor.Y);
    }

    /// <summary>
    /// 吸附态：<see cref="EnterGlideMs"/> 毫秒内缓出到给定坐标（物理像素，窗口中心）。
    ///
    /// 与 <see cref="Follow"/> 是一对：进入有过渡、解除瞬移。
    /// 插值途中再调用只更新目标（起点与计时不变），所以目标跟着光标走（橡皮筋）时不会重来一遍。
    /// </summary>
    public void GlideTo(int physX, int physY)
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var rect)) return;

        if (!_gliding)
        {
            _gx0 = rect.Left + rect.Width / 2.0;
            _gy0 = rect.Top + rect.Height / 2.0;
            _glideStart = Environment.TickCount;
            _gliding = true;
            _glide.Start();
        }

        _gx1 = physX;
        _gy1 = physY;
    }

    private void OnGlideTick(object? sender, EventArgs e)
    {
        double t = (Environment.TickCount - _glideStart) / EnterGlideMs;
        if (t >= 1)
        {
            t = 1;
            _gliding = false;
            _glide.Stop();     // 到位就停，不留一个空转的计时器
        }

        double k = 1 - Math.Pow(1 - t, 3);   // ease-out cubic：起步快、收尾稳
        CenterOn((int)Math.Round(_gx0 + (_gx1 - _gx0) * k),
                 (int)Math.Round(_gy0 + (_gy1 - _gy0) * k));
    }

    /// <summary>让幽灵的中心对准给定的屏幕物理坐标。</summary>
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
                // 合并态用"更深的主题色 + 更实"表达，**不再垫第二圈环**（那会变成框里叠框）
                _shell.SetResourceReference(Border.BorderBrushProperty, "AccentStrong");
                Opacity = 0.92;
                break;

            case GhostMode.Cancel:
                _shell.SetResourceReference(Border.BorderBrushProperty, "Ink3");
                Opacity = 0.45;
                break;

            default:
                _shell.SetResourceReference(Border.BorderBrushProperty, "Accent");
                Opacity = 0.78;
                break;
        }
    }

    public void HideGhost()
    {
        _glide.Stop();
        _gliding = false;
        if (IsVisible) Hide();
    }
}
