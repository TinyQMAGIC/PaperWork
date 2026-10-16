// 自动生成，**不要手改**。
//   来源：tools/ui-strings.tsv（界面文案的唯一来源）
//   命令：python tools/make-strings.py --lang cn
//   当前语言：zh-CN（仓库默认状态；英文只由打包脚本临时生成，打完必须切回中文）
//
// 代码里不许再硬编码界面文字：XAML 用 {x:Static s:Str.Xxx}，C# 用 Str.Xxx。
// 带 {0} {1} 的是格式串，用 string.Format —— 中英文语序可能不一样。

namespace Paperwork.Data;

/// <summary>
/// 界面文案（编译期常量）。173 条，由 tools\make-strings.py 生成。
/// </summary>
public static class Str
{
    /// <summary>board.rootSubtitle（zh-CN）</summary>
    public const string BoardRootSubtitle = "{0} 项 · {1} 个组合";

    /// <summary>board.emptyTitle（zh-CN）</summary>
    public const string BoardEmptyTitle = "把任意文件拖进来";

    /// <summary>board.emptySubtitle（zh-CN）</summary>
    public const string BoardEmptySubtitle = "程序、快捷方式、文件夹、文档、图片、压缩包…";

    /// <summary>board.folderLoading（zh-CN）</summary>
    public const string BoardFolderLoading = "正在读取该文件夹…";

    /// <summary>board.folderLoadingHint（zh-CN）</summary>
    public const string BoardFolderLoadingHint = "稍等一下";

    /// <summary>board.folderReadFailed（zh-CN）</summary>
    public const string BoardFolderReadFailed = "无法读取该文件夹";

    /// <summary>board.groupSubtitle（zh-CN）</summary>
    public const string BoardGroupSubtitle = "{0} 项 · 拖到此处即加入本组合";

    /// <summary>board.groupWord（zh-CN）</summary>
    public const string BoardGroupWord = "组合";

    /// <summary>board.folderCountCapped（zh-CN）</summary>
    public const string BoardFolderCountCapped = "共 {0} 项 · 已显示前 {1} 项";

    /// <summary>board.folderCount（zh-CN）</summary>
    public const string BoardFolderCount = "{0} 项";

    /// <summary>board.openInExplorer（zh-CN）</summary>
    public const string BoardOpenInExplorer = "在资源管理器中打开";

    /// <summary>board.pathUnavailable（zh-CN）</summary>
    public const string BoardPathUnavailable = "路径不可用";

    /// <summary>board.unnamed（zh-CN）</summary>
    public const string BoardUnnamed = "未命名";

    /// <summary>board.newGroupName（zh-CN）</summary>
    public const string BoardNewGroupName = "新组合";

    /// <summary>menu.open（zh-CN）</summary>
    public const string MenuOpen = "打开";

    /// <summary>menu.rename（zh-CN）</summary>
    public const string MenuRename = "重命名";

    /// <summary>menu.tilePreset（zh-CN）</summary>
    public const string MenuTilePreset = "更改磁贴预设图标";

    /// <summary>menu.groupPreset（zh-CN）</summary>
    public const string MenuGroupPreset = "更改组合预设图标";

    /// <summary>menu.restoreDefault（zh-CN）</summary>
    public const string MenuRestoreDefault = "恢复默认图标";

    /// <summary>menu.changeIcon（zh-CN）</summary>
    public const string MenuChangeIcon = "更改图标…";

    /// <summary>menu.newGroup（zh-CN）</summary>
    public const string MenuNewGroup = "新建组合…";

    /// <summary>menu.leaveGroup（zh-CN）</summary>
    public const string MenuLeaveGroup = "移出本组合";

    /// <summary>menu.removeFromPanel（zh-CN）</summary>
    public const string MenuRemoveFromPanel = "从面板移除";

    /// <summary>menu.deleteGroup（zh-CN）</summary>
    public const string MenuDeleteGroup = "删除组合（条目退回主界面）";

    /// <summary>menu.runAsAdmin（zh-CN）</summary>
    public const string MenuRunAsAdmin = "以管理员身份运行";

    /// <summary>menu.openTileLocation（zh-CN）</summary>
    public const string MenuOpenTileLocation = "打开磁贴位置";

