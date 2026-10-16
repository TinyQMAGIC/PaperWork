using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    /// <summary>
    /// 临时提示占住设置页状态行的时长，到点自动回到常驻摘要（程序名 · 版本号）。
    ///
    /// 主面板那条是 2.5 秒，这里给到 8 秒：设置页的提示多半是「已改为 Ctrl+Alt+D」
    /// 「取消开机自启失败…」这类需要读完并对照界面确认的信息，2.5 秒一闪就过去了。
    /// 回到摘要只是把行让出来，不依赖用户操作，所以给长一点没有副作用。
    /// </summary>
    private const int SettingsStatusHoldSeconds = 8;

    private DispatcherTimer? _settingsStatusTimer;

    /// <summary>
    /// 版本号，进程内取一次就够。唯一来源是 <c>Paperwork.csproj</c> 的 <c>&lt;Version&gt;</c>——
    /// 硬编码一份字符串，发版时必然有一处忘记改。
    /// </summary>
    private static readonly string VersionText = ReadVersionText();

    private static string ReadVersionText()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(PanelWindow).Assembly;

        // 用 InformationalVersion 而不是 GetName().Version：后者会补出第四位（0.0.32.0），
        // 直接显示出来就是多一个多余的 .0。
        string ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? asm.GetName().Version?.ToString(3)
                     ?? "?";

        // SDK 会把 InformationalVersion 写成 "0.0.32+<commit>"，只要加号之前那一段
        int plus = ver.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? ver[..plus] : ver;
    }

    /// <summary>改键要碰的那个已注册热键；由 App 在启动后塞进来。</summary>
    internal GlobalHotkey? RegisteredHotkey { get; set; }

    private void InitSettings()
    {
        // 先退搜索：设置页是整块覆盖层，压在搜索结果上面的话，
        // 关掉设置页会掉回一堆"已经不属于当前视野"的搜索结果里。
        SettingsButton.Click += (_, _) => { ExitSearch(); OpenSettings(); };
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
        // 三枚标题栏按钮一起禁：设置页是不透明覆盖层、RowSpan=5 盖住整条标题栏，
        // 禁掉是为了不弹 ToolTip（焦点路径会漏气泡）也不响应误触。
        // MemoButton 原来漏了 —— 它和另外两枚没有任何区别，漏它纯属手写列表少写一行。
        SettingsButton.IsEnabled = false;
        HideButton.IsEnabled = false;
        MemoButton.IsEnabled = false;

        RefreshSettingVisuals();
        SettingsPanel.Visibility = Visibility.Visible;

        // 状态行回到常驻摘要。上一次打开如果留了个还没到点的提示计时器，这里一并停掉，
        // 否则它会在新一次打开后中途把摘要再写一遍（虽然无害，但计时语义就乱了）。
        _settingsStatusTimer?.Stop();
        ShowSettingsSummary();

        // 清完焦点必须再交出去，否则设置页一个可聚焦的东西都没有。
        //
        // 上面那两行（ClearFocus + SetFocusedElement(null)）是 M4-P4 用来压掉标题栏按钮
        // 焦点虚框的，本身没错；但叠加了"ScrollViewer 也设了 Focusable=False"之后，
        // 焦点就彻底无处可落 —— WPF 没有键盘事件的目标，于是：
        //   · 改键永远停在「按下新组合键…」，按什么都没反应；
        //   · Esc 关设置页同样失效（OnSettingsKeyDown 根本不会触发）。
        // 面板仍然是前台窗口（实测确认），所以这不是"没激活"，是"窗口里没有焦点元素"。
        //
        // 焦点框不会因为这一个落点复现：App 级已把 FocusVisualStyleKey 覆盖成空模板。
        Keyboard.Focus(SettingsRoot);
    }

    private void CloseSettings()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        SettingsButton.IsEnabled = true;
        HideButton.IsEnabled = true;
        MemoButton.IsEnabled = true;
        EndHotkeyCaptureIfAny();
        // 残留的提示（比如改键失败那句）下次打开还在 —— 状态行只在有话说的那几秒有效。
        // 计时器也要停：面板已经藏起来了，留着它到点去写一行看不见的字纯属浪费，
        // 而且下一次打开时可能被它抢在 ShowSettingsSummary 之前写一次。
        _settingsStatusTimer?.Stop();
        SettingsStatus.Text = string.Empty;
    }

    /// <summary>
    /// 设置页状态行的常驻摘要：<c>Paperwork · 0.0.32</c>。
    ///
    /// 为什么是"摘要"而不是额外加一行：状态行右 86px 要留给右下角那两道页叠斜线，
    /// 可用宽度只有 422px（MinWidth=320 时只剩 218px），再并排放一段提示会把最长的那句
    /// （改键捕获提示）切得更狠。让常驻内容占这一行、临时提示整行替换，
    /// 既守住了宽度，也让"常驻"和"临时"一眼可分 —— 与主面板「N 项 · M 个组合」
    /// 那套「提示 2.5s 后回到摘要」是同一个模式，只是这里给到 8 秒。
    ///
    /// 版本号用等宽体（手册 §2：状态栏数字归等宽体），其余沿用状态行自己的 10.5 / Ink3。
    /// 分隔符是 <c>·</c>，与 <c>Board.RootSubtitle</c> 的「N 项 · M 个组合」同一支笔。
    /// </summary>
    private void ShowSettingsSummary()
    {
        SettingsStatus.Inlines.Clear();
        SettingsStatus.Inlines.Add(new Run("Paperwork · "));
        SettingsStatus.Inlines.Add(new Run(VersionText)
        {
            FontFamily = (FontFamily)FindResource("MonoFont")
        });
    }

    /// <summary>
    /// 往设置页状态行投一句临时提示。<b>整行替换</b>常驻摘要，8 秒后由计时器拉回。
    /// 连续提示时重新计时 —— 否则上一条的 8 秒会把刚写进去的这条提前顶掉。
    /// </summary>
    private void SaySettings(string text)
    {
        SettingsStatus.Text = text;   // 赋 .Text 会连摘要那两条 Inline 一起清掉
        _settingsStatusTimer ??= CreateSettingsStatusTimer();
        _settingsStatusTimer.Stop();
        _settingsStatusTimer.Start();
    }

    private DispatcherTimer CreateSettingsStatusTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SettingsStatusHoldSeconds) };
        t.Tick += (_, _) => { t.Stop(); ShowSettingsSummary(); };
        return t;
    }

    private void OnOptionRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject node) return;

        // 同 TileFrom：选项行里将来可能出现 Run 这类非 Visual 元素，用 ParentOf 兜住
        for (DependencyObject? cur = node; cur is not null; cur = ParentOf(cur))
        {
            if (cur is Border { Tag: string tag } && tag.Contains(':'))
            {
                // 点了别的选项行 = 用户已经离开"改键"这件事，顺手结束捕获。
                // 不这么做的话捕获会一直挂着：用户以为已经走了，下一次随手按一个键就被当成新键位录进去。
                // 点的还是改键那一行时不动它 —— 那一行的动作是重新进入捕获（下面的 ApplyOption 会做）。
                if (_capturingHotkey && !tag.StartsWith("hotkey", StringComparison.Ordinal))
                    CancelHotkeyCapture();

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
            case "autorun":
                // 写完注册表才知道成不成，所以这一项不能留在 ApplyTheme 里「顺带」写：
                // 失败必须当场把开关退回原位并说清原因。显示「开」而实际永不自启，
                // 是这个功能最坏的一种坏法 —— 用户要到下次开机才发现，那时已经没有任何线索了。
                settings.Autorun = !settings.Autorun;
                // userInitiated: true —— 这是**用户明确拨的开关**，允许它改写指向别的副本的登记
                // （启动时那条自动同步不许改，见 Autorun.Apply 的注释）。
                if (Lifecycle.Autorun.Apply(settings.Autorun, userInitiated: true)) break;

                settings.Autorun = !settings.Autorun;      // 退回原位，别留一个假的「开」
                Status(settings.Autorun
                    ? Str.SetAutorunOffFailed
                    : Str.SetAutorunOnFailed);
                break;

            case "trayonclose": settings.CloseToTray = !settings.CloseToTray; break;
            case "hideonfullscreen": settings.HideOnFullscreen = !settings.HideOnFullscreen; break;

            // 角标是磁贴模板里的一个元素，开关只改数据看不到效果，得把这一层重铺一遍。
            // 只有这一项需要 Render——主题/图标/锚点都走 DynamicResource 或只改窗口属性。
            case "groupbadge":
                settings.ShowGroupBadge = !settings.ShowGroupBadge;
                Render(animate: false);
                break;
        }
    }

    /// <summary>
    /// 换主题色/纸张。只改应用级资源字典，磁贴用 DynamicResource 所以不用重建。
    ///
    /// 这里<b>不再顺带写注册表</b>（原来每点任何一项都会 Apply 一遍 Autorun）：
    /// 换主题色、调图标档位跟开机自启毫无关系，却每次都去动一次 HKCU；
    /// 更要紧的是那样写不进去也没人能知道 —— 返回值被丢在了一条通用路径上。
    /// 现在 Run 键只有两处会写：启动时（App.OnStartup）与点「跟随系统启动」那一行。
    /// </summary>
    private void ApplyTheme()
    {
        var s = _store.State.Settings;
        Palette.Apply(s.Accent, s.Paper);
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
        PaintToggle(togFullscreen, knobFullscreen, s.HideOnFullscreen);
        PaintToggle(togAnchor, knobAnchor, s.Anchor == SummonAnchor.FollowCursor);
        // 这一处是红色的<b>唯一</b>复位点：成功、Esc 取消、下次打开设置页都走它。
        // 漏了它，红字会残留到下一次进设置页。
        // 注意只能设回 Ink2 —— XAML 里这行原本就是 {DynamicResource Ink2}；
        // 用 ClearValue 会掉成继承来的 Ink，比原来深一档，等于悄悄改了设计。
        HotkeyText.Text = RegisteredHotkey?.Current?.Display ?? Str.SetHotkeyUnset;
        HotkeyText.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
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
        HotkeyText.Text = Str.SetHotkeyCapturing;
        // 顺手复位颜色：万一上次的红字残留了下来，占位文字不该是红的
        HotkeyText.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
        // 这句提示现在要解释一种"什么都没发生"的情况：
        // 组合键被别的程序注册成全局热键时，那颗键会被对方截走 —— 我们的捕获连按键都收不到，
        // 于是既不会录入、也不会报冲突（拿不到 1409，红字那条分支到不了，见 README）。
        // 用户看到的就是"按下没反应"，所以这里先把这个现象翻译成一句话说清楚。
        // 信息顺序是刻意的：状态栏宽约 418px、字号 10.5，TextBlock 又没设 TextTrimming，
        // 超长会被直接切掉 —— 所以要紧的原因排在前面，"Esc 取消"放最后（最不怕被切）。
        Status(Str.SetHotkeyCaptureHint);

        // 再钉一次焦点：点选项行这种不可聚焦的元素时，WPF 有可能把焦点挪走。
        // 焦点一丢，下面的按键就一个都收不到（原因见 OpenSettings 里那段注释）。
        Keyboard.Focus(SettingsRoot);
    }

    /// <summary>
    /// 放弃这次录入：键位保持原样，捕获结束，那一行恢复成当前键位。
    /// <b>Esc 与"点空白"共用它</b> —— 两条路的语义必须完全一致，否则用户会记住两套手感。
    /// </summary>
    private void CancelHotkeyCapture()
    {
        _capturingHotkey = false;
        RefreshSettingVisuals();
        Status(Str.StatusDragCancelled);
    }

    /// <summary>是不是正停在改键行上（供面板级的点击/按键守卫询问）。</summary>
    internal bool IsCapturingHotkey => _capturingHotkey;

    /// <summary>
    /// 收尾：关闭设置页 / 收起面板时，捕获状态绝不允许残留。
    ///
    /// 留着会出怪事：面板级的点击守卫看到"正在改键"，就会把用户接下来点面板上任何地方
    /// 都当成"点空白退出改键"吃掉 —— 表现是"点了没反应"，而且怎么点都找不到原因。
    /// 这里不写 Status（面板都要收了，没人看得见）。
    /// </summary>
    internal void EndHotkeyCaptureIfAny()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        RefreshSettingVisuals();
    }

    /// <summary>返回 true 表示这次按键已被改键流程吃掉。</summary>
    private bool TryCaptureHotkey(KeyEventArgs e)
    {
        if (!_capturingHotkey) return false;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            CancelHotkeyCapture();
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
            Status(Str.SetHotkeyWinReserved);
            return true;
        }

        // 输入法/死键/类别标记，转出来不是有效虚拟键码
        if (!HotkeyChord.IsUsableKey(key))
        {
            Status(Str.SetHotkeyBadKey);
            return true;
        }

        if (mods == ModifierKeys.None || key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
        {
            Status(Str.SetHotkeyNeedModifier);
            return true;
        }

        CommitHotkey(HotkeyChord.FromInput(key, mods));
        return true;
    }

    /// <summary>
    /// 提交一个新键位：先注册，成功才写盘。<b>两条路共用它</b> ——
    /// ① WPF 按键那条（<see cref="TryCaptureHotkey"/>，捕获中按到任何"还没注册"的组合）；
    /// ② <c>WM_HOTKEY</c> 那条（捕获中按到<b>当前正注册着</b>的那个组合，见
    /// <see cref="CommitHotkeyFromHotkeyMessage"/>）。合成一处，免得两条路各写一份存盘与提示。
    /// </summary>
    private void CommitHotkey(HotkeyChord chord)
    {
        // 先注册新的，成功才提交；失败由 GlobalHotkey.TryRebind 自己回滚旧的
        var result = RegisteredHotkey?.TryRebind(chord) ?? HotkeyResult.Error(-1);

        if (result.Success)
        {
            _capturingHotkey = false;
            _store.State.Settings.Hotkey = chord.Display;
            _store.Save();
            Status(string.Format(Str.SetHotkeyChanged, chord));
            RefreshSettingVisuals();
            return;
        }

        // 失败：留在捕获态，把原因写进那一行（红字）。
        //
        // 为什么不能像以前那样"回滚 + 写状态栏就完事"：Status() 写的是底部那一行，
        // 而同一个 TextBlock 又被 Render 用来显示"N 项" —— 消息一写进去就被覆盖，
        // 于是用户看到的就是"按了没反应、键位还是原来那个"。改键行是唯一站得住的位置。
        //
        // 为什么留在捕获态：红字只在捕获期间有意义。一退出，下一次 RefreshSettingVisuals
        // 就会把它恢复成旧键位，红字等于闪一下就没了 —— 那就还是没提示。
        // 留着的话，用户可以直接按下一个候选，不用再点一次那一行。
        ShowHotkeyProblem(result.Status == HotkeyStatus.Conflict
            ? Str.SetHotkeyConflict
            : string.Format(Str.SetHotkeyRegFailed, result.Win32Error));

        Status(result.Message);
    }

    /// <summary>
    /// 把改键那一行变成红色的问题提示。字体、位置、chip 边框一概不动 —— 只换字与笔。
    ///
    /// 用 <c>SetResourceReference</c> 而不是取一次画笔：用户在设置页里顺手换个主题时，
    /// 这行红字要跟着变成新主题的危险色（D15）。取一次画笔的话它会留在旧主题的颜色上。
    /// </summary>
    private void ShowHotkeyProblem(string text)
    {
        HotkeyText.Text = text;
        HotkeyText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
    }

    /// <summary>
    /// 捕获中按到了<b>当前已注册的那个组合</b>。
    ///
    /// 这颗键被系统吞成了 <c>WM_HOTKEY</c>，WPF 的按键事件里根本没有它，
    /// 所以 <see cref="TryCaptureHotkey"/> 永远等不到 —— 先在 Win32 层把它认回来。
    /// 此刻它的语义是"用户又确认了一遍旧键"，不是"呼出/收起"：
    /// 拿注册着的组合直接完成录入，<b>面板保持不动</b>。
    ///
    /// 顺带说明为什么只可能是"同一个组合"：能触发 WM_HOTKEY 的只有已注册的那一个，
    /// 别的组合走的是普通按键流，本来就正常进 <see cref="TryCaptureHotkey"/>。
    /// </summary>
    /// <returns>true 表示这次 WM_HOTKEY 已被改键流程吃掉，不该再走呼出/收起。</returns>
    internal bool CommitHotkeyFromHotkeyMessage()
    {
        if (!_capturingHotkey) return false;

        var chord = RegisteredHotkey?.Current;
        if (chord is null) return false;

        CommitHotkey(chord.Value);
        return true;
    }
}
