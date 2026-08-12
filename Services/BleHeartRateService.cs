using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace HeartRater.Services;

public enum BleConnectionState
{
    Disconnected,
    Scanning,
    Connecting,
    Connected,
}

/// <summary>蓝牙设备列表项。</summary>
public sealed class BleDeviceInfo
{
    /// <summary>连接用 Key：DeviceInformation.Id 或格式化 MAC 地址。</summary>
    public string Key { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>设备 MAC（用于展示）。</summary>
    public string Address { get; init; } = "";

    /// <summary>广告中声明了心率服务 (0x180D)。</summary>
    public bool HasHeartRateService { get; init; }

    public string DisplayName =>
        (HasHeartRateService ? "♥ " : "") + (string.IsNullOrEmpty(Name) ? "未知设备" : Name) +
        (string.IsNullOrEmpty(Address) ? "" : $"  ({Address})");
}

/// <summary>
/// BLE 心率服务：扫描、连接、订阅 0x2A37 心率测量、断线自动重连（不死鸟模式）。
/// 所有事件统一投递到 UI 线程（通过构造时传入的 dispatch 委托）。
/// </summary>
public sealed class BleHeartRateService : IDisposable
{
    private static readonly Guid HeartRateServiceUuid = GattServiceUuids.HeartRate;
    private static readonly Guid HeartRateMeasurementUuid = GattCharacteristicUuids.HeartRateMeasurement;

    private readonly Action<Action> _dispatch;
    private readonly object _scanLock = new();

    private BluetoothLEAdvertisementWatcher? _watcher;
    private Dictionary<ulong, BleDeviceInfo> _scanResults = new();

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattCharacteristic? _characteristic;

    private CancellationTokenSource? _reconnectCts;
    private volatile bool _userRequestedDisconnect;

    private System.Threading.Timer? _demoTimer;
    private readonly Random _rand = new();
    private int _demoBpm = 72;

    private BleConnectionState _state = BleConnectionState.Disconnected;

    public BleHeartRateService(Action<Action> dispatch)
    {
        _dispatch = dispatch;
    }

    // ---- 事件 ----
    public event Action<BleConnectionState>? StateChanged;
    public event Action<int>? HeartRateReceived;
    /// <summary>状态文本（连接过程、重连提示等）。</summary>
    public event Action<string>? StatusMessage;
    /// <summary>错误/警告（用于气泡提示）。</summary>
    public event Action<string>? ErrorOccurred;
    /// <summary>非用户主动的断线（用于气泡提示重连）。</summary>
    public event Action? Disconnected;

    public BleConnectionState State => _state;
    public bool IsDemoMode => _demoTimer != null;
    public string? ConnectedDeviceName { get; private set; }

    // ==================== 扫描 ====================

    public async Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan duration)
    {
        SetState(BleConnectionState.Scanning);
        RaiseStatus("正在扫描蓝牙设备…");

        var byId = new Dictionary<string, BleDeviceInfo>();
        var byAddress = new Dictionary<ulong, BleDeviceInfo>();

        // 1. 已配对/已知设备
        try
        {
            var known = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector());
            foreach (var di in known)
            {
                var info = new BleDeviceInfo
                {
                    Key = di.Id,
                    Name = di.Name,
                    Address = ExtractMacFromId(di.Id),
                };
                byId[di.Id] = info;
                var addr = TryParseAddress(info.Address);
                if (addr.HasValue)
                {
                    byAddress[addr.Value] = info;
                }
            }
        }
        catch (Exception ex)
        {
            RaiseError($"读取已配对设备失败: {ex.Message}");
        }

