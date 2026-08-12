# HeartRater 心率助手

轻量便携的 Windows 心率悬浮窗软件。通过蓝牙 BLE 读取标准心率设备（手环 / 心率带 / iQOO Watch GT 等）的实时心率，以桌面悬浮窗（HUD）展示，支持系统托盘常驻与开机自启。

纯 WinUI 3 原生桌面应用，无需网页、无需安装，解压即用。

## 功能

- **蓝牙心率**：标准 BLE 心率服务（Heart Rate Service `0x180D` / 心率测量特征 `0x2A37`），兼容绝大多数心率设备
- **桌面悬浮窗 HUD**：置顶、可拖动、圆角，颜色随心率变化（绿 <100 → 黄 100-119 → 橙 120-139 → 红 ≥140），支持点击穿透
- **系统托盘**：双击恢复主界面，右键菜单（显示主界面 / 悬浮窗 / 演示模式 / 退出），连接状态气泡通知
- **开机托盘启动**：设置里一键开启，写入 `HKCU\...\CurrentVersion\Run`，以 `--minimized` 启动驻留托盘
- **启动自动回连**：记住上次连接的设备，启动后自动连接
- **不死鸟模式**：断线自动重连（指数退避 1s → 30s）
- **演示模式**：无设备时模拟心率数据，验证完整显示链路
- **轻量便携**：自包含 Windows App SDK 运行时，单目录解压即用，无安装、无后台服务

## 环境要求

- Windows 10 1809+ / Windows 11
- 64 位系统
- 蓝牙 4.0+（读心率需要）

## 构建

无需 Visual Studio，仅需 .NET 8 SDK：

```bash
dotnet build -c Debug
```

产物位于 `bin\Debug\net8.0-windows10.0.22000.0\win-x64\`。

发布（自包含便携版）：

```bash
dotnet publish -c Release
```

## 使用

1. 打开应用，点击「扫描设备」，选择你的心率设备
2. 连接成功后实时心率会显示在主界面与悬浮窗
3. 关闭主窗口会隐藏到系统托盘（双击托盘图标恢复）
4. 在「设置」中可开启：开机自启、启动自动连接、断线重连、悬浮窗点击穿透

没有设备？点击「开启演示模式」即可体验完整效果。

## 项目结构

```
HeartRater/
├── App.xaml(.cs)          # 应用生命周期、托盘接线、--minimized
├── MainWindow.xaml(.cs)   # 主界面：扫描/连接/设置
├── HudWindow.xaml(.cs)    # 桌面悬浮窗（置顶/拖动/穿透/圆角）
├── HrColors.cs            # 心率颜色映射
├── Services/
│   ├── BleHeartRateService.cs  # BLE 扫描/连接/订阅/自动重连
│   ├── HeartRateParser.cs      # 0x2A37 心率数据解析
│   ├── TrayIconService.cs      # 托盘图标（Shell_NotifyIconW）
│   ├── SettingsService.cs      # 设置持久化（JSON）
│   └── AutoStartService.cs     # 开机自启（注册表）
└── smoke-test.ps1         # 功能冒烟测试
```

## 技术栈

- [WinUI 3](https://github.com/microsoft/microsoft-ui-xaml)（Windows App SDK 1.7）
- .NET 8（C#）
- Windows 蓝牙 LE API（`Windows.Devices.Bluetooth`）

## 许可证

[MIT](LICENSE)
