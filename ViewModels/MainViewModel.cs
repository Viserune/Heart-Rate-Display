using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using HeartRater.Services;

namespace HeartRater.ViewModels;

/// <summary>主界面 ViewModel：聚合 BLE 服务与设置，驱动扫描/连接/心率显示/设置开关。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly BleHeartRateService _ble;

    private readonly ObservableCollection<BleDeviceInfo> _devices = new();
    private BleDeviceInfo? _selectedDevice;
    private string _deviceNameText = "未连接设备";
    private string _heartRateDisplay = "--";
    private Brush _heartRateBrush = HrColors.PlaceholderBrush;
    private string _statusText = "就绪，点击“扫描设备”开始";
    private bool _isScanning;
    private bool _isConnecting;

    public MainViewModel(BleHeartRateService ble, AppSettings settings)
    {
        _ble = ble;
        Settings = settings;

        // BLE 事件（服务已统一 dispatch 到 UI 线程）
        _ble.StateChanged += OnBleStateChanged;
        _ble.HeartRateReceived += OnHeartRateReceived;
        _ble.StatusMessage += SetStatus;
        _ble.ErrorOccurred += OnBleError;
        _ble.Disconnected += OnBleDisconnected;

        // 开机自启开关：写注册表 + 状态提示（设置变更由 SettingsService 自动落盘）
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.AutoStartEnabled))
            {
                AutoStartService.SetEnabled(Settings.AutoStartEnabled);
                SetStatus(Settings.AutoStartEnabled ? "已开启开机自启" : "已关闭开机自启");
            }
        };

        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsScanning);
        ConnectCommand = new RelayCommand(Connect, () => SelectedDevice != null && !IsScanning && !IsConnecting);
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync());
    }

    public AppSettings Settings { get; }

    public ObservableCollection<BleDeviceInfo> Devices => _devices;

    public BleDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DeviceNameText
    {
        get => _deviceNameText;
        private set => SetProperty(ref _deviceNameText, value);
    }

    public string HeartRateDisplay
    {
        get => _heartRateDisplay;
        private set => SetProperty(ref _heartRateDisplay, value);
    }

    public Brush HeartRateBrush
    {
        get => _heartRateBrush;
        private set => SetProperty(ref _heartRateBrush, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ---- 命令 ----

    public RelayCommand ScanCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }

    // ---- 事件（View 关注点冒泡：动画/下拉/托盘气泡） ----

    /// <summary>扫描完成（View 展开设备下拉）。</summary>
    public event Action? ScanCompleted;

    /// <summary>收到有效心率（View 触发脉冲动画）。</summary>
    public event Action? PulseRequested;

    /// <summary>需要托盘气泡通知（View/App 转发给 TrayIconService）。</summary>
    public event Action<string, string>? BalloonRequested;

    // ---- 托盘入口 ----

    public void ToggleHud() => Settings.HudVisible = !Settings.HudVisible;

    public void ToggleLock() => Settings.HudLocked = !Settings.HudLocked;

    // ---- 启动自动连接 ----

    public async Task AutoConnectLastAsync()
    {
        var lastId = Settings.LastDeviceId;
        if (string.IsNullOrEmpty(lastId))
        {
            return;
        }

        SetStatus("正在自动连接上次设备…");
        var ok = await _ble.AutoConnectLastAsync(lastId, Settings.LastDeviceName, Settings.AutoReconnect);
        if (!ok)
        {
            RaiseBalloon("HeartRater", "自动连接失败：未检测到上次连接的设备，请在主界面重新连接");
        }
    }

    // ---- 扫描/连接 ----

    private async Task ScanAsync()
    {
        IsScanning = true;
        try
        {
            var results = await _ble.ScanAsync(TimeSpan.FromSeconds(6));

            // 只保留带心率广播（0x180D）的设备；上次连接过的设备也保留，
            // 防止广播未声明心率服务的设备在自动回连后从列表消失
            var lastDeviceId = Settings.LastDeviceId;
            var heartDevices = results
                .Where(d => d.HasHeartRateService || (lastDeviceId != null && d.Key == lastDeviceId))
                .OrderBy(x => x.Name)
                .ToList();

            Devices.Clear();
            foreach (var d in heartDevices)
            {
                Devices.Add(d);
            }

            var filteredCount = results.Count - heartDevices.Count;
            SetStatus(Devices.Count == 0
                ? "未发现心率设备，请确认设备已开机且支持心率广播"
                : filteredCount > 0
                    ? $"发现 {Devices.Count} 个心率设备（已过滤 {filteredCount} 个非心率设备）"
                    : $"扫描完成，发现 {Devices.Count} 个心率设备");

            ScanCompleted?.Invoke();
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void Connect()
    {
        if (SelectedDevice is not BleDeviceInfo device)
        {
            return;
        }

        _ = ConnectInternalAsync(device);
    }

    private async Task ConnectInternalAsync(BleDeviceInfo device)
    {
        IsConnecting = true;
        try
        {
            var ok = await _ble.ConnectAsync(device.Key, device.Name, Settings.AutoReconnect);
            if (ok)
            {
                Settings.LastDeviceId = device.Key;
                Settings.LastDeviceName = device.Name;
            }
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task DisconnectAsync()
    {
        await _ble.DisconnectAsync();
        SetHeartRateDisplay(-1);
    }

    // ---- BLE 事件 ----

    private void OnBleStateChanged(BleConnectionState state)
    {
        DeviceNameText = state switch
        {
            BleConnectionState.Scanning => "正在扫描…",
            BleConnectionState.Connecting => "正在连接…",
            BleConnectionState.Connected => _ble.ConnectedDeviceName ?? "已连接",
            _ => "未连接设备",
        };
    }

    private void OnHeartRateReceived(int bpm)
    {
        HeartRateDisplay = bpm.ToString();
        HeartRateBrush = HrColors.GetBrush(bpm);
        PulseRequested?.Invoke();
    }

    private void OnBleError(string message)
    {
        SetStatus(message);
        RaiseBalloon("HeartRater 蓝牙", message);
    }

    private void OnBleDisconnected()
    {
        SetHeartRateDisplay(-1);
        if (Settings.AutoReconnect)
        {
            RaiseBalloon("HeartRater", "连接已断开，正在自动重连…");
        }
    }

    private void SetHeartRateDisplay(int bpm)
    {
        if (bpm <= 0)
        {
            HeartRateDisplay = "--";
            HeartRateBrush = HrColors.PlaceholderBrush;
            return;
        }

        HeartRateDisplay = bpm.ToString();
        HeartRateBrush = HrColors.GetBrush(bpm);
    }

    public void SetStatus(string message) => StatusText = message;

    private void RaiseBalloon(string title, string message) => BalloonRequested?.Invoke(title, message);
}