        // 2. 主动广告扫描
        lock (_scanLock)
        {
            _scanResults = byAddress;
        }

        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active,
            };
            _watcher.Received += OnAdvertisementReceived;
            _watcher.Start();
            await Task.Delay(duration);
        }
        catch (Exception ex)
        {
            RaiseError($"蓝牙不可用，请确认已开启蓝牙: {ex.Message}");
        }
        finally
        {
            if (_watcher != null)
            {
                _watcher.Stop();
                _watcher.Received -= OnAdvertisementReceived;
                _watcher = null;
            }
        }

        List<BleDeviceInfo> merged;
        lock (_scanLock)
        {
            merged = _scanResults.Values
                .Union(byId.Values)
                .GroupBy(d => d.Key)
                .Select(g => g.First())
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        SetState(BleConnectionState.Disconnected);
        RaiseStatus($"扫描完成，发现 {merged.Count} 个设备");
        return merged;
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var mac = FormatMac(args.BluetoothAddress);
        var hasHr = args.Advertisement.ServiceUuids.Contains(HeartRateServiceUuid);

        lock (_scanLock)
        {
            // 已配对列表中的设备优先保留其稳定 Id
            if (_scanResults.TryGetValue(args.BluetoothAddress, out var existing))
            {
                if (hasHr)
                {
                    _scanResults[args.BluetoothAddress] = new BleDeviceInfo
                    {
                        Key = existing.Key,
                        Name = existing.Name,
                        Address = existing.Address,
                        HasHeartRateService = true,
                    };
                }

                return;
            }

            var name = args.Advertisement.LocalName;
            _scanResults[args.BluetoothAddress] = new BleDeviceInfo
            {
                Key = mac, // 未知设备用 MAC 作 Key，连接时按地址解析
                Name = name,
                Address = mac,
                HasHeartRateService = hasHr,
            };
        }
    }

    // ==================== 连接 ====================

    /// <summary>连接设备（用户主动触发）。会先取消之前的自动重连。</summary>
    public async Task<bool> ConnectAsync(string deviceKey, string? deviceName, bool autoReconnect)
    {
        _userRequestedDisconnect = false;
        CancelReconnect();
        await StopDemoInternalAsync();

        return await ConnectInternalAsync(deviceKey, deviceName, autoReconnect, quiet: false);
    }

    private async Task<bool> ConnectInternalAsync(string deviceKey, string? deviceName, bool autoReconnect, bool quiet)
    {
        _reconnectCts ??= new CancellationTokenSource();
        var ct = _reconnectCts.Token;

        _reconnectAutoEnabled = autoReconnect;
        SetState(BleConnectionState.Connecting);
        if (!quiet)
        {
            RaiseStatus(string.IsNullOrEmpty(deviceName) ? "正在连接…" : $"正在连接 {deviceName}…");
        }

        BluetoothLEDevice? device = null;
        try
        {
            device = await ResolveDeviceAsync(deviceKey);
            if (device == null)
            {
                RaiseError("无法打开设备，请确认设备电源开启且处于可发现状态");
                SetState(BleConnectionState.Disconnected);
                return false;
            }

            var session = await GattSession.FromDeviceIdAsync(BluetoothDeviceId.FromId(device.DeviceId));
            session.MaintainConnection = true;

            var (status, characteristic) = await FindHrCharacteristicAsync(device);
            if (characteristic == null)
            {
                // 未配对或服务缓存问题 → 尝试配对后重试
                var paired = await TryPairAsync(device);
                if (!paired)
                {
                    RaiseError($"无法读取心率服务（{status}），配对未成功");
                    session.Dispose();
                    device.Dispose();
                    SetState(BleConnectionState.Disconnected);
                    return false;
                }

                (status, characteristic) = await FindHrCharacteristicAsync(device);
                if (characteristic == null)
                {
                    RaiseError($"配对后仍无法读取心率服务（{status}）");
                    session.Dispose();
                    device.Dispose();
                    SetState(BleConnectionState.Disconnected);
                    return false;
                }
            }

            characteristic.ValueChanged += OnValueChanged;
            var subResult = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);

            if (subResult != GattCommunicationStatus.Success)
            {
                characteristic.ValueChanged -= OnValueChanged;
                RaiseError($"订阅心率通知失败（{subResult}）");
                session.Dispose();
                device.Dispose();
                SetState(BleConnectionState.Disconnected);
                return false;
            }

            // 成功：替换旧连接
            CleanupDevice();
            _device = device;
            _session = session;
            _characteristic = characteristic;
            _session.SessionStatusChanged += OnSessionStatusChanged;
            ConnectedDeviceName = string.IsNullOrEmpty(deviceName) ? device.Name : deviceName;

            SetState(BleConnectionState.Connected);
            RaiseStatus($"已连接 {ConnectedDeviceName}");
            return true;
        }
        catch (OperationCanceledException)
        {
            device?.Dispose();
            SetState(BleConnectionState.Disconnected);
            return false;
        }
        catch (Exception ex)
        {
            RaiseError($"连接失败: {ex.Message}");
            device?.Dispose();
            SetState(BleConnectionState.Disconnected);
            return false;
        }
    }

    private async Task<BluetoothLEDevice?> ResolveDeviceAsync(string key)
    {
        if (key.Contains("BluetoothLE#", StringComparison.OrdinalIgnoreCase))
        {
            return await BluetoothLEDevice.FromIdAsync(key);
        }

        if (ulong.TryParse(key.Replace(":", ""), NumberStyles.HexNumber, null, out var addr))
        {
            return await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
        }

        return null;
    }

    private async Task<(GattCommunicationStatus Status, GattCharacteristic? Characteristic)> FindHrCharacteristicAsync(BluetoothLEDevice device)
    {
        try
        {
            var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                return (servicesResult.Status, null);
            }

            foreach (var service in servicesResult.Services)
            {
                if (service.Uuid != HeartRateServiceUuid)
                {
                    continue;
                }

                var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charsResult.Status != GattCommunicationStatus.Success)
                {
                    continue;
                }

                foreach (var ch in charsResult.Characteristics)
                {
                    if (ch.Uuid == HeartRateMeasurementUuid &&
                        (ch.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0)
                    {
                        return (GattCommunicationStatus.Success, ch);
                    }
                }
            }

            return (GattCommunicationStatus.Success, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取服务失败: {ex.Message}");
            return (GattCommunicationStatus.Unreachable, null);
        }
    }

    private async Task<bool> TryPairAsync(BluetoothLEDevice device)
    {
        try
        {
            var pairing = device.DeviceInformation.Pairing;
            if (pairing.IsPaired)
            {
                return true;
            }

            RaiseStatus("正在与设备配对，请在系统弹窗中确认…");
            var result = await pairing.Custom.PairAsync(
                DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Default);
            return result.Status == DevicePairingResultStatus.Paired;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"配对失败: {ex.Message}");
            return false;
        }
    }

    // ==================== 断线重连（不死鸟模式） ====================

    private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        if (args.Status == GattSessionStatus.Active)
        {
            return;
        }

        // Closed/Disconnected
        var deviceKey = _device?.DeviceId;
        var name = ConnectedDeviceName;
        var userInitiated = _userRequestedDisconnect;
        var autoReconnect = _reconnectAutoEnabled;

        CleanupDevice();
        SetState(BleConnectionState.Disconnected);

        if (userInitiated)
        {
            return;
        }

        RaiseDisconnected();
        RaiseStatus("连接已断开");

        if (autoReconnect && deviceKey != null)
        {
            _ = ReconnectLoopAsync(deviceKey, name);
        }
    }

    private volatile bool _reconnectAutoEnabled;

    private async Task ReconnectLoopAsync(string deviceKey, string? name)
    {
        var cts = _reconnectCts;
        if (cts == null || cts.IsCancellationRequested)
        {
            return;
        }

        int attempt = 0;
        while (!cts.IsCancellationRequested)
        {
            // 指数退避：1s, 2s, 4s, 8s, 16s, 之后固定 30s
            var delaySec = Math.Min(30, 1 << Math.Min(attempt, 5));
            RaiseStatus($"连接断开，{delaySec} 秒后自动重连（第 {attempt + 1} 次）…");
            attempt++;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySec), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            var ok = await ConnectInternalAsync(deviceKey, name, autoReconnect: true, quiet: true);
            if (ok || cts.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>用户主动断开。</summary>
    public async Task DisconnectAsync()
    {
        await StopDemoInternalAsync();
        _userRequestedDisconnect = true;
        _reconnectAutoEnabled = false;
        CancelReconnect();
        CleanupDevice();
        SetState(BleConnectionState.Disconnected);
        RaiseStatus("已断开连接");
    }

    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
    }

    // ==================== 演示模式 ====================

    public void StartDemo()
    {
        _userRequestedDisconnect = true;
        _reconnectAutoEnabled = false;
        CancelReconnect();
        CleanupDevice();

        _demoBpm = 70;
        _demoTimer?.Dispose();
        _demoTimer = new System.Threading.Timer(_ =>
        {
            _demoBpm += _rand.Next(-3, 4);
            _demoBpm = Math.Clamp(_demoBpm, 55, 165);
            RaiseHeartRate(_demoBpm);
        }, null, 0, 1000);

        ConnectedDeviceName = "演示模式";
        SetState(BleConnectionState.Connected);
        RaiseStatus("演示模式：正在输出模拟心率");
    }

    public async Task StopDemoAsync()
    {
        await StopDemoInternalAsync();
        SetState(BleConnectionState.Disconnected);
        RaiseStatus("已退出演示模式");
    }

    private Task StopDemoInternalAsync()
    {
        if (_demoTimer != null)
        {
            _demoTimer.Dispose();
            _demoTimer = null;
        }

        return Task.CompletedTask;
    }

    // ==================== 数据回调 ====================

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = BufferToBytes(args.CharacteristicValue);
        var hr = HeartRateParser.Parse(bytes);
        if (hr != null && hr.Bpm > 0 && hr.Bpm <= 250)
        {
            RaiseHeartRate(hr.Bpm);
        }
    }

    private static byte[] BufferToBytes(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    // ==================== 清理 ====================

    private void CleanupDevice()
    {
        if (_characteristic != null)
        {
            _characteristic.ValueChanged -= OnValueChanged;
            _characteristic = null;
        }

        if (_session != null)
        {
            _session.SessionStatusChanged -= OnSessionStatusChanged;
            _session.Dispose();
            _session = null;
        }

        if (_device != null)
        {
            _device.Dispose();
            _device = null;
        }

        ConnectedDeviceName = null;
    }

    public void Dispose()
    {
        CancelReconnect();
        _demoTimer?.Dispose();
        _demoTimer = null;
        CleanupDevice();
        _watcher?.Stop();
        _watcher = null;
    }

    // ==================== 工具 ====================

    private void SetState(BleConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        _dispatch(() => StateChanged?.Invoke(state));
    }

    private void RaiseStatus(string message)
    {
        _dispatch(() => StatusMessage?.Invoke(message));
    }

    private void RaiseError(string message)
    {
        _dispatch(() => ErrorOccurred?.Invoke(message));
    }

    private void RaiseDisconnected()
    {
        _dispatch(() => Disconnected?.Invoke());
    }

    private void RaiseHeartRate(int bpm)
    {
        _dispatch(() => HeartRateReceived?.Invoke(bpm));
    }

    /// <summary>ulong 蓝牙地址 → 标准 MAC 字符串。</summary>
    public static string FormatMac(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return string.Join(":", bytes[^6..].Select(b => b.ToString("X2")));
    }

    private static string ExtractMacFromId(string deviceId)
    {
        var match = Regex.Match(deviceId, @"([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}(?!.*([0-9A-Fa-f]{2}:){5})");
        return match.Success ? match.Value.ToUpperInvariant() : "";
    }

    private static ulong? TryParseAddress(string mac)
    {
        if (string.IsNullOrEmpty(mac))
        {
            return null;
        }

        var compact = mac.Replace(":", "");
        if (compact.Length != 12 || !ulong.TryParse(compact, NumberStyles.HexNumber, null, out var addr))
        {
            return null;
        }

        // 反转为 ulong 存储格式
        var bytes = Convert.FromHexString(compact);
        Array.Reverse(bytes);
        var padded = new byte[8];
        Array.Copy(bytes, 0, padded, 2, 6);
        return BitConverter.ToUInt64(padded, 0);
    }
}
