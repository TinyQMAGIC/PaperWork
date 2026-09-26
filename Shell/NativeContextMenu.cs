using System;
using System.Runtime.InteropServices;

namespace Paperwork.Shell;

/// <summary>
/// 把资源管理器的原生右键菜单挂到我们的浮窗上。
///
/// 标准的 Shell 三段式：路径 → PIDL → 父 IShellFolder.GetUIObjectOf 拿 IContextMenu
/// → QueryContextMenu 填一个 HMENU → TrackPopupMenu 弹出 → InvokeCommand 执行。
/// 用 TPM_RETURNCMD 让 TrackPopupMenu 阻塞并直接返回命令号，省掉自己路由 WM_COMMAND。
///
/// COM 方法全部走手动 vtable 调用，不声明 ComImport 接口——省掉封送层的不确定性，
/// 代价是要自己数槽位，所以每个槽位都标了注释。
///
/// <b>两个曾经把这条功能坑死两周的点，务必不要再踩：</b>
/// <list type="number">
///   <item><c>IShellFolder.GetUIObjectOf</c> 是 vtable 第 <b>10</b> 槽，不是第 8 槽。
///         第 8 槽是 <c>CreateViewObject(hwnd, riid, ppv)</c>，只有三个参数；
///         按七个参数去调它，callee 会把我们的 cidl(=1) 当成 REFIID 去解引用地址 0x1，
///         稳定抛 0xC0000005。上一轮所有「IUnknown 都 AV」的现象都是这一个错。</item>
///   <item><c>ILCreateFromPathW</c> 的返回值<b>就是 PIDL</b>，且只有一个入参。
///         声明成 <c>int(string, out IntPtr)</c> 的话，指针被当 HRESULT 判掉、
///         out 参数对应的寄存器没人写、永远是 0。
///         另外「PIDL 尺寸必须是偶数」是错的（C:\ 的相对 PIDL 就是 47 字节），
///         上一轮拿它当合法性判据，误诊了一整轮。</item>
///   <item><c>SHBindToParent</c> 的 <c>pidlLast</c> 出参是 fullPidl <b>内部的指针</b>，
///         不是独立分配，<b>绝对不能 ILFree</b>。free 它触发 0xC0000374 堆损坏，
///         进程当场死、任何 catch 都拦不住。上一轮的「进程级崩溃」其实是这个。</item>
/// </list>
///
/// 还有两个使用上的硬要求：
/// <list type="bullet">
///   <item>TrackPopupMenu 前必须 SetForegroundWindow，弹完要 PostMessage(WM_NULL)，
///         否则点菜单外区域时菜单不会正确消失。</item>
///   <item>Win11 的原生菜单里大量条目是 MFT_OWNERDRAW，字和图标由 shell 扩展自己画。
///         它靠 hwndOwner 收到 WM_INITMENUPOPUP / WM_DRAWITEM / WM_MEASUREITEM / WM_MENUCHAR
///         才画，所以这四条消息必须转发给 IContextMenu3::HandleMenuMsg2——
///         见 <see cref="HandleMenuMessage"/>，由 PanelWindow 的 WndProc 调用。
///         不转发的症状是菜单能弹出来但一半条目空白。</item>
/// </list>
/// </summary>
public static class NativeContextMenu
{
    // ---- IShellFolder vtable：0 QI / 1 AddRef / 2 Release / 3 ParseDisplayName / 4 EnumObjects
    //      5 BindToObject / 6 BindToStorage / 7 CompareIDs / 8 CreateViewObject
    //      9 GetAttributesOf / 10 GetUIObjectOf
    private const int ShellFolder_GetUIObjectOf = 10;

    // ---- IContextMenu vtable：IUnknown 占 0..2
    private const int ContextMenu_QueryContextMenu = 3;
    private const int ContextMenu_InvokeCommand = 4;

    // ---- IContextMenu3 vtable：IContextMenu 占 0..5，IContextMenu2.HandleMenuMsg=6，HandleMenuMsg2=7
    private const int ContextMenu3_HandleMenuMsg2 = 7;

