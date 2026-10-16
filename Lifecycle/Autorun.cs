using System;
using Microsoft.Win32;
using Paperwork.Data;   // Str：开机自启写失败的原因会经气泡给用户看到

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

    /// <summary>
    /// 把开机自启写进 / 移出 Run 键。<b>返回 false 表示没写成</b>。
    ///
    /// 返回值不是可选的装饰：注册表被组策略锁掉、受控文件夹、HKCU 被重定向之类的情形下
    /// 写入会失败，而失败一旦静默，用户看到的就是「开关显示开、state.json 里也存了 true、
    /// 下次开机却没自启」—— 三处口径互相矛盾，且没有任何一处能告诉他真相。
    /// 调用方拿到 false 之后要把开关退回关并说清原因（见 <c>App.OnStartup</c> 与
    /// <c>PanelWindow.ToggleFlag</c>）。
    /// </summary>
    public static bool Apply(bool enabled, bool userInitiated = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            string ours = Quote(Environment.ProcessPath ?? "Paperwork.exe") + " " + StartupArg;
            string? current = key.GetValue(ValueName) as string;

            // ⚠️ 防"自启劫持"（2026-10-17 实测到的 bug）：
            // Run 键只有一条（值名就是 "Paperwork"），而**每一个打包副本启动时都会走到这里**。
            // 原样重写的后果：用户只要在临时目录里双击过一次某个副本，开机自启就被改指向那份副本；
            // 副本一删，开机就再也不会启动 —— 全程零提示，用户要到下次开机才发现。
            // 所以启动这条路上（userInitiated=false）**只碰自己那一条**：
            //   打开：没有 / 指的正是本 exe → 写；指向别的副本 → 跳过（记一行日志）
            //   关闭：指的正是本 exe → 删；别的副本的条目 → 跳过（不是我们该动的）
            // 用户明确要改指向/要关掉，走设置页那颗开关（userInitiated=true，强制生效）。
            bool foreign = current is not null && !IsOurs(current, ours);
            if (foreign && !userInitiated)
            {
                App.LogError(string.Format(Str.ErrAutorunKeptForeign, current));
                return true;      // 不算失败：别人的登记没被动，本次副本自己不自启而已
            }

            if (enabled)
                // 带上启动标记，让 App 能把"开机自启"和"用户双击"分开处理。
                // 顺带修一个迁移问题：旧版本写进去的值没有标记，这里每次启动都会重写，
                // 所以升级之后最多只有一次开机是被兜底逻辑判断的。
                key.SetValue(ValueName, ours);
            else if (current is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex)
        {
            // 不该让面板崩，但也不能什么都不留 —— 这一行是排查「为什么没自启」的唯一线索
            App.LogError(string.Format(Str.ErrAutorunWriteFailed, enabled, ex.Message));
            return false;
        }
    }

    /// <summary>带空格的路径必须整体加引号，否则 Run 键会只执行到第一个空格。</summary>
    private static string Quote(string path) => $"\"{path}\"";

    /// <summary>
    /// Run 键里那条登记**是不是本进程这个 exe**（只比路径，后面跟的参数不管 ——
    /// 旧版本写进去的值可能没有 <see cref="StartupArg"/> 标记）。
    /// </summary>
    private static bool IsOurs(string runValue, string ours) =>
        string.Equals(LeadingPath(runValue), LeadingPath(ours), StringComparison.OrdinalIgnoreCase);

    /// <summary>取出"程序路径"那一段：有引号取引号内，没引号取到第一个空格前。</summary>
    private static string LeadingPath(string value)
    {
        string v = value.Trim();
        if (v.StartsWith('"'))
        {
            int end = v.IndexOf('"', 1);
            return end > 1 ? v.Substring(1, end - 1) : v.Trim('"');
        }
        int space = v.IndexOf(' ');
        return space > 0 ? v.Substring(0, space) : v;
    }
}
