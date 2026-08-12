using System.Windows.Media;

namespace HeartRater;

/// <summary>心率颜色映射：绿 &lt;100 → 黄 100-119 → 橙 120-139 → 红 ≥140。</summary>
public static class HrColors
{
    public static readonly Color Green = Color.FromRgb(0x00, 0x99, 0x51);
    public static readonly Color Yellow = Color.FromRgb(0xE6, 0xA8, 0x00);
    public static readonly Color Orange = Color.FromRgb(0xE8, 0x5D, 0x00);
    public static readonly Color Red = Color.FromRgb(0xD9, 0x2C, 0x20);

    public static Color GetColor(int bpm)
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
}
