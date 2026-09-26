using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Paperwork.Diagnostics;

/// <summary>
/// 呼出延迟测量（方案 §13.3）：从 WM_HOTKEY 处理入口打点，
/// 到 Show() 之后第一帧 CompositionTarget.Rendering 为止。
/// 断言 P95 &lt; 30ms。
/// </summary>
internal sealed class PerfProbe
{
    private readonly List<double> _samples = new(256);
    private long _startTimestamp;
    private bool _armed;

    public double LastMs { get; private set; }
    public double P95Ms { get; private set; }
    public int ShowCount => _samples.Count;

    public void ArmShowStart()
    {
        _startTimestamp = Stopwatch.GetTimestamp();
        _armed = true;
    }

    public void MarkFirstFrame()
    {
        if (!_armed) return;
        _armed = false;

        double ms = (Stopwatch.GetTimestamp() - _startTimestamp) * 1000.0 / Stopwatch.Frequency;
        LastMs = ms;
        _samples.Add(ms);
        if (_samples.Count > 200) _samples.RemoveAt(0);

        var sorted = new List<double>(_samples);
        sorted.Sort();
        int idx = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
        P95Ms = sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    public static string SnapshotResource()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return $"WS {p.WorkingSet64 / 1048576.0:F1}MB · Private {p.PrivateMemorySize64 / 1048576.0:F1}MB · " +
               $"Threads {p.Threads.Count} · Handles {p.HandleCount}";
    }
}
