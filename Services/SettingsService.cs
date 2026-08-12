using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeartRater.Services;

/// <summary>应用设置（JSON 持久化到 %LOCALAPPDATA%\HeartRater\settings.json）。</summary>
public sealed class AppSettings
{
    /// <summary>上次连接成功的蓝牙设备地址（如 11:22:33:44:55:66）。</summary>
    public string? LastDeviceId { get; set; }

    /// <summary>上次连接成功的设备显示名称。</summary>
    public string? LastDeviceName { get; set; }

    /// <summary>启动时自动连接上次设备。</summary>
    public bool AutoConnectOnStart { get; set; } = true;

    /// <summary>开机自动启动（托盘驻留）。</summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>断线自动重连（不死鸟模式）。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>悬浮窗可见。</summary>
    public bool HudVisible { get; set; } = true;

    /// <summary>悬浮窗锁定：禁拖动 + 自动点击穿透（全屏下可点击后面）。</summary>
    public bool HudLocked { get; set; }

    /// <summary>悬浮窗 X（屏幕坐标，-1 表示默认右上角）。</summary>
    public double HudX { get; set; } = -1;

    /// <summary>悬浮窗 Y。</summary>
    public double HudY { get; set; } = -1;

    /// <summary>主窗口上次宽度（高度由内容自适应，不记忆）。</summary>
    public double MainWidth { get; set; } = 430;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public AppSettings Current { get; }

    public SettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeartRater");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取设置失败: {ex.Message}");
        }
        return new AppSettings();
    }

    /// <summary>保存当前设置到磁盘。</summary>
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
            }
        }
    }
}
