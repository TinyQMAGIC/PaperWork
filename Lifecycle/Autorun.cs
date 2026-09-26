using System;
using Microsoft.Win32;

namespace Paperwork.Lifecycle;

/// <summary>
/// 开机自启。用 HKCU 的 Run 键——不需要管理员、不需要计划任务、不会在启动文件夹里
/// 留下一个用户能误删的快捷方式。
/// </summary>
public static class Autorun
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Paperwork";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
                key.SetValue(ValueName, Quote(Environment.ProcessPath ?? "Paperwork.exe"));
            else if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // 注册表被组策略锁掉之类的情形：设置项留着，但不该让面板崩
        }
    }

    /// <summary>带空格的路径必须整体加引号，否则 Run 键会只执行到第一个空格。</summary>
    private static string Quote(string path) => $"\"{path}\"";
}
