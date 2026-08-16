using System.Windows.Media;

namespace HeartRater;

/// <summary>心率颜色映射：绿 &lt;100 → 黄 100-119 → 橙 120-139 → 红 ≥140。
/// Brush 为静态 Frozen 实例，心率更新时直接复用，避免每次 new SolidColorBrush 的分配。</summary>
public static class HrColors
{
    private static readonly Brush Green = Frozen(Color.FromRgb(0x00, 0x99, 0x51));
    private static readonly Brush Yellow = Frozen(Color.FromRgb(0xE6, 0xA8, 0x00));
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xE8, 0x5D, 0x00));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xD9, 0x2C, 0x20));

    /// <summary>未连接/无数据时的占位色（灰）。</summary>
    public static readonly Brush PlaceholderBrush = Frozen(Color.FromRgb(0x99, 0x99, 0x99));

    public static Brush GetBrush(int bpm)
    {
        if (bpm < 100)
        {
            return Green;
        }

        if (bpm < 120)
        {
            return Yellow;
        }

        if (bpm < 140)
        {
            return Orange;
        }

        return Red;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
