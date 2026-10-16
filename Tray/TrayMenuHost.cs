using System;
using System.Windows;
using System.Windows.Interop;
using Paperwork.Shell;

namespace Paperwork.Tray;

/// <summary>
/// 托盘右键菜单的宿主窗口：一个 1×1、完全透明、停在屏幕外、不进任务栏、
/// **也不进 Alt+Tab 与任务视图**的窗口（<c>WS_EX_TOOLWINDOW</c>，见 <see cref="OnSourceInitialized"/>）。
///
/// <b>为什么需要它</b>：右键托盘时面板往往是<b>收起</b>的，而 WPF 的 <c>ContextMenu</c>
/// 必须挂在一个<b>已显示且能激活</b>的宿主上。没有宿主时，弹出层既拿不到键盘焦点、
/// 也拿不到鼠标捕获 —— 实测后果是：菜单<b>关不掉</b>（Esc 没反应、点别处也没反应，
/// 卡在屏幕上），↑↓ / Enter 也选不动。
///
/// 面板开着时（<c>PlacementTarget = 面板</c>）Esc 是能关的，所以这个坑只在
/// "面板收起 → 右键托盘" 这条路上出现 —— 而那恰恰是最常见的一条路。
///
/// 这是托盘菜单的经典解法：给菜单一个隐形宿主，弹之前先把它提到前台。
/// 焦点在菜单关掉之后要还回原来的窗口（见 <c>App.ShowTrayMenu</c>），
/// 否则用户正在用的程序会被我们这个透明窗口抢走焦点。
/// </summary>
internal sealed class TrayMenuHost : Window
{
    public TrayMenuHost()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Opacity = 0;                 // 完全透明：既不显示，也不挡任何命中
        Width = 1;
        Height = 1;
        Left = -32000;               // 与 PanelWindow.WarmUp 同一个屏幕外落点
        Top = -32000;

        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// 与 <see cref="PanelWindow"/> / <see cref="DragGhost"/> 同一支笔：补上 <c>WS_EX_TOOLWINDOW</c>。
    ///
    /// <b>为什么非要这一行</b>（2026-10-15 用户截图："程序开在后台，Alt+Tab 里有个小图标、没有预览"）：
    /// Alt+Tab 与任务视图的收录规则是「**可见的、非 TOOL 的顶层窗口**」。这个宿主
    /// <see cref="ActivateForMenu"/> 里 <c>Show()</c> 过之后<b>一直保持可见</b>（不能 Hide：
    /// WPF 一 Hide 就销毁 HWND，下次开菜单要重建，"常驻宿主"的意义就没了），
    /// 而 <c>ShowInTaskbar=false</c> 只管任务栏、管不到那两个列表 —— 于是每开过一次托盘菜单，
    /// Alt+Tab 里就多一个只有图标、没有预览的空条目（窗口 1×1，缩略图无从生成）。
    /// 主面板当年就吃过这一刀（<c>OnSourceInitialized</c> 第 307 行），拖拽幽灵也带了三件套，
    /// 只有这个后来加的宿主漏了。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.ApplyToolWindowStyle(hwnd);
    }

    /// <summary>建出 HWND、钉回屏幕外，并提到前台给菜单当宿主。</summary>
    public IntPtr ActivateForMenu()
    {
        if (!IsLoaded) Show();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        // Show 之后窗口可能已被系统挪动，钉回去
        Native.SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 1, 1,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        // 激活这一步是重点：菜单的键盘与鼠标捕获都靠宿主是前台窗口
        Native.SetForegroundWindow(hwnd);
        Activate();
        return hwnd;
    }
}
