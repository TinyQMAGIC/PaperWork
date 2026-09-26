using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Paperwork.Data;
using Paperwork.Hotkeys;
using Paperwork.Themes;

namespace Paperwork.Shell;

/// <summary>
/// 设置页。按 design/ui-mock.html 的布局做：主题色 3 × 纸张 3 = 9 套、
/// 图标三档、呼出锚点、两个行为开关、改键。
///
/// 选项行是 XAML 里写死的 <c>Border Tag="accent:forest"</c>，这里统一在面板上挂一个
/// 鼠标事件按 Tag 分派——比给每一行写一遍 Click 处理器省事，也不会漏。
/// </summary>
public partial class PanelWindow
{
    private readonly List<(Border Row, string Tag)> _optionRows = new();
    private bool _capturingHotkey;

    /// <summary>改键要碰的那个已注册热键；由 App 在启动后塞进来。</summary>
    internal GlobalHotkey? RegisteredHotkey { get; set; }

    private void InitSettings()
    {
        SettingsButton.Click += (_, _) => OpenSettings();
        SettingsClose.Click += (_, _) => CloseSettings();
        PreviewKeyDown += OnSettingsKeyDown;

        CollectOptionRows(SettingsPanel);
        SettingsPanel.PreviewMouseLeftButtonUp += OnOptionRowClick;
    }

