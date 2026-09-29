using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Paperwork.Data;

/// <summary>一条命中来自哪里。排序时按这个顺序分档，不靠颜色区分（见 UI 手册 §0.1）。</summary>
public enum SearchHitKind { Entry, Group, Note }

/// <summary>
/// 一段被切成「前 / 命中 / 后」的文本。XAML 里横排三个 Run，命中的那段自己上色。
///
/// 为什么不直接给一个"整块高亮"的开关：现有 <c>TileVm.Highlighted</c> 是给整块磁贴换底色，
/// 那是"这整块东西命中了"的意思；在列表里一行有大半是无关注释，整块染色只会糊成一片。
/// </summary>
public sealed record MatchText(string Prefix, string Match, string Suffix)
{
    public static MatchText Plain(string text) => new(text, string.Empty, string.Empty);

    public bool HasMatch => Match.Length > 0;
}

/// <summary>
/// 一条命中。<b>刻意做成扁平的</b>：图形、副信息、分段文本全部算好给 XAML
/// （与 <c>NoteVm</c> 预先算 Visibility 同一套做法），模板里不再做判断。
/// </summary>
public sealed record SearchHit(
    SearchHitKind Kind,
    MatchText Name,
    MatchText Sub,
    Geometry? Glyph,
    string? Path,
    bool IsDir,
    int? EntryId,
    int? GroupId,
    int? NoteId,
    bool WantsShellIcon = false)
{
    /// <summary>备忘：右端钉一个小标签，图形看不清时它是唯一能说明类型的信息。</summary>
    public bool IsNote => Kind == SearchHitKind.Note;
}

/// <summary>
/// 全局搜索。<b>零 IO</b>：三个来源（条目 / 组合 / 备忘）全在 <c>AppState</c> 的内存副本里，
/// 一次查询是几十微秒级，所以既不需要防抖等磁盘（防抖只为防手速），也不需要索引或后台线程。
///
/// <b>不搜文件夹内部</b>——那是产品决定，不是实现偷懒：盘上内容只在被展开时才读
/// （<c>BoardBuilder.BuildFolder</c>，且必须后台跑，网络盘能阻塞数十秒）。
/// 已登记的文件夹磁贴仍然算一条 Entry 参与搜索，点它是进那一层，不是扫它。
/// </summary>
internal static class SearchService
{
    /// <summary>结果上限。面板只有 374px 高，全量倒出来等于没有搜索。</summary>
    internal const int DefaultLimit = 40;

    /// <summary>备忘正文片段：命中前后各带这么多字，够看出上下文又不撑爆一行。</summary>
    private const int SnippetContext = 12;

    private const int PreviewMax = 42;

    internal static (List<SearchHit> Hits, int Total) Query(
        IReadOnlyList<EntryItem> entries,
        IReadOnlyList<GroupItem> groups,
        IReadOnlyList<NoteItem> notes,
        string query,
        int limit = DefaultLimit)
    {
        var ranked = new List<(int Rank, int Score, string Sort, SearchHit Hit)>();

        if (string.IsNullOrWhiteSpace(query)) return (new List<SearchHit>(), 0);

        // ---- 条目（含组合里的：Entries 不分 GroupId 一起搜）----
        foreach (var e in entries)
        {
            // 显示名必须和面板上一致：自定义名优先，没有就从路径推导（与 BoardBuilder.FromEntry 同一套）
            string name = e.Label ?? BoardBuilder.DeriveName(e.Path, e.IsDir);
            var (text, score) = Split(name, query);
            if (score == 0) continue;

            ranked.Add((0, score, name, new SearchHit(
                SearchHitKind.Entry, text, MatchText.Plain(e.Path),
                Glyphs.ForPath(e.Path, e.IsDir), e.Path, e.IsDir, e.Id, null, null,
                // 程序/快捷方式与显式改过图标的，结果行里给真图标——
                // VS Code 和微信长得完全不一样，线稿反而是丢信息（与磁贴 WantsShellIcon 同一口径）
                Glyphs.IsProgram(e.Path) || e.Icon is not null)));
        }

        // ---- 组合 ----
        foreach (var g in groups)
        {
            var (text, score) = Split(g.Title, query);
            if (score == 0) continue;

            int count = entries.Count(x => x.GroupId == g.Id);
            ranked.Add((1, score, g.Title, new SearchHit(
                SearchHitKind.Group, text, MatchText.Plain($"组合 · {count} 项"),
                null, null, false, null, g.Id, null)));
        }

        // ---- 备忘：标题 + 正文 ----
        foreach (var n in notes)
        {
            string title = (n.Title ?? string.Empty).Trim();
            string body = n.Body ?? string.Empty;

            int ti = IndexOf(title, query);
            int bi = IndexOf(body, query);
            if (ti < 0 && bi < 0) continue;

            // 没标题的备忘很常见：名字位取正文首行，右端仍钉「备忘」标签
            string nameText = title.Length > 0 ? title : FirstLine(body);
            var name = ti >= 0 ? Cut(nameText, query) : MatchText.Plain(nameText);

            // 正文命中时副信息给片段；否则给常规的开头预览
            var sub = bi >= 0 ? Snippet(body, query) : MatchText.Plain(Preview(body));

            // 标题命中比正文命中值钱：标题是这个东西叫什么，正文只是它提到过
            int score = ti >= 0 ? Score(nameText, query) : 40;

            ranked.Add((2, score, nameText, new SearchHit(
                SearchHitKind.Note, name, sub, Glyphs.Note, null, false, null, null, n.Id)));
        }

        // Rank 先分档（条目 → 组合 → 备忘，备忘统一在后），同档内再按分数，
        // 最后按名字保证同分时顺序稳定，不会因为 List 的插入顺序而跳动。
        var ordered = ranked
            .OrderBy(x => x.Rank)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Sort, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Hit)
            .ToList();

