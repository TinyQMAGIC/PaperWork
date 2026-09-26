using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Paperwork.Shell;

/// <summary>
/// 拖动滚动 + 惯性（D9）。手感对齐触屏：按住空白处往下拖就跟着走，松手后继续滑一段。
///
/// 与"点空白返回上一层"（D10）共用同一块区域，靠位移阈值区分：
/// 移动超过系统的拖动阈值就算滚动、不再当作点击。这个判定通过
/// <see cref="ConsumedDrag"/> 告诉外面，避免一次拖动同时触发返回。
///
/// 只处理"按在空白处"的情况——按在磁贴上留给磁贴自己的点击与将来的拖拽排序。
/// </summary>
public sealed class PanScroll : IDisposable
{
    /// <summary>惯性衰减系数，每帧乘一次。0.90 大约滑 0.6 秒停下。</summary>
    private const double Decay = 0.90;

    /// <summary>低于这个速度（px/ms）就停止惯性，避免无限逼近。</summary>
    private const double StopEpsilon = 0.02;

    private readonly ScrollViewer _view;
    private readonly Func<object, bool> _isBlankTarget;
    private readonly DispatcherTimer _inertia;

    private Point _start;
    private double _startOffset;
    private bool _dragging;
    private long _lastTicks;
    private double _lastY;
    private double _velocity;

    /// <summary>本次抬手前发生过一次真正的拖动。外部读完应忽略同一次点击。</summary>
    public bool ConsumedDrag { get; private set; }

    public PanScroll(ScrollViewer view, Func<object, bool> isBlankTarget)
    {
        _view = view;
        _isBlankTarget = isBlankTarget;

        _inertia = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _inertia.Tick += OnInertiaTick;

        view.PreviewMouseLeftButtonDown += OnDown;
        view.PreviewMouseMove += OnMove;
        view.PreviewMouseLeftButtonUp += OnUp;
        view.MouseWheel += (_, _) => StopInertia();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        StopInertia();
        ConsumedDrag = false;

        if (!_isBlankTarget(e.OriginalSource)) return;
        if (!_view.ComputedVerticalScrollBarVisibility.Equals(Visibility.Visible)) return;

        _dragging = true;
        _start = e.GetPosition(_view);
        _startOffset = _view.VerticalOffset;
        _lastTicks = Stopwatch.GetTimestamp();
        _lastY = _start.Y;
        _velocity = 0;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var now = e.GetPosition(_view);
        double dy = now.Y - _start.Y;

        if (!_view.IsMouseCaptured && Math.Abs(dy) > SystemParameters.MinimumVerticalDragDistance)
            _view.CaptureMouse();

        if (!_view.IsMouseCaptured) return;

        ConsumedDrag = true;
        _view.ScrollToVerticalOffset(_startOffset - dy);

        long ticks = Stopwatch.GetTimestamp();
        double ms = (ticks - _lastTicks) * 1000.0 / Stopwatch.Frequency;
        if (ms > 12)   // 采样太密会让速度估计抖成毛刺
        {
            _velocity = (now.Y - _lastY) / ms;
            _lastTicks = ticks;
            _lastY = now.Y;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;

        if (_view.IsMouseCaptured && Math.Abs(_velocity) > 0.08)
            _inertia.Start();
    }

    private void OnInertiaTick(object? sender, EventArgs e)
    {
        _velocity *= Decay;

        if (Math.Abs(_velocity) < StopEpsilon)
        {
            StopInertia();
            return;
        }

        // 位移方向与拖动一致：向上拖（Y 减小）→ 内容往下翻
        _view.ScrollToVerticalOffset(_view.VerticalOffset - _velocity * _inertia.Interval.TotalMilliseconds);
    }

    private void StopInertia()
    {
        _inertia.Stop();
        _velocity = 0;
        if (_view.IsMouseCaptured) _view.ReleaseMouseCapture();
    }

    public void Dispose()
    {
        StopInertia();
        _inertia.Stop();
        _view.PreviewMouseLeftButtonDown -= OnDown;
        _view.PreviewMouseMove -= OnMove;
        _view.PreviewMouseLeftButtonUp -= OnUp;
    }
}
