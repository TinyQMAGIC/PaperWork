using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Paperwork.Shell;

/// <summary>
/// 取 Shell 图标、显示名与失效状态。
///
/// 图标走 <c>IShellItemImageFactory.GetImage</c>（Vista+ 的现代入口），它能按我们要的
/// 精确像素尺寸返回 32 位带 alpha 的位图，并且自带快捷方式角标。
///
/// 为什么不用另外两条路：
/// <list type="bullet">
///   <item><c>SHGetFileInfo(SHGFI_ICON)</c> 只给 16 / 32px，放进 42 或 56px 的磁贴会糊</item>
///   <item><c>SHGetImageList</c> 只有序号导出（#727），实测在 x64 shell32 上返回
///         E_NOINTERFACE，版本相关、不可靠</item>
/// </list>
///
/// 所有调用都必须在后台线程跑：对网络共享和未下载的 OneDrive 占位文件，Shell 查询能阻塞数秒。
/// 而且那个后台线程必须是 <b>STA</b>——这里在创建 COM 对象。
/// </summary>
public static class ShellIcons
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_DISPLAYNAME = 0x000000200;
    private const uint SHGFI_TYPENAME = 0x000000400;

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly Guid ShellItemIID = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    /// <summary>
    /// IShellItem 只需要声明出来当 QI 的锚点，一个方法都不调。
    /// 把 RCW 直接强转成 IShellItemImageFactory 时，运行时会替我们做 QueryInterface。
    /// </summary>
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
    }

    /// <summary>IShellItemImageFactory。GetImage 是接口声明的第一个方法 → vtable 槽 3。</summary>
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        [PreserveSig] int GetSubList(out IntPtr ppsi);
    }

    public enum HandleKind { None, GdiBitmap, Icon }

    /// <summary>
    /// 待完成的图标请求。<see cref="Handle"/> 是 GDI 对象，必须在 UI 线程用
    /// <see cref="Materialize"/> 转成 BitmapSource——<c>Imaging.CreateBitmapSourceFrom*</c>
    /// 要求线程有 Dispatcher，在 Task.Run 的 MTA 线程上调用会抛异常并被静默吞掉。
    /// </summary>
    public sealed record Pending(
        IntPtr Handle, HandleKind Kind, string DisplayName, string TypeName, bool Exists, string? DeadReason);

    /// <summary>诊断用：最近一次失败的原因。正常运行不读。</summary>
    internal static string LastFailure { get; private set; } = string.Empty;

    public static Pending Query(string path, int targetPx)
    {
        bool exists = false, isDir = false;
        try
        {
            exists = File.Exists(path);
            if (!exists)
            {
                isDir = Directory.Exists(path);
                exists = isDir;
            }
        }
        catch (Exception) { /* 无权限或路径非法，按不存在处理 */ }

        var shfi = default(SHFILEINFO);
        uint flags = SHGFI_TYPENAME | SHGFI_DISPLAYNAME | (exists ? 0u : SHGFI_USEFILEATTRIBUTES)
                   | (targetPx <= 20 ? SHGFI_SMALLICON : SHGFI_LARGEICON);

        _ = SHGetFileInfo(path, isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                          ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

        string displayName = string.IsNullOrWhiteSpace(shfi.szDisplayName)
            ? Path.GetFileName(path.TrimEnd('\\', '/'))
            : shfi.szDisplayName;

        if (!exists)
            return new Pending(IntPtr.Zero, HandleKind.None, displayName, shfi.szTypeName ?? string.Empty,
                               false, ReasonFor(path));

        IntPtr hbmp = BitmapFromShellItem(path, targetPx);
        if (hbmp != IntPtr.Zero)
            return new Pending(hbmp, HandleKind.GdiBitmap, displayName, shfi.szTypeName ?? string.Empty, true, null);

        return new Pending(IntPtr.Zero, HandleKind.None, displayName, shfi.szTypeName ?? string.Empty, true, null);
    }

    private static IntPtr BitmapFromShellItem(string path, int px)
    {
        IShellItem? item = null;
        IShellItemImageFactory? factory = null;
        try
        {
            Guid iid = ShellItemIID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (item is null)
            {
                LastFailure = "SHCreateItemFromParsingName 没给回 IShellItem";
                return IntPtr.Zero;
            }

            // 强转触发 QueryInterface；不支持该接口的对象会在这一步抛 InvalidCastException
            factory = (IShellItemImageFactory)item;

            int hr = factory.GetImage(new SIZE { cx = px, cy = px }, 0, out IntPtr hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero)
            {
                LastFailure = $"GetImage({px}) hr=0x{hr:x8}";
                return IntPtr.Zero;
            }
            return hbmp;
        }
        catch (Exception ex)
        {
            LastFailure = $"ShellItem {ex.GetType().Name} 0x{ex.HResult:x8}";
            return IntPtr.Zero;
        }
        finally
        {
            // GDI 句柄那边由调用者在 Materialize 里配对 DeleteObject / DestroyIcon，
            // 但这两层 COM 对象默认只能等 GC 跑到终结器才释放。 STA 线程上一个来不及收的
            // RCW 就是一个常驻的 native shell item——翻一次几百项的文件夹层能堆几百个，
            // 而且这块内存在托管工作集里看不见，只能拿私有内存才量得出来。
            SafeReleaseCom(factory);
            SafeReleaseCom(item);
        }
    }

    /// <summary>
    /// 释放一个 RCW。用 <see cref="Marshal.ReleaseComObject"/> 而不是 FinalReleaseComObject：
    /// 每个 RCW 手上正好握一个引用计数，减一次就够；FinalRelease 会把整条链一起拍平，
    /// 万一上面两个局部变量背后是同一个 RCW，就会释放过头。
    /// </summary>
    private static void SafeReleaseCom(object? rcw)
    {
        if (rcw is null) return;
        try { Marshal.ReleaseComObject(rcw); }
        catch (Exception) { /* 已经和底层分离时会抛；此处之后没有任何动作依赖它 */ }
    }

    /// <summary>在 UI 线程调用：把 GDI 句柄转成已冻结的 BitmapSource，并负责释放句柄。</summary>
    public static ImageSource? Materialize(Pending pending)
    {
        if (pending.Handle == IntPtr.Zero) return null;

        try
        {
            var source = pending.Kind switch
            {
                HandleKind.GdiBitmap => Imaging.CreateBitmapSourceFromHBitmap(
                    pending.Handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()),
                HandleKind.Icon => Imaging.CreateBitmapSourceFromHIcon(
                    pending.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()),
                _ => null
            };
            source?.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            LastFailure = $"Materialize: {ex.GetType().Name}";
            return null;
        }
        finally
        {
            Release(pending.Handle, pending.Kind);
        }
    }

    /// <summary>放弃一个还没转换过的 GDI 句柄。不释放就是 GDI 对象泄漏。</summary>
    public static void Release(IntPtr handle, HandleKind kind)
    {
        if (handle == IntPtr.Zero) return;
        switch (kind)
        {
            case HandleKind.Icon: _ = DestroyIcon(handle); break;
            case HandleKind.GdiBitmap: _ = DeleteObject(handle); break;
        }
    }

    /// <summary>
    /// 失效原因。只做本地、确定不会阻塞的判断——对掉线的网络共享再查一次存在性
    /// 会把后台线程也卡住。
    /// </summary>
    private static string ReasonFor(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return "网络位置当前不可用";

            string? root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) return $"驱动器 {root} 当前不可用";

            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) return "上级文件夹已不存在";
        }
        catch (ArgumentException)
        {
            return "路径格式无效";
        }

        return "已被删除或移动";
    }
}
