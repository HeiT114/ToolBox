#Requires -Version 5.1
<#
.SYNOPSIS
    用 PrintWindow 抓取应用截图（窗口被遮挡也能抓），产出 artifacts/ui-review/ 下的新图：
      * 10-main-light.png   —— 浅色主界面（插件已执行过一次真实检测）
      * 11-palette.png      —— Ctrl+K 命令面板
      * 12-settings.png     —— 设置窗口（外观主题切换）
      * 13-main-dark.png    —— 深色主界面

.DESCRIPTION
    全程 UI Automation 驱动：点「检测 →」让报告有真实内容、呼出命令面板、打开设置窗口。
    深色截图通过 --theme dark 参数启动（参数不写回主题偏好文件）。
#>
[CmdletBinding()]
param(
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type -Namespace ToolboxCap -Name Api -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
'@

$repo = Split-Path -Parent $PSScriptRoot
$config = 'release'
$appDir = Join-Path $repo "artifacts\bin\Toolbox.App\$config"
$appDll = Join-Path $appDir 'Toolbox.App.dll'
$outDir = Join-Path $repo 'artifacts\ui-review'

$mainTitle = 'Toolbox · 插件化工具箱'
$settingsTitle = '设置 · Toolbox'

function Resolve-DotnetHost {
    param([string]$Explicit)
    if ($Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }
    $candidate = Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe'
    if (Test-Path $candidate) { return $candidate }
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw '找不到 dotnet.exe'
}

function Wait-Window {
    param([int]$ProcessId, [string]$Title, [int]$Seconds = 40)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and
                $e.Current.Name -eq $Title) {
                return $e
            }
        }
    }
    return $null
}

