using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HeartRater.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HeartRater;

public partial class App : Application
{
    private readonly string[] _args;
    private readonly DispatcherQueue _uiQueue;
    private MainWindow? _mainWindow;
    private HudWindow? _hud;
    private TrayIconService? _tray;
    private BleHeartRateService? _ble;
    private SettingsService? _settings;

    public App(string[] args)
    {
        _args = args;
        _uiQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"未处理异常: {e.Exception}");
            e.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _settings = new SettingsService();
        _ble = new BleHeartRateService(_uiQueue);
        _hud = new HudWindow(_settings);
        _tray = new TrayIconService(_uiQueue, IconPath);
        _mainWindow = new MainWindow(_settings, _ble, _hud, _tray);

        // 托盘事件
        _tray.ShowMainRequested += ShowMainWindow;
        _tray.ToggleHudRequested += () => _mainWindow?.OnTrayToggleHudPublic();
        _tray.ToggleDemoRequested += () => _ = _mainWindow?.ToggleDemoPublicAsync();
        _tray.ExitRequested += () => Exit();

        _tray.Show();

        // 启动流程
        var minimized = _args.Contains("--minimized");
        if (!minimized)
        {
            _mainWindow.Activate();
        }

        _hud.ApplyHudFromSettingsPublic();

        if (_settings.Current.AutoConnectOnStart && !string.IsNullOrEmpty(_settings.Current.LastDeviceId))
        {
            _mainWindow.SetStatusPublic("正在自动连接上次设备…");
            _ = TryAutoConnectAsync();
        }
    }

    private async Task TryAutoConnectAsync()
    {
        var settings = _settings!;
        var ok = await _ble!.ConnectAsync(
            settings.Current.LastDeviceId!,
            settings.Current.LastDeviceName,
            autoReconnect: settings.Current.AutoReconnect);

        if (!ok)
        {
            _tray?.ShowBalloon("HeartRater", "自动连接失败，可在主界面重新选择设备");
        }
    }

    private void ShowMainWindow()
    {
        _mainWindow?.ShowMainPublic();
    }

    private string IconPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"),
                Path.Combine(AppContext.BaseDirectory, "icon.ico"),
            };
            return candidates.FirstOrDefault(File.Exists) ?? "";
        }
    }
}
