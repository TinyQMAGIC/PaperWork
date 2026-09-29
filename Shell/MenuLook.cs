using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Paperwork.Shell;

/// <summary>
/// 右键菜单条目的附加属性：圆角、图标、右侧提示，都由代码算好写进来。
/// </summary>
internal static class MenuLook
{
    // ================= 值的传递方式：模板加载完成后**直接赋值**（2026-09-29）=================
    //
    // 原来走的是"附加属性 + 模板里的相对源绑定"：模板写
    // `{Binding (shell:MenuLook.IconData), RelativeSource={RelativeSource TemplatedParent}}`。
    // 这条路实测**整条不通** —— 运行时的绑定状态是 `PathError`、`ResolvedSource=null`，
    // 既不画图标也不报错：图标列空着但位置占着、右侧热键提示不出字，
    // 看着就像"菜单漏了一半"（用户报的两个现象：没有图标、高亮框不对）。
    // 换成 `RelativeSource=AncestorType=MenuItem` 同样 PathError；补上 Getter 也没救回来。
    // 模板里的 DataTrigger 更早就被证伪过（见下面 SetCorner 的注释），于是走这条路：
    // **条目加载完成后按名字取模板部件，直接赋本地值**。笨，但没有中间环节可以坏。
    //
    // 附加属性仍然保留：它是"代码想传什么"的载体。而 Code 赋值是本地值，
    // 会顺手把同名绑定顶掉 —— 这也正是我们要的（绑不上的绑定本身也是负资产）。
    private static void HookLoad(MenuItem item)
    {
        item.Loaded -= OnItemLoaded;   // 三个 Setter 都可能调进来，别叠成多次
        item.Loaded += OnItemLoaded;
    }

    private static void OnItemLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;

        if (item.Template?.FindName("ico", item) is Path ico)
        {
            ico.Data = GetIconData(item);
            ico.Visibility = GetIconVisibility(item);
        }

        if (item.Template?.FindName("hint", item) is TextBlock hint)
        {
            hint.Text = GetHint(item);
            hint.Visibility = GetHintVisibility(item);
        }

