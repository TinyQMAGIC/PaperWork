using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Paperwork.Data;

namespace Paperwork.Shell;

/// <summary>
/// 图标的 LRU 缓存 + 单后台解码队列。
///
/// 取图标、取显示名、判失效三件事必须放在一起，因为它们都要查 Shell；
/// 拆成两套会重复访问文件系统，而且很容易在 UI 线程上不小心调一次
/// <c>File.Exists</c>——那对掉线的网络共享能阻塞数秒。
///
/// <b>只有要显示真图标的磁贴才取位图</b>（<see cref="TileVm.WantsShellIcon"/>：程序/快捷方式，
/// 以及用户显式「改图标」的）。普通文件与文件夹显示的是预设线稿，但它们<b>照样</b>要查 Shell——
/// 显示名（本地化名）和失效判定都从那儿来——所以走的还是同一条队列，只是
/// <see cref="Request.WantImage"/> 为 false 时不碰图标。这条口子来自 2026-09-29 的内存排查：
/// 进文件夹会白缓存几十张永远不显示的位图。
///
/// 线程分工是这里最容易写错的地方：
/// <list type="bullet">
///   <item>后台线程：只做 Shell 查询（SHGetFileInfo / SHGetImageList / GetIcon），拿到 HICON 句柄</item>
///   <item>UI 线程：把 HICON 转成 BitmapSource 并 Freeze，然后销毁句柄</item>
/// </list>
/// 转换不能放在后台线程，因为 <c>CreateBitmapSourceFromHIcon</c> 要求线程有 Dispatcher，
/// 而 <c>Task.Run</c> 给的是 MTA 线程池线程——失败还会被静默吞掉，表现为"图标永远是空的"。
/// </summary>
public sealed class IconPump : IDisposable
{
    /// <summary>可见期间的缓存上限，按**像素预算**算而不是按条数（BGRA32 = 4 字节/像素）。
    /// 这里只管位图占的字节；条数另有 <see cref="MaxEntries"/> 兜底。</summary>
    private const long VisibleBudget = 8L * 1024 * 1024;

    /// <summary>隐藏期间的预算（D18：骨架留着，可重建的数据丢掉）。</summary>
    private const long TrimmedBudget = 2L * 1024 * 1024;

    /// <summary>
    /// 条目数的硬上限。**光按像素收是不够的**：取不到位图的条目（失效路径、Shell 拒绝返回）
    /// 计入 0 像素，字节数上它们永远超不了预算，字典和链表就会只增不减。
    /// 2026-09-29 之后普通文件也不再存位图，条数上限成了缓存唯一的兜底闸门。
    /// 2000 条 ×（记录 + 路径 + 显示名 + 类型名）≈ 1MB 以内。
    /// </summary>
    private const int MaxEntries = 2000;

    /// <summary>BGRA32。</summary>
    private const int BytesPerPixel = 4;

    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly object _gate = new();

    /// <summary>
    /// 已知连不上的 UNC 主机 → 记下来的时刻（TickCount64）。<see cref="DeadHostTtlMs"/> 内过期。
    /// </summary>
    private readonly Dictionary<string, long> _deadHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 熔断记忆保留多久。太短等于没熔断（一次呼出就过期），太长会让"网盘刚重新上线"
    /// 半分钟内还显示灰的。30 秒是"人手一次操作的间隔"这个量级。
    /// </summary>
    private const long DeadHostTtlMs = 30_000;

    // 这里**刻意没有**「在途去重」。加过一版（_inFlight：同 key 已在队列里就不再排），
    // 结果是个真回归：Render 每次都重建 TileVm 实例，启动时 OnSourceInitialized 与
    // OnAfterShow 各 Render 一次 —— 第二次的实例上屏时，第一个实例的请求还在途，
    // 去重把第二次的请求丢掉，worker 回来只 Apply 到那个已经不在视觉树上的孤儿实例，
    // 屏上的磁贴就永远停在推导名（badge-size 而不是 badge-size.html）。
    // 重复排队的代价只是同一条多查一遍 Shell；去重的代价是显示名永久缺失。别再加回来。

    /// <summary>当前缓存里的位图占的像素字节数。用条目数当上限会被 DPI 打穿：
    /// 56px 档在 200% 缩放下实际是 112px，单张 50KB——512 条就是 25MB，
    /// 而同一份数量在 32px 档只有 2MB。所以这里必须按字节收，让高 DPI 自动少缓存几张。</summary>
    private long _pixels;

