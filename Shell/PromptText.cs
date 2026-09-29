using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Paperwork.Shell;

/// <summary>
/// 重命名用的小输入框（条目 / 组合），纯代码搭、不开第二份 XAML。
///
/// 2026-09-28 改成纸张化外壳（原意向 A）：原来是一个普通 WPF 窗口 ——
/// 系统标题栏 + 系统灰按钮，背景还写死了象牙纸色，换主题完全不跟随。
/// 现在照抄主面板那套：WindowStyle=None + AllowsTransparency，
/// 内容自己画一个 Paper 底 / Edge 边 / PanelRadius 圆角的卡片。
/// </summary>
internal static class PromptText
{
    /// <param name="hint">标题下的小字（Ink3）。给条目时传**完整路径**：只显示名的话，
    /// 用户改完经常发现自己改的不是以为的那个文件。</param>
    public static string? Show(Window owner, string title, string initial, string? hint = null)
    {
        // 输入框：圆角、底色、焦点色都画在外壳 Border 上，而不是重做 TextBox 的模板 ——
        // 只为圆角去抄一份 TextBox 模板，代价是把它内部那一堆 PART 名字也背过来，
        // 而这里的输入框不需要任何 TextBox 特有的东西（无滚动、无校验装饰）。
        var input = new TextBox
        {
            Text = initial,
            FontSize = 13,
            MinWidth = 286,
            MaxWidth = 286,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };
        // CaretBrush / Foreground 都读动态资源：直接赋 Brush 的话，实例会钉死在旧主题色上
        input.SetResourceReference(Control.ForegroundProperty, "Ink");
        // CaretBrushProperty 声明在 TextBoxBase 上，这里用派生类型名引用同一个 DP
        input.SetResourceReference(TextBox.CaretBrushProperty, "Accent");
        // 默认选中色是系统那支 #3399FF，在纸上很跳。改成按 D15 从主题色派生；
        // 文字色保持 Ink，别让选中把字也换了个颜色。
        input.SetResourceReference(TextBox.SelectionBrushProperty, "AccentSoft");
        input.SetResourceReference(TextBox.SelectionTextBrushProperty, "Ink");
        // TextBoxBase 默认 SelectionOpacity=0.4，会把上面那把刷子再压淡六成，
        // 结果和菜单行、选项行那些 AccentSoft 悬停态不是一个浓度。设成 1 才是同一个 AccentSoft。
        input.SelectionOpacity = 1;

        var inputShell = new Border
        {
            Child = input,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 12, 0, 0),
            SnapsToDevicePixels = true
        };
        inputShell.SetResourceReference(Border.BackgroundProperty, "Paper2");
        inputShell.SetResourceReference(Border.BorderBrushProperty, "Rule");

        // 焦点态抬到主题色：光标在别处时，用户看不出"现在打字会进到哪"。
        // 用 SetResourceReference 而不是直接赋 Brush，否则换主题后这几个值会钉死在旧色上。
        input.GotKeyboardFocus += (_, _) =>
        {
            inputShell.SetResourceReference(Border.BackgroundProperty, "Paper");
            inputShell.SetResourceReference(Border.BorderBrushProperty, "AccentLine");
        };
        input.LostKeyboardFocus += (_, _) =>
        {
            inputShell.SetResourceReference(Border.BackgroundProperty, "Paper2");
            inputShell.SetResourceReference(Border.BorderBrushProperty, "Rule");
        };

        var ok = new Button
        {
            Content = "确定",
            IsDefault = true,
            MinWidth = 78,
            Style = StyleOf("DialogButtonPrimary")
        };
        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 78,
            Style = StyleOf("DialogButtonGhost")
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(18) };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = Font("BookFont") ?? owner.FontFamily
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        body.Children.Add(titleText);

        if (!string.IsNullOrWhiteSpace(hint))
        {
            var hintText = new TextBlock
            {
                Text = hint,
                FontSize = 10.5,
                MaxWidth = 286,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                // 路径被截断了还能悬停看全，省得改成"把窗口拉宽"
                ToolTip = hint
            };
            hintText.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
            body.Children.Add(hintText);
        }

        body.Children.Add(inputShell);
        body.Children.Add(buttons);

        var shell = new Border
        {
            Child = body,
            CornerRadius = Radius("PanelRadius"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };
        shell.SetResourceReference(Border.BackgroundProperty, "Paper");
        shell.SetResourceReference(Border.BorderBrushProperty, "Edge");

        var dlg = new Window
        {
            Title = title,
            Content = shell,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            MinWidth = 340,
            FontFamily = owner.FontFamily,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        // 没有系统标题栏就拖不动了，所以整张卡片都可以拖动。
        // 排除输入框和按钮：在输入框里按住拖是选词，在按钮上按下是要点它。
        shell.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.Source is TextBox || e.Source is Button) return;
            dlg.DragMove();
        };

        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

        return dlg.ShowDialog() == true ? NullIfBlank(input.Text) : null;
    }

    private static Style? StyleOf(string key) => Application.Current.TryFindResource(key) as Style;

    private static FontFamily? Font(string key) => Application.Current.TryFindResource(key) as FontFamily;

    private static CornerRadius Radius(string key) =>
        Application.Current.TryFindResource(key) is CornerRadius r ? r : new CornerRadius(22);

    private static string? NullIfBlank(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
