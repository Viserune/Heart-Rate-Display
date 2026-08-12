using System;
using System.Runtime.InteropServices;

namespace HeartRater.Services;

/// <summary>
/// 系统托盘图标：Shell_NotifyIconW 自绘实现（零第三方依赖）。
/// 在独立 STA 线程上创建隐藏窗口 + 消息循环，回调事件统一投递到 UI 线程（dispatch 委托）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    // ---- 消息常量 ----
    private const uint WM_APP = 0x8000;
    private const uint TRAY_CALLBACK = WM_APP + 1;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_NULL = 0x0000;
    private const uint NIN_SELECT = 0x0400; // 版本 4 下左键单击回调

    // ---- Shell_NotifyIcon ----
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;

    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIF_INFO = 0x10;

    private const uint NIIF_INFO = 0x1;
    private const uint NIS_HIDDEN = 0x1;
    private const uint NOTIFYICON_VERSION_4 = 4;

    // ---- 菜单 ----
    private const uint MF_STRING = 0x0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint TPM_RETURNCMD = 0x100;

    private const uint MENU_SHOW_MAIN = 1;
    private const uint MENU_TOGGLE_HUD = 2;
    private const uint MENU_TOGGLE_DEMO = 3;
    private const uint MENU_TOGGLE_LOCK = 4;
    private const uint MENU_EXIT = 5;

    private static readonly string ClassName = "HeartRaterTrayWindow_" + Guid.NewGuid().ToString("N");

    // 窗口过程委托必须存根，防止被 GC 回收
    private readonly WndProcDelegate _wndProc;
    private readonly Action<Action> _dispatch;
    private readonly string _iconPath;
    private readonly object _lock = new();

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private volatile bool _demoRunning;
    private volatile bool _locked;
    private bool _disposed;

    public TrayIconService(Action<Action> dispatch, string iconPath)
    {
        _dispatch = dispatch;
        _iconPath = iconPath;
        _wndProc = WndProc;
    }

    /// <summary>左键单击：显示主界面。</summary>
    public event Action? LeftClick;
    public event Action? ShowMainRequested;
    public event Action? ToggleHudRequested;
    public event Action? ToggleDemoRequested;
    public event Action? ToggleLockRequested;
    public event Action? ExitRequested;

    /// <summary>演示模式是否开启（影响托盘菜单文案）。UI 线程写入，托盘线程读取。</summary>
    public bool DemoRunning
    {
        get => _demoRunning;
        set => _demoRunning = value;
    }

    /// <summary>悬浮窗是否锁定（影响托盘菜单文案）。UI 线程写入，托盘线程读取。</summary>
    public bool Locked
    {
        get => _locked;
        set => _locked = value;
    }

    public void Show()
    {
        lock (_lock)
        {
            if (_thread != null || _disposed)
            {
                return;
            }

            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "HeartRaterTrayThread",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        _hwnd = CreateTrayWindow();
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _hIcon = LoadTrayIcon();
        AddTrayIcon();

        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        // 循环退出（WM_QUIT）：清理
        RemoveTrayIconCore();
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private IntPtr CreateTrayWindow()
    {
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
            style = 0,
        };

        if (RegisterClassW(ref wc) == 0)
        {
            // 可能已注册（重复创建），继续尝试
        }

        // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE，消息专用窗口
        return CreateWindowExW(
            0x80 | 0x08000000,
            ClassName,
            "HeartRaterTray",
            0,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private IntPtr LoadTrayIcon()
    {
        // LoadImage 直接加载 ico 文件（Vista+ 支持 PNG 压缩帧）
        IntPtr icon = LoadImageW(
            IntPtr.Zero, _iconPath, 1 /*IMAGE_ICON*/, 0, 0,
            0x10 /*LR_LOADFROMFILE*/ | 0x40 /*LR_DEFAULTSIZE*/);
        if (icon != IntPtr.Zero)
        {
            return icon;
        }

        // 兜底：从 exe 资源加载应用图标
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            ExtractIconExW(exePath, 0, out IntPtr large, out IntPtr small, 1);
            icon = small != IntPtr.Zero ? small : large;
        }

        return icon;
    }

    private void AddTrayIcon()
    {
        var data = CreateNotifyIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = TRAY_CALLBACK;
        data.hIcon = _hIcon;
        data.szTip = "HeartRater 心率助手";
        Shell_NotifyIconW(NIM_ADD, ref data);

        // 请求版本 4 行为（NIN_SELECT / WM_CONTEXTMENU）
        var ver = CreateNotifyIconData();
        ver.uVersion = NOTIFYICON_VERSION_4;
        ver.dwState = NIS_HIDDEN;
        ver.dwStateMask = NIS_HIDDEN;
        Shell_NotifyIconW(NIM_SETVERSION, ref ver);
    }

    private NOTIFYICONDATAW CreateNotifyIconData()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 0,
            guidItem = Guid.Empty,
        };
        return data;
    }

    /// <summary>显示气泡通知（任意线程可调用）。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void RemoveTrayIconCore()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData();
        Shell_NotifyIconW(NIM_DELETE, ref data);
    }

    // ---- 托盘回调 ----

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TRAY_CALLBACK)
        {
            uint mouseMsg = (uint)lParam.ToInt64();
            if (mouseMsg == NIN_SELECT)
            {
                Raise(LeftClick);
                Raise(ShowMainRequested);
                return IntPtr.Zero;
            }

            if (mouseMsg == WM_CONTEXTMENU)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        GetCursorPos(out POINT pt);

        IntPtr menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, MENU_SHOW_MAIN, "显示主界面");
        AppendMenuW(menu, MF_STRING, MENU_TOGGLE_HUD, "显示/隐藏悬浮窗");
        AppendMenuW(menu, MF_STRING, MENU_TOGGLE_DEMO, _demoRunning ? "退出演示模式" : "开启演示模式");
        AppendMenuW(menu, MF_STRING, MENU_TOGGLE_LOCK, _locked ? "解锁悬浮窗" : "锁定悬浮窗");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, MENU_EXIT, "退出");

        SetForegroundWindow(_hwnd);
        uint cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

        switch (cmd)
        {
            case MENU_SHOW_MAIN:
                Raise(ShowMainRequested);
                break;
            case MENU_TOGGLE_HUD:
                Raise(ToggleHudRequested);
                break;
            case MENU_TOGGLE_DEMO:
                Raise(ToggleDemoRequested);
                break;
            case MENU_TOGGLE_LOCK:
                Raise(ToggleLockRequested);
                break;
            case MENU_EXIT:
                Raise(ExitRequested);
                break;
        }
    }

    private void Raise(Action? action)
    {
        if (action == null)
        {
            return;
        }

        _dispatch(action);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_thread != null && _threadId != 0)
            {
                PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(2000);
                _thread = null;
            }
        }
    }

    // ---- 窗口过程委托 ----
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string szFileName, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