    private const uint CMF_NORMAL = 0x000000000;
    private const uint CMF_EXPLORE = 0x000000004;
    private const uint CMF_CANRENAME = 0x000000010;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint WM_NULL = 0x0000;
    private const uint WM_INITMENUPOPUP = 0x0117;
    private const uint WM_DRAWITEM = 0x002B;
    private const uint WM_MEASUREITEM = 0x002C;
    private const uint WM_MENUCHAR = 0x0120;

    private static readonly Guid ShellFolderIID = new("000214e6-0000-0000-c000-000000000046");
    private static readonly Guid ContextMenuIID = new("000214e4-0000-0000-c000-000000000046");
    private static readonly Guid ContextMenu3IID = new("bcfce0a0-ec17-11d0-8d10-00a0c90f2719");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetUIObjectOfDel(
        IntPtr that, IntPtr hwndOwner, uint cidl, IntPtr apidl,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        ref uint reserved, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueryContextMenuDel(
        IntPtr that, IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int InvokeCommandDel(IntPtr that, ref CMINVOKECOMMANDINFO info);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandleMenuMsg2Del(IntPtr that, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);

    /// <summary>
    /// 真实签名：<c>PIDLIST_ABSOLUTE ILCreateFromPathW(PCWSTR pszPath)</c>。
    /// 返回值就是 PIDL，只有一个入参。
    /// </summary>
    [DllImport("shell32.dll", EntryPoint = "ILCreateFromPathW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr parent, out IntPtr lastItemId);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr verb;
        public IntPtr parameters;
        public IntPtr directory;
        public int icon;
        public IntPtr hInst;
    }

    internal static string LastFailure { get; private set; } = string.Empty;
    internal static int LastVerbCount { get; private set; }

    /// <summary>
    /// 弹菜单期间持有 IContextMenu3，供 <see cref="HandleMenuMessage"/> 转发 owner-draw 消息。
    /// TrackPopupMenu 是阻塞的，所以这个字段只在它返回前后的一小段窗口内非空。
    ///
    /// <b>只允许是真正的 IContextMenu3 指针，或者 0。</b>把 contextMenu（IContextMenu 本体）
    /// 填进来会让 <see cref="HandleMenuMessage"/> 去读它的第 7 槽，而 IContextMenu 只有 0..5 槽。
    /// </summary>
    private static IntPtr _liveCm3;

    /// <summary>
    /// <c>TrackPopupMenu</c> 的模态循环正在进行。全屏自动隐藏要避开这段时间。
    ///
    /// 为什么不能借 <c>_liveCm3 != 0</c> 来表达：第三方 shell 扩展很可能拿不出 IContextMenu3，
    /// 那种情况下菜单照样是弹着的（本字段必须为真），而 <c>_liveCm3</c> 必须是 0。
    /// 两个语义共用一个字段正是当初写出槽位越界的原因。
    /// </summary>
    private static volatile bool _menuActive;

    /// <summary>原生菜单正在弹出（<c>TrackPopupMenu</c> 的模态循环里）。</summary>
    internal static bool IsLive => _menuActive;

    /// <summary>
    /// Win11 原生菜单的 owner-draw 条目靠这四条消息画自己。消息发给 hwndOwner，
    /// 所以必须由宿主窗口的 WndProc 转进来。返回 true 表示消息已被消费，宿主应回 handled。
    /// </summary>
    public static bool HandleMenuMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (_liveCm3 == IntPtr.Zero) return false;
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)) return false;

        int hr = Slot<HandleMenuMsg2Del>(_liveCm3, ContextMenu3_HandleMenuMsg2)(
            _liveCm3, msg, wParam, lParam, out result);
        return hr == 0;
    }

