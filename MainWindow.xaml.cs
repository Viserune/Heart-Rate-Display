using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HeartRater.Services;
using HeartRater.ViewModels;

namespace HeartRater;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly HudWindow _hud;
    private readonly TrayIconService _tray;

    private bool _viewSwitching;

    public MainWindow(MainViewModel viewModel, HudWindow hud, TrayIconService tray)
    {
        _vm = viewModel;
        _hud = hud;
        _tray = tray;

        InitializeComponent();

        // 托盘气泡：VM 冒泡 → 托盘
        _vm.BalloonRequested += (title, message) => _tray.ShowBalloon(title, message);
        // 心率脉冲动画（View 关注点）
        _vm.PulseRequested += PulseHeartRate;
        // 扫描完成：有设备则自动展开下拉
        _vm.ScanCompleted += OnScanCompleted;

        // 设置联动：开关状态已由绑定同步，这里处理窗口级副作用
        _vm.Settings.PropertyChanged += OnSettingsPropertyChanged;
        _tray.Locked = _vm.Settings.HudLocked;

        SourceInitialized += (_, _) =>
        {
            var s = _vm.Settings;
            if (s.MainWidth >= 360)
            {
                Width = s.MainWidth;
            }

            WindowChrome.ApplyMica(this);
            FadeInWindow();
        };
    }

    // ==================== 动画 ====================

    /// <summary>窗口启动淡入。</summary>
    private void FadeInWindow()
    {
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>心率数字脉冲（放大回弹），与颜色变化同时发生。</summary>
    private void PulseHeartRate()
    {
        HeartRateScale.BeginAnimation(ScaleTransform.ScaleXProperty, BuildPulse());
        HeartRateScale.BeginAnimation(ScaleTransform.ScaleYProperty, BuildPulse());
    }

    private static DoubleAnimationUsingKeyFrames BuildPulse()
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(200),
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        return anim;
    }

    // ==================== 视图切换（主界面 ↔ 设置） ====================

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        SwitchToSettings(show: true);
    }

    private void OnExitSettingsClicked(object sender, RoutedEventArgs e)
    {
        SwitchToSettings(show: false);
    }

    /// <summary>主视图与设置视图整窗口切换：旧视图淡出 → 新视图淡入。</summary>
    private void SwitchToSettings(bool show)
    {
        if (_viewSwitching)
        {
            return;
        }

        _viewSwitching = true;
        var from = show ? MainView : SettingsView;
        var to = show ? SettingsView : MainView;

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fadeOut.Completed += (_, _) =>
        {
            from.Visibility = Visibility.Collapsed;
            to.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            fadeIn.Completed += (_, _) => _viewSwitching = false;
            to.BeginAnimation(OpacityProperty, fadeIn);
        };
        from.BeginAnimation(OpacityProperty, fadeOut);
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

    // ==================== 设备下拉 ====================

    /// <summary>扫描完成自动展开下拉（无设备时不展开空列表）。</summary>
    private void OnScanCompleted()
    {
        if (_vm.Devices.Count > 0)
        {
            DeviceCombo.IsDropDownOpen = true;
        }
    }

    // ==================== 设置联动 ====================

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var s = _vm.Settings;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.HudVisible):
                ApplyHudVisibility();
                break;
            case nameof(AppSettings.HudLocked):
                _tray.Locked = s.HudLocked;
                break;
        }
    }

    private void ApplyHudVisibility()
    {
        if (_vm.Settings.HudVisible)
        {
            _hud.ShowHud();
        }
        else
        {
            _hud.HideHud();
        }
    }

    // ==================== 关闭行为 ====================

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        // 关闭主窗口 → 隐藏到托盘（不退出）
        e.Cancel = true;
        _vm.Settings.MainWidth = ActualWidth;
        Hide();
        _tray.ShowBalloon("HeartRater", "已最小化到系统托盘，双击图标可重新打开");
    }
}