    /// <summary>处于隐藏期的压缩状态。有新位图进来就自动放开。</summary>
    private bool _trimmed;

    private readonly Channel<Request> _queue = Channel.CreateUnbounded<Request>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _worker;

    private int _served;
    private int _failed;

    public int Served => Volatile.Read(ref _served);
    public int Failed => Volatile.Read(ref _failed);

    private sealed record CacheEntry(
        string Key, ImageSource? Image, string DisplayName, string TypeName, bool Exists, string? DeadReason);

    /// <summary>
    /// <paramref name="WantImage"/> 是发起这次查询时就定下来的：为 false 的请求只拿
    /// 显示名与失效状态，缓存条目里 <c>Image</c> 恒为 null。同一个路径的这个值恒定
    /// （由扩展名 / 自定义图标决定，见 <see cref="TileVm.WantsShellIcon"/>），
    /// 所以不会出现"同一个 key 一会儿要图一会儿不要图"。
    /// </summary>
    private sealed record Request(TileVm Tile, string Key, int Px, bool WantImage);

    public IconPump(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        // 必须是 STA 专用线程，不能用 Task.Run：
        // SHGetImageList 内部要创建 COM 对象，MTA 线程池线程上会失败，
        // 表现就是"显示名和失效判定都正常，只有图标永远是空的"。
        _worker = new Thread(Pump) { IsBackground = true, Name = "Paperwork.IconPump" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    private void Pump()
    {
        while (!_cts.IsCancellationRequested)
        {
            Request req;
            try
            {
                req = _queue.Reader.ReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                break;
            }

            var result = QuerySafely(req);
            if (result is null) continue;

            // 已经取消（= 正在退出）就别再往 UI 线程投递了。
            // Dispatcher 关闭之后 BeginInvoke 既不排队也不抛异常，那个委托永远不会执行，
            // 而这个 Pending 手里正握着本次查询唯一的 HBITMAP —— 它的 DeleteObject 只在
            // Complete() 里发生，一旦委托石沉大海，这个 GDI 句柄就再也没人释放。
            // 既然 UI 线程不会接手了，这里就地把它销毁掉。
            if (_cts.IsCancellationRequested)
            {
                ShellIcons.Release(result.Handle, result.Kind);
                break;
            }

            // 句柄必须由 UI 线程接手转换；BeginInvoke 不阻塞本线程
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, () => Complete(req, result));
        }
    }

    public void Enqueue(IReadOnlyList<TileVm> tiles, int targetPx)
    {
        foreach (var tile in tiles)
        {
            if (tile.Kind is TileKind.Group or TileKind.More) continue;
            if (string.IsNullOrWhiteSpace(tile.Path)) continue;

            // 离线主机熔断。worker 只有一条且串行，一条掉线 UNC 的 File.Exists 能卡几十秒，
            // 把整条队列 —— 包括所有磁贴的显示名与失效判定 —— 一起拖死，观感等同假死。
            // 30 秒内同主机的路径直接按失效处理、不进队列。
            // 第一次碰到仍会卡一次，卡完就记住（用户拍板接受这个代价，不加线程）。
            if (IsUnc(tile.Path) && KnownDeadHost(tile.Path) is string deadHost)
            {
                Apply(tile, DeadEntry(tile.Path, string.Format(Str.LogNetHostUnavailable, deadHost)));
                continue;
            }

            string key = CacheKey(tile, targetPx);

            CacheEntry? hit = null;
            lock (_gate)
            {
                if (_index.TryGetValue(key, out var node))
                {
                    Touch(node);
                    hit = node.Value;
                }
            }

            // Apply 会改 TileVm 的属性、同步驱动 WPF 绑定更新，绝不能在持锁期间跑
            // （原来它在 lock 里面）。取锁只为读缓存，出锁再写 UI。
            // 缓存没命中就照排：不做在途去重，理由见字段区那段注释。
            if (hit is not null) Apply(tile, hit);
            else
            {
                // 要不要真图由磁贴自己说了算（TileVm.WantsShellIcon）：程序/快捷方式与显式改图标的取，
                // 其余文件类只查显示名与失效判定。别小看这一句——原来进一个 30 项的文件夹，
                // 30 个普通文件每张图都要 Shell 同步渲染一遍再白存进缓存（42px 档 ≈0.2MB、
                // 56px@200% 档 ≈1.5MB），而界面上这些磁贴显示的是预设线稿，取回来就扔。
                _queue.Writer.TryWrite(new Request(tile, key, targetPx, tile.WantsShellIcon));
            }
        }
    }

    // ---------------------------------------------------------------- 离线主机熔断

    private static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary><c>\\host\share\...</c> 里的 host。</summary>
    private static string UncHostOf(string path)
    {
        int slash = path.IndexOf('\\', 2);
        return slash > 2 ? path[2..slash] : path[2..];
    }

    private string? KnownDeadHost(string path)
    {
        string host = UncHostOf(path);
        lock (_gate)
        {
            long now = Environment.TickCount64;
            // 顺手清过期的，别让这张表只增不减
            foreach (var stale in _deadHosts.Where(kv => now - kv.Value > DeadHostTtlMs)
                                            .Select(kv => kv.Key).ToList())
                _deadHosts.Remove(stale);

            return _deadHosts.ContainsKey(host) ? host : null;
        }
    }

    private void RememberDeadHost(string path)
    {
        if (!IsUnc(path)) return;
        lock (_gate) _deadHosts[UncHostOf(path)] = Environment.TickCount64;
    }

    private static CacheEntry DeadEntry(string path, string reason) =>
        new(path, null, System.IO.Path.GetFileName(path.TrimEnd('\\', '/')),
            string.Empty, false, reason);

    /// <summary>
    /// 某个文件夹的磁盘内容变了：把该目录下所有缓存条目丢掉。
    /// 「路径没变但 Shell 结论变了」（外面改了名、快捷方式换了目标）靠这个定点失效；
    /// 不这么做的话缓存只写不删，结论永远停在第一次查到的那一刻。
    /// </summary>
    public void InvalidateUnder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        string prefix = folderPath.TrimEnd('\\', '/') + "\\";

        lock (_gate)
        {
            foreach (var key in _index.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                           .ToList())
            {
                if (_index.Remove(key, out var node))
                {
                    _pixels -= PixelsOf(node.Value.Image);
                    _lru.Remove(node);
                }
            }
        }
    }

