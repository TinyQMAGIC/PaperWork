using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Paperwork.Data;

namespace Paperwork.Shell;

/// <summary>
/// 磁贴拖拽排序 + 相碰合并 + 拖进已有组合（M4）。
///
/// <b>故意不走 <c>DragDrop.DoDragDrop</c>。</b>那个 API 会开一个模态消息循环，
/// 在循环里改数据模型触发实时重排，会和 <see cref="PanScroll"/> 的速度采样、
/// 图标异步回填互相抢，掉帧和高亮错乱都不好治。这里全程是普通鼠标事件 +
/// <c>CaptureMouse</c>，什么时候算拖、拖到哪，判定权完全在我们手里。
///
/// 三条边界：
/// <list type="bullet">
///   <item>阈值沿用系统的 <c>MinimumHorizontalDragDistance</c> / <c>MinimumVerticalDragDistance</c>，
///         和 <see cref="PanScroll"/> 用的是同一套，所以"单击展开"和"拖动排序"不会打架：
///         没超过阈值就是一次普通抬手，照常走 <c>OnMouseUp</c> 的单击语义。</item>
///   <item>预览阶段<b>只动视图、不写盘</b>。<c>StateStore.Touch()</c> 是同步落盘的，
///         每跨一个磁贴就写一次 state.json 既浪费，也会把 <c>.bak</c> 刷成一串中间态。
///         松手时才一次性 <c>ReorderLayer</c>。</item>
///   <item>预览走 <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> 的
///         <c>Move</c>，不换 <c>ItemsSource</c>、不调 <c>Render</c>。换实例会让 WPF 整体重置容器：
///         图标白重建一遍，实测之后 UIA 再也读不到磁贴（屏幕阅读器同理）。</item>
/// </list>
///
/// 落点分三种，按"光标压住了什么"判定，全靠几何、没有计时器：
/// <list type="number">
///   <item>条目压到<b>组合磁贴的图标中心</b>（与相碰合并同一个判定圆）→ 并入这个已有组合
///         （<see cref="TileVm.DropHint"/>）；压到组合磁贴的两侧就是普通的插前/插后——
///         用户拍板："拖到组合上"要和别的磁贴一套手感，不能整块磁贴都是并入区。
///         组合栏自己被拖到任何地方都<b>不并入</b>（组合不是收纳的源，D17）。</item>
///   <item>压到<b>另一块磁贴的图标中心</b>（中心圆半径 = <b>图标边长</b> × <see cref="MergeRadiusRatio"/>，
///         <b>不是</b>磁贴列宽，理由见 <see cref="MergeRadiusRatio"/>）
///         → 两个图标叠到一起，松手新建一个组合把两块收进去（<see cref="TileVm.MergeHint"/>）。
///         只在主界面成立，见 <see cref="MergeAllowedHere"/>；组合栏永远不当合并的源（D17）。</item>
///   <item>其余位置 → 普通插入。光标在目标<b>左半</b>插到它前面、<b>右半</b>插到它后面，
///         中间那条缝就是用户要的"前后判定区"。</item>
/// </list>
///
/// 主界面的组合与条目<b>自由混排</b>：组合栏可以拖进条目之间，Sort 是一个共享的序号空间——
/// <c>StateStore.NormalizeRootOrder</c> 负责把老数据迁过来，<see cref="StateStore.ReorderRoot"/> 落盘。
/// 落点指示就是"磁贴自己在实时挪动"：试过在缝隙里画竖杠，实截之后被否掉了。
/// </summary>
public partial class PanelWindow
{
    /// <summary>
    /// "两个图标叠到一起"的判定半径 = <b>图标边长</b> × 这个比例。
    /// 0.62 让中心圆刚好罩住图标本身、又够不着下面的标签——
    /// 于是"压住图标"和"压在名字上"是两个动作，不会互相误触。
    /// 这是本功能唯一的旋钮：调大→更容易合并、更难插入。
    ///
    /// <b>注意不要拿磁贴（列）宽度算。</b>列宽是 <c>图标 × 2.05 + 16</c>，
    /// 在 56px 图标下约 131px，乘 0.34 出来的圆比磁贴本身还高，会连标签一起吃掉。
    /// </summary>
    private const double MergeRadiusRatio = 0.62;

    /// <summary>半径下限（DIP）。小图标磁贴算出来的圆会小到瞄不准。</summary>
    private const double MergeRadiusFloor = 18;

    /// <summary>
    /// <b>退出半径 = 进入半径 × 这个倍数</b>（A 方案：迟滞）。
    ///
    /// 进入用 <see cref="MergeRadiusRatio"/>、**离开用更大的这个**，中间那条环带里什么都不做
    /// —— 既不吸附也不重排。没有它的话，光标停在判定圆边界上、手抖 1px，
    /// 判定就一秒翻十几次；而每翻一次都要改列表顺序，列表一动磁贴就动，
    /// 磁贴恰恰是下一次判定的输入 —— 自激振荡，看上去就是"抽搐"。
    ///
    /// <b>带宽只要吞得掉手抖就够，多出来的全是白送的"吸力"。</b>手静止时的抖动约 ±1.5px，
    /// 判定侧还有 <see cref="DecideDeadZone"/> 挡着不足 3px 的位移 —— 合起来 3px 左右的带就够稳。
    /// 1.12 是"进 26px / 出 29px"（42 档）：脱身只要多挪 3px。
    ///
    /// 这个数被调过一次：最初写 1.3（出 34px，脱身要 7.8px），用户反馈<b>吸进去以后拽不出来</b> ——
    /// 那是需求的 2.6 倍带宽，多出来的 5px 纯粹让人以为"被粘住了"。
    /// </summary>
    private const double MergeExitRatio = 1.12;