        var hits = ordered.Take(limit).ToList();
        return (hits, ordered.Count);
    }

    /// <summary>切三段 + 打分。返回 score = 0 表示没命中。</summary>
    private static (MatchText Text, int Score) Split(string text, string query)
    {
        int score = Score(text, query);
        return score == 0 ? (MatchText.Plain(text), 0) : (Cut(text, query), score);
    }

    private static MatchText Cut(string text, string query)
    {
        int i = IndexOf(text, query);
        if (i < 0) return MatchText.Plain(text);
        return new MatchText(text[..i], text.Substring(i, query.Length), text[(i + query.Length)..]);
    }

    /// <summary>
    /// 打分：完全相等 &gt; 前缀 &gt; 词首 &gt; 包含。
    /// 词首（前面是空格 / 点 / 短横 / 下划线 / 斜杠）单独一档，因为 "studio" 搜 "dio"
    /// 不该和搜 "stu" 一样的权重。
    /// </summary>
    private static int Score(string text, string query)
    {
        int i = IndexOf(text, query);
        if (i < 0) return 0;

        if (i == 0)
        {
            if (text.Length == query.Length) return 200;
            return 150;
        }

        char prev = text[i - 1];
        bool wordStart = char.IsWhiteSpace(prev) || prev is '.' or '-' or '_' or '/' or '\\' or '(' or '[';
        if (wordStart) return 120;

        // 越靠后越不值钱，但压到 0 会让顺序随机
        return 90 - Math.Min(i, 40);
    }

    private static int IndexOf(string? text, string query) =>
        string.IsNullOrEmpty(text) ? -1 : text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>正文命中：截一段带上下文的片段，两端补省略号。</summary>
    private static MatchText Snippet(string body, string query)
    {
        int i = IndexOf(body, query);
        if (i < 0) return MatchText.Plain(Preview(body));

        int start = Math.Max(0, i - SnippetContext);
        int end = Math.Min(body.Length, i + query.Length + SnippetContext);

        string prefix = (start > 0 ? "…" : string.Empty) + Clean(body[start..i]);
        string match = body.Substring(i, query.Length);
        string suffix = Clean(body[(i + query.Length)..end]) + (end < body.Length ? "…" : string.Empty);

        return new MatchText(prefix, match, suffix);
    }

    private static string Preview(string body)
    {
        string one = Clean(body);
        return one.Length <= PreviewMax ? one : one[..PreviewMax] + "…";
    }

    private static string FirstLine(string body)
    {
        string one = Clean(body);
        return one.Length <= PreviewMax ? one : one[..PreviewMax] + "…";
    }

    /// <summary>换行压成空格：一行里出现换行会把行高撑坏，而备忘是随手敲的，换行到处都是。</summary>
    private static string Clean(string s) =>
        s.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