    /// <summary>menu.openFileLocation（zh-CN）</summary>
    public const string MenuOpenFileLocation = "打开文件位置";

    /// <summary>menu.explorerMenu（zh-CN）</summary>
    public const string MenuExplorerMenu = "资源管理器菜单";

    /// <summary>status.folderUpdated（zh-CN）</summary>
    public const string StatusFolderUpdated = "文件夹已更新";

    /// <summary>status.watcherBroken（zh-CN）</summary>
    public const string StatusWatcherBroken = "监听已中断，重新进入该文件夹可恢复";

    /// <summary>status.added（zh-CN）</summary>
    public const string StatusAdded = "已加入 {0} 项";

    /// <summary>status.alreadyThere（zh-CN）</summary>
    public const string StatusAlreadyThere = "这些条目已经在这一层了";

    /// <summary>status.droppedNoPath（zh-CN）</summary>
    public const string StatusDroppedNoPath = "没收到文件路径";

    /// <summary>status.dropReadFailed（zh-CN）</summary>
    public const string StatusDropReadFailed = "读取拖放数据失败：{0}";

    /// <summary>status.dropQueryFailed（zh-CN）</summary>
    public const string StatusDropQueryFailed = "查询拖放格式失败：{0}";

    /// <summary>status.dropIgnoredWhileOverlay（zh-CN）</summary>
    public const string StatusDropIgnoredWhileOverlay = "设置 / 备忘 / 搜索开着的时候不收拖放";

    /// <summary>status.folderLayerNoDrop（zh-CN）</summary>
    public const string StatusFolderLayerNoDrop = "文件夹层显示的是磁盘内容，请回主界面或组合里再拖";

    /// <summary>status.deadPath（zh-CN）</summary>
    public const string StatusDeadPath = "路径已失效，可在右键菜单里移除";

    /// <summary>status.renamed（zh-CN）</summary>
    public const string StatusRenamed = "已重命名为「{0}」";

    /// <summary>status.iconChanged（zh-CN）</summary>
    public const string StatusIconChanged = "已更换图标";

    /// <summary>status.defaultRestored（zh-CN）</summary>
    public const string StatusDefaultRestored = "已恢复默认图标";

    /// <summary>status.removedFromPanel（zh-CN）</summary>
    public const string StatusRemovedFromPanel = "已从面板移除（未删除磁盘文件）";

    /// <summary>status.groupCreated（zh-CN）</summary>
    public const string StatusGroupCreated = "已创建「{0}」，含 {1} 项";

    /// <summary>status.newGroupCreated（zh-CN）</summary>
    public const string StatusNewGroupCreated = "已新建组合「{0}」，2 项已收纳";

    /// <summary>status.groupCreateFailed（zh-CN）</summary>
    public const string StatusGroupCreateFailed = "创建失败";

    /// <summary>status.merged（zh-CN）</summary>
    public const string StatusMerged = "已并入组合";

    /// <summary>status.mergeFailed（zh-CN）</summary>
    public const string StatusMergeFailed = "合并失败";

    /// <summary>status.groupInGroup（zh-CN）</summary>
    public const string StatusGroupInGroup = "组合内不能再放组合";

    /// <summary>status.saveFailed（zh-CN）</summary>
    public const string StatusSaveFailed = "上次保存失败：{0}（面板上的改动还没写进磁盘）";

    /// <summary>status.cancelAdmin（zh-CN）</summary>
    public const string StatusCancelAdmin = "已取消管理员授权";

    /// <summary>status.openFailed（zh-CN）</summary>
    public const string StatusOpenFailed = "无法打开：{0}";

    /// <summary>status.dragCancelled（zh-CN）</summary>
    public const string StatusDragCancelled = "已取消";

    /// <summary>status.dropToOpenFolder（zh-CN）</summary>
    public const string StatusDropToOpenFolder = "点开这个文件夹";

    /// <summary>status.atDeepest（zh-CN）</summary>
    public const string StatusAtDeepest = "已经到最里一层了";

    /// <summary>status.folderUnreachable（zh-CN）</summary>
    public const string StatusFolderUnreachable = "该文件夹当前不可访问";

