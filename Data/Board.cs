using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Paperwork.Data;

public enum TileKind { Shortcut, Folder, Group, More, Ghost }

/// <summary>
/// 一个磁贴的视图模型。图标、显示名、失效状态都由后台探测异步填，
/// 所以这几个属性要通知 UI。
/// </summary>
public sealed class TileVm : INotifyPropertyChanged
{
    public int? EntryId { get; init; }
    public int? GroupId { get; init; }
    public TileKind Kind { get; init; }
    public string Path { get; init; } = string.Empty;

    /// <summary>角标：组合的成员数 / 文件夹的条目数。0 表示不显示。</summary>
    public int Count { get; init; }

    /// <summary>用户改过名就不再被 Shell 显示名覆盖。</summary>
    public bool HasCustomLabel { get; init; }

    /// <summary>用户指定的自定义图标文件（.ico / .exe）。M2 的"改图标"。</summary>
    public string? CustomIcon { get; init; }

    /// <summary>M1 的占位字形（扩展名大写）。拿到真实图标后自动让位。</summary>
    public string Glyph { get; init; } = string.Empty;

    public bool IsFolderLike => Kind is TileKind.Folder or TileKind.Group;

    // 给 XAML 模板用的可见性开关，省掉一堆自定义转换器
    public bool IsGroupTile => Kind == TileKind.Group;
    public bool IsMoreTile => Kind == TileKind.More;
    public bool IsFolderTile => Kind == TileKind.Folder;

    /// <summary>拖放占位格：只画一圈虚线，没有路径、没有文字、不参与任何交互。</summary>
    public bool IsGhostTile => Kind == TileKind.Ghost;

    /// <summary>
    /// 这张磁贴要不要<b>双击</b>才执行（单击只选中、不做事）。
    ///
    /// 2026-10-01 起统一成一条：<b>会打开 / 进入某个东西的动作一律双击</b> ——
    /// 文件、快捷方式、文件夹，以及「在资源管理器中打开」那个收尾格。
    /// 只有<b>组合</b>保持单击（展开是可逆的导航动作，误触代价小）。
    ///
    /// 判定写在模型上、不写在各个鼠标处理器里：入口有三处（首页 <c>OnMouseUp</c> /
    /// <c>OnDoubleClick</c>、搜索结果的单击 / 双击），口径必须只有一份 ——
    /// 否则改一处就会漏一处，而漏掉的那处恰好是"手一滑就打开一堆程序"。
    /// </summary>
    public bool NeedsDoubleClick => Kind is TileKind.Shortcut or TileKind.Folder or TileKind.More;

    public bool HasCount => Count > 0;

    /// <summary>
    /// 字形文字的唯一来源：收尾格固定是 →，其余条目用扩展名占位字形；
    /// 真实 Shell 图标到了就让位（组合格永远不走字形，它显示 2×2 迷你格）。
    /// </summary>
    private string GlyphOrNone => IsMoreTile ? "→" : _icon is null ? Glyph : string.Empty;

    /// <summary>
    /// 扩展名文字字形。**有预设图形就不再显示它**（<see cref="GlyphData"/> 优先），
    /// 现在只剩"图形解析不出来"这一种兜底场景。
    /// </summary>
    public bool HasGlyphText => GlyphData is null && GlyphOrNone.Length > 0;

    public string DisplayGlyph => GlyphOrNone;

    /// <summary>悬停提示：正常显示名字，失效时多一行原因。</summary>
    public string Tip => IsDead ? $"{Label}\n{(string.IsNullOrEmpty(DeadReason) ? "路径不可用" : DeadReason)}" : Label;

    private string _label = string.Empty;
    /// <summary>先显示从路径推导的名字，Shell 显示名到了再替换。</summary>
    public string Label
    {
        get => _label;
        set { if (_label == value) return; _label = value; OnChanged(); }
    }

