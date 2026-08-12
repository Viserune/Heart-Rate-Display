using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HeartRater.Services;

namespace HeartRater;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;
    private readonly BleHeartRateService _ble;
    private readonly HudWindow _hud;
    private readonly TrayIconService _tray;

    private readonly ObservableCollection<BleDeviceInfo> _devices = new();
    private bool _suppressToggleEvents;

    public MainWindow(SettingsService settings, BleHeartRateService ble, HudWindow hud, TrayIconService tray)
    {
        _settings = settings;
        _ble = ble;
        _hud = hud;
        _tray = tray;

        InitializeComponent();

        DeviceList.ItemsSource = _devices;

        // 蓝牙事件
        _ble.StateChanged += OnBleStateChanged;
        _ble.HeartRateReceived += OnHeartRateReceived;
        _ble.StatusMessage += s => SetStatus(s);
        _ble.ErrorOccurred += OnBleError;
        _ble.Disconnected += OnBleDisconnected;

        // 托盘事件
        _tray.ShowMainRequested += ShowMainPublic;
        _tray.ToggleHudRequested += ToggleHudFromTrayPublic;
        _tray.ToggleDemoRequested += ToggleDemoFromTrayPublic;
        _tray.ToggleLockRequested += ToggleLockFromTrayPublic;
        _tray.ExitRequested += () => Application.Current.Shutdown();

        LoadSettingsIntoUi();

        SourceInitialized += (_, _) =>
        {
            var s = _settings.Current;
            if (s.MainWidth >= 360 && s.MainHeight >= 500)
            {
                Width = s.MainWidth;
                Height = s.MainHeight;
            }
        };
    }

    // ==================== 公开入口（托盘 / App 调用） ====================

    public void ShowMainPublic()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void ToggleHudFromTrayPublic()
    {
        _settings.Current.HudVisible = !_settings.Current.HudVisible;
        _settings.Save();
        _suppressToggleEvents = true;
        HudToggle.IsChecked = _settings.Current.HudVisible;
        _suppressToggleEvents = false;
        ApplyHudVisibility();
    }

    public void ToggleDemoFromTrayPublic()
    {
        _ = ToggleDemoAsync();
    }

    public void ToggleLockFromTrayPublic()
    {
        _settings.Current.HudLocked = !_settings.Current.HudLocked;
        _settings.Save();
        _suppressToggleEvents = true;
        LockedToggle.IsChecked = _settings.Current.HudLocked;
        _suppressToggleEvents = false;
        _hud.Locked = _settings.Current.HudLocked;
        _tray.Locked = _settings.Current.HudLocked;
        SetStatus(_settings.Current.HudLocked ? "已锁定悬浮窗" : "已解锁悬浮窗");
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    // ==================== 设置加载 ====================

    private void LoadSettingsIntoUi()
    {
        var s = _settings.Current;
        _suppressToggleEvents = true;
        AutoConnectToggle.IsChecked = s.AutoConnectOnStart;
        AutoStartToggle.IsChecked = s.AutoStartEnabled;
        AutoReconnectToggle.IsChecked = s.AutoReconnect;
        HudToggle.IsChecked = s.HudVisible;
        ClickThroughToggle.IsChecked = s.HudClickThrough;
        LockedToggle.IsChecked = s.HudLocked;
        _tray.Locked = s.HudLocked;
        _suppressToggleEvents = false;
    }

    // ==================== 蓝牙 UI ====================

    private async void OnScanClicked(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        SetStatus("正在扫描蓝牙设备…");

        try
        {
            var results = await _ble.ScanAsync(TimeSpan.FromSeconds(6));
            _devices.Clear();
            foreach (var d in results.OrderByDescending(x => x.HasHeartRateService).ThenBy(x => x.Name))
            {
                _devices.Add(d);
            }

            SetStatus(_devices.Count == 0
                ? "未发现设备，请确认设备已开机且蓝牙已开启"
                : $"扫描完成，发现 {_devices.Count} 个设备");
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void OnDeviceFilterChanged(object sender, TextChangedEventArgs e)
    {
        var filter = DeviceFilterBox.Text?.Trim() ?? "";
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
        SetHeartRateDisplay(-1);
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
        SetHeartRateDisplay(-1);
        _hud.SetHeartRate(-1);
        if (_settings.Current.AutoReconnect)
        {
            _tray.ShowBalloon("HeartRater", "连接已断开，正在自动重连…");
        }
    }

    private void SetHeartRateDisplay(int bpm)
    {
        if (bpm <= 0)
        {
            HeartRateDisplay.Text = "--";
            HeartRateDisplay.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            return;
        }

        HeartRateDisplay.Text = bpm.ToString();
        HeartRateDisplay.Foreground = new SolidColorBrush(HrColors.GetColor(bpm));
    }

    // ==================== 演示模式 ====================

    private async void OnDemoClicked(object sender, RoutedEventArgs e)
    {
        await ToggleDemoAsync();
    }

    private async Task ToggleDemoAsync()
    {
        if (_ble.IsDemoMode)
        {
            await _ble.StopDemoAsync();
            _tray.DemoRunning = false;
            SetHeartRateDisplay(-1);
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

        _settings.Current.HudVisible = HudToggle.IsChecked == true;
        _settings.Save();
        ApplyHudVisibility();
    }

    private void OnClickThroughToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
        {
            return;
        }

        _settings.Current.HudClickThrough = ClickThroughToggle.IsChecked == true;
        _settings.Save();
        _hud.ClickThrough = ClickThroughToggle.IsChecked == true;
    }

    private void OnLockedToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
        {
            return;
        }

        _settings.Current.HudLocked = LockedToggle.IsChecked == true;
        _settings.Save();
        _hud.Locked = LockedToggle.IsChecked == true;
        _tray.Locked = LockedToggle.IsChecked == true;
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

        _settings.Current.AutoConnectOnStart = AutoConnectToggle.IsChecked == true;
        _settings.Save();
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
        {
            return;
        }

        var enabled = AutoStartToggle.IsChecked == true;
        AutoStartService.SetEnabled(enabled);
        _settings.Current.AutoStartEnabled = enabled;
        _settings.Save();
        SetStatus(enabled ? "已开启开机自启" : "已关闭开机自启");
    }

    private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
        {
            return;
        }

        _settings.Current.AutoReconnect = AutoReconnectToggle.IsChecked == true;
        _settings.Save();
    }

    // ==================== 关闭行为 ====================

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        // 关闭主窗口 → 隐藏到托盘（不退出）
        e.Cancel = true;
        _settings.Current.MainWidth = ActualWidth;
        _settings.Current.MainHeight = ActualHeight;
        _settings.Save();
        Hide();
        _tray.ShowBalloon("HeartRater", "已最小化到系统托盘，双击图标可重新打开");
    }
}