    /// <summary>status.sysMenuUnavailable（zh-CN）</summary>
    public const string StatusSysMenuUnavailable = "系统菜单不可用：";

    /// <summary>dlg.ok（zh-CN）</summary>
    public const string DlgOk = "确定";

    /// <summary>dlg.cancel（zh-CN）</summary>
    public const string DlgCancel = "取消";

    /// <summary>dlg.chooseIconFile（zh-CN）</summary>
    public const string DlgChooseIconFile = "选择图标文件";

    /// <summary>dlg.iconFileFilter（zh-CN）</summary>
    public const string DlgIconFileFilter = "图标文件 (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|所有文件 (*.*)|*.*";

    /// <summary>tray.show（zh-CN）</summary>
    public const string TrayShow = "呼出面板";

    /// <summary>tray.hide（zh-CN）</summary>
    public const string TrayHide = "收起面板";

    /// <summary>tray.settings（zh-CN）</summary>
    public const string TraySettings = "设置";

    /// <summary>tray.exit（zh-CN）</summary>
    public const string TrayExit = "退出";

    /// <summary>tray.dataIncomplete（zh-CN）</summary>
    public const string TrayDataIncomplete = "面板数据没能完整读出来";

    /// <summary>tray.hotkeyFallback（zh-CN）</summary>
    public const string TrayHotkeyFallback = "呼出热键未生效";

    /// <summary>tray.hotkeyChangedTo（zh-CN）</summary>
    public const string TrayHotkeyChangedTo = "默认热键被占用，已改用 {0}";

    /// <summary>tray.hotkeyHint（zh-CN）</summary>
    public const string TrayHotkeyHint = "；可在设置里改键";

    /// <summary>tray.started（zh-CN）</summary>
    public const string TrayStarted = "Paperwork 已启动";

    /// <summary>tray.autorunFailed（zh-CN）</summary>
    public const string TrayAutorunFailed = "开机自启没能设置成功";

    /// <summary>tray.autorunFailedDetail（zh-CN）</summary>
    public const string TrayAutorunFailedDetail = "注册表 Run 键写不进去（常见于组策略锁定）。设置里的开关已退回关闭。";

    /// <summary>tray.noAdmin（zh-CN）</summary>
    public const string TrayNoAdmin = "不要用管理员身份运行 Paperwork";

    /// <summary>tray.noAdminLine1（zh-CN）</summary>
    public const string TrayNoAdminLine1 = "Windows 的 UIPI 会阻止资源管理器向提权进程拖放文件，";

    /// <summary>tray.noAdminLine2（zh-CN）</summary>
    public const string TrayNoAdminLine2 = "「把文件拖进来」这个核心功能会完全失效。请以普通权限启动。";

    /// <summary>tray.crashLogged（zh-CN）</summary>
    public const string TrayCrashLogged = "Paperwork 出了个问题";

    /// <summary>tray.crashLoggedTo（zh-CN）</summary>
    public const string TrayCrashLoggedTo = "已记录到 {0}";

    /// <summary>memo.summary（zh-CN）</summary>
    public const string MemoSummary = "{0} 条备忘 · 按最近更新";

    /// <summary>memo.edit（zh-CN）</summary>
    public const string MemoEdit = "编辑";

    /// <summary>memo.pin（zh-CN）</summary>
    public const string MemoPin = "置顶";

    /// <summary>memo.unpin（zh-CN）</summary>
    public const string MemoUnpin = "取消置顶";

    /// <summary>memo.delete（zh-CN）</summary>
    public const string MemoDelete = "删除";

    /// <summary>memo.untitled（zh-CN）</summary>
    public const string MemoUntitled = "（无标题）";

    /// <summary>memo.lengthAndTime（zh-CN）</summary>
    public const string MemoLengthAndTime = "{0} 字 · {1}";

    /// <summary>memo.yesterday（zh-CN）</summary>
    public const string MemoYesterday = "昨天";

    /// <summary>search.count（zh-CN）</summary>
    public const string SearchCount = "{0} 项{1} · Esc 返回";

    /// <summary>search.countZero（zh-CN）</summary>
    public const string SearchCountZero = "0 项 · Esc 返回";

    /// <summary>search.moreSuffix（zh-CN）</summary>
    public const string SearchMoreSuffix = "（还有 {0} 项）";