    /// <summary>
    /// <b>没有自定义名时本该显示的名字</b> —— 也就是 Shell 显示名（IconPump 查到后写进来）。
    /// 它<b>不参与显示</b>，只用来回答一个问题："用户敲的是不是就等于系统本来就给的那个名字？"
    ///
    /// 为什么不能拿 <see cref="Label"/> 当那个基准：条目一旦改过名，<c>Label</c> 就是用户自己的字，
    /// 拿它当基准会让"再打开一次、原样点确定"悄悄退化成恢复原名。
    /// 为什么不能拿 <c>BoardBuilder.DeriveName</c>：它把扩展名剥掉了（steam.exe → steam），
    /// 于是"在重命名里把 .exe 删掉"敲出来的 steam 会被误判成"就是默认" → 自定义标记被清掉 →
    /// IconPump 立刻用 Shell 显示名把 .exe 又写回来（2026-09-30 修的那个 BUG）。
    ///
    /// Shell 还没查到时（刚拖进来那一瞬）为 null，那时回落到推导名。
    /// </summary>
    public string? DefaultLabel { get; set; }

    /// <summary>
    /// 预设图形（UI 手册 §6 的图标族，<see cref="Glyphs"/> 生成自 design/ui-mock.html）。
    /// 按 Kind 与文件类型取，**不查 Shell**——所以拖进来立刻就有形状，不用等图标解码。
    /// </summary>
    public Geometry? GlyphData => _glyphData ??= Kind switch
    {
        TileKind.Group => null,                                  // 组合走 2x2 迷你格
        TileKind.Ghost => Glyphs.Drop,
        TileKind.More => Glyphs.More,
        _ => Glyphs.ForPath(Path, Kind == TileKind.Folder)
    };
    private Geometry? _glyphData;

    /// <summary>有预设图形、且还没被真图标顶掉时显示它。</summary>
    public bool HasGlyphData => GlyphData is not null && _icon is null;

    /// <summary>
    /// 要不要解真 Shell 图标。**只给两类**：程序/快捷方式（VS Code / 微信 / Steam 这类图标的
    /// 辨识度就是内容本身）与用户显式「改图标」指定的；其余文件类一律预设线稿——
    /// 否则观感被各软件的图标带走，UI 手册 §0 的第 3 条硬约束就破了。
    /// </summary>
    public bool WantsShellIcon => CustomIcon is not null || Glyphs.IsProgram(Path);

    private bool _isDead;
    public bool IsDead
    {
        get => _isDead;
        set { if (_isDead == value) return; _isDead = value; OnChanged(); OnChanged(nameof(HasReason)); OnChanged(nameof(Tip)); }
    }

    private string? _deadReason;
    /// <summary>失效原因，悬停时显示。</summary>
    public string? DeadReason
    {
        get => _deadReason;
        set { if (_deadReason == value) return; _deadReason = value; OnChanged(); OnChanged(nameof(HasReason)); }
    }

    public bool HasReason => IsDead && !string.IsNullOrEmpty(_deadReason);

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnChanged();
            OnChanged(nameof(HasIcon));
            OnChanged(nameof(HasGlyphData));
            OnChanged(nameof(HasGlyphText));
            OnChanged(nameof(DisplayGlyph));
        }
    }

    public bool HasIcon => _icon is not null;

    // 原先这里有个 Highlighted（搜索命中时给整块磁贴换底色）。
    // 搜索改成全局之后不再按查询过滤磁贴，它没人用了 —— 直接删，不留死代码。
    // 片段级高亮由 SearchHit 的 Prefix / Match / Suffix 三段承担，那是另一回事。

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; OnChanged(); }
    }

    private bool _dragging;
    /// <summary>正在被拖的那一块。拖拽排序期间半透明显示。</summary>
    public bool Dragging
    {
        get => _dragging;
        set { if (_dragging == value) return; _dragging = value; OnChanged(); }
    }

    private bool _dropHint;
    /// <summary>作为"松手就把条目并进本组合"的落点提示。</summary>
    public bool DropHint
    {
        get => _dropHint;
        set { if (_dropHint == value) return; _dropHint = value; OnChanged(); }
    }

    private bool _mergeHint;
    /// <summary>
    /// 作为"松手就和拖拽项并成一个新组合"的落点提示。
    /// 与 <see cref="DropHint"/> 的区别是语义：那个是"进已有的组合"，这个是"新建一个组合"。
    /// </summary>
    public bool MergeHint
    {
        get => _mergeHint;
        set { if (_mergeHint == value) return; _mergeHint = value; OnChanged(); }
    }



    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一屏的内容：标题、副标题、磁贴，以及"当前能不能往这里塞组合"。</summary>