    /// <summary>
    /// 判定死区（DIP，C 方案）：光标位移不足这么多就不重算判定。
    /// 真实鼠标静止时也在抖 1~2px，那一层抖动够不着判定圆，却足够让"每帧重算"白忙一遍。
    /// </summary>
    private const double DecideDeadZone = 3.0;

    /// <summary>
    /// 插入判定的死带（DIP，方案 2）：光标停在目标磁贴<b>中线附近</b>时，±这么多像素内
    /// 不改变"插前 / 插后"的结论。
    ///
    /// 插入的唯一判据是"光标在目标的左半还是右半"，也就是说分界线就在格子正中间 ——
    /// 光标压在中线上抖 1px，结论就翻一次，被拖的那块在脚下来回跳。
    /// 死带里沿用上一次的结论，跟吸附那边是同一套思想。
    /// </summary>
    private const double InsertDeadZone = 4.0;

    /// <summary>新组合的默认名。用户拍板：不拿文件名当组合名，一律「未命名」。</summary>
    private const string NewGroupName = "未命名";

    private TileVm? _dragTile;
    private List<TileVm>? _dragOrder;
    private Point _dragStart;
    private bool _dragActive;

    /// <summary>这次拖的是组合栏本身。组合不当合并的源（两层封顶，D17）。</summary>
    private bool _dragIsGroup;
    private TileVm? _groupHint;
    private TileVm? _mergeHint;
    private DragGhost? _ghost;

    /// <summary>
    /// 当前被"锁定"的落点目标（B 方案）。命中一次就锁住，直到光标<b>明确</b>离开
    /// <see cref="MergeExitRatio"/> 的退出半径才释放 —— 判定不再每个鼠标事件重来一遍。
    /// </summary>
    private TileVm? _hintTarget;

    /// <summary>上一次做判定时光标在哪（死区用）。<see cref="_decided"/> 为 false 表示还没定过。</summary>
    private Point _lastDecide;
    private bool _decided;

    /// <summary>
    /// 上一次插入判定的<b>槽位</b>与结论（死带用）。槽位一换就重新判。
    /// 旧版存的是 <c>TileVm</c> 目标 —— 改成槽位是整个改动的一部分：判定不再依赖"压住了哪一块"。
    /// </summary>
    private int _insertSlot = -1;
    private bool _insertAfter;

    /// <summary>按下时有没有落在磁贴上。与 <see cref="_dragTile"/> 不同：它不考虑"这块能不能拖"。</summary>
    private bool _pressOnTile;

    /// <summary>刚用 Esc 取消过一次拖拽，或刚刚拖动过：紧跟着的那次抬手不算点击。</summary>
    private bool _suppressClick;

    /// <summary>这一层能不能排：只有我们自己维护顺序的两层可以，文件夹内容是磁盘的。</summary>
    private bool LayerIsSortable =>
        _board.Kind is NavKind.Root or NavKind.Group && _query.Length == 0;

    /// <summary>
    /// 这一层能不能"相碰合并成新组合"：只有主界面。
    /// 组合子界面里再建组合是 D17 明令禁止的（两层封顶，砍掉递归、环检测和更深的导航栈），
    /// 所以那里压到中心圆只当普通插入处理。
    /// </summary>
    private bool MergeAllowedHere => _board.Kind == NavKind.Root && _query.Length == 0;

    private void ReorderDown(object sender, MouseButtonEventArgs e)
    {
        _dragTile = null;
        _dragOrder = null;
        _dragActive = false;
        _suppressClick = false;
        _pressOnTile = false;
        _dragIsGroup = false;
        _decided = false;       // 死区：新的一次拖拽从头开始判
        _insertSlot = -1;       // 插入死带同理
        ClearHints();

        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (SettingsPanel.Visibility == Visibility.Visible) return;

        // 双击的**第二下**不参与拖拽：它紧接着就会换层（Activate → Render），
        // 而这次按下记下的是【旧层】那枚磁贴 —— 留着它，第二下按住时手一抖就会在新层上
        // 冒出旧层的幽灵，松手还会走到 ReorderUp 的落盘分支（见 InvalidateDragForLayer）。
        // 不存在"双击后按住拖"这种用法，直接不认这次按下。
        // 放在重置之后：上一轮的残留照旧先清掉。
        if (e.ClickCount >= 2) return;

        var hit = TileFrom(e.OriginalSource);

        // 按下点只要落在磁贴上就先记下来，**不管这块能不能拖**。
        // 组合磁贴是典型：它不能当拖拽源，但"按住拖一下"也不该退化成"点进去了"。
        _pressOnTile = hit is not null;
        if (hit is not null) _dragStart = e.GetPosition(Tiles);

        if (!LayerIsSortable) return;

        // 真实条目和组合栏都能拖，两者在同一个序号空间里**混排**（BuildRoot 按 Sort 混排，
        // StateStore.ReorderRoot 落盘）。组合栏当不了合并/并入的**源**——那是两层封顶（D17）决定的，见 ReorderMove。
        bool isGroup = hit is { EntryId: null, GroupId: not null };
        if (hit is not ({ EntryId: not null } or { EntryId: null, GroupId: not null })) return;

        _dragTile = hit;
        _dragIsGroup = isGroup;
        _dragOrder = _shown.ToList();
    }

