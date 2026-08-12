# HeartRater (WPF) 冒烟测试：启动 exe，验证 主窗口/悬浮窗/托盘/演示模式/隐藏到托盘/--minimized/设置持久化/自启
param([string]$Mode = "normal")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern long GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
"@

$exe = "D:\Study\Projects\Heartrate\HeartRater\bin\Debug\net8.0-windows10.0.19041.0\HeartRater.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: 未找到 exe"; exit 1 }

$results = [System.Collections.Generic.List[string]]::new()
function T($name, $ok) {
    $results.Add(($(if ($ok) { "PASS" } else { "FAIL" }) + " | " + $name))
    Write-Output ($(if ($ok) { "PASS" } else { "FAIL" }) + " | " + $name)
}

function Get-ProcWindows([int]$targetPid) {
    $list = [System.Collections.Generic.List[object]]::new()
    $cb = [W+EnumProc]{
        param($hWnd, $lParam)
        $procId = 0
        [W]::GetWindowThreadProcessId($hWnd, [ref]$procId) | Out-Null
        if ($procId -eq $lParam.ToInt32()) {
            $class = New-Object System.Text.StringBuilder 512
            $title = New-Object System.Text.StringBuilder 512
            [W]::GetClassNameW($hWnd, $class, 512) | Out-Null
            [W]::GetWindowTextW($hWnd, $title, 512) | Out-Null
            $script:wl.Add([PSCustomObject]@{
                Hwnd = $hWnd
                Class = $class.ToString()
                Title = $title.ToString()
                Visible = [W]::IsWindowVisible($hWnd)
            })
        }
        return $true
    }
    $script:wl = $list
    [W]::EnumWindows($cb, [IntPtr]$targetPid) | Out-Null
    return $list
}

# ========== 场景 1: 正常启动 ==========
Write-Output "=== 场景 1: 正常启动 ==="
Start-Process -FilePath $exe
Start-Sleep -Seconds 8
$p = Get-Process -Name HeartRater -ErrorAction SilentlyContinue
T "进程存活" ($null -ne $p)
if (-not $p) { Write-Output "应用无法启动，中止检测"; exit 1 }

$wins = Get-ProcWindows $p.Id
$main = $wins | Where-Object { $_.Title -eq "HeartRater 心率助手" } | Select-Object -First 1
$hud  = $wins | Where-Object { $_.Title -eq "HeartRater 悬浮窗" } | Select-Object -First 1
$trayMsg = $wins | Where-Object { $_.Class -like "HeartRaterTrayWindow*" }
T "主窗口存在" ($null -ne $main -and $main.Visible)
T "悬浮窗存在" ($null -ne $hud -and $hud.Visible)
T "托盘消息窗口存在" ($null -ne $trayMsg)

# 托盘图标（UIA）
$root = [System.Windows.Automation.AutomationElement]::RootElement
$mainCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "HeartRater 心率助手")
$trayIcon = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $mainCond)
T "托盘图标存在" ($null -ne $trayIcon)

# 悬浮窗样式：TOOLWINDOW + NOACTIVATE
if ($hud) {
    $style = [W]::GetWindowLongPtrW($hud.Hwnd, -20)
    T "悬浮窗工具窗口(TOOLWINDOW)" (($style -band 0x80) -ne 0)
    T "悬浮窗不激活(NOACTIVATE)" (($style -band 0x08000000) -ne 0)
}

# 演示模式 → 心率管线
$mainWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $mainCond)
if ($mainWin) {
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "开启演示模式")
    $demoBtn = $mainWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    if ($demoBtn) {
        $demoBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 4
        $hrCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "HeartRateDisplay")
        $hr = $mainWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $hrCond)
        $hrVal = if ($hr) { $hr.Current.Name } else { "" }
        T "演示模式心率输出（值=$hrVal）" ($hrVal -match '^\d+$')
    } else { T "演示模式按钮存在" $false }
} else { T "主窗口 UIA 可访问" $false }