    /// <summary>search.groupHit（zh-CN）</summary>
    public const string SearchGroupHit = "组合 · {0} 项";

    /// <summary>set.hotkeyUnset（zh-CN）</summary>
    public const string SetHotkeyUnset = "未设置";

    /// <summary>set.hotkeyCapturing（zh-CN）</summary>
    public const string SetHotkeyCapturing = "按下新组合键…";

    /// <summary>set.hotkeyCaptureHint（zh-CN）</summary>
    public const string SetHotkeyCaptureHint = "请按下新组合键（至少一个修饰键）；若按下后毫无反应，说明它已被其它程序占用。Esc 取消";

    /// <summary>set.hotkeyWinReserved（zh-CN）</summary>
    public const string SetHotkeyWinReserved = "Win 组合由系统保留，请换一个";

    /// <summary>set.hotkeyBadKey（zh-CN）</summary>
    public const string SetHotkeyBadKey = "这个键不能用作热键，请换一个";

    /// <summary>set.hotkeyNeedModifier（zh-CN）</summary>
    public const string SetHotkeyNeedModifier = "需要至少一个修饰键 + 一个普通键";

    /// <summary>set.hotkeyChanged（zh-CN）</summary>
    public const string SetHotkeyChanged = "已改为 {0}";

    /// <summary>set.hotkeyConflict（zh-CN）</summary>
    public const string SetHotkeyConflict = "有组合键冲突";

    /// <summary>set.hotkeyRegFailed（zh-CN）</summary>
    public const string SetHotkeyRegFailed = "注册失败（Win32 {0}）";

    /// <summary>set.autorunOffFailed（zh-CN）</summary>
    public const string SetAutorunOffFailed = "取消开机自启失败：注册表 Run 键写不进去（常见于组策略锁定）";

    /// <summary>set.autorunOnFailed（zh-CN）</summary>
    public const string SetAutorunOnFailed = "设置开机自启失败：注册表 Run 键写不进去（常见于组策略锁定）";

    /// <summary>set.hotkeyActive（zh-CN）</summary>
    public const string SetHotkeyActive = "已生效";

    /// <summary>set.hotkeyTaken（zh-CN）</summary>
    public const string SetHotkeyTaken = "该热键已被其他程序占用，请换一个";

    /// <summary>icon.statusDefault（zh-CN）</summary>
    public const string IconStatusDefault = "共 {0} 个 · 当前「{1}」";

    /// <summary>icon.defaultSystemIcon（zh-CN）</summary>
    public const string IconDefaultSystemIcon = "默认（系统图标）";

    /// <summary>icon.defaultTypeGlyph（zh-CN）</summary>
    public const string IconDefaultTypeGlyph = "默认（类型图形）";

    /// <summary>icon.defaultGroup（zh-CN）</summary>
    public const string IconDefaultGroup = "组合（默认）";

    /// <summary>err.stateUnreadable（zh-CN）</summary>
    public const string ErrStateUnreadable = "state.json 读不出来（{0}）";

    /// <summary>err.usedBackup（zh-CN）</summary>
    public const string ErrUsedBackup = "已改用备份恢复 —— 最近一次保存之后的改动不在里面。";

    /// <summary>err.corruptKept（zh-CN）</summary>
    public const string ErrCorruptKept = "坏掉的主文件另存为 state.json.corrupt-{0}，可以从那里手工救回。";

    /// <summary>err.bothUnreadable（zh-CN）</summary>
    public const string ErrBothUnreadable = "state.json 与备份都无法读取（{0}）";

    /// <summary>err.startedEmpty（zh-CN）</summary>
    public const string ErrStartedEmpty = "已按空面板启动。原文件另存为 state.json.corrupt-{0}，";

    /// <summary>err.recoverHint（zh-CN）</summary>
    public const string ErrRecoverHint = "可以从那里手工救回。";

    /// <summary>err.contentNull（zh-CN）</summary>
    public const string ErrContentNull = "文件内容是 null";

    /// <summary>err.skippedRecords（zh-CN）</summary>
    public const string ErrSkippedRecords = "{0} 里有 {1} 条记录缺路径或标题，已跳过（其余数据保留）";

