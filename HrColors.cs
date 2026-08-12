using Windows.UI;

namespace HeartRater;

/// <summary>心率颜色映射：绿 &lt;100 → 黄 100-119 → 橙 120-139 → 红 ≥140。</summary>
public static class HrColors
{
    public static readonly Color Green = Color.FromArgb(0xFF, 0x00, 0xE6, 0x76);
    public static readonly Color Yellow = Color.FromArgb(0xFF, 0xFF, 0xD6, 0x00);
    public static readonly Color Orange = Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00);
    public static readonly Color Red = Color.FromArgb(0xFF, 0xFF, 0x3B, 0x30);

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
