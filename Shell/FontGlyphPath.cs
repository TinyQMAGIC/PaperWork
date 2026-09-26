using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Paperwork.Shell;

/// <summary>
/// 把字体里的一个字形取成 <see cref="Geometry"/>，用于**笔宽可调**的图标。
///
/// 为什么需要它：<b>图标字体只提供离散的字重</b>。Segoe Fluent Icons 的
/// <c>FamilyTypefaces</c> 只有 Normal 和 Bold 两套——实测请求 Medium(500) 与 SemiBold(600)
/// 都会被匹配算法回落到这两档之一，量出来的墨量和 Normal / Bold 完全一致。
/// 于是"比 Bold 细一点点"这件事用字重根本表达不出来，只能二选一，
/// 而两个极端都不合用：15px 的 Normal 太虚，Bold 又压手。
///
/// 取成路径之后粗细就是连续的：字形轮廓用 <c>Fill</c> 画（= Normal 档），
/// 再叠一层同色描边把笔画均匀加粗。实测标尺（15px，96 DPI，与字体渲染同一条管线）：
///
/// <code>
///   StrokeThickness 0          → 墨量 61.5   = 字形自带的 Normal
///   StrokeThickness 0.10       → 墨量 68.0
///   StrokeThickness 0.15       → 墨量 71.5
///   StrokeThickness 0.20       → 墨量 75.0
///   StrokeThickness 0.25       → 墨量 77.6   ← 当前使用：比 Bold 细一点点
///   StrokeThickness 0.30       → 墨量 81.3   = 字形自带 Bold 的笔画粗细
/// </code>
///
/// <b>一个必须知道的坑：字体渲染器会加深抗锯齿边缘。</b>
/// 同一个轮廓，走字体渲染和走路径渲染，覆盖的像素数几乎一样（128 vs 131），
/// 但墨量差 16%（71.4 vs 61.5）—— 差值全在边缘的 alpha 上，这是字体在
/// 小字号下的笔画补偿。后果是：<b>同样的几何粗细，路径渲染看起来会轻一档</b>。
/// 所以上面这张表里，"和字体 Bold 等重"对应的是 0.30 这个几何值，
/// 而不是让墨量数字相等（那需要更大的描边，几何上会比 Bold 更粗、齿缝会糊掉）。
///
/// 形状取自系统字体本身，所以不会和隔壁那个仍在用字体的 ✕ 走形；
/// 想换粗细只改 <c>Path.StrokeThickness</c>，不用碰任何坐标。
/// </summary>
internal static class FontGlyphPath
{
    /// <summary>
    /// 取一个字形轮廓。<paramref name="families"/> 用逗号分隔，语义与 XAML 里的
    /// <c>FontFamily="A, B"</c> 相同（前一个字体缺这个字形时往后回落）。
    /// 取不到就返回 <c>null</c>，调用方保持"没有图标"而不是崩掉。
    /// </summary>
    public static Geometry? Build(string families, string glyph, double emSize)
    {
        try
        {
            var typeface = new Typeface(
                new FontFamily(families), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // pixelsPerDip 钉死 1.0：这里要的是"设计坐标下的外形"，不是某个 DPI 下的栅格化结果。
            // 传真实 DPI 会把像素对齐的量化误差烤进轮廓里，换台机器就变形。
            var formatted = new FormattedText(
                glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, emSize, Brushes.Black, pixelsPerDip: 1.0);

            Geometry? geo = formatted.BuildGeometry(new Point(0, 0));
            if (geo is null || geo.IsEmpty()) return null;

            // 把 bounds 的左上角归到原点。
            //
            // 为什么自己动手：Shape 摆放几何时要按 bounds 算一次偏移，而字形轮廓的左边界
            // 通常是 0.41 这种小数。归位与否差的就是这零点几像素——与其去猜 Shape 内部
            // 到底怎么摆，不如先把它按到整数起点，让最终位置只由 Width/Height + 对齐方式决定。
            Rect bounds = geo.Bounds;
            if (bounds.X != 0 || bounds.Y != 0)
            {
                Geometry moved = geo.Clone();
                moved.Transform = new TranslateTransform(-bounds.X, -bounds.Y);
                geo = moved;
            }

            // 图标几何是全窗口共享、每帧都要读的常驻资源。冻结之后省掉每个 Visual
            // 各维护一份变更订阅和边界缓存的开销（和 Glyphs.cs 里那些 Geometry 同一个理由）。
            geo.Freeze();
            return geo;
        }
        catch (Exception)
        {
            // 字体缺失 / 字形不存在 / 轮廓为空：退化成"没有这个图标"，不能影响窗口起来
            return null;
        }
    }
}
