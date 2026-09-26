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
    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        _icon = new WF.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Paperwork",
            Visible = true
        };

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("呼出 / 收起", null, (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += OnMouseClick;
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
        _icon.Visible = false;
        _icon.Dispose();
    }
}