    private static string CacheKey(TileVm tile, int px) =>
        $"{tile.CustomIcon ?? tile.Path}|{px}|{(tile.CustomIcon is null ? 0 : 1)}";

    private static ShellIcons.Pending? QuerySafely(Request req)
    {
        try
        {
            if (req.Tile.CustomIcon is not { Length: > 0 } custom)
                return ShellIcons.Query(req.Tile.Path, req.Px, req.WantImage);

            var viaCustom = ShellIcons.Query(custom, req.Px, req.WantImage);
            if (viaCustom.Handle != IntPtr.Zero && viaCustom.Exists) return viaCustom;

            // 自定义图标文件本身没了：释放 GDI 句柄，退回真实路径的图标
            ShellIcons.Release(viaCustom.Handle, viaCustom.Kind);
            return ShellIcons.Query(req.Tile.Path, req.Px, req.WantImage);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>在 UI 线程执行：转 BitmapSource（并释放 GDI 句柄）、入缓存、写回磁贴。</summary>
    private void Complete(Request req, ShellIcons.Pending result)
    {
        var entry = new CacheEntry(
            req.Key,
            ShellIcons.Materialize(result),
            result.DisplayName,
            result.TypeName,
            result.Exists,
            result.DeadReason);

        // 两个计数由 Served / Failed 对外暴露，不写日志——图标没取到是常态
        // （缺权限、网络盘离线、缩略图不可用都会走到），记下来只会淹掉真异常。
        // 没请求位图的那批（普通文件/文件夹）两边都不计：它们"没有图"是设计，不是失败，
        // 否则 Failed 会被这些白名单条目淹掉，真失败反而看不出来。
        if (req.WantImage)
        {
            if (entry.Image is not null) Interlocked.Increment(ref _served);
            else Interlocked.Increment(ref _failed);
        }

        // 失效结论<b>不进缓存</b>：「死」是这条链上最容易翻案的状态（回收站还原、
        // 网盘重新上线、U 盘插回来），而「活着 + 图标长什么样」几乎不变。
        // 原来缓存只写不删，灰掉的磁贴就永远灰着 —— 还原了也看不见。
        // 不缓存之后，下次呼出会重新问一次 Shell，翻案立刻可见；
        // 本地路径的 File.Exists 很便宜，掉线 UNC 则由上面的熔断器挡着。
        if (entry.Exists) Insert(entry);
        else RememberDeadHost(req.Tile.Path);

        Apply(req.Tile, entry);
    }

    private void Apply(TileVm tile, CacheEntry entry)
    {
        // 只把**图像**写回磁贴，而且只给程序/快捷方式与显式「改图标」的（见 TileVm.WantsShellIcon）。
        // Enqueue 已经保证了不要图的磁贴不会取到图，这里是同一条口径的第二道闸：
        // 缓存命中复用那条路径也走这句，两边不对齐就会出现"线稿磁贴突然变成 Shell 图标"。
        // 这次 Shell 查询还要顺带拿显示名与失效判定，那两样一律照单全收——它们跟图标是什么画风无关。
        if (entry.Image is not null && tile.WantsShellIcon) tile.Icon = entry.Image;

        // 默认显示名**一律**记下来，不管这一格有没有自定义名：
        // 重命名要拿它当"用户是不是没改"的基准，而改过名的格子也得知道真正的默认名是什么
        // （否则第二次打开对话框原样点确定就会退化成恢复原名）。见 TileVm.DefaultLabel。
        if (!string.IsNullOrWhiteSpace(entry.DisplayName))
            tile.DefaultLabel = entry.DisplayName;

        // 用户没改过名才用 Shell 显示名覆盖占位名
        if (!tile.HasCustomLabel && !string.IsNullOrWhiteSpace(entry.DisplayName))
            tile.Label = entry.DisplayName;

        tile.IsDead = !entry.Exists;
        tile.DeadReason = entry.DeadReason;
    }

    // ------------------------------------------------------------------ LRU

    private void Insert(CacheEntry entry)
    {
        lock (_gate)
        {
            if (_index.ContainsKey(entry.Key)) return;

            var node = _lru.AddFirst(entry);
            _index[entry.Key] = node;
            _pixels += PixelsOf(entry.Image);

            // 有新位图进来说明面板正在被用，回到可见预算
            _trimmed = false;
            EvictToBudget();
        }
    }

    private void EvictOne()
    {
        var last = _lru.Last;
        if (last is null) return;
        _pixels -= PixelsOf(last.Value.Image);
        _lru.RemoveLast();
        _index.Remove(last.Value.Key);
    }

    private void EvictToBudget()
    {
        long budget = _trimmed ? TrimmedBudget : VisibleBudget;

        // 两个闸门里任意一个越线就继续丢最旧的。条数那条不能省：失效路径这类
        // 取不到位图的条目计 0 像素，只盯字节数的话它们一辈子都不会被淘汰。
        while (_lru.Last is not null && (_pixels > budget || _lru.Count > MaxEntries))
            EvictOne();
    }

    private static long PixelsOf(ImageSource? image) =>
        image is BitmapSource bmp ? (long)bmp.PixelWidth * bmp.PixelHeight * BytesPerPixel : 0;

    private void Touch(LinkedListNode<CacheEntry> node)
    {
        if (_lru.First == node) return;
        _lru.Remove(node);
        _lru.AddFirst(node);
    }

    /// <summary>
    /// 窗口隐藏时调用（D18）。注意调用方必须**先**把 TileVm 上的 ImageSource 引用摘掉，
    /// 否则这里只是把条目从链表上摘下来，位图仍然被磁贴指着，一块都释放不掉。
    /// </summary>
    public void Trim()
    {
        lock (_gate)
        {
            _trimmed = true;
            EvictToBudget();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();

        // 故意不 Join 后台线程。此刻它很可能正阻塞在 Shell 查询里——对掉线的网络共享，
        // File.Exists 与 IShellItemImageFactory.GetImage 能卡几十秒；在退出路径上等它，
        // 就等于让程序关不掉。
        // 线程是 IsBackground=true，进程退出时 OS 直接回收，代价只是一个被掐断的图标查询；
        // 而它已经拿到的那个 GDI 句柄会在查询结果出来之后由上面的取消检查就地释放。
    }
}