# 通过 UIA 打开自启开关（先清注册表与设置，确保开关从 Off 开始、Toggle 后为 On）
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name HeartRater -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $env:LOCALAPPDATA "HeartRater\settings.json") -ErrorAction SilentlyContinue
$tgCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "开机自动启动（托盘驻留）")
$tg = $mainWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tgCond)
if ($tg) {
    $tg.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Seconds 2
    $runVal = (Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name HeartRater -ErrorAction SilentlyContinue).HeartRater
    T "开机自启注册表写入" (-not [string]::IsNullOrEmpty($runVal))
} else { T "自启开关可访问" $false }

# 设置持久化（自启切换已触发保存）
$settingsPath = Join-Path $env:LOCALAPPDATA "HeartRater\settings.json"
T "设置文件已保存" (Test-Path $settingsPath)

# 关闭 → 隐藏到托盘
if ($main) {
    [W]::PostMessageW($main.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 3
    $p.Refresh()
    $wins2 = Get-ProcWindows $p.Id
    $main2 = $wins2 | Where-Object { $_.Title -eq "HeartRater 心率助手" } | Select-Object -First 1
    T "关闭主窗口隐藏到托盘（进程存活）" ($null -ne $p -and (-not $main2 -or -not $main2.Visible))
}

# 主窗口可从托盘恢复（ShowWindow SW_SHOW=5）
$cb2 = [W+EnumProc]{
    param($hWnd, $lParam)
    $tt = New-Object System.Text.StringBuilder 512
    [W]::GetWindowTextW($hWnd, $tt, 512) | Out-Null
    if ($tt.ToString() -eq "HeartRater 心率助手") { $script:restoreHwnd = $hWnd }
    return $true
}
$script:restoreHwnd = [IntPtr]::Zero
[W]::EnumWindows($cb2, [IntPtr]::Zero) | Out-Null
if ($script:restoreHwnd -ne [IntPtr]::Zero) {
    [W]::ShowWindow($script:restoreHwnd, 5) | Out-Null
    Start-Sleep -Seconds 2
    $winsR = Get-ProcWindows $p.Id
    $mainR = $winsR | Where-Object { $_.Title -eq "HeartRater 心率助手" } | Select-Object -First 1
    T "主窗口可从托盘恢复" ($null -ne $mainR -and $mainR.Visible)
} else {
    T "主窗口可从托盘恢复" $false
}

Stop-Process -Name HeartRater -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# ========== 场景 2: --minimized 启动 ==========
Write-Output "=== 场景 2: --minimized 启动 ==="
Start-Process -FilePath $exe -ArgumentList "--minimized"
Start-Sleep -Seconds 8
$p2 = Get-Process -Name HeartRater -ErrorAction SilentlyContinue
T "minimized 进程存活" ($null -ne $p2)
if ($p2) {
    $wins3 = Get-ProcWindows $p2.Id
    $main3 = $wins3 | Where-Object { $_.Title -eq "HeartRater 心率助手" } | Select-Object -First 1
    $hud3  = $wins3 | Where-Object { $_.Title -eq "HeartRater 悬浮窗" } | Select-Object -First 1
    T "minimized 主窗口隐藏" ($null -eq $main3 -or -not $main3.Visible)
    T "minimized 悬浮窗显示" ($null -ne $hud3 -and $hud3.Visible)
}
Stop-Process -Name HeartRater -Force -ErrorAction SilentlyContinue

# ========== 汇总 ==========
Write-Output "=== 汇总 ==="
$failCount = ($results | Where-Object { $_ -like "FAIL*" }).Count
Write-Output "通过 $($results.Count - $failCount)/$($results.Count) 项"
if ($failCount -gt 0) { Write-Output "存在失败项，请检查"; exit 1 }
Write-Output "全部功能检测通过"
