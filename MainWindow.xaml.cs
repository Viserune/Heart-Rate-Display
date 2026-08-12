using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HeartRater.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace HeartRater;

public sealed partial class MainWindow : Window
{
    private const int SW_SHOW = 5;

    private readonly SettingsService _settings;
    private readonly BleHeartRateService _ble;
    private readonly HudWindow _hud;
    private readonly TrayIconService _tray;

    private readonly ObservableCollection<BleDeviceInfo> _devices = new();
    private bool _suppressToggleEvents;
    private int _lastBpm = -1;

    public MainWindow(SettingsService settings, BleHeartRateService ble, HudWindow hud, TrayIconService tray)
    {
        _settings = settings;
        _ble = ble;
        _hud = hud;
        _tray = tray;

        InitializeComponent();
        Title = "HeartRater 心率助手";

        DeviceList.ItemsSource = _devices;

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }

        var size = _settings.Current;
        if (size.MainWidth >= 300 && size.MainHeight >= 400)
        {
            AppWindow.Resize(new SizeInt32((int)size.MainWidth, (int)size.MainHeight));
        }

        // 关闭 = 隐藏到托盘
        Closed += OnWindowClosed;

        // 蓝牙事件
        _ble.StateChanged += OnBleStateChanged;
        _ble.HeartRateReceived += OnHeartRateReceived;
        _ble.StatusMessage += s => SetStatus(s);
        _ble.ErrorOccurred += OnBleError;
        _ble.Disconnected += OnBleDisconnected;

        // 托盘事件
        _tray.ShowMainRequested += ShowMain;
        _tray.ToggleHudRequested += OnTrayToggleHud;
        _tray.ToggleDemoRequested += OnTrayToggleDemo;
        _tray.ExitRequested += OnExitRequested;