    /// <summary>err.orphanEntries（zh-CN）</summary>
    public const string ErrOrphanEntries = "{0} 里有 {1} 条条目指向不存在的组合，已退回主界面";

    /// <summary>err.parseFailed（zh-CN）</summary>
    public const string ErrParseFailed = "{0} 解析失败：{1} {2}";

    /// <summary>err.saveFailed（zh-CN）</summary>
    public const string ErrSaveFailed = "state.json 落盘失败（{0}）：{1}";

    /// <summary>err.autorunWriteFailed（zh-CN）</summary>
    public const string ErrAutorunWriteFailed = "开机自启写入失败（enabled={0}）：{1}";

    /// <summary>err.autorunKeptForeign（zh-CN）</summary>
    public const string ErrAutorunKeptForeign = "自启项指向别的副本（{0}），本次没有改动它 —— 要改指向请在设置里拨一次开关";

    /// <summary>palette.accentForest（zh-CN）</summary>
    public const string PaletteAccentForest = "森林绿";

    /// <summary>palette.accentSky（zh-CN）</summary>
    public const string PaletteAccentSky = "天空蓝";

    /// <summary>palette.accentSunset（zh-CN）</summary>
    public const string PaletteAccentSunset = "夕阳橙";

    /// <summary>palette.paperIvory（zh-CN）</summary>
    public const string PalettePaperIvory = "象牙纸";

    /// <summary>palette.paperKraft（zh-CN）</summary>
    public const string PalettePaperKraft = "牛皮纸";

    /// <summary>palette.paperNight（zh-CN）</summary>
    public const string PalettePaperNight = "夜空纸";

    /// <summary>log.appDomainUnhandled（zh-CN）</summary>
    public const string LogAppDomainUnhandled = "AppDomain 未处理异常（进程即将结束）：";

    /// <summary>log.taskUnobserved（zh-CN）</summary>
    public const string LogTaskUnobserved = "未观察的 Task 异常：";

    /// <summary>log.folderReadFailed（zh-CN）</summary>
    public const string LogFolderReadFailed = "读取文件夹失败 {0}：{1}";

    /// <summary>log.ilCreateNull（zh-CN）</summary>
    public const string LogIlCreateNull = "ILCreateFromPathW 返回空";

    /// <summary>log.slotIUnknownNull（zh-CN）</summary>
    public const string LogSlotIUnknownNull = "Slot({0}): IUnknown 指针为空";

    /// <summary>log.slotVtableNull（zh-CN）</summary>
    public const string LogSlotVtableNull = "Slot({0}): vtable 指针为空";

    /// <summary>log.slotVtableShallow（zh-CN）</summary>
    public const string LogSlotVtableShallow = "Slot({0}): 该对象的 vtable 没这么深，槽号有误";

    /// <summary>log.shellItemNull（zh-CN）</summary>
    public const string LogShellItemNull = "SHCreateItemFromParsingName 没给回 IShellItem";

    /// <summary>log.netUnavailable（zh-CN）</summary>
    public const string LogNetUnavailable = "网络位置当前不可用";

    /// <summary>log.netHostUnavailable（zh-CN）</summary>
    public const string LogNetHostUnavailable = "网络位置当前不可用（{0} 近期连不上）";

    /// <summary>log.driveUnavailable（zh-CN）</summary>
    public const string LogDriveUnavailable = "驱动器 {0} 当前不可用";

    /// <summary>log.parentGone（zh-CN）</summary>
    public const string LogParentGone = "上级文件夹已不存在";

    /// <summary>log.badPathFormat（zh-CN）</summary>
    public const string LogBadPathFormat = "路径格式无效";

    /// <summary>log.deletedOrMoved（zh-CN）</summary>
    public const string LogDeletedOrMoved = "已被删除或移动";

    /// <summary>log.reorderLast（zh-CN）</summary>
    public const string LogReorderLast = "最后";

    /// <summary>ui.memoButton（zh-CN）</summary>
    public const string UiMemoButton = "备忘录";

    /// <summary>ui.close（zh-CN）</summary>
    public const string UiClose = "关闭";

    /// <summary>ui.searchPlaceholder（zh-CN）</summary>
    public const string UiSearchPlaceholder = "搜索…";

