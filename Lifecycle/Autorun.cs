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

    /// <summary>
    /// 写进 Run 键的启动标记：这一次的启动是**开机自启**，不是用户双击。
    ///
    /// 没有这个标记，App 就分不清两种启动方式，只能二选一：
    /// 要么手动双击也不弹面板（现在的行为），要么每次开机脸上都糊一块面板。
    /// 有了它，手动启动可以直接弹面板，开机那次仍然只留托盘。
    /// </summary>
    public const string StartupArg = "--startup";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
                // 带上启动标记，让 App 能把"开机自启"和"用户双击"分开处理。
                // 顺带修一个迁移问题：旧版本写进去的值没有标记，这里每次启动都会重写，
                // 所以升级之后最多只有一次开机是被兜底逻辑判断的。
                key.SetValue(ValueName, Quote(Environment.ProcessPath ?? "Paperwork.exe") + " " + StartupArg);
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
