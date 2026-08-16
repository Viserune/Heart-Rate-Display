using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeartRater.ViewModels;

namespace HeartRater.Services;

/// <summary>应用设置（JSON 持久化到 %LOCALAPPDATA%\HeartRater\settings.json）。
/// 继承 ObservableObject：绑定直接双向同步，变更由 SettingsService 自动落盘。</summary>
public sealed class AppSettings : ObservableObject
{
    private string? _lastDeviceId;
    private string? _lastDeviceName;
    private bool _autoConnectOnStart = true;
    private bool _autoStartEnabled;
    private bool _autoReconnect = true;
    private bool _hudVisible = true;
    private bool _hudLocked;
    private double _hudX = -1;
    private double _hudY = -1;
    private double _mainWidth = 430;

    /// <summary>上次连接成功的蓝牙设备地址（如 11:22:33:44:55:66）。</summary>
    public string? LastDeviceId
    {
        get => _lastDeviceId;
        set => SetProperty(ref _lastDeviceId, value);
    }

    /// <summary>上次连接成功的设备显示名称。</summary>
    public string? LastDeviceName
    {
        get => _lastDeviceName;
        set => SetProperty(ref _lastDeviceName, value);
    }

    /// <summary>启动时自动连接上次设备。</summary>
    public bool AutoConnectOnStart
    {
        get => _autoConnectOnStart;
        set => SetProperty(ref _autoConnectOnStart, value);
    }

    /// <summary>开机自动启动（托盘驻留）。</summary>
    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set => SetProperty(ref _autoStartEnabled, value);
    }

    /// <summary>断线自动重连（不死鸟模式）。</summary>
    public bool AutoReconnect
    {
        get => _autoReconnect;
        set => SetProperty(ref _autoReconnect, value);
    }

    /// <summary>悬浮窗可见。</summary>
    public bool HudVisible
    {
        get => _hudVisible;
        set => SetProperty(ref _hudVisible, value);
    }

    /// <summary>悬浮窗锁定：禁拖动 + 自动点击穿透（全屏下可点击后面）。</summary>
    public bool HudLocked
    {
        get => _hudLocked;
        set => SetProperty(ref _hudLocked, value);
    }

    /// <summary>悬浮窗 X（屏幕坐标，-1 表示默认右上角）。</summary>
    public double HudX
    {
        get => _hudX;
        set => SetProperty(ref _hudX, value);
    }

    /// <summary>悬浮窗 Y。</summary>
    public double HudY
    {
        get => _hudY;
        set => SetProperty(ref _hudY, value);
    }

    /// <summary>主窗口上次宽度（高度由内容自适应，不记忆）。</summary>
    public double MainWidth
    {
        get => _mainWidth;
        set => SetProperty(ref _mainWidth, value);
    }
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

        // 加载完成后订阅：之后任何属性变更自动落盘（加载期间的 setter 不会触发）
        Current.PropertyChanged += (_, _) => Save();
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

    /// <summary>把当前设置写入磁盘（属性变更已自动触发，托盘等外部修改也走这里）。</summary>
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
