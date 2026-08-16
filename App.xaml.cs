using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using HeartRater.Services;
using HeartRater.ViewModels;

namespace HeartRater;

public partial class App : Application
{
    private MainWindow? _main;
    private HudWindow? _hud;
    private MainViewModel? _mainVm;
    private HudViewModel? _hudVm;
    private TrayIconService? _tray;
    private BleHeartRateService? _ble;
    private SettingsService? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 后台任务未观察异常：记录 + 防止进程崩溃（重连/自动连接等 fire-and-forget 任务）
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };

        // 关闭主窗口不退出（驻留托盘），仅托盘“退出”真正退出
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 跟随系统深浅色主题（须在创建窗口前完成，控件首次绑定即取正确色）
        ThemeService.Initialize(Resources);

        _settings = new SettingsService();
        Action<Action> dispatch = a => Dispatcher.BeginInvoke(a);
        _ble = new BleHeartRateService(dispatch);
        _hudVm = new HudViewModel(_ble);
        _hud = new HudWindow(_settings.Current, _hudVm);
        _mainVm = new MainViewModel(_ble, _settings.Current);
        _tray = new TrayIconService(dispatch, IconPath);
        _main = new MainWindow(_mainVm, _hud, _tray);
        _main.DataContext = _mainVm;

        // 托盘事件
        _tray.ShowMainRequested += ShowMainWindow;
        _tray.ToggleHudRequested += _mainVm.ToggleHud;
        _tray.ToggleLockRequested += _mainVm.ToggleLock;
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
            _ = _mainVm.AutoConnectLastAsync();
        }
    }

    private void ShowMainWindow()
    {
        _main?.ShowMainPublic();
    }

    /// <summary>UI 线程未处理异常：记录 + 友好提示，阻止 WPF 默认崩溃对话框。</summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        MessageBox.Show(
            "程序遇到未预期的错误，已记录并继续运行。\n\n" +
            $"详细信息：{e.Exception.Message}",
            "HeartRater",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>异常落盘到 %LOCALAPPDATA%\HeartRater\error.log（追加），便于真机排查。</summary>
    private static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeartRater");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
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
