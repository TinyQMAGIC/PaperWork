using System.Collections.Generic;

namespace Paperwork.Data;

/// <summary>导航帧的类型。组合不嵌套，所以栈最长 3 帧：主界面 → 组合 → 组合里的文件夹。</summary>
public enum NavKind { Root, Group, Folder }

/// <summary>
/// 一帧导航。<paramref name="Id"/> 用于 Group，<paramref name="Path"/> 用于 Folder。
/// </summary>
public sealed record NavFrame(NavKind Kind, int Id, string? Path, string Title)
{
    /// <summary>栈底恒为主界面。</summary>
    public static NavFrame Root { get; } = new(NavKind.Root, 0, null, "Paperwork");
}

/// <summary>
/// 两层封顶（D17）的导航栈。Root 恒在底部，最多再压两帧。
///
/// 能走到第三帧的有两条路，不止「主界面 → 组合 → 组合里的文件夹」那一条：
/// 用户把文件夹直接拖到主界面时，主界面上也是一枚文件夹磁贴
/// （<c>FromEntry</c> 里 <c>IsDir → TileKind.Folder</c>），
/// 于是「主界面 → 文件夹 → 文件夹」同样能到栈底。
/// </summary>
public sealed class NavStack
{
    public const int MaxFrames = 3;

    private readonly List<NavFrame> _frames = new() { NavFrame.Root };

    public IReadOnlyList<NavFrame> Frames => _frames;

    public NavFrame Current => _frames[^1];

    public bool IsRoot => _frames.Count == 1;

    /// <summary>当前所在的组合。不在组合内时为 null——决定「加入组合」入口是否出现。</summary>
    public int? CurrentGroupId
    {
        get
        {
            for (int i = _frames.Count - 1; i >= 0; i--)
                if (_frames[i].Kind == NavKind.Group) return _frames[i].Id;
            return null;
        }
    }

    /// <summary>
    /// 已到栈底。此时再往下钻不可能成功，调用方应该改走别的交互
    /// （文件夹改成交给资源管理器打开），而不是让用户面对一个没反应的点击。
    /// </summary>
    public bool AtMaxDepth => _frames.Count >= MaxFrames;

    /// <summary>
    /// 压一帧。<b>返回 false 表示没压进去</b>：已到栈底，或者和当前帧重复。
    ///
    /// 为什么必须有返回值：调用方（<c>Activate</c>）原来不接收返回值，压不进去的时候
    /// 照样 <c>Render(animate: true)</c>，于是界面闪一下又回到原样 —— 用户只看到
    /// "点了没反应"，而且没有任何提示。这类静默失败是最难排查的一类交互 bug。
    /// </summary>
    public bool Push(NavFrame frame)
    {
        if (_frames.Count >= MaxFrames) return false;
        // 同一帧重复进入没有意义，会把栈拉长成 主界面 → A → A
        if (_frames[^1] == frame) return false;
        _frames.Add(frame);
        return true;
    }

    /// <summary>弹一帧。点四周空白、Esc、Alt+←、返回按钮走的都是这里。</summary>
    public bool Pop()
    {
        if (_frames.Count <= 1) return false;
        _frames.RemoveAt(_frames.Count - 1);
        return true;
    }

    /// <summary>回到指定索引（面包屑点击用）。索引 0 即主界面。</summary>
    public void PopTo(int index)
    {
        if (index < 0 || index >= _frames.Count) return;
        _frames.RemoveRange(index + 1, _frames.Count - index - 1);
    }

    public void Reset()
    {
        _frames.Clear();
        _frames.Add(NavFrame.Root);
    }

    public void ReplaceWith(IReadOnlyList<NavFrame> frames)
    {
        _frames.Clear();
        _frames.AddRange(frames);
        if (_frames.Count == 0) _frames.Add(NavFrame.Root);
        while (_frames.Count > MaxFrames) _frames.RemoveAt(_frames.Count - 1);
    }
}

/// <summary>一个条目。任意文件都统一成这个形状（D11），类型只在"是目录"时显式记一笔。</summary>
public sealed record EntryItem(
    int Id,
    int? GroupId,
    string Path,
    string? Label,
    int Sort,
    bool IsDir,
    string? Icon = null);

/// <summary>一个组合栏。没有 parent 字段——两层封顶（D17）。</summary>
public sealed record GroupItem(int Id, string Title, int Sort);

/// <summary>
/// 一条备忘。**备忘是数据，不是文件**，所以不能塞进 <see cref="EntryItem"/>：
/// 那条链以 <c>Path</c> 为主键，失效检测、拖放去重、图标队列全都建立在
/// "这是个真实路径"之上，伪造路径会让它们全部误判。
///
/// 独立成一份平行集合反而省事：备忘磁贴的 Path 恒为空，
/// <c>IconPump.Enqueue</c> 里那句 <c>IsNullOrWhiteSpace(tile.Path)</c> 会自动跳过它，
/// 于是备忘天然不会进图标队列、不会被判失效、不会查 Shell —— 零额外分支。
/// </summary>
public sealed record NoteItem(int Id, string Title, string Body, bool Pinned, long UpdatedAt);

/// <summary>呼出锚点策略。</summary>
public enum SummonAnchor { FollowCursor, RememberedPosition }

/// <summary>持久化的外观与行为。字段名即 state.json 里的 camelCase 键。</summary>
public sealed class AppSettings
{
    public string Accent { get; set; } = "forest";
    public string Paper { get; set; } = "ivory";
    /// <summary>32 / 42 / 56</summary>
    public int IconSize { get; set; } = 42;
    public SummonAnchor Anchor { get; set; } = SummonAnchor.FollowCursor;
    public bool Autorun { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    /// <summary>组合磁贴右上角的"收纳了几项"角标。<c>false</c> 时只画磁贴不画数。</summary>
    public bool ShowGroupBadge { get; set; } = true;
    /// <summary>人类可读形式，如 "Ctrl+Alt+D"。实际注册用启动时解析的结果。</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+D";
}

public sealed class AppState
{
    public int Version { get; set; } = StateStore.CurrentVersion;

    public List<GroupItem> Groups { get; set; } = new();
    public List<EntryItem> Entries { get; set; } = new();

    /// <summary>备忘。与 Groups / Entries 平行的一份集合，不参与组合收纳与拖放排序。</summary>
    public List<NoteItem> Notes { get; set; } = new();

    public AppSettings Settings { get; set; } = new();

    /// <summary>导航栈，重启后回到上次位置。</summary>
    public List<NavFrame> Nav { get; set; } = new();
}