    /// <summary>ui.searchKindNote（zh-CN）</summary>
    public const string UiSearchKindNote = "备忘";

    /// <summary>ui.searchEmptyTitle（zh-CN）</summary>
    public const string UiSearchEmptyTitle = "未找到，搜索不含文件夹";

    /// <summary>ui.searchEmptySub（zh-CN）</summary>
    public const string UiSearchEmptySub = "能搜到的是：条目 · 组合 · 备忘标题与正文";

    /// <summary>ui.memoNew（zh-CN）</summary>
    public const string UiMemoNew = "新建备忘";

    /// <summary>ui.memoEmptyTitle（zh-CN）</summary>
    public const string UiMemoEmptyTitle = "还没有备忘";

    /// <summary>ui.memoEmptySub（zh-CN）</summary>
    public const string UiMemoEmptySub = "点这里，或右下角的 ＋，写一条";

    /// <summary>ui.memoSaveAndBack（zh-CN）</summary>
    public const string UiMemoSaveAndBack = "保存并返回";

    /// <summary>ui.memoFieldTitle（zh-CN）</summary>
    public const string UiMemoFieldTitle = "标题";

    /// <summary>ui.memoFieldBody（zh-CN）</summary>
    public const string UiMemoFieldBody = "内容";

    /// <summary>ui.memoDeleteThis（zh-CN）</summary>
    public const string UiMemoDeleteThis = "删除这条备忘";

    /// <summary>ui.iconTitle（zh-CN）</summary>
    public const string UiIconTitle = "更改预设图标";

    /// <summary>set.sectionAccent（zh-CN）</summary>
    public const string SetSectionAccent = "主题色";

    /// <summary>set.sectionPaper（zh-CN）</summary>
    public const string SetSectionPaper = "纸张";

    /// <summary>set.sectionSummon（zh-CN）</summary>
    public const string SetSectionSummon = "呼出与停靠";

    /// <summary>set.hotkey（zh-CN）</summary>
    public const string SetHotkey = "呼出快捷键";

    /// <summary>set.anchorFollow（zh-CN）</summary>
    public const string SetAnchorFollow = "出现在鼠标所在屏幕";

    /// <summary>set.anchorRemember（zh-CN）</summary>
    public const string SetAnchorRemember = "关闭则记住上次位置";

    /// <summary>set.sectionSize（zh-CN）</summary>
    public const string SetSectionSize = "尺寸";

    /// <summary>set.iconSize（zh-CN）</summary>
    public const string SetIconSize = "图标大小";

    /// <summary>set.iconSizeHint（zh-CN）</summary>
    public const string SetIconSizeHint = "拉伸只重排列数，不改变图标尺寸";

    /// <summary>set.sizeSmall（zh-CN）</summary>
    public const string SetSizeSmall = "小";

    /// <summary>set.sizeMid（zh-CN）</summary>
    public const string SetSizeMid = "中";

    /// <summary>set.sizeLarge（zh-CN）</summary>
    public const string SetSizeLarge = "大";

    /// <summary>set.sectionBehavior（zh-CN）</summary>
    public const string SetSectionBehavior = "行为";

    /// <summary>set.autorun（zh-CN）</summary>
    public const string SetAutorun = "跟随系统启动";

    /// <summary>set.trayOnClose（zh-CN）</summary>
    public const string SetTrayOnClose = "点 X 收进托盘而非退出";

    /// <summary>set.trayOnCloseHint（zh-CN）</summary>
    public const string SetTrayOnCloseHint = "避免误触把整个面板关掉";

    /// <summary>set.groupBadge（zh-CN）</summary>
    public const string SetGroupBadge = "显示组合收纳角标";

    /// <summary>set.groupBadgeHint（zh-CN）</summary>
    public const string SetGroupBadgeHint = "组合磁贴右上角的成员数";

    /// <summary>set.hideOnFullscreen（zh-CN）</summary>
    public const string SetHideOnFullscreen = "全屏应用时自动收起";

    /// <summary>set.hideOnFullscreenHint（zh-CN）</summary>
    public const string SetHideOnFullscreenHint = "游戏、视频等全屏时面板主动让位";
}