    /// <summary>
    /// 只走「拿 IContextMenu + QueryContextMenu 填菜单」这一段，不弹窗。
    /// 用于自动化验证：TrackPopupMenu 需要人眼确认，而这段 Shell COM 管线能否跑通可以机器判定。
    /// </summary>
    public static (bool Ok, int Verbs, string Failure) Probe(string path, IntPtr ownerHwnd)
    {
        LastFailure = string.Empty;
        LastVerbCount = 0;

        IntPtr contextMenu = IntPtr.Zero;
        if (!Build(path, ownerHwnd, out IntPtr menu, ref contextMenu, out string failure))
            return (false, 0, failure);

        _ = DestroyMenu(menu);
        if (contextMenu != IntPtr.Zero) _ = Marshal.Release(contextMenu);
        return (true, LastVerbCount, string.Empty);
    }

    /// <summary>
    /// 在屏幕坐标 (screenX, screenY) 弹出某文件的原生 Shell 菜单。
    /// 返回 true 表示用户选了某个动词并已交给 Shell 执行。
    /// </summary>
    public static bool TryShow(IntPtr ownerHwnd, string path, int screenX, int screenY)
    {
        LastFailure = string.Empty;
        LastVerbCount = 0;

        IntPtr contextMenu = IntPtr.Zero;
        if (!Build(path, ownerHwnd, out IntPtr menu, ref contextMenu, out string failure))
        {
            LastFailure = failure;
            return false;
        }

        IntPtr cm3 = IntPtr.Zero;
        try
        {
            // QI 很可能拿不到：第三方命名空间扩展大量只实现到 IContextMenu / IContextMenu2。
            //
            // **绝不能**在拿不到时回退到 contextMenu 本体。IContextMenu 的 vtable 只有 0..5 槽
            // （IUnknown 0..2 + QueryContextMenu 3 + InvokeCommand 4 + GetCommandString 5），
            // 而 HandleMenuMsg2 是槽 7。拿 IContextMenu 去读槽 7 会越过 vtable 末尾，读到一个
            // 随机指针再 call 过去 —— 0xC0000005，且这段跳转发生在 Marshal 层外面，
            // 本文件的 try/catch 拦不住，进程当场死。
            //
            // 拿不到就留 0：HandleMenuMessage 开头会直接返回 false，
            // 而这类老式 IContextMenu 本来也没有 owner-draw 条目需要转发。
            Guid cm3Iid = ContextMenu3IID;
            int hrCm3 = Marshal.QueryInterface(contextMenu, in cm3Iid, out cm3);
            if (hrCm3 != 0)
            {
                // 失败时 out 参数也可能是脏值，先Release再归零，避免 finally 里二次释放
                if (cm3 != IntPtr.Zero) { Marshal.Release(cm3); cm3 = IntPtr.Zero; }
            }
            _liveCm3 = cm3;

            // 「菜单正弹着」这个语义走独立布尔，与 _liveCm3 是否为 QI 结果解耦
            _menuActive = true;

            // 不 SetForegroundWindow 的话，菜单在点击别处时不会正确消失（Win32 的硬性要求）
            _ = SetForegroundWindow(ownerHwnd);

            int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                                     screenX, screenY, 0, ownerHwnd, IntPtr.Zero);
            _ = PostMessage(ownerHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd <= 0) return false;

            Invoke(contextMenu, ownerHwnd, (uint)(cmd - 1));
            return true;
        }
        catch (Exception ex)
        {
            LastFailure = ex.GetType().Name;
            return false;
        }
        finally
        {
            _liveCm3 = IntPtr.Zero;
            _menuActive = false;
            if (cm3 != IntPtr.Zero) _ = Marshal.Release(cm3);
            if (menu != IntPtr.Zero) _ = DestroyMenu(menu);
            if (contextMenu != IntPtr.Zero) _ = Marshal.Release(contextMenu);
        }
    }

    /// <summary>路径 → PIDL → 父 IShellFolder → IContextMenu → 填好 hMenu。</summary>
    private static bool Build(
        string path, IntPtr ownerHwnd, out IntPtr menu, ref IntPtr contextMenu, out string failure)
    {
        menu = IntPtr.Zero;
        failure = string.Empty;

        IntPtr fullPidl = IntPtr.Zero;
        IntPtr parent = IntPtr.Zero;
        IntPtr childPidl = IntPtr.Zero;
        IntPtr apidlBlock = IntPtr.Zero;

        try
        {
            // 部分 shell folder 实现对 hwndOwner 不判空，传 0 会在 GetUIObjectOf 里直接 AV
            if (ownerHwnd == IntPtr.Zero) ownerHwnd = GetDesktopWindow();

            fullPidl = ILCreateFromPathW(path);
            if (fullPidl == IntPtr.Zero)
            {
                failure = "ILCreateFromPathW 返回空";
                return false;
            }

            Guid folderIid = ShellFolderIID;
            int hrBind = SHBindToParent(fullPidl, ref folderIid, out parent, out childPidl);
            if (hrBind != 0 || parent == IntPtr.Zero || childPidl == IntPtr.Zero)
            {
                failure = $"SHBindToParent hr=0x{hrBind:x8}";
                return false;
            }

            // apidl 是「PIDL 指针的数组」，手工分配一个指针大小的块
            apidlBlock = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(apidlBlock, childPidl);

            Guid cmIid = ContextMenuIID;
            uint reservedFlags = 0;
            int hrUi = Slot<GetUIObjectOfDel>(parent, ShellFolder_GetUIObjectOf)(
                parent, ownerHwnd, 1, apidlBlock, cmIid, ref reservedFlags, out contextMenu);
            if (hrUi != 0 || contextMenu == IntPtr.Zero)
            {
                failure = $"GetUIObjectOf hr=0x{hrUi:x8}";
                return false;
            }

            menu = CreatePopupMenu();
            int inserted = Slot<QueryContextMenuDel>(contextMenu, ContextMenu_QueryContextMenu)(
                contextMenu, menu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME);

            if (inserted < 0)
            {
                failure = $"QueryContextMenu hr=0x{inserted:x8}";
                return false;
            }

            LastVerbCount = inserted;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name;
            return false;
        }
        finally
        {
            if (apidlBlock != IntPtr.Zero) Marshal.FreeHGlobal(apidlBlock);
            // childPidl 不能 ILFree：SHBindToParent 给的是 fullPidl 内部的指针，
            // 不是独立分配。free 它 = 堆损坏（0xC0000374），进程直接死，catch 不住。
            if (parent != IntPtr.Zero) _ = Marshal.Release(parent);
            if (fullPidl != IntPtr.Zero) ILFree(fullPidl);
        }
    }

    /// <summary>
    /// 从 IUnknown 指针取第 slot 个虚函数并包成委托。
    ///
    /// 槽号错了是本项目历史上最贵的一类 bug（GetUIObjectOf 写成第 8 槽，稳定 AV 两周）。
    /// 所以这里逐层校验，把「读到一个野指针然后 call 过去」降级成「抛一个能被 catch 的异常」：
    /// 前者进程当场死且不在任何 try 的保护范围内，后者最多变成 error.log 里的一行。
    /// </summary>
    private static T Slot<T>(IntPtr unknown, int slot) where T : Delegate
    {
        if (unknown == IntPtr.Zero)
            throw new InvalidOperationException($"Slot({slot}): IUnknown 指针为空");

        IntPtr vtbl = Marshal.ReadIntPtr(unknown);
        if (vtbl == IntPtr.Zero)
            throw new InvalidOperationException($"Slot({slot}): vtable 指针为空");

        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        if (fn == IntPtr.Zero)
            throw new InvalidOperationException($"Slot({slot}): 该对象的 vtable 没这么深，槽号有误");

        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static void Invoke(IntPtr contextMenu, IntPtr ownerHwnd, uint verbId)
    {
        // idCmdFirst 传的是 1，所以 TrackPopupMenu 回来的永远是数字偏移，走 lpVerb=MAKEINTRESOURCE 分支
        var info = new CMINVOKECOMMANDINFO
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
            fMask = 0,
            hwnd = ownerHwnd,
            verb = new IntPtr(verbId)
        };
        Slot<InvokeCommandDel>(contextMenu, ContextMenu_InvokeCommand)(contextMenu, ref info);
    }
}