        if (item.Template?.FindName("bg", item) is Border bg)
            bg.CornerRadius = GetCorner(item);
    }

    // ---------------- 圆角（D27：首末项同心弧 = PanelRadius − 菜单上下内边距 = 15）----------------

    public static readonly DependencyProperty CornerProperty =
        DependencyProperty.RegisterAttached(
            "Corner", typeof(CornerRadius), typeof(MenuLook),
            new PropertyMetadata(new CornerRadius(8)));

    public static void SetCorner(DependencyObject element, CornerRadius value)
    {
        element.SetValue(CornerProperty, value);
        if (element is MenuItem item) HookLoad(item);
    }

    public static CornerRadius GetCorner(DependencyObject element) =>
        (CornerRadius)element.GetValue(CornerProperty);

    // ---------------- 左侧图标 ----------------

    public static readonly DependencyProperty IconDataProperty =
        DependencyProperty.RegisterAttached("IconData", typeof(Geometry), typeof(MenuLook));

    public static readonly DependencyProperty IconVisibilityProperty =
        DependencyProperty.RegisterAttached(
            "IconVisibility", typeof(Visibility), typeof(MenuLook),
            new PropertyMetadata(Visibility.Collapsed));

    /// <summary>一次设齐数据与可见性：没有图形就不该占那一列，否则每个条目左边都空出一块。</summary>
    public static void SetIcon(DependencyObject element, Geometry? data)
    {
        element.SetValue(IconDataProperty, data);
        element.SetValue(IconVisibilityProperty, data is null ? Visibility.Collapsed : Visibility.Visible);
        if (element is MenuItem item) HookLoad(item);
    }

    // ============ 下面这四个 Getter 是<b>必须</b>的，别以为它们没人调用就删掉 ============
    //
    // 模板里的绑定写的是 `(shell:MenuLook.IconData)` 这种**带前缀的附加属性路径**。
    // 这种路径在运行时是<b>靠反射找 CLR 存取器</b>（GetIconData / SetIconData…）来解析的，
    // 不是靠 DependencyProperty 字段名。只注册 DP、不写 Getter 的后果实测是：
    // 绑定状态变成 `PathError`（ResolvedSource=null），**既不报错也不画东西** ——
    // 图标整列空白、右侧热键提示不出字、按钮也照样能点，只是全瞎。
    //
    // 对照证据：`Corner` 一直有 GetCorner，所以首末项的同心圆角从没出过问题；
    // 上面前四个（IconData / IconVisibility / Hint / HintVisibility）漏了 Getter，
    // 托盘菜单的图标与 Alt+V 提示就一直没画出来（2026-09-29 定位）。
    public static Geometry? GetIconData(DependencyObject element) =>
        (Geometry?)element.GetValue(IconDataProperty);

    public static Visibility GetIconVisibility(DependencyObject element) =>
        (Visibility)element.GetValue(IconVisibilityProperty);

    // ---------------- 右侧提示（比如托盘菜单里的当前热键）----------------

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.RegisterAttached("Hint", typeof(string), typeof(MenuLook));

    public static readonly DependencyProperty HintVisibilityProperty =
        DependencyProperty.RegisterAttached(
            "HintVisibility", typeof(Visibility), typeof(MenuLook),
            new PropertyMetadata(Visibility.Collapsed));

    public static void SetHint(DependencyObject element, string? text)
    {
        element.SetValue(HintProperty, text ?? string.Empty);
        element.SetValue(HintVisibilityProperty,
            string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible);
        if (element is MenuItem item) HookLoad(item);
    }

    /// <summary>Getter 是必须的，理由见上面 IconData 那段注释。</summary>
    public static string GetHint(DependencyObject element) =>
        (string)element.GetValue(HintProperty);

    public static Visibility GetHintVisibility(DependencyObject element) =>
        (Visibility)element.GetValue(HintVisibilityProperty);
}

/// <summary>
/// 菜单用的线性图形。刻意不复用 <see cref="Paperwork.Data.Glyphs"/> 那套文件类型图形：
/// 这里是"动作"，不是"文件类型"，画的是面板 / 调节滑杆 / 电源。
/// 笔宽与 <c>Glyphs</c> 一致（1.6），同一支笔。
/// </summary>
internal static class MenuIcons
{
    /// <summary>面板：一张矩形纸 + 顶部标题条。</summary>
    public static readonly Geometry Panel = Geometry.Parse(
        "M 5.6,4 H 18.4 A 2.4,2.4 0 0 1 20.8,6.4 V 17.6 A 2.4,2.4 0 0 1 18.4,20 H 5.6 A 2.4,2.4 0 0 1 3.2,17.6 V 6.4 A 2.4,2.4 0 0 1 5.6,4 Z  M 3.2 8.4 H 20.8");

    /// <summary>设置：三根滑杆 + 错位旋钮（2026-09-29 换掉原来那颗齿轮）。</summary>
    public static readonly Geometry Sliders = Geometry.Parse(
        "M 4,6.8 H 7.7  M 11.5,6.8 H 20  M 9.6,4.9 A 1.9,1.9 0 1 1 9.59,4.9 Z" +
        "M 4,12 H 13.7  M 17.5,12 H 20  M 15.6,10.1 A 1.9,1.9 0 1 1 15.59,10.1 Z" +
        "M 4,17.2 H 5.7  M 9.5,17.2 H 20  M 7.6,15.3 A 1.9,1.9 0 1 1 7.59,15.3 Z");

    /// <summary>电源：一竖 + 一个开口向上的弧（退出）。</summary>
    public static readonly Geometry Power = Geometry.Parse(
        "M 12,3.4 V 12  M 7.2,7.4 A 7.6,7.6 0 1 0 16.8,7.4");
}
