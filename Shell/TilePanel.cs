using System;
using System.Windows;
using System.Windows.Controls;

namespace Paperwork.Shell;

/// <summary>
/// 磁贴网格。D7 的落点：
/// <b>图标尺寸决定能放几列，列宽再把剩余宽度均分</b>——所以拉伸窗口只会重排列数，
/// 磁贴里的图标始终是那么大。
///
/// 不用 WrapPanel 是因为它会把右侧留成参差空白；不用 UniformGrid 是因为它列数固定、
/// 不会随宽度自动重排。
/// </summary>
public sealed class TilePanel : Panel
{
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(TilePanel),
            new FrameworkPropertyMetadata(42.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(TilePanel),
            new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>
    /// 单列最小宽度 = 图标 + 左右内边距 + 两字标签的可读下限。
    /// 系数 2.05 是校准出来的：42px 图标在 524px 面板上正好 4 列、32px 时 5 列、56px 时 3 列，
    /// 与设计稿一致。取 1.9 会让 42px 多挤出一列。
    /// </summary>
    private double MinTileWidth => IconSize * 2.05 + 16;

    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize, arrange: false);

    protected override Size ArrangeOverride(Size finalSize) => Layout(finalSize, arrange: true);

    private Size Layout(Size available, bool arrange)
    {
        int count = InternalChildren.Count;
        double width = double.IsInfinity(available.Width) || available.Width <= 0
            ? MinTileWidth
            : available.Width;

        if (count == 0) return new Size(width, 0);

        // 列数只由面板宽度决定，**不收 count 的限制**：
        // 收了的话磁贴少的层（比如只有两块的新组合）会被均分出超宽的列，
        // 两块磁贴隔着大半个面板，跟首页的观感完全对不上。
        // 空出来的格子就空着，磁贴靠左排——这正是"每格宽度与首页一致"的意思。
        int cols = Math.Max(1, (int)Math.Floor((width + Gap) / (MinTileWidth + Gap)));
        double tileWidth = Math.Max(MinTileWidth, (width - (cols - 1) * Gap) / cols);

        int rows = (count + cols - 1) / cols;
        var rowHeights = new double[rows];

        // 先量（或复用上一轮 DesiredSize），并逐行取最大高度
        for (int i = 0; i < count; i++)
        {
            UIElement child = InternalChildren[i];
            if (!arrange) child.Measure(new Size(tileWidth, double.PositiveInfinity));

            int row = i / cols;
            double h = child.DesiredSize.Height;
            if (h > rowHeights[row]) rowHeights[row] = h;
        }

        double total = 0;
        for (int r = 0; r < rows; r++) total += rowHeights[r] + (r > 0 ? Gap : 0);

        if (arrange)
        {
            double y = 0;
            for (int r = 0; r < rows; r++)
            {
                int first = r * cols;
                int last = Math.Min(count, first + cols);
                double rowH = rowHeights[r];

                for (int i = first; i < last; i++)
                {
                    UIElement child = InternalChildren[i];
                    double h = child.DesiredSize.Height;
                    child.Arrange(new Rect(
                        (i - first) * (tileWidth + Gap),
                        y + (rowH - h) / 2,          // 行内垂直居中，标签行数不同也不会顶齐
                        tileWidth,
                        h));
                }

                y += rowH + Gap;
            }
        }

        return new Size(width, Math.Max(0, total));
    }
}
