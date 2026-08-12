using System;
using System.Collections.Generic;

namespace HeartRater.Services;

/// <summary>解析结果。</summary>
public sealed class HeartRateData
{
    public int Bpm { get; init; }
    public int? EnergyExpended { get; init; }
    public IReadOnlyList<int>? RrIntervalsMs { get; init; }
}

/// <summary>
/// 标准 BLE 心率服务 (0x180D) 心率测量特征 (0x2A37) 数据解析器。
/// 参考 Bluetooth SIG 心率服务规范。
/// </summary>
public static class HeartRateParser
{
    public static HeartRateData? Parse(byte[] data)
    {
        if (data == null || data.Length < 2)
        {
            return null;
        }

        // Flags 字节：bit0=心率格式(0:uint8, 1:uint16)，bit3=能量消耗，bit4=RR 间期
        byte flags = data[0];
        int offset = 1;

        int bpm;
        if ((flags & 0x01) != 0)
        {
            if (offset + 2 > data.Length)
            {
                return null;
            }

            bpm = data[offset] | (data[offset + 1] << 8);
            offset += 2;
        }
        else
        {
            bpm = data[offset];
            offset += 1;
        }

        int? energy = null;
        if ((flags & 0x08) != 0)
        {
            if (offset + 1 > data.Length)
            {
                return null;
            }

            energy = data[offset];
            offset += 1;
        }

        List<int>? rrIntervals = null;
        if ((flags & 0x10) != 0)
        {
            // RR 间期：uint16 数组，单位 1/1024 秒 → 毫秒
            rrIntervals = new List<int>();
            while (offset + 2 <= data.Length)
            {
                ushort rrRaw = (ushort)(data[offset] | (data[offset + 1] << 8));
                offset += 2;
                if (rrRaw == 0)
                {
                    continue;
                }

                // 1.024ms 精度
                int ms = (int)Math.Round(rrRaw * 1.024);
                rrIntervals.Add(ms);
            }
        }

        return new HeartRateData
        {
            Bpm = bpm,
            EnergyExpended = energy,
            RrIntervalsMs = rrIntervals,
        };
    }
}