    /// <summary>
    /// Esc 优先关设置页。「‹ 返回」按钮按设计稿去掉了，不补这一条的话退出设置页
    /// 就只剩右上角那个 ✕，而 Esc 会往下掉进「返回上一层 / 收起面板」。
    /// 改键进行中要让开——那段的 Esc 语义是取消录入。
    /// </summary>
    private void OnSettingsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _capturingHotkey) return;
        if (SettingsPanel.Visibility != Visibility.Visible) return;

        CloseSettings();
        e.Handled = true;
    }

    /// <summary>
    /// 走逻辑树而不是视觉树：InitSettings 在窗口显示之前跑，那时视觉树还是空的，
    /// 用 VisualTreeHelper 会一个都找不到，设置页就变成点了没反应。
    /// </summary>
    private void CollectOptionRows(DependencyObject root)
    {
        foreach (var node in LogicalTreeHelper.GetChildren(root))
        {
            if (node is not DependencyObject child) continue;

            if (child is Border { Tag: string tag } border && tag.Contains(':'))
                _optionRows.Add((border, tag));

            CollectOptionRows(child);
        }
    }

    private void OpenSettings()
    {
        // 点击齿轮后键盘焦点还留在按钮上。设置页虽然盖住了它的视觉，
        // 但焦点框画在窗口级 AdornerLayer——那层在所有内容之上，按一下 Alt
        // 就会穿透盖层现形。清焦点 + 禁用标题栏两个按钮：
        // 禁用的元素不弹 ToolTip（"设置"那个气泡就是这么漏出来的），也不会响应误触。
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
        SettingsButton.IsEnabled = false;
        HideButton.IsEnabled = false;

        RefreshSettingVisuals();
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void CloseSettings()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        SettingsButton.IsEnabled = true;
        HideButton.IsEnabled = true;
    }

    private void OnOptionRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject node) return;

        for (DependencyObject? cur = node; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is Border { Tag: string tag } && tag.Contains(':'))
            {
                ApplyOption(tag);
                e.Handled = true;
                return;
            }
        }
    }

    private void ApplyOption(string tag)
    {
        int split = tag.IndexOf(':');
        string kind = split < 0 ? tag : tag[..split];
        string value = split < 0 ? string.Empty : tag[(split + 1)..];
        var settings = _store.State.Settings;

        switch (kind)
        {
            case "accent":
                settings.Accent = value;
                break;

            case "paper":
                settings.Paper = value;
                break;

            case "icon":
                if (int.TryParse(value, out int px))
                {
                    settings.IconSize = px;
                    IconSize = px;
                }
                break;

            // 锚点是**一行开关**，所以是切换语义（点一下翻一次），不是原来两行的"点谁选谁"
            case "anchor":
                settings.Anchor = settings.Anchor == SummonAnchor.FollowCursor
                    ? SummonAnchor.RememberedPosition
                    : SummonAnchor.FollowCursor;
                break;

            case "flag":
                ToggleFlag(value);
                break;

            case "hotkey":
                BeginHotkeyCapture();
                return;

            default:
                return;
        }

        ApplyTheme();
        RefreshSettingVisuals();
        ScheduleIcons();
    }

    private void ToggleFlag(string value)
    {
        var settings = _store.State.Settings;
        switch (value)
        {
            case "autorun": settings.Autorun = !settings.Autorun; break;
            case "trayonclose": settings.CloseToTray = !settings.CloseToTray; break;

            // 角标是磁贴模板里的一个元素，开关只改数据看不到效果，得把这一层重铺一遍。
            // 只有这一项需要 Render——主题/图标/锚点都走 DynamicResource 或只改窗口属性。
            case "groupbadge":
                settings.ShowGroupBadge = !settings.ShowGroupBadge;
                Render(animate: false);
                break;
        }
    }

    /// <summary>换主题色/纸张。只改应用级资源字典，磁贴用 DynamicResource 所以不用重建。</summary>
    private void ApplyTheme()
    {
        var s = _store.State.Settings;
        Palette.Apply(s.Accent, s.Paper);
        Lifecycle.Autorun.Apply(s.Autorun);
        _store.Save();
    }

    /// <summary>
    /// 改**可继承**的文字色。chip / 分段按钮里的标题不设本地 <c>Foreground</c>，
    /// 全靠这一条往子级传；设了本地值就再也改不动选中态了（本地值优先级高于继承）。
    /// </summary>
    private static void SetInk(DependencyObject o, object brush) =>
        o.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, brush);

    private void RefreshSettingVisuals()
    {
        var s = _store.State.Settings;
        var chosen = new HashSet<string>(StringComparer.Ordinal)
        {
            $"accent:{s.Accent}", $"paper:{s.Paper}", $"icon:{s.IconSize}"
        };

        foreach (var (row, tag) in _optionRows)
        {
            bool on = chosen.Contains(tag);

            // 分段控件的按钮：选中 = 主题色实底 + 反白字，未选中 = 透明底
            if (tag.StartsWith("icon:", StringComparison.Ordinal))
            {
                row.Background = on ? (Brush)FindResource("Accent") : Brushes.Transparent;
                SetInk(row, FindResource(on ? "AccentOn" : "Ink2"));
                continue;
            }

            row.Background = on ? (Brush)FindResource("AccentSoft") : Brushes.Transparent;
            row.BorderBrush = on ? (Brush)FindResource("AccentLine") : Brushes.Transparent;
            SetInk(row, FindResource(on ? "Accent" : "Ink"));

            // 勾选标记只在主题色/纸张两组里有。是圆点（Border Tag="tick"）而不是字形，
            // 所以按 Tag 找——Tag 里没冒号，CollectOptionRows 不会把它当成选项行。
            if (tag.StartsWith("accent:", StringComparison.Ordinal)
                || tag.StartsWith("paper:", StringComparison.Ordinal))
            {
                if (VisualTreeHelper.GetChild(row, 0) is Panel host)
                {
                    foreach (var child in host.Children)
                        if (child is Border { Tag: "tick" } tick)
                            tick.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        PaintToggle(togAutorun, knobAutorun, s.Autorun);
        PaintToggle(togTrayOnClose, knobTrayOnClose, s.CloseToTray);
        PaintToggle(togGroupBadge, knobGroupBadge, s.ShowGroupBadge);
        PaintToggle(togAnchor, knobAnchor, s.Anchor == SummonAnchor.FollowCursor);
        HotkeyText.Text = RegisteredHotkey?.Current?.Display ?? "未设置";
    }

    private void PaintToggle(Border pill, FrameworkElement knob, bool on)
    {
        pill.Background = on ? (Brush)FindResource("Accent") : (Brush)FindResource("Rule");
        knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        knob.Margin = on ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
    }

    // ---------------------------------------------------------------- 改键

    private void BeginHotkeyCapture()
    {
        _capturingHotkey = true;
        HotkeyText.Text = "按下新组合键…";
        Status("请按下新的组合键（至少一个修饰键），Esc 取消");
    }

    /// <summary>返回 true 表示这次按键已被改键流程吃掉。</summary>
    private bool TryCaptureHotkey(KeyEventArgs e)
    {
        if (!_capturingHotkey) return false;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _capturingHotkey = false;
            RefreshSettingVisuals();
            Status("已取消");
            return true;
        }

        // Alt 组合在 WPF 里被归为"系统键"：这时的 e.Key 是 Key.System（一个类别标记，
        // 不是具体键），真键码在 e.SystemKey。直接拿 e.Key 去转虚拟键码会得到垃圾值，
        // 而且 Display 会写成 "Alt+System" 存进 state.json——能解析但注册不上去，
        // 表现为"改完了没反应，重启后热键彻底失效，只能手改 state.json"。
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // Win 组合由系统保留，注册基本必失败，不收
        if (mods.HasFlag(ModifierKeys.Windows) || key is Key.LWin or Key.RWin)
        {
            Status("Win 组合由系统保留，请换一个");
            return true;
        }

        // 输入法/死键/类别标记，转出来不是有效虚拟键码
        if (!HotkeyChord.IsUsableKey(key))
        {
            Status("这个键不能用作热键，请换一个");
            return true;
        }

        if (mods == ModifierKeys.None || key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
        {
            Status("需要至少一个修饰键 + 一个普通键");
            return true;
        }

        var chord = HotkeyChord.FromInput(key, mods);

        // 先注册新的，成功才提交；失败由 GlobalHotkey.TryRebind 自己回滚旧的
        var result = RegisteredHotkey?.TryRebind(chord) ?? HotkeyResult.Error(-1);
        _capturingHotkey = false;

        if (result.Success)
        {
            _store.State.Settings.Hotkey = chord.Display;
            _store.Save();
            Status($"已改为 {chord}");
        }
        else
        {
            Status(result.Message);
        }

        RefreshSettingVisuals();
        return true;
    }
}
