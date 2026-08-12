using System;
using System.Windows;
using HeartRater.Services;

namespace HeartRater;

/// <summary>
/// 独立设置窗口：全部设置开关。
/// 每次 Show 时从设置重新加载开关状态（主界面/托盘改动后重开仍同步）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly HudWindow _hud;
    private readonly TrayIconService _tray;
    private bool _suppressToggleEvents;

    public SettingsWindow(SettingsService settings, HudWindow hud, TrayIconService tray)
    {
        _settings = settings;
        _hud = hud;
        _tray = tray;

        InitializeComponent();
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        WindowChrome.ApplyMica(this);
    }

    /// <summary>打开设置窗口（先同步开关状态到当前设置）。</summary>
    public void ShowFromMain()
    {
        LoadSettingsIntoUi();
        Show();
        Activate();
    }

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
        _suppressToggleEvents = false;
    }

    // ==================== 开关事件 ====================

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
}
