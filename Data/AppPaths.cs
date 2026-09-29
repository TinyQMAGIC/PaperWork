using System;
using System.IO;

namespace Paperwork.Data;

/// <summary>
/// 数据目录的唯一决定处。<see cref="StateStore"/> / <see cref="Lifecycle.WindowStateStore"/> /
/// <see cref="App"/> 三处一律走这里，不要再各写一遍 <c>AppData\Paperwork</c>。
///
/// <b>取值顺序</b>：
///   ① exe 同级目录下的 <c>UserData</c>（能写就用它 —— 数据跟着程序走，备份就是复制文件夹）；
///   ② 写不了就退回 <c>%APPDATA%\Paperwork</c>（装在 Program Files 这类受保护目录时的保命路径）。
///
/// 为什么必须留 ②：标准用户对 Program Files 没有写权限，而本项目 manifest 声明的是
/// <c>asInvoker</c>（不提权），<b>有 manifest 的程序不享受 UAC 虚拟化</b>——那种"静默重定向到
/// VirtualStore"的兜底只给没 manifest 的老程序。硬写 exe 同级又不回退的话，失败模式是
/// "面板照常起来、看着一切正常、但什么都没存住"，这是最难被发现的一类问题。
///
/// <b>成本</b>：只在首次访问时跑一次（两三次文件系统调用，几十微秒到 1 毫秒），
/// 结果存进静态字段，之后所有读写只是读一个字符串。不加线程、不加计时器、不轮询，
/// 面板收起常驻时零开销。
/// </summary>
internal static class AppPaths
{
    /// <summary>exe 同级目录下的文件夹名。</summary>
    private const string FolderName = "UserData";

    /// <summary>回退用的旧位置。</summary>
    private const string LegacyFolderName = "Paperwork";

    private static string? _dir;

    /// <summary>数据目录。首次访问时确定，之后不再变。</summary>
    public static string DataDir => _dir ??= Resolve();

    private static string LegacyDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);

    private static string Resolve()
    {
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        if (!string.IsNullOrEmpty(exeDir))
        {
            string portable = Path.Combine(exeDir, FolderName);
            if (IsUsable(portable))
            {
                MigrateFromLegacy(portable);
                return portable;
            }
        }

        return LegacyDir;
    }

    /// <summary>
    /// 能不能用这个目录。建目录 + 真写一个探针文件再删掉：
    /// 只 CreateDirectory 成功还不够，存在"目录能建、文件写不了"的情况（极少但确实有）。
    /// 任何异常都当作不可用——这里不能让路径解析把程序拖挂。
    /// </summary>
    private static bool IsUsable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);

            string probe = Path.Combine(dir, ".writetest");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 首次启动把旧位置的数据搬过来。只在目标文件不存在时复制，
    /// 所以跑过一次之后就自动变成空操作，不需要额外的"已迁移"标记文件。
    ///
    /// 旧文件<b>不删</b>：留着当退路，万一新位置出问题还能人工救回。
    /// </summary>
    private static void MigrateFromLegacy(string target)
    {
        string legacy = LegacyDir;
        if (string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(legacy)) return;

        string[] names =
        [
            "state.json", "state.json.bak",
            "window.json", "window.json.bak",
            "error.log", "error.log.old"
        ];

        foreach (string name in names)
        {
            try
            {
                string src = Path.Combine(legacy, name);
                string dst = Path.Combine(target, name);
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst, overwrite: false);
            }
            catch (Exception)
            {
                // 某一份没搬成也不影响启动：用户数据以 state.json 为主，
                // 真出问题时旧文件还在，可以人工复制
            }
        }
    }
}
