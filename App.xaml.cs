using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using HeartRater.Services;

namespace HeartRater;

public partial class App : Application
{
    private MainWindow? _main;
    private HudWindow? _hud;
    private TrayIconService? _tray;
    private BleHeartRateService? _ble;
    private SettingsService? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 关闭主窗口不退出（驻留托盘），仅托盘“退出”真正退出
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settings = new SettingsService();
        Action<Action> dispatch = a => Dispatcher.BeginInvoke(a);
        _ble = new BleHeartRateService(dispatch);
        _hud = new HudWindow(_settings);
        _tray = new TrayIconService(dispatch, IconPath);
        _main = new MainWindow(_settings, _ble, _hud, _tray);

        // 托盘事件
        _tray.ShowMainRequested += ShowMainWindow;
        _tray.ToggleHudRequested += () => _main?.ToggleHudFromTrayPublic();
        _tray.ToggleLockRequested += () => _main?.ToggleLockFromTrayPublic();
        _tray.ExitRequested += Shutdown;

        _tray.Show();

        // 启动流程
        var minimized = e.Args.Contains("--minimized");
        if (!minimized)
        {
            _main.Show();
        }

        _hud.ApplyHudFromSettings();

        if (_settings.Current.AutoConnectOnStart && !string.IsNullOrEmpty(_settings.Current.LastDeviceId))
        {
            _main.SetStatus("正在自动连接上次设备…");
            _ = TryAutoConnectAsync();
        }
    }

    private async Task TryAutoConnectAsync()
    {
        var settings = _settings!;
        var ok = await _ble!.AutoConnectLastAsync(
            settings.Current.LastDeviceId!,
            settings.Current.LastDeviceName,
            autoReconnect: settings.Current.AutoReconnect);

        if (!ok)
        {
            _tray?.ShowBalloon("HeartRater", "自动连接失败：未检测到上次连接的设备，请在主界面重新连接");
        }
    }

    private void ShowMainWindow()
    {
        _main?.ShowMainPublic();
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
