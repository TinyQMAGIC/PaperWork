using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paperwork.Data;

/// <summary>
/// 用户数据的唯一真相源 + 落盘。
///
/// 只存一个 state.json。窗口尺寸单独放 window.json（见 WindowStateStore），
/// 因为移动/缩放会高频写、而用户数据低频写，混在一起会让每次拖动都重写一遍条目。
/// </summary>
public sealed class StateStore
{
    /// <summary>
    /// v2：主界面的组合与条目共用一个顺序空间（组合栏可以拖到条目之间）。
    /// v1 的数据里两者是独立的两套 Sort，<see cref="NormalizeRootOrder"/> 负责一次性迁移。
    /// </summary>
    public const int CurrentVersion = 2;

    // 走 AppPaths，不要再在这里写死 AppData\Paperwork：
    // 数据目录可能是 exe 同级的 UserData，也可能是回退后的 AppData，由 AppPaths 一处决定。
    private static readonly string StoreDir = AppPaths.DataDir;

    private static readonly string FilePath = Path.Combine(StoreDir, "state.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<string> _loadProblems = new();

    public AppState State { get; private set; } = new();

    public IReadOnlyList<string> LoadProblems => _loadProblems;

    /// <summary>
    /// 最近一次落盘失败的原因，<c>null</c> 表示没失败过（成功一次就清掉）。
    /// 面板在下次呼出时把它显示到状态栏——落盘失败必须让用户看见，
    /// 否则"以为存住了、重启发现全空"是这一层最难解释的一种丢法。
    /// </summary>
    public string? LastSaveFailure { get; private set; }

    // ------------------------------------------------------------------ 读写

    public void Load()
    {
        _loadProblems.Clear();

        var restored = TryRead(FilePath) ?? TryRead(FilePath + ".bak");
        if (restored is null)
        {
            State = new AppState();

            if (File.Exists(FilePath))
            {
                // 现场一定要留住。这时候面板是空的，用户随手拖一个文件进来就会
                // Save() 覆盖掉原文件——那才是真的没法挽回。
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Quarantine(FilePath, stamp);
                Quarantine(FilePath + ".bak", stamp);
                _loadProblems.Add($"state.json 与备份都无法读取，已按空面板启动。" +
                                  $"原文件另存为 state.json.corrupt-{stamp}，可以从那里手工救回。");
            }
            return;
        }

        State = restored;
        if (State.Version < 2)
        {
            NormalizeRootOrder();
            State.Version = 2;
        }
    }

    private static void Quarantine(string path, string stamp)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".corrupt-" + stamp);
        }
        catch (Exception)
        {
            // 挪不动也不能让程序起不来；原文件至少还在原地
        }
    }

    /// <summary>
    /// v1 → v2 的顺序迁移：把独立的两套 Sort（组合 10/20…、条目 0/1…）统一成一个空间，
    /// 按"组合先、条目后"重编——视觉顺序与迁移前完全一致，之后拖到哪儿就落在哪儿。
    /// <b>只对 v1 执行</b>：v2 之后组合可以混排在条目后面，"组合先"这条假设不再成立，
    /// 每次 Load 都跑一遍会把用户的混排结果改回去。
    /// </summary>
    private void NormalizeRootOrder()
    {
        int next = 0;
        foreach (var g in State.Groups.OrderBy(x => x.Sort).ThenBy(x => x.Id).ToList())
            State.Groups[State.Groups.IndexOf(g)] = g with { Sort = next++ };

        foreach (var e in State.Entries.Where(x => x.GroupId == null)
                                            .OrderBy(x => x.Sort).ThenBy(x => x.Id).ToList())
            Replace(e with { Sort = next++ });
    }

    private static AppState? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var parsed = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Json);
            if (parsed is null) return null;
            // 结构校验：宁可当作读失败走备份，也不要带着坏数据继续写下去覆盖掉好文件
            if (parsed.Entries.Any(e => string.IsNullOrWhiteSpace(e.Path))) return null;
            if (parsed.Groups.Any(g => string.IsNullOrWhiteSpace(g.Title))) return null;
            return parsed;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save()
    {
        var tmp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(State, Json));

            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, FilePath + ".bak");
            else
                File.Move(tmp, FilePath);

            LastSaveFailure = null;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }

            // 静默失败是这一层最坏的行为：磁盘满、杀软锁文件、APPDATA 被 OneDrive 同步撞车，
            // 表现都成"我明明加了，重启又没了"。留一行 error.log，并把原因交给面板显示。
            LastSaveFailure = ex.Message;
            App.LogError($"state.json 落盘失败（{FilePath}）：{ex}");
        }
    }

    /// <summary>落盘。调用点自己负责把视图重建出来。</summary>
    private void Touch() => Save();

    // ------------------------------------------------------------------ 查询

    public GroupItem? Group(int id) => State.Groups.FirstOrDefault(g => g.Id == id);

    public EntryItem? Entry(int id) => State.Entries.FirstOrDefault(e => e.Id == id);

    public IEnumerable<EntryItem> EntriesIn(int? groupId) =>
        State.Entries.Where(e => e.GroupId == groupId).OrderBy(e => e.Sort).ThenBy(e => e.Id);

    public IEnumerable<GroupItem> Groups() => State.Groups.OrderBy(g => g.Sort).ThenBy(g => g.Id);

    // ------------------------------------------------------------------ 备忘

    /// <summary>备忘列表：**置顶优先**，其余按最近更新倒序。</summary>
    public IReadOnlyList<NoteItem> Notes() =>
        State.Notes.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.UpdatedAt).ToList();

    public NoteItem? Note(int id) => State.Notes.FirstOrDefault(n => n.Id == id);

    /// <summary>
    /// 备忘改动只写内存，<b>不落盘</b>。
    ///
    /// 备忘是实时保存的（每敲一个键都会走到这里）。如果这里也 <see cref="Touch"/>，
    /// 敲一行字就是几十次「整份序列化 + 原子写三份文件」——正是之前专门修过的写放大。
    /// 落盘交给调用方的去抖计时器（<c>PanelWindow</c> 里 400ms 合并一次）。
    /// </summary>
    private void Replace(NoteItem fresh)
    {
        int i = State.Notes.FindIndex(x => x.Id == fresh.Id);
        if (i >= 0) State.Notes[i] = fresh;
    }

    /// <summary>新建一条空备忘并立即落盘（新建是低频动作，不必去抖）。返回它供调用方打开编辑页。</summary>
    public NoteItem AddNote()
    {
        var note = new NoteItem(NextId(), string.Empty, string.Empty, false, Now());
        State.Notes.Add(note);
        Touch();
        return note;
    }

    /// <summary>
    /// 改标题 / 正文。<b>实时保存走这里</b>。内容完全没变就直接返回，
    /// 免得光标进出输入框也产生一次全量重写。
    /// </summary>
    public void UpdateNote(int id, string? title, string? body)
    {
        var n = Note(id);
        if (n is null) return;

        string t = title ?? string.Empty;
        string b = body ?? string.Empty;
        if (string.Equals(n.Title, t, StringComparison.Ordinal) &&
            string.Equals(n.Body, b, StringComparison.Ordinal)) return;

        Replace(n with { Title = t, Body = b, UpdatedAt = Now() });
    }

    public void SetNotePinned(int id, bool pinned)
    {
        var n = Note(id);
        if (n is null || n.Pinned == pinned) return;
        Replace(n with { Pinned = pinned });
        Touch();
    }

    public void DeleteNote(int id)
    {
        var n = Note(id);
        if (n is null) return;
        State.Notes.Remove(n);
        Touch();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// 下一个可用 id。<b>三份集合共用一个 id 空间</b>。
    ///
    /// 加入备忘是必须的：备忘 id 如果从自己的序列发，迟早会和某个条目/组合撞号，
    /// 而列表里是按 id 反查的 —— 撞号的表现是点 A 打开了 B，且很难复现。
    /// </summary>
    private int NextId()
    {
        int max = 0;
        if (State.Entries.Count > 0) max = Math.Max(max, State.Entries.Max(e => e.Id));
        if (State.Groups.Count > 0) max = Math.Max(max, State.Groups.Max(g => g.Id));
        if (State.Notes.Count > 0) max = Math.Max(max, State.Notes.Max(n => n.Id));
        return max + 1;
    }

    private static int NextSort(Func<int> selector) => selector() + 10;

    /// <summary>
    /// 主界面混排空间里最大的 Sort。新磁贴（拖入的文件 / 新建组合）一律排在整层最后，
    /// 基准必须同时看组合和条目——只看一边的话，新磁贴会插到组合栏前面去。
    /// </summary>
    private int RootMaxSort() => Math.Max(
        State.Groups.Select(g => g.Sort).DefaultIfEmpty(0).Max(),
        State.Entries.Where(e => e.GroupId == null).Select(e => e.Sort).DefaultIfEmpty(0).Max());

    // ------------------------------------------------------------------ 变更

    /// <summary>
    /// 把若干路径加入当前层。同一层里同一路径只留一份——重复拖入是最常见的误操作。
    /// 返回真正新增的条数。
    /// </summary>
    public int AddPaths(IReadOnlyList<string> paths, int? groupId)
    {
        int added = 0;
        int sort = groupId is null
            ? NextSort(RootMaxSort)
            : NextSort(() => State.Entries.Where(e => e.GroupId == groupId).Select(e => e.Sort).DefaultIfEmpty(0).Max());

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (State.Entries.Any(e => e.GroupId == groupId &&
                                       string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            bool isDir = SafeIsDirectory(path);
            State.Entries.Add(new EntryItem(NextId(), groupId, path, Label: null, sort, isDir));
            sort += 10;
            added++;
        }

        if (added > 0) Touch();
        return added;
    }

    /// <summary>用给定的若干条目建一个新组合（两层封顶：新组合一律挂在主界面下）。</summary>
    public GroupItem? CreateGroup(string title, IReadOnlyList<int> entryIds)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var group = new GroupItem(NextId(), title.Trim(), NextSort(RootMaxSort));
        State.Groups.Add(group);

        // 走 AppendToLayer 而不是就地改 GroupId：新组合里也要有一套干净的 0..n-1，
        // 否则条目会把主界面的 Sort 带进组内，和以后往组里追加的条目撞号。
        AppendToLayer(group.Id, entryIds);

        Touch();
        return group;
    }

    public void RenameEntry(int id, string? label)
    {
        var e = Entry(id);
        if (e is null) return;
        Replace(e with { Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() });
        Touch();
    }

    public void RenameGroup(int id, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var g = Group(id);
        if (g is null) return;
        int index = State.Groups.IndexOf(g);
        State.Groups[index] = g with { Title = title.Trim() };
        Touch();
    }

    /// <summary>设置或清除自定义图标（.ico / .exe 路径）。传 null 表示恢复系统图标。</summary>
    public void SetEntryIcon(int id, string? iconPath)
    {
        var e = Entry(id);
        if (e is null || e.Icon == iconPath) return;
        Replace(e with { Icon = string.IsNullOrWhiteSpace(iconPath) ? null : iconPath });
        Touch();
    }

    /// <summary>只从面板移除，绝不碰磁盘上的真实文件。</summary>
    public void RemoveEntry(int id)
    {
        var e = Entry(id);
        if (e is null) return;
        State.Entries.Remove(e);
        Touch();
    }

    public void RemoveGroup(int id)
    {
        var g = Group(id);
        if (g is null) return;
        State.Groups.Remove(g);
        // 组合没了，里面的条目退回主界面而不是跟着一起消失
        AppendToLayer(null, State.Entries.Where(x => x.GroupId == id).Select(x => x.Id).ToList());
        Touch();
    }

    /// <summary>把条目挪进某个组合（groupId 为 null 表示退回主界面）。</summary>
    public void MoveEntry(int entryId, int? groupId)
    {
        var e = Entry(entryId);
        if (e is null || e.GroupId == groupId) return;
        AppendToLayer(groupId, new[] { entryId });
        Touch();
    }

    /// <summary>
    /// 把若干条目搬进某一层（<c>groupId</c> 为 null 即主界面）并<b>接到那一层最后</b>，
    /// 然后整层重编号成 0..n-1。
    ///
    /// 为什么不能只改 <c>GroupId</c> 就走：<c>Sort</c> 是<b>层内</b>序号，主界面和每个组合
    /// 各有一套。条目从组合里退回主界面时带的是组内的 0、1、2，主界面也是 0、1、2，
    /// 两边必然撞；撞了之后顺序落到 <c>BuildRoot</c> 那个"组合先进列表、条目按 (Sort,Id) 排"
    /// 的稳定序上，等于由运气决定。实测删一个组合就把首页从
    /// <c>GRP_A E1 E2 E3</c> 变成 <c>M1 E1 M2 E2 E3</c>——退回的两条抢到前面，
    /// 还插进用户自己排好的序列中间。
    /// </summary>
    private void AppendToLayer(int? groupId, IReadOnlyList<int> entryIds)
    {
        var moving = entryIds.Select(Entry).OfType<EntryItem>()
                             .OrderBy(e => e.Sort).ThenBy(e => e.Id)
                             .ToList();
        if (moving.Count == 0) return;

        var movingIds = new HashSet<int>(moving.Select(e => e.Id));

        // "这一层原来谁在前"必须在改归属<b>之前</b>算，改完再问就把搬进来的算成原有成员了
        List<int> order = groupId is null
            ? RootOrderExcept(movingIds)
            : State.Entries.Where(e => e.GroupId == groupId && !movingIds.Contains(e.Id))
                           .OrderBy(e => e.Sort).ThenBy(e => e.Id)
                           .Select(e => e.Id).ToList();

        foreach (var e in moving) Replace(e with { GroupId = groupId });
        order.AddRange(moving.Select(e => e.Id));

        if (groupId is null) ReorderRoot(order);
        else ReorderLayer(groupId, order);
    }

    /// <summary>
    /// 主界面当前的混排顺序，跳过指定条目。
    /// 并列 Sort 时的先后要和 <c>BuildRoot</c> 一致（组合在前、同类按 Id），
    /// 不然"保持原顺序"这一步本身就会把顺序改掉。
    /// </summary>
    private List<int> RootOrderExcept(HashSet<int> exclude) =>
        State.Groups.Select(g => (Sort: g.Sort, Tie: 0, Id: g.Id))
            .Concat(State.Entries.Where(e => e.GroupId == null && !exclude.Contains(e.Id))
                                 .Select(e => (Sort: e.Sort, Tie: 1, Id: e.Id)))
            .OrderBy(x => x.Sort).ThenBy(x => x.Tie).ThenBy(x => x.Id)
            .Select(x => x.Id).ToList();

    /// <summary>
    /// 整层重编号：按新的显示顺序把这一层的 Sort 写成 0..n-1。
    ///
    /// 为什么不能只改被拖的那一条：<c>EntriesIn</c> 是按 <c>Sort, Id</c> 排的，
    /// 单独把一条改成目标位置的 Sort，会和邻居撞成同一个 Sort 值，下次排序结果就不确定了。
    /// 所以排序必须整层落。
    /// </summary>
    public void ReorderLayer(int? groupId, IReadOnlyList<int> entryIdsInOrder)
    {
        var current = State.Entries
            .Where(e => e.GroupId == groupId)
            .OrderBy(e => e.Sort).ThenBy(e => e.Id)
            .ToList();

        // 只接受"这一层 id 的一个完整排列"。数量对不上或缺 id 一律不改，
        // 免得后台探测/并发写入把条目弄丢。
        if (entryIdsInOrder.Count != current.Count) return;
        if (current.Any(w => !entryIdsInOrder.Contains(w.Id))) return;

        var byId = current.ToDictionary(w => w.Id);
        bool changed = false;
        for (int i = 0; i < entryIdsInOrder.Count; i++)
        {
            var e = byId[entryIdsInOrder[i]];
            if (e.Sort == i) continue;
            Replace(e with { Sort = i });
            changed = true;
        }

        if (changed) Touch();
    }

    /// <summary>
    /// 主界面整层重编号：传入的 id 顺序里组合和条目可以**自由混排**，
    /// 一律写成 0..n-1。id 全局唯一（<see cref="NextId"/> 对组合与条目共用一个计数器），
    /// 所以一个 int 就能定位到是组合还是条目。
    /// 守卫与 <see cref="ReorderLayer"/> 同款：数量对不上或出现陌生 id，整个放弃。
    /// </summary>
    public void ReorderRoot(IReadOnlyList<int> idsInOrder)
    {
        int entryCount = State.Entries.Count(x => x.GroupId == null);
        if (idsInOrder.Count != State.Groups.Count + entryCount) return;
        if (idsInOrder.Count == 0) return;

        int i = 0;
        bool changed = false;
        foreach (int id in idsInOrder)
        {
            var g = State.Groups.FirstOrDefault(x => x.Id == id);
            if (g is not null)
            {
                if (g.Sort != i)
                {
                    State.Groups[State.Groups.IndexOf(g)] = g with { Sort = i };
                    changed = true;
                }
                i++;
                continue;
            }

            var e = Entry(id);
            if (e is null || e.GroupId != null) return;   // 陌生 id / 组内条目混进来了，整个放弃
            if (e.Sort != i)
            {
                Replace(e with { Sort = i });
                changed = true;
            }
            i++;
        }

        if (changed) Touch();
    }

    public void SaveNav(IReadOnlyList<NavFrame> frames)
    {
        // 先看有没有变，变了才去查文件系统。<b>这个顺序是刻意的</b>：
        // 下面那句 LINQ 里含 Directory.Exists，对掉线的网络共享和拔掉的 U 盘能阻塞数秒
        // （见 SafeIsDirectory 的注释）。而 SaveNav 的调用点里有 Render()，也就是
        // 每次热键呼出——把查询放在比较之前，等于每次呼出都在 UI 线程上做一次网络 IO。
        //
        // 代价：只有当导航栈真的变了，才会顺便把已经失效的帧裁掉。停在同一个文件夹上时，
        // 那个文件夹即使被删了，这条已落盘的帧也留着。可接受——文件夹层现在是后台异步加载的
        // （见 BoardBuilder.LoadingFolder），落在失效目录上只会显示「无法读取该文件夹」，不再卡界面。
        if (SameNav(frames, State.Nav)) return;

        // 只存还存在的帧，避免重启后跳进一个已被删掉的组合
        var valid = frames.Where(f => f.Kind switch
        {
            NavKind.Root => true,
            NavKind.Group => Group(f.Id) is not null,
            _ => SafeIsDirectory(f.Path ?? string.Empty)
        }).ToList();

        if (valid.Count == 0) valid.Add(NavFrame.Root);

        // 导航没变就别动盘：Render() 每次呼出都走到这里，而写盘 = 整个 state.json 序列化
        // + 原子写三份文件，纯属白跑，还会在 UI 线程上做目录存在性检查。
        if (SameNav(valid, State.Nav)) return;

        State.Nav = valid;
        Save();
    }

    private static bool SameNav(IReadOnlyList<NavFrame> a, IReadOnlyList<NavFrame> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != b[i].Kind || a[i].Id != b[i].Id) return false;
            if (!string.Equals(a[i].Path, b[i].Path, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void Replace(EntryItem fresh)
    {
        int index = State.Entries.FindIndex(x => x.Id == fresh.Id);
        if (index >= 0) State.Entries[index] = fresh;
    }

    /// <summary>
    /// 判目录。<b>绝不能当成廉价调用放到 UI 线程上批量跑</b>：
    /// 对掉线的网络共享或拔掉的 U 盘，它可能阻塞数秒（README 踩坑合集）。
    /// 这里只在"加入"和"重建可见层"这两种低频场合用。
    /// </summary>
    public static bool SafeIsDirectory(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }
}
