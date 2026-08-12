using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace HeartRater.Services;

/// <summary>
/// 窗口外观工具：Windows 11 22621+ 启用 Mica 背景 + 系统圆角，其余系统用主题渐变兜底。
/// Mica 自动跟随系统深浅色。
/// </summary>
public static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>在窗口 SourceInitialized 后调用（hwnd 已存在）。</summary>
    public static void ApplyMica(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (Environment.OSVersion.Version.Build >= 22621)
            {
                // DWMWA_SYSTEMBACKDROP_TYPE = 38，Mica = 2；窗口背景置透明让 Mica 透出
                var backdropType = 2;
                DwmSetWindowAttribute(hwnd, 38, ref backdropType, sizeof(int));
                // DWMWA_WINDOW_CORNER_PREFERENCE = 33，圆角 = 2
                var cornerPref = 2;
                DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));
                window.Background = Brushes.Transparent;
            }
            else if (window.FindResource("WindowFallbackBrush") is Brush fallback)
            {
                // 非 Win11：主题渐变兜底
                window.Background = fallback;
            }
        }
        catch
        {
            // DWM 调用失败（如旧系统）→ 保留 XAML 主题背景
        }
    }
}
