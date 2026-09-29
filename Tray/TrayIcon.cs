using System;
using System.Diagnostics;
using WpfApplication = System.Windows.Application;
using WF = System.Windows.Forms;

namespace Paperwork.Tray;

/// <summary>
/// 托盘图标（方案 §7）。用 WinForms 的 NotifyIcon 而不是手写 Shell_NotifyIcon，
/// 代价约 2MB，换掉 150 行 32/64 位结构体版本兼容代码。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>WinForms 对左键双击会先回调两次 MouseClick；不去抖就会 Toggle 两次，观感是"点了没反应"。</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly WF.NotifyIcon _icon;
    private Stopwatch? _lastClick;

    public event EventHandler? ToggleRequested;

    // 原来这里还有个 ExitRequested。退出改成从纸张菜单里走（菜单的「退出」→ 窗口的
    // ExitRequested → App.ExitApp），托盘自己这条就没人订阅了 —— 删掉，别留死事件。

    /// <summary>
    /// 右键：交给外层弹<b>纸张菜单</b>。
    /// 这里不再挂 WinForms 的 <c>ContextMenuStrip</c> —— 那是系统原生的灰白菜单，
    /// 和纸张界面不是一个世界（字体、圆角、悬停色全都不受控）。
    /// </summary>
    public event EventHandler? MenuRequested;

    public TrayIcon()
    {
        _icon = new WF.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Paperwork",
            Visible = true
        };

        // 不设 ContextMenuStrip：右键收到的那一下由 OnMouseUp 转成 MenuRequested，
        // 外层用和磁贴菜单同一套样式弹（见事件上的注释）
        _icon.MouseClick += OnMouseClick;
        _icon.MouseUp += OnMouseUp;
    }

    private void OnMouseUp(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Right) return;
        MenuRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 托盘图标按当前 DPI 取对应的那一帧：100% 用 16、125% 用 20、150% 用 24。
    /// 不这么干的话 Shell 会拿 32 帧自己缩，边缘发糊；而这三小帧在生成器里是
    /// 逐像素对齐画的（tools/make-icon.py 的 snap_for），1px 描边正好落在像素上。
    /// </summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = typeof(TrayIcon).Assembly
            .GetManifestResourceStream("Paperwork.Assets.Paperwork.ico")!;
        var side = Math.Clamp(WF.SystemInformation.SmallIconSize.Width, 16, 24);
        var icon = new System.Drawing.Icon(stream, side, side);
        _ = icon.Handle;      // Icon 是延迟读流的，必须在 stream 释放前把句柄建出来
        return icon;
    }

    private void OnMouseClick(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left) return;

        if (_lastClick is not null && _lastClick.Elapsed < Debounce) return;
        _lastClick = Stopwatch.StartNew();

        ToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>explorer 重启后调用（TaskbarCreated）。.NET 10 的 NotifyIcon 是否已内部处理由 Spike S3 验证。</summary>
    public void ReRegister()
    {
        _icon.Visible = false;
        _icon.Visible = true;
    }

    public void Balloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, WF.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.MouseClick -= OnMouseClick;
        _icon.MouseUp -= OnMouseUp;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
