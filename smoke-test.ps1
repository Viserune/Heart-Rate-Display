# HeartRater (WPF) 后台冒烟测试
# 全程不显示/不激活主窗口，不抢占前台（全屏游戏不受影响）：
#   - --minimized --demo 启动：进程/悬浮窗/托盘/心率管线（UIA 读悬浮窗 BpmText）
#   - 验证前台进程在测试前后不变（不最小化全屏应用）
# 主窗口 UI 交互类功能（自启开关、演示按钮点击、关闭隐藏到托盘、恢复）在交互式验证中确认。

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class B {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern long GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
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
function S($name) { Write-Output ("SKIP | " + $name + "（需交互式验证）") }

# 记录测试前前台进程（如全屏游戏）
$fgPidBefore = 0
$fgHwnd = [B]::GetForegroundWindow()
if ($fgHwnd -ne [IntPtr]::Zero) { [B]::GetWindowThreadProcessId($fgHwnd, [ref]$fgPidBefore) | Out-Null }
$fgNameBefore = (Get-Process -Id $fgPidBefore -ErrorAction SilentlyContinue).ProcessName
Write-Output ("前台进程(前): " + $fgNameBefore + " pid=" + $fgPidBefore)

function Get-ProcWindows([int]$targetPid) {
    $list = [System.Collections.Generic.List[object]]::new()
    $cb = [B+EnumProc]{
        param($hWnd, $lParam)
        $procId = 0
        [B]::GetWindowThreadProcessId($hWnd, [ref]$procId) | Out-Null
        if ($procId -eq $lParam.ToInt32()) {
            $class = New-Object System.Text.StringBuilder 512
            $title = New-Object System.Text.StringBuilder 512
            [B]::GetClassNameW($hWnd, $class, 512) | Out-Null
            [B]::GetWindowTextW($hWnd, $title, 512) | Out-Null
            $script:wl.Add([PSCustomObject]@{ Hwnd = $hWnd; Class = $class.ToString(); Title = $title.ToString(); Visible = [B]::IsWindowVisible($hWnd) })
        }
        return $true
    }
    $script:wl = $list
    [B]::EnumWindows($cb, [IntPtr]$targetPid) | Out-Null
    return $list
}

function Check-Foreground {
    $fg = [B]::GetForegroundWindow()
    $pid2 = 0
    if ($fg -ne [IntPtr]::Zero) { [B]::GetWindowThreadProcessId($fg, [ref]$pid2) | Out-Null }
    $name = (Get-Process -Id $pid2 -ErrorAction SilentlyContinue).ProcessName
    Write-Output ("  前台进程(后): " + $name + " pid=" + $pid2)
    return ($pid2 -eq $fgPidBefore)
}

# ========== 场景 1: --minimized --demo 后台启动 ==========
Write-Output "=== 场景 1: --minimized --demo（后台演示） ==="
Start-Process -FilePath $exe -ArgumentList "--minimized --demo"
Start-Sleep -Seconds 8
$p = Get-Process -Name HeartRater -ErrorAction SilentlyContinue
T "进程存活" ($null -ne $p)
if (-not $p) { Write-Output "无法启动，中止"; exit 1 }

$wins = Get-ProcWindows $p.Id
$hud = $wins | Where-Object { $_.Title -eq "HeartRater 悬浮窗" } | Select-Object -First 1
$main = $wins | Where-Object { $_.Title -eq "HeartRater 心率助手" } | Select-Object -First 1
$trayMsg = $wins | Where-Object { $_.Class -like "HeartRaterTrayWindow*" }
T "主窗口未显示（后台）" (-not $main -or -not $main.Visible)
T "悬浮窗存在" ($null -ne $hud -and $hud.Visible)
T "托盘消息窗口存在" ($null -ne $trayMsg)

# 托盘图标：Win11 通知区域对 UIA 不可见（Subtree 全树 0 命中），标记 SKIP
# 应用侧 Shell_NotifyIconW(NIM_ADD) 已由托盘消息窗口存在性间接验证；图标可见性需交互确认
S "托盘图标存在（Win11 通知区域 UIA 不可达）"

# 悬浮窗样式
if ($hud) {
    $style = [B]::GetWindowLongPtrW($hud.Hwnd, -20)
    T "悬浮窗工具窗口(TOOLWINDOW)" (($style -band 0x80) -ne 0)
    T "悬浮窗不激活(NOACTIVATE)" (($style -band 0x08000000) -ne 0)
}

# 演示模式心率（UIA 读悬浮窗 BpmText）
$root = [System.Windows.Automation.AutomationElement]::RootElement
$hudCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "HeartRater 悬浮窗")
$hudWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $hudCond)
if ($hudWin) {
    $bpmCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "BpmText")
    $bpm = $hudWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $bpmCond)
    $val = if ($bpm) { $bpm.Current.Name } else { "" }
    T "演示模式心率输出（值=$val）" ($val -match '^\d+$')
} else { T "演示模式心率输出" $false }

# 前台未被抢占
T "前台未被抢占（全屏应用不最小化）" (Check-Foreground)

Stop-Process -Name HeartRater -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# ========== 场景 2: --minimized 后台启动 ==========
Write-Output "=== 场景 2: --minimized（无演示） ==="
Start-Process -FilePath $exe -ArgumentList "--minimized"
Start-Sleep -Seconds 8
$p2 = Get-Process -Name HeartRater -ErrorAction SilentlyContinue
T "minimized 进程存活" ($null -ne $p2)
if ($p2) {
    $wins2 = Get-ProcWindows $p2.Id
    $main2 = $wins2 | Where-Object { $_.Title -eq "HeartRater 心率助手" } | Select-Object -First 1
    $hud2 = $wins2 | Where-Object { $_.Title -eq "HeartRater 悬浮窗" } | Select-Object -First 1
    T "minimized 主窗口未显示" (-not $main2 -or -not $main2.Visible)
    T "minimized 悬浮窗显示" ($null -ne $hud2 -and $hud2.Visible)
    T "前台未被抢占（场景2）" (Check-Foreground)
}
Stop-Process -Name HeartRater -Force -ErrorAction SilentlyContinue

# ========== 主窗口 UI 交互（后台不可行，标注） ==========
Write-Output "=== 主窗口 UI 交互（SKIP） ==="
S "自启开关切换 + 注册表写入"
S "演示模式按钮点击（主窗口内）"
S "关闭主窗口隐藏到托盘"
S "主窗口从托盘恢复"

# ========== 汇总 ==========
$failCount = ($results | Where-Object { $_ -like "FAIL*" }).Count
Write-Output ("=== 汇总: 通过 " + ($results.Count - $failCount) + "/" + $results.Count + " 项 ===")
if ($failCount -gt 0) { Write-Output "存在失败项，请检查"; exit 1 }
Write-Output "后台冒烟全部通过"