    private void ReorderMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(Tiles);

        // 按在磁贴上拖过了阈值 → 这次抬手不算点击。
        // 没有这一条的话，拖一下组合栏就会钻进那个组合，而用户以为自己只是"拎了一下"。
        if (_pressOnTile && !_suppressClick && PastDragThreshold(now)) _suppressClick = true;

        if (_dragTile is null) return;

        if (!_dragActive)
        {
            if (!PastDragThreshold(now)) return;

            _dragActive = true;
            _dragTile.Dragging = true;
            Tiles.CaptureMouse();
            Ghost().ShowFor(_dragTile, IconSize);   // 里面会自己贴一次光标
        }

        // ---- C 死区：位移不足 3 DIP 就不重算判定。
        // 手抖那一层（1~2px）够不着判定圆，却足够让"每个鼠标事件重算一遍"的结论反复横跳。
        if (!_decided || (now - _lastDecide).Length >= DecideDeadZone)
        {
            _lastDecide = now;
            _decided = true;
            DecideDrop(now);
        }
        else if (_hintTarget is null)
        {
            // 判定没重算，但幽灵还得跟着光标走（吸附态下它由插值接管，这里不动）
            _ghost?.Follow();
        }
    }

    /// <summary>
    /// 落点判定 —— 一次拖拽里<b>唯一改变状态的地方</b>。
    ///
    /// 2026-09-30 重做：吸附时"抽搐"的根因是判定在边界上反复翻转，而每翻一次都要同时改
    /// 三样东西（幽灵锚点、目标高亮、列表顺序），其中"改列表顺序"会挪动磁贴，
    /// 磁贴正是下一次判定的输入 —— 自激振荡。对策：
    /// <list type="bullet">
    ///   <item>进入半径 <see cref="MergeRadiusRatio"/>、退出半径 × <see cref="MergeExitRatio"/>，
    ///         中间那条环带里<b>什么都不做</b>：不吸附、也不重排；</item>
    ///   <item>命中即<b>锁定</b>目标，只有明确离开退出半径才释放；</item>
    ///   <item>位移不足 <see cref="DecideDeadZone"/> 不重算判定。</item>
    /// </list>
    /// 幽灵的位移分两段：**进入吸附走 70ms 过渡**（<see cref="DragGhost.GlideTo"/>），
    /// **解除一律瞬移**（<c>Follow()</c> 当场打断插值）。锚点还带橡皮筋
    /// （<see cref="RubberBandRatio"/>），拉着拉着它跟着你走，不会"卡住 → 突然弹开"。
    /// 教训：跟手是底线，能省的只有"进入"那一下 —— 早先那版两个方向都插值，
    /// 结果解除后幽灵飘着追光标，手感立刻变油腻。
    /// </summary>
    private void DecideDrop(Point now)
    {
        // 捕获状态下 OriginalSource 会固定成捕获元素，所以命中测试必须自己做。
        // **但命中测试只用来回答"压住了哪一块"（并入 / 合并要用它）** ——
        // 插入位次不再用它，见 SlotInsert：那个问题纯几何就能答。
        var target = TileAt(now);
        if (target is null)
        {
            // 没压在任何磁贴上（列间/行间缝隙、首尾之外、行带侧边）：以前这里直接走人，
            // 于是缝隙成了死区、首尾之外还会切成 Cancel。现在照样让几何去算位次
            // —— 它自带"离所有行都太远就不插入"的门槛，拖到下方一大截空白仍然什么都不做。
            ClearHints();
            _ghost?.Follow();
            _ghost?.SetMode(IsCursorInsideContentArea() ? GhostMode.Normal : GhostMode.Cancel);
            SlotInsert(now);
            return;
        }

        // ---- 方案 1：二级界面（组合子界面）**没有合并/并入这回事**，判定圆与迟滞环带
        // 一律不参与 —— 整块磁贴都是排序区。
        //
        // 这一段是补的：合并几何是主界面专属能力（D17：组合里不能再放组合），
        // 但"迟滞环带"当初是无条件判的，于是二级界面里每块磁贴的图标中心周围
        // 29px 半径（42 档）成了"不许排序区"，磁贴中间三分之一拖过去什么都不发生
        // —— 用户反馈"要拖到很边上才会触发排序"。
        if (!MergeAllowedHere)
        {
            ClearHints();
            _ghost?.Follow();
            _ghost?.SetMode(GhostMode.Normal);
            SlotInsert(now);
            return;
        }

        // ---- B 锁定：还压在已锁定目标的**退出半径**里 → 保持，一次状态都不改
        if (_hintTarget is { } held)
        {
            if (TileBounds(held) is { } heldBox && InsideMergeCircle(now, heldBox, MergeExitRatio))
            {
                GlideGhostTo(held, now);
                return;
            }
            ClearHints();
        }

        var bounds = TileBounds(target);
        if (bounds is null)
        {
            ClearHints();
            _ghost?.Follow();
            return;
        }

        // 落点一：条目压到组合磁贴的**图标中心** = 并入这个已有组合。
        // 判定圆与相碰合并是同一个（InsideMergeCircle）：用户拍板，"拖到组合上"
        // 必须和别的磁贴一套手感——压住图标才收纳，压到两侧就是插前/插后。
        // 组合栏自己被拖到另一个组合上时走的是**排序**，不是"并入"——所以这里要看拖的是什么。
        if (!_dragIsGroup && target.Kind == TileKind.Group && _board.CanCreateGroup
            && InsideMergeCircle(now, bounds.Value, 1.0))   // 1.0 = 进入半径
        {
            SetGroupHint(target);
            _ghost?.SetMode(GhostMode.Normal);
            GlideGhostTo(target, now);
            return;
        }

        if (ReferenceEquals(target, _dragTile))
        {
            ClearHints();
            _ghost?.Follow();
            _ghost?.SetMode(GhostMode.Normal);
            return;
        }

        // 落点二：压到另一块**条目**磁贴的正中心 = 两个图标叠到一起 → 新建组合收纳。
        // 组合栏不当合并的源、组合磁贴也不当合并的"另一块"（那是并入，见落点一；D17）。
        if (!_dragIsGroup && MergeAllowedHere && target.Kind != TileKind.Group
            && InsideMergeCircle(now, bounds.Value, 1.0))   // 1.0 = 进入半径
        {
            SetMergeHint(target);
            _ghost?.SetMode(GhostMode.Merge);
            GlideGhostTo(target, now);
            return;
        }

        // ---- A 迟滞环带：进入半径之外、退出半径之内 —— 不吸附、也不重排。
        // 反馈环就是从这一句断开的。
        if (InsideMergeCircle(now, bounds.Value, MergeExitRatio))
        {
            ClearHints();
            _ghost?.Follow();
            _ghost?.SetMode(GhostMode.Normal);
            return;
        }

        // 落点三：普通插入 —— 按几何槽位算"插到第几位"（见 SlotInsert）。
        // 落点指示就是"磁贴自己在实时挪动"，不再额外画竖杠。
        ClearHints();
        _ghost?.Follow();
        _ghost?.SetMode(GhostMode.Normal);
        SlotInsert(now);
    }

    /// <summary>
    /// 吸附时幽灵"保留多少光标偏移"（橡皮筋，方案 2）。
    ///
    /// 锚点 = 目标中心 + (光标 − 目标中心) × 这个比例。
    /// <list type="bullet">
    ///   <item>0 = 死钉在目标中心（旧行为）：往外拉时幽灵一动不动，
    ///         观感是"卡住 → 越过阈值突然弹开"，用户反馈"吸力太大、不好拽出来"；</item>
    ///   <item>0.35 = 留 35%：<b>试过，偏"不跟手"</b> —— 拖来拖去时幽灵离光标太远；</item>
    ///   <item>0.60 = 留 60%：<b>试过，仍偏"吸"</b>；</item>
    ///   <item>0.70 = 留 70%：几乎就是跟手，剩下的牵引只用来表达"我被吸着"；</item>
    ///   <item><b>1.0 = 幽灵完全跟手</b>（当前，正在试）：锚点就是光标，
    ///         吸完全由目标那圈环与高亮表达 —— 幽灵不再被牵引一丝一毫。</item>
    /// </list>
    /// 调这个数只影响"吸附期间幽灵离光标多远"，与进入/退出半径、过渡时长都无关，可以单独试。
    /// </summary>
    private const double RubberBandRatio = 1.0;

    /// <summary>
    /// 吸附态：幽灵朝"目标磁贴的图标中心"过渡过去（进入方向 70ms，见 <see cref="DragGhost.GlideTo"/>），
    /// 但只走到 <see cref="RubberBandRatio"/> 处 —— 留一部分跟着光标。
    /// 解除时由 <c>Follow()</c> 当场打断插值、直接贴光标（跟手优先）。
    /// </summary>
    private void GlideGhostTo(TileVm target, Point cursorInTiles)
    {
        if (TileBounds(target) is not { } b) return;

        var center = Tiles.PointToScreen(CircleCenter(b));
        var cursor = Tiles.PointToScreen(cursorInTiles);

        double ax = center.X + (cursor.X - center.X) * RubberBandRatio;
        double ay = center.Y + (cursor.Y - center.Y) * RubberBandRatio;

        // 锚点落在光标上时（比例 = 1）**不能走过渡**：那样每动 3px 就重启一次 70ms 缓动，
        // 幽灵会一直落在光标后面 —— 明明是"完全跟手"的设定，手感反而最差。
        // 锚点与光标重合就退回瞬移跟随。
        if (Math.Abs(ax - cursor.X) < 1 && Math.Abs(ay - cursor.Y) < 1)
        {
            _ghost?.Follow();
            return;
        }

        _ghost?.GlideTo((int)Math.Round(ax), (int)Math.Round(ay));
    }

    /// <summary>判定圆的圆心（<c>Tiles</c> 坐标系）：横向正中、纵向 0.4 高 —— 与判定共用一处。</summary>
    private static Point CircleCenter(Rect bounds) =>
        new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height * 0.4);

    private void ReorderUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragTile is null) return;

        var dragged = _dragTile;

        if (!_dragActive)
        {
            // 一次普通抬手：什么都不做，交给 OnMouseUp 的语义去处理
            // （组合单击即展开；文件 / 快捷方式 / 文件夹 / 收尾格要双击，见 TileVm.NeedsDoubleClick）
            _dragTile = null;
            _dragOrder = null;
            return;
        }

        int? intoGroup = _groupHint?.GroupId;
        int? mergeWith = _mergeHint?.EntryId;
        bool outside = !IsCursorInsideContentArea();
        FinishDragVisual();

        // 拖到面板外面松手 = 取消
        if (outside)
        {
            RestoreOrder();
        }
        else if (intoGroup is int gid && dragged.EntryId is int moving)
        {
            _store.MoveEntry(moving, gid);
            Render(animate: false);
            Status("已并入组合");
        }
        else if (mergeWith is int other && dragged.EntryId is int self)
        {
            MergeIntoNewGroup(self, other);
        }
        else if (_board.Kind == NavKind.Root)
        {
            // 主界面整层落盘：组合与条目混排，一个 id 序列就够（id 全局唯一）。
            // 这个分支必须排在并入/合并之后——落点是"有效操作"时优先按操作语义落盘，
            // 否则排序会把合并抢走（第一版就犯了这个错，合并全程失效）。
            var ids = _shown.Where(t => t.GroupId is not null || t.EntryId is not null)
                            .Select(t => t.GroupId ?? t.EntryId!.Value)
                            .ToList();
            _store.ReorderRoot(ids);

            // 视图已经是最终顺序，把 _board 跟着换掉就够；再调 Render 会白白重建一遍磁贴。
            // 过滤掉占位格——它是拖放期间临时塞进 _shown 的，不该进 _board.Tiles
            _board = _board with { Tiles = _shown.Where(t => !t.IsGhostTile).ToList() };
        }
        else
        {
            var ids = _shown.Where(t => t.EntryId is not null)
                            .Select(t => t.EntryId!.Value)
                            .ToList();
            _store.ReorderLayer(_board.GroupId, ids);

            // 视图已经是最终顺序，把 _board 跟着换掉就够；再调 Render 会白白重建一遍磁贴。
            // 过滤掉占位格——它是拖放期间临时塞进 _shown 的，不该进 _board.Tiles
            _board = _board with { Tiles = _shown.Where(t => !t.IsGhostTile).ToList() };
        }

        _dragTile = null;
        _dragOrder = null;
        e.Handled = true;   // 拖动过就不许再被当成一次单击
    }

    /// <summary>Esc 取消正在进行的拖拽。返回 true 表示这次按键是被拖拽流程吃掉的。</summary>
    private bool CancelDragIfAny()
    {
        if (_dragTile is null) return false;

        FinishDragVisual();
        RestoreOrder();
        _dragTile = null;
        _dragOrder = null;
        _suppressClick = true;   // 紧接着的抬手不能变成"点开这个文件夹"
        Status("已取消");
        return true;
    }

    /// <summary>
    /// 收起面板时收掉拖拽残留（D18 的 OnBeforeHide 契约）。
    /// 窗口藏了却在屏幕上留一个幽灵图标飘着，是最难解释的一种孤儿态。
    /// </summary>
    internal void DropDragState()
    {
        FinishDragVisual();
        _dragTile = null;
        _dragOrder = null;
    }

    /// <summary>
    /// <b>换层时作废拖拽上下文</b>（2026-10-01）。所有"进到另一层"的入口都要调它：
    /// <c>Activate</c> 里组合 / 文件夹 Push 成功之后、<c>OpenHit</c> 里钻文件夹 / 进组合之后。
    ///
    /// 为什么必须作废：拖拽是<b>基于旧层的磁贴与顺序</b>的 —— <c>_dragTile</c> 指向旧层那枚对象、
    /// <c>_dragOrder</c> 是旧层的显示顺序、位置判定用的也是旧层坐标。层一变这些全都失效，
    /// 留着它的两个后果：
    /// <list type="number">
    ///   <item>双击进文件夹时，第二下**按住**期间手一抖（阈值只有
    ///         <c>SystemParameters.Minimum*DragDistance</c>，150% DPI 下约 2.7 DIP），
    ///         <c>ReorderMove</c> 就在<b>新层</b>上冒出一个属于旧层的幽灵磁贴；
    ///         —— 进层以前发生在抬手时，<c>ReorderUp</c> 会先把它收干净，所以老代码看不到。</item>
    ///   <item>松手后 <c>ReorderUp</c> 看到 <c>_dragActive</c> → 落到落盘分支，
    ///         而它取的是<b>新层</b>的 <c>_shown</c>：文件夹层的 <c>_board.GroupId</c> 是 null，
    ///         等于把文件夹里的条目 id 当成主界面顺序写进 state.json。<b>这条更严重。</b></item>
    /// </list>
    ///
    /// <c>_suppressClick</c> 一并立起来：换层后紧接着的那次抬手不该再被当成点击 ——
    /// 否则它落在新层的空白处就会被"点空白返回"（D10）立刻弹回去，白进一层。
    /// 它会在下一次按下时被 <see cref="ReorderDown"/> 清掉，所以只影响这一次抬手。
    /// </summary>
    private void InvalidateDragForLayer()
    {
        DropDragState();
        _pressOnTile = false;
        _suppressClick = true;
    }

    /// <summary>
    /// 把两块磁贴并成一个新组合（用户说的"两个图标叠到一起就合并"）。
    ///
    /// 落盘只用现成的 <see cref="StateStore.CreateGroup"/>：它本来是为右键"新建组合"
    /// 写的，语义完全一致，不需要为拖拽另开一条写路径。
    /// 组合内部的顺序无所谓——新组合只有两块，整层重编号的意义不大。
    /// </summary>
    private void MergeIntoNewGroup(int draggingId, int targetId)
    {
        if (!MergeAllowedHere)
        {
            // 理论上到不了这里：ReorderMove 在非主界面根本不点亮 MergeHint
            Status("组合内不能再放组合");
            return;
        }

        var group = _store.CreateGroup(NextGroupName(), new[] { draggingId, targetId });
        Render(animate: false);

        if (group is null)
        {
            Status("合并失败");
            return;
        }

        // 主界面先渲染组合、再渲染条目，所以新组合会出现在组合区的末尾。
        // 不自动钻进去——连续整理的时候被强行切走上下文比"东西看着不见了"更烦。
        Status($"已新建组合「{group.Title}」，2 项已收纳");
        FlashNewGroup(group.Id);
    }

    /// <summary>新组合默认叫「未命名」；重名往后排号，免得界面上并排两个同名组合没法区分。</summary>
    private string NextGroupName()
    {
        var taken = new HashSet<string>(
            _store.State.Groups.Select(g => g.Title), StringComparer.CurrentCultureIgnoreCase);

        if (!taken.Contains(NewGroupName)) return NewGroupName;

        for (int i = 2; ; i++)
        {
            string candidate = $"{NewGroupName} {i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>让刚建出来的组合磁贴闪一下，把"东西去哪了"这个问题在视觉上答掉。</summary>
    private void FlashNewGroup(int groupId)
    {
        var tile = _board.Tiles.FirstOrDefault(t => t.GroupId == groupId);
        if (tile is null) return;

        tile.Selected = true;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            tile.Selected = false;
        };
        timer.Start();
    }

    /// <summary>
    /// <b>普通插入：按几何算出"插到第几位"，并就地预览移动。</b>
    ///
    /// 为什么不问"光标压住了哪一块"（2026-10-01 重做）：命中测试只在光标压在某块磁贴上时
    /// 才有答案 —— 列间/行间那 6px 缝隙是死区、行边界归属含糊（"差一格"的来源）、
    /// 首尾之外干脆没有答案（上一版为此加过一个"划出去就插到最前"的吸附，
    /// 结果在"想插到第二行第一个、手往左偏几像素"时把磁贴蹦到了全表第一个，见 README）。
    /// 而拖动排序真正要回答的只有一个问题：<b>插到这条一维序列的第几位</b>
    /// （左上 → 右下，一行满就顺延下一行）。这个问题纯几何就能答，而且答案唯一、连续、
    /// 不随预览回流变化。
    ///
    /// 三条规则：
    /// <list type="bullet">
    ///   <item><b>行</b>：光标 Y 落在哪一行的带里（含 <see cref="RowSlack"/> 的宽容）。
    ///         离所有行都太远就直接不插入 —— 拖到磁贴下方一大截的空白仍然什么都不做。</item>
    ///   <item><b>列</b>：按列宽 + 间距换算，缝隙中点归到最近的那一列；首尾之外 clamp 到首/末列。
    ///         注意是<b>那一行</b>的首/末列 —— 所以"在第二行里划到最左边"插的是第二行第一个，
    ///         不会蹦到全表第一个。</item>
    ///   <item><b>插前 / 插后</b>：光标在该格中线的左 / 右（甲方案：每行最后一格的右半边 =
    ///         插到它后面 = 下一行的第一个）。中线附近另有 <see cref="InsertDeadZone"/> 的死带，
    ///         沿用上一次的结论，免得停在中线上时来回翻。</item>
    /// </list>
    /// </summary>
    private void SlotInsert(Point now)
    {
        if (_dragTile is null) return;
        if (SlotGeometry() is not { } g) return;

        if (now.Y < g.Top - RowSlack || now.Y > g.Bottom + RowSlack) return;

        int row = g.RowAt(now.Y);
        int col = Math.Clamp((int)Math.Floor((now.X + g.GapX / 2) / g.Stride), 0, g.Cols - 1);

        double center = col * g.Stride + g.CellW / 2;
        bool after = now.X > center;

        int slot = row * g.Cols + col;
        if (_insertSlot == slot && Math.Abs(now.X - center) <= InsertDeadZone)
        {
            after = _insertAfter;      // 死带：沿用上一次的结论
        }
        else
        {
            _insertSlot = slot;
            _insertAfter = after;
        }

        MoveToSlot(slot, after);
    }

    /// <summary>行带的纵向宽容（DIP）。手横向划动时总带一点上下偏移，卡死在边界上会时灵时不灵。</summary>
    private const double RowSlack = 12;

    /// <summary>
    /// 从<b>实际排列出来的矩形</b>反推槽位几何：列数、列宽、间距、每行的 y 范围。
    ///
    /// <b>不复述 <c>TilePanel</c> 的算法</b> —— 复述就等于养了第二份口径（改一处忘一处，
    /// "拖拽幽灵漏掉预设线稿那一档"就是这么来的）；读它排完的结果，则永远与它对得上。
    /// 代价是每次判定要把当前这些磁贴的矩形取一遍（十几块、每个一次 TransformToAncestor），
    /// 相对拖动本身可以忽略；换来的是"几何永远是真话"。
    /// </summary>
    private SlotGeo? SlotGeometry()
    {
        var rects = new List<Rect>();
        foreach (var t in _shown)
            if (TileBounds(t) is { } b) rects.Add(b);
        if (rects.Count == 0) return null;

        // 行：排布是按索引来的，同一行的 Y 完全相同 —— Y 变了就是换行
        var rows = new List<(double Top, double Bottom)>();
        foreach (var r in rects)
        {
            if (rows.Count > 0 && Math.Abs(rows[^1].Top - r.Y) < 0.5) continue;
            rows.Add((r.Y, r.Bottom));
        }

        var g = new SlotGeo { CellW = rects[0].Width, Rows = rows };
        g.Cols = rects.Count(r => Math.Abs(r.Y - rows[0].Top) < 0.5);

        // 间距：同一行里相邻两块的左边界之差 − 列宽（只有一块 / 只有一列时按 0 处理）
        if (rects.Count > 1 && Math.Abs(rects[1].Y - rects[0].Y) < 0.5)
            g.GapX = Math.Max(0, rects[1].X - rects[0].X - g.CellW);

        g.Stride = g.CellW + g.GapX;
        return g.Cols > 0 && g.Stride > 0 ? g : null;
    }

    /// <summary>把"第几位"落到列表上。<b>插入顺序的唯一出口</b>，索引换算只此一处。</summary>
    private void MoveToSlot(int slot, bool after)
    {
        if (_dragTile is null) return;

        int from = _shown.IndexOf(_dragTile);
        if (from < 0) return;

        int to = slot + (after ? 1 : 0);
        if (to > _shown.Count) to = _shown.Count;   // 划到末尾之外 → 夹到"最后"

        // 把源从列表里摘出来之后，它右边所有元素的索引都会左移一格
        if (from < to) to--;

        if (to == from) return;
        if (to < 0 || to >= _shown.Count) return;   // 越界（理论上到不了）就当没发生

        _shown.Move(from, to);
    }

    /// <summary>槽位几何，见 <see cref="SlotGeometry"/>。</summary>
    private sealed class SlotGeo
    {
        public int Cols;
        public double CellW;
        public double GapX;
        public double Stride;
        public List<(double Top, double Bottom)> Rows = new();

        public double Top => Rows[0].Top;
        public double Bottom => Rows[^1].Bottom;

        /// <summary>光标落在哪一行：在行间缝隙里归到下面那一行，超过末行归末行。</summary>
        public int RowAt(double y)
        {
            for (int i = 0; i < Rows.Count; i++)
                if (y <= Rows[i].Bottom) return i;

            return Rows.Count - 1;
        }
    }

    private TileVm? TileAt(Point inTiles) =>
        Tiles.InputHitTest(inTiles) is DependencyObject hit ? TileFrom(hit) : null;

    /// <summary>
    /// 光标是否还在<b>可落点区域</b>里 —— 出界就意味着"拖到面板外面松手 = 取消"。
    ///
    /// 2026-10-01 从 <c>Tiles</c> 的矩形改成 <c>Scroller</c>（滚动区）的矩形。
    /// 两者之间还夹着 ScrollViewer 的 <c>Margin(12,10,6,0)</c> 与 <c>Padding(0,0,4,0)</c>，
    /// 外加"磁贴不满一屏时下方那片空白"—— 这些地带原来一律算"出界"，
    /// 于是想往最左/最右划出去时，稍微一偏就被切成取消。
    ///
    /// 现在这一类位置由 <see cref="SlotInsert"/> 的几何槽位接管 —— 它算的是
    /// "**那一行**的首/末列"，所以不需要再额外解释"划出去 = 插到最前/最后"
    /// （上一版正是那样特判的，结果"想插到第二行第一个、手往左偏几像素"会蹦到全表第一个）。
    /// </summary>
    private bool IsCursorInsideContentArea()
    {
        var p = Mouse.GetPosition(Scroller);
        return p.X >= 0 && p.Y >= 0 && p.X <= Scroller.ActualWidth && p.Y <= Scroller.ActualHeight;
    }

    /// <summary>磁贴在 <c>Tiles</c> 坐标系里的矩形；容器还没实现布局就返回 null。</summary>
    private Rect? TileBounds(TileVm tile)
    {
        if (Tiles.ItemContainerGenerator.ContainerFromItem(tile) is not FrameworkElement fe) return null;
        if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return null;

        try
        {
            return new Rect(fe.TransformToAncestor(Tiles).Transform(new Point(0, 0)),
                            new Size(fe.ActualWidth, fe.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return null;   // 容器此刻不在视觉树里（正在重建），这一帧跳过判定
        }
    }

    /// <summary>同一块磁贴的屏幕物理像素矩形。幽灵窗口在屏幕坐标系里摆，两边要换算一次。</summary>
    private Rect? TileScreenBounds(TileVm tile)
    {
        if (TileBounds(tile) is not { } b) return null;
        return new Rect(Tiles.PointToScreen(b.TopLeft), Tiles.PointToScreen(b.BottomRight));
    }

    private bool PastDragThreshold(Point now) =>
        Math.Abs(now.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance
     || Math.Abs(now.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance;

    /// <summary>
    /// 光标是否压在磁贴的"图标中心"圆里。
    ///
    /// 圆心横向取正中、纵向取 <c>0.4 × 磁贴高</c>。这个 0.4 是把磁贴的结构折进去算出来的：
    /// 磁贴高 = root 上内边距 7 + 图标 + 标签行，所以图标中心在 <c>7 + 图标/2 + 2</c>
    /// （那 2 是 box 自己的 4px 上边距带来的一半位移）。三档图标下 0.4 高与图标中心差
    /// 1px 以内（32→26/25、42→30/30、56→35.6/37），标签换成两行时最多偏 6.6px，
    /// 仍在圆内。用几何中心就会偏低十几像素，"叠图标"必须往下压才触发，手感是歪的。
    ///
    /// <param name="scale">
    /// 以<b>进入半径</b>为 1 的倍数：<b>进入</b>判定传 <c>1.0</c>，
    /// <b>退出</b>判定传 <see cref="MergeExitRatio"/>（A 方案的迟滞：进去难、出来更难）。
    /// </param>
    /// </summary>
    private bool InsideMergeCircle(Point cursor, Rect bounds, double scale)
    {
        var c = CircleCenter(bounds);
        double radius = EnterRadius * scale;

        double dx = cursor.X - c.X;
        double dy = cursor.Y - c.Y;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>
    /// 合并 / 并入判定圆的<b>进入半径</b>：图标边长 × <see cref="MergeRadiusRatio"/>，
    /// 再兜一个下限。它是这套几何的基准 —— <b>退出半径 = 它 × <see cref="MergeExitRatio"/></b>。
    ///
    /// 2026-10-01 修的（这是真 bug）：原来退出半径直接算 <c>IconSize × MergeExitRatio</c>，
    /// 42 档下是 <b>47px</b>；而注释与设计意图一直是"进 26px / 出 29px"（26 × 1.12）、
    /// "最初写 1.3 时是出 34px"（26 × 1.3）—— <b>两处数字都证明该以"进入半径"为基数，
    /// 公式漏了一次换算</b>。后果：迟滞带从 3px 撑到 21px，列宽 106 的格子里
    /// 距列心 47px 的中段全成了"不许排序区"，只剩左右各 6px 能插入 ——
    /// 这就是"最左/最右两块顶不掉、必须划到很边缘"的主因（实测见 README 与 drag-trace.log）。
    /// </summary>
    private double EnterRadius => Math.Max(MergeRadiusFloor, IconSize * MergeRadiusRatio);

    private void SetGroupHint(TileVm target)
    {
        if (ReferenceEquals(_groupHint, target)) return;
        ClearHints();
        _groupHint = target;
        _hintTarget = target;      // B：命中即锁定
        target.DropHint = true;
    }

    private void SetMergeHint(TileVm target)
    {
        if (ReferenceEquals(_mergeHint, target)) return;
        ClearHints();
        _mergeHint = target;
        _hintTarget = target;      // B：命中即锁定
        target.MergeHint = true;
    }

    private void ClearHints()
    {
        if (_groupHint is not null)
        {
            _groupHint.DropHint = false;
            _groupHint = null;
        }
        if (_mergeHint is not null)
        {
            _mergeHint.MergeHint = false;
            _mergeHint = null;
        }
        _hintTarget = null;
    }

    private void FinishDragVisual()
    {
        if (_dragTile is not null) _dragTile.Dragging = false;
        ClearHints();
        _decided = false;
        _insertSlot = -1;
        if (Tiles.IsMouseCaptured) Tiles.ReleaseMouseCapture();
        _ghost?.HideGhost();
        _dragActive = false;
    }

    /// <summary>回到拖之前的显示顺序。只动视图，磁盘上什么都没写过。</summary>
    private void RestoreOrder()
    {
        if (_dragOrder is null) return;

        // 逐项 Move 回原索引，**不要** Clear 了再 Add 一遍：
        // 整体重置会让 ItemsControl 把所有容器销毁重建，实测之后 UIA 再也读不到磁贴
        // （屏幕阅读器同理），表现就是"面板还在，但里面一块磁贴都没有"。
        for (int i = 0; i < _dragOrder.Count; i++)
        {
            var want = _dragOrder[i];
            int at = _shown.IndexOf(want);
            if (at < 0 || at == i) continue;
            _shown.Move(at, i);
        }
    }

    /// <summary>
    /// 幽灵窗口懒建、复用。每次拖拽都新开一个顶层窗口要重走一遍句柄创建与分层合成，
    /// 而"拖拽"本身是个高频动作。
    /// </summary>
    private DragGhost Ghost()
    {
        if (_ghost is not null) return _ghost;

        _ghost = new DragGhost();
        Closed += (_, _) =>
        {
            try { _ghost?.Close(); }
            catch (Exception) { /* 收尾阶段，关不掉也不能再抛出去 */ }
            _ghost = null;
        };
        return _ghost;
    }
}