public sealed record Board(
    string Title,
    string Subtitle,
    NavKind Kind,
    int? GroupId,
    string? FolderPath,
    IReadOnlyList<TileVm> Tiles)
{
    /// <summary>两层封顶：已经在组合里了，就不允许再建/再塞组合。</summary>
    public bool CanCreateGroup => Kind != NavKind.Group;
}

public static class BoardBuilder
{
    /// <summary>文件夹展开默认显示前 30 项（D8）。</summary>
    public const int FolderShowLimit = 30;

    /// <summary>数"还有多少项"时的上限，避免为了一个角标去遍历十万文件的目录。</summary>
    private const int CountCap = 500;

    /// <summary>文件夹层读取中的占位副标题。</summary>
    public const string FolderLoadingText = "正在读取该文件夹…";

    /// <summary>
    /// 文件夹层的空壳：<b>不做任何 IO</b>，只给出标题 / 路径，磁贴列表为空。
    ///
    /// 之所以需要它：<see cref="StateStore.SafeIsDirectory"/> 的注释已经写明，对掉线的网络共享
    /// 和未下载的 OneDrive 占位文件，<c>Directory.Exists</c> 与目录枚举能阻塞数十秒。
    /// 而 <c>Render</c> 的调用点里包含每次呼出，那时候同步读盘会直接把 UI 冻住。
    /// 于是 Render 先拿这个空壳把界面铺好，真内容由调用方<b>在后台线程</b>跑
    /// <see cref="BuildFolder"/> 之后回投 UI 线程补上。
    /// </summary>
    public static Board LoadingFolder(NavFrame frame) =>
        new(frame.Title, FolderLoadingText, NavKind.Folder, null, frame.Path, Array.Empty<TileVm>());

    public static Board Build(StateStore store, NavFrame frame)
    {
        return frame.Kind switch
        {
            NavKind.Group => BuildGroup(store, frame),
            // 只铺空壳；真内容后台补，见 LoadingFolder 的说明
            NavKind.Folder => LoadingFolder(frame),
            _ => BuildRoot(store)
        };
    }

    private static Board BuildRoot(StateStore store)
    {
        // 组合与条目**按 Sort 混排**：组合栏可以拖到条目之间（M4-P5）。
        // Sort 的编号在 StateStore.NormalizeRootOrder 里统一成 0..n-1，
        // 所以这里不需要"组合区在前、条目区在后"的特判。
        var seq = new List<(int Sort, TileVm Tile)>();

        // 角标在设置里可以关掉。TileVm 的契约就是"Count=0 表示不画角标"，
        // 所以关掉等于把真实数量喂成 0，不用另开一个可见性开关。
        bool badge = store.State.Settings.ShowGroupBadge;

        foreach (var g in store.Groups())
        {
            seq.Add((g.Sort, new TileVm
            {
                GroupId = g.Id,
                Kind = TileKind.Group,
                Path = string.Empty,
                Label = g.Title,
                Count = badge ? store.EntriesIn(g.Id).Count() : 0
            }));
        }

        foreach (var e in store.EntriesIn(null))
            seq.Add((e.Sort, FromEntry(e)));

        var tiles = seq.OrderBy(x => x.Sort).Select(x => x.Tile).ToList();
        return new Board("Paperwork", RootSubtitle(store), NavKind.Root, null, null, tiles);
    }

