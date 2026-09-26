using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Paperwork.Shell;

/// <summary>
/// 一次性的小输入框，纯代码搭、不开第二份 XAML。
/// M4 做设置页时会换成磁贴上的内联编辑（F2），这个类到时候删掉。
/// </summary>
internal static class PromptText
{
    public static string? Show(Window owner, string title, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            FontSize = 13,
            Padding = new Thickness(5, 4, 5, 4)
        };

        var ok = new Button
        {
            Content = "确定",
            IsDefault = true,
            MinWidth = 78,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 78,
            Padding = new Thickness(10, 4, 10, 4)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title,
            Content = panel,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            MinWidth = 340,
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xEF, 0xE3)),
            FontFamily = owner.FontFamily
        };

        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return dlg.ShowDialog() == true ? NullIfBlank(box.Text) : null;
    }

    private static string? NullIfBlank(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
