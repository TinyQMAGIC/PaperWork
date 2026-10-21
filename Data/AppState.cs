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

    /// <summary>
    /// 栈内容真的变过一次就 +1。只给双击守卫用：判断"第一下单击之后有没有换层"。
    /// 不用 Render 次数，是因为目录监视器、改名、换主题也会 Render，那些不该吞掉正常双击。
    /// </summary>
    public int Version { get; private set; }

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
        Version++;
        return true;
    }

    /// <summary>弹一帧。点四周空白、Esc、Backspace、返回按钮走的都是这里。</summary>
    public bool Pop()
    {
        if (_frames.Count <= 1) return false;
        _frames.RemoveAt(_frames.Count - 1);
        Version++;
        return true;
    }

    /// <summary>回到指定索引（面包屑点击用）。索引 0 即主界面。</summary>
    public void PopTo(int index)
    {
        if (index < 0 || index >= _frames.Count) return;
        int remove = _frames.Count - index - 1;
        if (remove <= 0) return;
        _frames.RemoveRange(index + 1, remove);
        Version++;
    }

    public void Reset()
    {
        _frames.Clear();
        _frames.Add(NavFrame.Root);
        Version++;
    }

    public void ReplaceWith(IReadOnlyList<NavFrame> frames)
    {
        _frames.Clear();
        _frames.AddRange(frames);
        if (_frames.Count == 0) _frames.Add(NavFrame.Root);
        while (_frames.Count > MaxFrames) _frames.RemoveAt(_frames.Count - 1);
        Version++;
    }
}

/// <summary>
/// 一个条目。任意文件都统一成这个形状（D11），类型只在"是目录"时显式记一笔。
///
/// <b>图标是三层，从上往下（2026-10-14）：</b>
/// <list type="number">
///   <item><see cref="PresetIcon"/> —— 用户从预设图标族（<see cref="Glyphs.Group"/>，60 个）里挑的 key，
///         <b>盖住下面两层</b>：程序/快捷方式的真 Shell 图标也会被它盖住（用户显式指定，不算"抢"）；</item>
///   <item><see cref="Icon"/> —— 用户「更改图标…」挑的图标文件（.ico/.exe）路径；</item>
///   <item>磁贴自己的图标 —— 程序/快捷方式 = 真 Shell 图标，文件 = 类型线稿，文件夹 = 文件夹线稿。</item>
/// </list>
/// 「恢复默认图标」<b>一次把上面两层都清掉</b>回到第 3 层，不做两套逻辑 ——
/// 所以第 3 层不需要存：真图标由 <c>IconPump</c> 现取，线稿由扩展名映射，全是派生值。
/// </summary>
/// <param name="Icon">自定义图标文件的<b>路径</b>（不是 key）。null = 没改过。</param>
/// <param name="PresetIcon">预设图标的 <b>key</b>（例如 <c>"game"</c> / <c>"toolbox"</c>）。null = 没选过。</param>
public sealed record EntryItem(
    int Id,
    int? GroupId,
    string Path,
    string? Label,
    int Sort,
    bool IsDir,
    string? Icon = null,
    string? PresetIcon = null);

/// <summary>
/// 一个组合栏。没有 parent 字段——两层封顶（D17）。
///
/// <paramref name="Icon"/> 是**组合磁贴选的分类图标 key**（<see cref="Glyphs.Group"/> 里的键，
/// 例如 <c>"game"</c> / <c>"device"</c>）。null = 没选过，磁贴还是那个 2×2 迷你格。
/// 存 key 不存图形：key 是稳定标识，改画（换路径）不影响老数据；认不出来的 key 会让磁贴落回 2×2，
/// 不会变成空框（见 <see cref="Glyphs.Group.Find"/>）。
/// </summary>
public sealed record GroupItem(int Id, string Title, int Sort, string? Icon = null);

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
/// <summary>
/// 持久化的外观与行为。字段名即 state.json 里的 camelCase 键。
///
/// <b>下面这批初始值 = 「首次启动」的默认值</b>：只有<b>没有 state.json</b> 时才会用到
/// （<see cref="StateStore"/> 读不出来就 <c>new AppState()</c>）。存过盘的用户一律用文件里的值，
/// 改这些初始值<b>不会影响已有用户</b> —— 要看到新默认值，得是全新安装（或把 state.json 挪走）。
/// </summary>
public sealed class AppSettings
{
    public string Accent { get; set; } = "forest";
    /// <summary>牛皮纸（2026-10-17 起；原象牙）。配森林绿就是默认的那一套。</summary>
    public string Paper { get; set; } = "kraft";
    /// <summary>32 / 42 / 56</summary>
    public int IconSize { get; set; } = 42;
    public SummonAnchor Anchor { get; set; } = SummonAnchor.FollowCursor;
    public bool Autorun { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    /// <summary>组合磁贴右上角的"收纳了几项"角标。<c>false</c> 时只画磁贴不画数。</summary>
    public bool ShowGroupBadge { get; set; } = true;

    /// <summary>
    /// 前台进入「铺满整块显示器」的应用时，是否自动收起面板（D28）。
    /// <b>2026-10-17 起默认改为「开」</b>：用户拍板"行为那一段四项全部默认打开"，
    /// 而另外三项（跟随系统启动 / 点 X 收托盘 / 组合角标）本来就是 true —— 实际改的就是这一条。
    ///
    /// ⚠️ 这是<b>对 D28 的反向决定</b>：D28 当初定默认关，理由是全屏的 IDE / 远程桌面等
    /// 「工作型全屏」会被误伤（面板被自作主张藏起来打断工作）。代价与理由见 README
    /// 「首次启动的默认值」那条，需要的人仍然可以在设置里关掉。
    ///
    /// ⚠️ 已知的边界（做法 1 的固有代价）：一份<b>很旧</b>的 state.json（这个字段还没出现时的版本）
    /// 缺字段时会反序列化落到初始值，那位用户的此项会被静默打开。近几个版本存的文件都带这个字段，
    /// 风险只落在考古数据上 —— 已评估并接受，如要杜绝得改成"首次运行专用工厂"（多维护一套默认值）。
    /// </summary>
    public bool HideOnFullscreen { get; set; } = true;
    /// <summary>
    /// 人类可读形式，如 "Alt+Space"。实际注册用启动时解析的结果。
    /// <b>2026-10-17 起默认 Alt+Space</b>（原 Ctrl+Alt+D）。实机验过（见 HotkeyChord.Default）：
    /// 能注册成功，且前台是别的程序时按下能收到按键、那个程序不会弹它的系统菜单。
    /// </summary>
    public string Hotkey { get; set; } = "Alt+Space";
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
