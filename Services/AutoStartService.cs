using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HeartRater.Services;

/// <summary>
/// 开机自启：写 HKCU\...\CurrentVersion\Run，指向当前 exe，并带 --minimized 参数驻留托盘。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HeartRater";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取自启注册表失败: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                return;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    exePath = Path.Combine(AppContext.BaseDirectory, "HeartRater.exe");
                }

                // 引号包裹路径，防止含空格
                key.SetValue(ValueName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"设置自启失败: {ex.Message}");
        }
    }
}
