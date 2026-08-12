using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HeartRater.Services;

namespace HeartRater;

/// <summary>
/// 桌面悬浮窗（HUD）：置顶、无边框、背景透明、可拖动、可点击穿透、圆角。
/// 心率颜色：绿 → 黄 → 橙 → 红。
/// 透明实现：WPF 原生 AllowsTransparency（逐像素 alpha 合成），无需 DWM hack。
/// </summary>
public partial class HudWindow : Window
{
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly SettingsService _settings;
    private IntPtr _hwnd;
    private bool _locked;
    private bool _dragging;
    private System.Windows.Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;

    public HudWindow(SettingsService settings)
    {
        _settings = settings;
        _locked = settings.Current.HudLocked;
        InitializeComponent();

        // 默认停靠屏幕右上角
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Top + 12;

        var s = settings.Current;
        if (s.HudX >= 0 && s.HudY >= 0)
        {
            Left = s.HudX;
            Top = s.HudY;
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// 是否锁定悬浮窗：锁定后禁拖动，并自动点击穿透（全屏游戏/视频时不会误拖动、可点击到后面）。
    /// </summary>
    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked == value)
            {
                return;
            }

            _locked = value;
            ApplyWindowStyles();
        }
    }

    /// <summary>显示悬浮窗（置顶、无激活）。</summary>
    public void ShowHud()
    {
        if (!IsLoaded)
        {
            Show();
            // 无焦点显示
            ApplyWindowStyles();
            return;
        }

        ApplyWindowStyles();
        Show();
    }

    public void HideHud()
    {
        Hide();
    }

    /// <summary>设置心率显示（bpm &lt;= 0 时显示占位符）。</summary>
    public void SetHeartRate(int bpm)
    {
        if (bpm <= 0)
        {
            BpmText.Text = "--";
            BpmText.Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
            return;
        }

        var color = HrColors.GetColor(bpm);
        BpmText.Text = bpm.ToString();
        BpmText.Foreground = new SolidColorBrush(color);
    }

    /// <summary>按设置应用悬浮窗显示状态（供 App 启动时调用）。</summary>
    public void ApplyHudFromSettings()
    {
        if (_settings.Current.HudVisible)
        {
            ShowHud();
        }
    }

    // ---- 窗口初始化 ----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyWindowStyles();
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyWindowStyles();
    }

    private void ApplyWindowStyles()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        long style = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        // 锁定时自动点击穿透（可点击到悬浮窗后面的窗口/全屏应用）
        if (_locked)
        {
            style |= WS_EX_TRANSPARENT;
        }
        else
        {
            style &= ~WS_EX_TRANSPARENT;
        }

        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, style);
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    // ---- 拖动 ----

    private void OnPointerPressed(object sender, MouseButtonEventArgs e)
    {
        // 锁定时禁拖动（同时已穿透）
        if (_locked)
        {
            return;
        }

        _dragging = true;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var cur = PointToScreen(e.GetPosition(this));
        // PointToScreen 返回物理像素，而 Left/Top 是逻辑(DIP)坐标，需按 DPI 缩放换算，
        // 否则高 DPI 下拖动距离会漂移放大，保存的位置也不准
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = _dragStartLeft + (cur.X - _dragStartScreen.X) / dpiScale;
        Top = _dragStartTop + (cur.Y - _dragStartScreen.Y) / dpiScale;
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        SavePosition();
        e.Handled = true;
    }

    /// <summary>把当前悬浮窗位置写入设置并持久化。</summary>
    private void SavePosition()
    {
        _settings.Current.HudX = Left;
        _settings.Current.HudY = Top;
        _settings.Save();
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        // 应用退出（托盘“退出”）时也保存一次位置，确保关闭后再开启回到原位
        SavePosition();
    }

    // ---- P/Invoke ----

    [DllImport("user32.dll")]
    private static extern long GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern long SetWindowLongPtrW(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