    private static string RootSubtitle(StateStore store)
    {
        int groups = store.State.Groups.Count;
        int entries = store.State.Entries.Count;
        return groups == 0 && entries == 0
            ? "把任意文件拖进来"
            : $"{entries} 项 · {groups} 个组合";
    }

    private static Board BuildGroup(StateStore store, NavFrame frame)
    {
        var g = store.Group(frame.Id);
        string title = g?.Title ?? "组合";
        var tiles = store.EntriesIn(frame.Id).Select(FromEntry).ToList();
        return new Board(title, $"{tiles.Count} 项 · 拖到此处即加入本组合", NavKind.Group, frame.Id, null, tiles);
    }

    /// <summary>
    /// 枚举一个文件夹。<b>必须在后台线程调用</b>——可能因为网络路径超时阻塞数十秒。
    ///
    /// 这里构造的 <see cref="TileVm"/> 还不会被 WPF 触碰：<c>GlyphData</c> 是惰性属性，
    /// 要等 UI 线程绑上去才现读 <see cref="Glyphs"/> 的静态实例，所以静态初始化仍旧发生在
    /// UI 线程上，不会让那些 Geometry 被后台线程"认领"（Freezable 有线程亲和性）。
    /// </summary>
    public static Board BuildFolder(NavFrame frame)
    {
        var path = frame.Path ?? string.Empty;
        var tiles = new List<TileVm>();
        string subtitle;

        try
        {
            var dir = new DirectoryInfo(path);
            int total = 0;
            var shown = new List<FileSystemInfo>();

            // 只数到 CountCap，不全量遍历：一个有几万文件的目录会把 UI 卡住
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                if ((item.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                total++;
                if (shown.Count < FolderShowLimit) shown.Add(item);
                else if (total >= CountCap) break;
            }

            foreach (var item in shown
                         .OrderByDescending(i => i is DirectoryInfo)
                         .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                bool isDir = item is DirectoryInfo;
                tiles.Add(new TileVm
                {
                    Kind = isDir ? TileKind.Folder : TileKind.Shortcut,
                    Path = item.FullName,
                    Label = isDir ? item.Name : Path.GetFileNameWithoutExtension(item.Name),
                    Glyph = isDir ? string.Empty : (Path.GetExtension(item.Name).TrimStart('-').ToUpperInvariant()),
                    Count = 0
                });
            }

            subtitle = total > tiles.Count
                ? $"共 {(total >= CountCap ? $"{CountCap}+" : total.ToString())} 项 · 已显示前 {tiles.Count} 项"
                : $"{tiles.Count} 项";

            if (total > tiles.Count)
                tiles.Add(new TileVm { Kind = TileKind.More, Path = path, Label = "在资源管理器中打开" });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            subtitle = "无法读取该文件夹";
        }

        return new Board(frame.Title, subtitle, NavKind.Folder, null, path, tiles);
    }

    private static TileVm FromEntry(EntryItem e)
    {
        string name = e.Label ?? DeriveName(e.Path, e.IsDir);
        return new TileVm
        {
            EntryId = e.Id,
            Kind = e.IsDir ? TileKind.Folder : TileKind.Shortcut,
            Path = e.Path,
            Label = name,
            HasCustomLabel = e.Label is not null,
            CustomIcon = e.Icon,
            Glyph = e.IsDir ? string.Empty : Path.GetExtension(e.Path).TrimStart('-').ToUpperInvariant()
        };
    }

    /// <summary>从路径推导显示名。internal：重命名对话框要用同一套推导（面板显示什么就预填什么）。</summary>
    internal static string DeriveName(string path, bool isDir)
    {
        if (isDir)
        {
            var trimmed = path.TrimEnd('\\', '/');
            int slash = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
        }
        return Path.GetFileNameWithoutExtension(path);
    }
}