function Find-ByName {
    param($Root, $ControlType, [string]$Name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $condition = New-Object System.Windows.Automation.AndCondition($typeCondition, $nameCondition)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Named {
    param($Root, $ControlType, [string]$Name)
    $element = Find-ByName -Root $Root -ControlType $ControlType -Name $Name
    if ($null -eq $element) { return $false }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    return $true
}

function Capture-Window {
    param($Element, [string]$Path, [string]$Label)

    $hwnd = [IntPtr]$Element.Current.NativeWindowHandle
    $r = New-Object ToolboxCap.Api+RECT

    # GetWindowRect 偶尔会给出异常小的值：等一拍重试一次
    [ToolboxCap.Api]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -lt 320 -or $h -lt 240) {
        Start-Sleep -Seconds 1
        [ToolboxCap.Api]::GetWindowRect($hwnd, [ref]$r) | Out-Null
        $w = $r.Right - $r.Left
        $h = $r.Bottom - $r.Top
    }
    if ($w -lt 320 -or $h -lt 240) {
        throw "${Label}: 窗口矩形异常（${w}x${h}），拒绝输出废图。"
    }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    # flag 2 = PW_RENDERFULLCONTENT（DWM 合成内容也能抓到，被遮挡照样出图）
    $ok = [ToolboxCap.Api]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()

    if (-not $ok) {
        $bmp.Dispose()
        throw "${Label}: PrintWindow 失败。"
    }

    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("  saved {0}（{1}x{2}）" -f (Split-Path -Leaf $Path), $w, $h) -ForegroundColor Green
}

function Launch-App {
    param([string[]]$ExtraArgs)
    $argumentList = @($appDll) + $ExtraArgs
    return Start-Process -FilePath (Resolve-DotnetHost -Explicit $Dotnet) `
        -ArgumentList $argumentList -WorkingDirectory $appDir -PassThru
}

function Wait-PluginReady {
    param($Window)
    # 等插件标签页 + 报告按钮就位
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $button = Find-ByName -Root $Window -ControlType ([System.Windows.Automation.ControlType]::Button) -Name '检测 →'
        if ($null -ne $button) { return $button }
    }
    return $null
}

if (-not (Test-Path -LiteralPath $appDll)) {
    throw "找不到主程序：$appDll（先跑 build / verify）"
}

if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

Write-Host '== 抓取应用截图 ==' -ForegroundColor Cyan

# ── 场景 A：浅色主界面 + 命令面板 + 设置窗口 ────────────────────────────────
$proc = Launch-App -ExtraArgs @('--theme=light')
try {
    $window = Wait-Window -ProcessId $proc.Id -Title $mainTitle
    if ($null -eq $window) { throw '浅色场景：主窗口未出现。' }
    Start-Sleep -Seconds 3

    $probe = Wait-PluginReady -Window $window
    if ($null -ne $probe) {
        $probe.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 1200
        $probe = $null
    }

    Capture-Window -Element $window -Path (Join-Path $outDir '10-main-light.png') -Label '浅色主界面'

    # Ctrl+K 命令面板
    if (Invoke-Named -Root $window -ControlType ([System.Windows.Automation.ControlType]::Button) -Name '搜索（Ctrl K）') {
        Start-Sleep -Milliseconds 1200
        Capture-Window -Element $window -Path (Join-Path $outDir '11-palette.png') -Label '命令面板'
        Invoke-Named -Root $window -ControlType ([System.Windows.Automation.ControlType]::Button) -Name '搜索（Ctrl K）'
        Start-Sleep -Milliseconds 600
    }
    else {
        Write-Host '  [--] 找不到「搜索（Ctrl K）」，跳过命令面板截图' -ForegroundColor Yellow
    }

    # 设置窗口
    if (Invoke-Named -Root $window -ControlType ([System.Windows.Automation.ControlType]::Button) -Name '设置') {
        $settings = Wait-Window -ProcessId $proc.Id -Title $settingsTitle -Seconds 15
        if ($null -ne $settings) {
            Start-Sleep -Seconds 2
            Capture-Window -Element $settings -Path (Join-Path $outDir '12-settings.png') -Label '设置窗口'
            $closeButton = Find-ByName -Root $settings -ControlType ([System.Windows.Automation.ControlType]::Button) -Name '关闭'
            if ($null -ne $closeButton) {
                $closeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 600
            }
        }
    }

    # 关于窗口
    if (Invoke-Named -Root $window -ControlType ([System.Windows.Automation.ControlType]::Button) -Name '关于') {
        $about = Wait-Window -ProcessId $proc.Id -Title '关于 · Toolbox' -Seconds 10
        if ($null -ne $about) {
            Start-Sleep -Milliseconds 1200
            Capture-Window -Element $about -Path (Join-Path $outDir '14-about.png') -Label '关于窗口'
            $closeAbout = Find-ByName -Root $about -ControlType ([System.Windows.Automation.ControlType]::Button) -Name '关闭'
            if ($null -ne $closeAbout) {
                $closeAbout.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 600
            }
        }
    }

    # 插件市场
    if (Invoke-Named -Root $window -ControlType ([System.Windows.Automation.ControlType]::Button) -Name '插件市场') {
        $market = Wait-Window -ProcessId $proc.Id -Title '插件市场 · Toolbox' -Seconds 10
        if ($null -ne $market) {
            Start-Sleep -Seconds 2
            Capture-Window -Element $market -Path (Join-Path $outDir '15-market.png') -Label '插件市场'
            $closeMarket = Find-ByName -Root $market -ControlType ([System.Windows.Automation.ControlType]::Button) -Name '关闭'
            if ($null -ne $closeMarket) {
                $closeMarket.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 600
            }
        }
    }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 800
}

# ── 场景 B：深色主界面（--theme 参数不写回偏好文件）───────────────────────────
$proc = Launch-App -ExtraArgs @('--theme=dark')
try {
    $window = Wait-Window -ProcessId $proc.Id -Title $mainTitle
    if ($null -eq $window) { throw '深色场景：主窗口未出现。' }
    Start-Sleep -Seconds 3

    $probe = Wait-PluginReady -Window $window
    if ($null -ne $probe) {
        $probe.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 1200
        $probe = $null
    }

    Capture-Window -Element $window -Path (Join-Path $outDir '13-main-dark.png') -Label '深色主界面'
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 500
}

# ── 场景 C：启动画面（splash，出现即抓，成败不影响其它截图）─────────────────
$proc = Launch-App -ExtraArgs @('--theme=light')
try {
    $splash = $null
    $splashDeadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $splashDeadline -and $null -eq $splash) {
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 100
        $splash = Wait-Window -ProcessId $proc.Id -Title 'Toolbox 启动中' -Seconds 1
    }
    if ($null -ne $splash) {
        try {
            Capture-Window -Element $splash -Path (Join-Path $outDir '16-splash.png') -Label '启动画面'
        }
        catch {
            Write-Host "  [--] 启动画面截图失败（可能刚好淡出）：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
    else {
        Write-Host '  [--] 没抓到启动画面（可能加载太快）' -ForegroundColor Yellow
    }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 500
}

Write-Host ''
Write-Host "截图完成：$outDir" -ForegroundColor Green