        LoadSettingsIntoUi();
        SetStatus("就绪，点击“扫描设备”开始");
    }

    private void LoadSettingsIntoUi()
    {
        var s = _settings.Current;
        _suppressToggleEvents = true;
        AutoConnectToggle.IsOn = s.AutoConnectOnStart;
        AutoStartToggle.IsOn = s.AutoStartEnabled;
        AutoReconnectToggle.IsOn = s.AutoReconnect;
        HudToggle.IsOn = s.HudVisible;
        ClickThroughToggle.IsOn = s.HudClickThrough;
        _suppressToggleEvents = false;
    }

    // ==================== 主窗口显示/隐藏 ====================

    public void ShowMain()
    {
        ShowWindow(WindowNative.GetWindowHandle(this), SW_SHOW);
        Activate();
    }

    // ---- 供 App 调用的公开入口（托盘事件） ----
    public void ShowMainPublic() => ShowMain();

    public void OnTrayToggleHudPublic() => OnTrayToggleHud();

    public Task ToggleDemoPublicAsync() => ToggleDemoAsync();

    public void SetStatusPublic(string message) => SetStatus(message);

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        args.Handled = true;
        HideWindowToTray();
    }

    public void HideWindowToTray()
    {
        AppWindow.Hide();
        _tray.ShowBalloon("HeartRater", "已最小化到系统托盘，双击图标可重新打开");
    }

    // ==================== 蓝牙 UI ====================

    private async void OnScanClicked(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        LoadingOverlay.Visibility = Visibility.Visible;
        OverlayText.Text = "正在扫描蓝牙设备…";

        try
        {
            var results = await _ble.ScanAsync(TimeSpan.FromSeconds(6));
            _devices.Clear();
            foreach (var d in results.OrderByDescending(x => x.HasHeartRateService).ThenBy(x => x.Name))
            {
                _devices.Add(d);
            }

            if (_devices.Count == 0)
            {
                SetStatus("未发现设备，请确认设备已开机且蓝牙已开启");
            }
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = true;
        }
    }

    private void OnDeviceFilterChanged(object sender, TextChangedEventArgs e)
    {
        var filter = DeviceFilterBox.Text?.Trim() ?? "";
        DeviceList.ItemsSource = null;
        var source = string.IsNullOrEmpty(filter)
            ? _devices
            : new ObservableCollection<BleDeviceInfo>(
                _devices.Where(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        DeviceList.ItemsSource = source;
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConnectButton.IsEnabled = DeviceList.SelectedItem is BleDeviceInfo;
    }

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not BleDeviceInfo device)
        {
            return;
        }

        ConnectButton.IsEnabled = false;
        var ok = await _ble.ConnectAsync(device.Key, device.Name, _settings.Current.AutoReconnect);
        if (ok)
        {
            _settings.Current.LastDeviceId = device.Key;
            _settings.Current.LastDeviceName = device.Name;
            _settings.Save();
        }

        ConnectButton.IsEnabled = true;
    }

    private async void OnDisconnectClicked(object sender, RoutedEventArgs e)
    {
        await _ble.DisconnectAsync();
        HeartRateDisplay.Text = "--";
        HeartRateDisplay.Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        _lastBpm = -1;
    }

    // ==================== 蓝牙事件 ====================

    private void OnBleStateChanged(BleConnectionState state)
    {
        DeviceNameText.Text = state switch
        {
            BleConnectionState.Scanning => "正在扫描…",
            BleConnectionState.Connecting => "正在连接…",
            BleConnectionState.Connected => _ble.ConnectedDeviceName ?? "已连接",
            _ => _ble.IsDemoMode ? "演示模式" : "未连接设备",
        };
    }

    private void OnHeartRateReceived(int bpm)
    {
        _lastBpm = bpm;
        HeartRateDisplay.Text = bpm.ToString();
        HeartRateDisplay.Foreground = new SolidColorBrush(HrColors.GetColor(bpm));
        _hud.SetHeartRate(bpm);
    }

    private void OnBleError(string message)
    {
        SetStatus(message);
        _tray.ShowBalloon("HeartRater 蓝牙", message);
    }

    private void OnBleDisconnected()
    {
        _hud.SetHeartRate(-1);
        HeartRateDisplay.Text = "--";
        HeartRateDisplay.Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        _lastBpm = -1;
        if (_settings.Current.AutoReconnect)
        {
            _tray.ShowBalloon("HeartRater", "连接已断开，正在自动重连…");
        }
    }

    // ==================== 演示模式 ====================

    private async void OnDemoClicked(object sender, RoutedEventArgs e)
    {
        await ToggleDemoAsync();
    }

    private async void OnTrayToggleDemo()
    {
        await ToggleDemoAsync();
    }

    private async Task ToggleDemoAsync()
    {
        if (_ble.IsDemoMode)
        {
            await _ble.StopDemoAsync();
            _tray.DemoRunning = false;
            HeartRateDisplay.Text = "--";
            HeartRateDisplay.Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            SetStatus("已退出演示模式");
        }
        else
        {
            _ble.StartDemo();
            _tray.DemoRunning = true;
            _tray.ShowBalloon("HeartRater", "演示模式已开启（模拟心率数据）");
        }
    }

    // ==================== 悬浮窗 ====================

    private void OnHudToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
        {
            return;
        }

        _settings.Current.HudVisible = HudToggle.IsOn;
        _settings.Save();
        ApplyHudVisibility();
    }

    private void OnClickThroughToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
        {
            return;
        }

        _settings.Current.HudClickThrough = ClickThroughToggle.IsOn;
        _settings.Save();
        _hud.ClickThrough = ClickThroughToggle.IsOn;
    }

    private void OnTrayToggleHud()
    {
        _settings.Current.HudVisible = !_settings.Current.HudVisible;
        _settings.Save();
        HudToggle.IsOn = _settings.Current.HudVisible; // 触发 Toggled（suppress 已关闭）
        ApplyHudVisibility();
    }

    private void ApplyHudVisibility()
    {
        if (_settings.Current.HudVisible)
        {
            _hud.ShowHud();
        }
        else
        {
            _hud.HideHud();
        }
    }

    // ==================== 设置 ====================

    private void OnAutoConnectToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
        {
            return;
        }

        _settings.Current.AutoConnectOnStart = AutoConnectToggle.IsOn;
        _settings.Save();
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
        {
            return;
        }

        AutoStartService.SetEnabled(AutoStartToggle.IsOn);
        _settings.Current.AutoStartEnabled = AutoStartToggle.IsOn;
        _settings.Save();
        SetStatus(AutoStartToggle.IsOn ? "已开启开机自启" : "已关闭开机自启");
    }

    private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
        {
            return;
        }

        _settings.Current.AutoReconnect = AutoReconnectToggle.IsOn;
        _settings.Save();
    }

    // ==================== 退出 ====================

    private void OnExitRequested()
    {
        App.Current?.Exit();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
