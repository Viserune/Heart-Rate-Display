using System;
using System.Runtime.InteropServices;
using HeartRater.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace HeartRater;

/// <summary>
/// 桌面悬浮窗（HUD）：置顶、无边框、可拖动、可点击穿透、圆角。
/// 心率颜色：绿 → 黄 → 橙 → 红。
/// </summary>
public sealed partial class HudWindow : Window
{
    // ---- 窗口扩展样式 ----
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;

    private const uint SPI_GETWORKAREA = 0x0030;

    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly SettingsService _settings;
    private IntPtr _hwnd;
    private bool _clickThrough;
    private bool _dragging;
    private POINT _dragStartScreen;
    private PointInt32 _dragStartWindow;

    public HudWindow(SettingsService settings)
    {
        _settings = settings;
        _clickThrough = settings.Current.HudClickThrough;
        InitializeComponent();

        Title = "HeartRater 悬浮窗";

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        Activated += OnActivated;
    }

    /// <summary>是否点击穿透（鼠标事件直接穿透到下层窗口）。</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value)
            {
                return;
            }

            _clickThrough = value;
            ApplyWindowStyles();
        }
    }

    /// <summary>显示悬浮窗（置顶、无激活）。</summary>
    public void ShowHud()
    {
        if (_hwnd == IntPtr.Zero)
        {
            EnsureWindowCreated();
        }

        ApplyWindowStyles();
        ApplyWindowRgn();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    public void HideHud()
    {
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_HIDE);
        }
    }

    /// <summary>设置心率显示（bpm &lt;= 0 时显示占位符）。</summary>
    public void SetHeartRate(int bpm)    {
        if (bpm <= 0)
        {
            BpmText.Text = "--";
            BpmText.Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            return;
        }

        var color = HrColors.GetColor(bpm);
        BpmText.Text = bpm.ToString();
        BpmText.Foreground = new SolidColorBrush(color);
        CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, color.R, color.G, color.B));
    }

    /// <summary>按设置应用悬浮窗显示状态（供 App 启动时调用）。</summary>
    public void ApplyHudFromSettingsPublic()
    {
        if (_settings.Current.HudVisible)
        {
            ShowHud();
        }
    }

    // ---- 窗口初始化 ----

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = WindowNative.GetWindowHandle(this);
            ApplyWindowStyles();
            PositionWindow();
            ApplyWindowRgn();
        }
    }

    private void EnsureWindowCreated()
    {
        // 首次创建：Activate 会创建原生窗口并触发 OnActivated
        Activate();
        if (_hwnd != IntPtr.Zero)
        {
            // 悬浮窗不应抢占焦点
            ShowWindow(_hwnd, SW_HIDE);
        }
    }

    private void PositionWindow()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)(200 * scale);
        var height = (int)(115 * scale);
        AppWindow.Resize(new SizeInt32(width, height));

        if (_settings.Current.HudX >= 0 && _settings.Current.HudY >= 0)
        {
            AppWindow.Move(new PointInt32(
                (int)(_settings.Current.HudX * scale),
                (int)(_settings.Current.HudY * scale)));
        }
        else
        {
            // 默认停靠屏幕右上角
            GetWorkArea(out RECT wa);
            AppWindow.Move(new PointInt32(wa.Right - width - (int)(16 * scale), wa.Top + (int)(12 * scale)));
        }
    }

    private void ApplyWindowStyles()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        long style = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
        if (_clickThrough)
        {
            style |= WS_EX_TRANSPARENT;
        }
        else
        {
            style &= ~WS_EX_TRANSPARENT;
        }

        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, style);
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyWindowRgn();
    }

    /// <summary>用 SetWindowRgn 实现圆角。</summary>
    private void ApplyWindowRgn()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int w = (int)(RootGrid.ActualWidth * scale);
        int h = (int)(RootGrid.ActualHeight * scale);
        int r = (int)(16 * scale);

        if (w <= 0 || h <= 0)
        {
            return;
        }

        var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r);
        SetWindowRgn(_hwnd, rgn, true); // 区域所有权转交给系统
    }

    // ---- 拖动 ----

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_clickThrough)
        {
            return;
        }

        _dragging = true;
        RootGrid.CapturePointer(e.Pointer);
        GetCursorPos(out _dragStartScreen);
        _dragStartWindow = AppWindow.Position;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        GetCursorPos(out POINT cur);
        AppWindow.Move(new PointInt32(
            _dragStartWindow.X + (cur.X - _dragStartScreen.X),
            _dragStartWindow.Y + (cur.Y - _dragStartScreen.Y)));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag();
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        RootGrid.ReleasePointerCaptures();

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        _settings.Current.HudX = AppWindow.Position.X / scale;
        _settings.Current.HudY = AppWindow.Position.Y / scale;
        _settings.Save();
    }

    // ---- P/Invoke ----

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern long GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern long SetWindowLongPtrW(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    private static void GetWorkArea(out RECT rect)
    {
        rect = new RECT();
        SystemParametersInfoW(SPI_GETWORKAREA, 0, ref rect, 0);
    }
}
