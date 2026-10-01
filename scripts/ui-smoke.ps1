#Requires -Version 5.1
<#
.SYNOPSIS
    主程序 GUI 端到端验证（UI Automation）：真实检测一次系统环境 + 卸载插件 + 校验 ALC 回收。

.DESCRIPTION
    1. 启动 Toolbox.App（WPF 主程序）；
    2. 找到插件贡献的「系统环境检测 · 环境报告」标签页并选中；
    3. 在插件界面里点「检测 →」（插件会真的去探测本机环境）；
    4. 读取报告框，与 PowerShell 侧独立算出的机器名/用户名/逻辑处理器/系统目录逐项比对
       （证明是真实探测，不是装饰文本）；
    5. 在插件列表里选中 sysinfo → 右键 → 菜单里点「卸载」（1.0.0-beta 起单插件操作走右键菜单）；
    6. 校验：视图标签页消失（视图移出视觉树）、报告内容从界面上消失、
       结果历史里出现卸载记录、日志里出现"已卸载并回收"且没有"未被回收"。

.EXAMPLE
    pwsh ./scripts/ui-smoke.ps1
    pwsh ./scripts/ui-smoke.ps1 -KeepOpen      # 验证完不关闭窗口，方便手动体验
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Dotnet,

    # 对照模式：不触碰插件界面元素（不点检测），只验证"卸载后能否回收"，
    # 用来区分"真实渲染钉住 ALC"与"自动化客户端引用钉住 ALC"。
    [Switch]$SkipProbe,

    [Switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$pluginId = 'sysinfo'
$tabNamePattern = '*系统环境*'
$reportTitleBoxName = '报告标题'
$reportBoxName = '环境报告'
$probeButtonName = '检测 →'

$repo = Split-Path -Parent $PSScriptRoot
$config = $Configuration.ToLowerInvariant()
$appDir = Join-Path $repo "artifacts\bin\Toolbox.App\$config"
$appDll = Join-Path $appDir 'Toolbox.App.dll'
$logDir = Join-Path $appDir 'logs'

$script:Total = 0
$script:Failures = 0

function Check {
    param([bool]$Condition, [string]$Description)
    $script:Total++
    if ($Condition) {
        Write-Host "  [PASS] $Description" -ForegroundColor Green
    }
    else {
        $script:Failures++
        Write-Host "  [FAIL] $Description" -ForegroundColor Red
    }
}

function Resolve-DotnetHost {
    param([string]$Explicit)

    if ($Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }

    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe')

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $version = (& $candidate --version 2>&1 | Out-String)
            if ($version -match '(?m)^10\.') { return $candidate }
        }
        finally { $ErrorActionPreference = $previous }
    }

    throw '找不到 .NET 10 SDK，请用 -Dotnet 指定 dotnet.exe。'
}

function Find-ByType {
    param($Root, $ControlType)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ByTypeAndName {
    param($Root, $ControlType, [string]$Name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $condition = New-Object System.Windows.Automation.AndCondition($typeCondition, $nameCondition)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

# 按"进程 + 窗口标题"定位主窗口：必须带 ProcessId，否则会连到残留的旧实例上。
function Get-AppWindow {
    param([int]$ProcessId, [string]$Title)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and
            $element.Current.Name -eq $Title) {
            return $element
        }
    }
    return $null
}

function Find-ButtonByName {
    param($Root, [string]$Name)
    return Find-ByTypeAndName -Root $Root -ControlType ([System.Windows.Automation.ControlType]::Button) -Name $Name
}

function Get-EditValue {
    param($Edit)
    if ($null -eq $Edit) { return $null }
    $pattern = $Edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    return $pattern.Current.Value
}

function Invoke-Element {
    param($Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

# ── 真实右键点击（上下文菜单是系统弹层，UIA .NET 客户端没有 ContextMenu 模式，只能发真实鼠标事件）──
Add-Type -Namespace ToolboxUi -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
'@

function Invoke-RightClick {
    param($Element)
    $rect = $Element.Current.BoundingRectangle
    $x = [int]($rect.X + ($rect.Width / 2))
    $y = [int]($rect.Y + ($rect.Height / 2))
    [ToolboxUi.Native]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    [ToolboxUi.Native]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)   # RIGHTDOWN
    Start-Sleep -Milliseconds 60
    [ToolboxUi.Native]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)   # RIGHTUP
}

# 浮层/右键菜单是独立的顶层 HWND：必须从 RootElement 按进程过滤来找。
function Find-ProcessMenuItem {
    param([int]$ProcessId, [string]$NamePattern)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem)))
    foreach ($item in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($item.Current.Name -like $NamePattern) { return $item }
    }
    return $null
}

if (-not (Test-Path -LiteralPath $appDll)) {
    throw "找不到主程序：$appDll（先运行 scripts/verify.ps1）"
}

$dotnetPath = Resolve-DotnetHost -Explicit $Dotnet
Remove-Item (Join-Path $logDir '*.log') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $appDir 'plugins\.shadow') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '== 主程序 GUI 端到端验证 ==' -ForegroundColor Cyan
Write-Host "  应用      : $appDll"
Write-Host "  dotnet    : $dotnetPath"

$process = Start-Process -FilePath $dotnetPath -ArgumentList @($appDll) -WorkingDirectory $appDir -PassThru

try {
    # ── 1. 等主窗口与插件标签页 ──────────────────────────────────────────────
    Write-Host ''
    Write-Host '--- 1/4 等待主窗口 ---' -ForegroundColor Cyan

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $window = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and $null -eq $window) {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited) { break }
        # ⚠ 必须同时按 ProcessId 过滤：否则机器上如果还残留着另一个 Toolbox 实例，
        #    这里会连到那个旧窗口上（它可能加载着完全不同的插件），断言全部失去意义。
        $window = Get-AppWindow -ProcessId $process.Id -Title 'Toolbox · 插件化工具箱'
    }

    if ($process.HasExited) {
        Write-Host "主程序启动即退出（退出码 0x$('{0:X8}' -f $process.ExitCode)）。" -ForegroundColor Red
        Check $false '主程序启动未崩溃'
        return
    }

    Check ($null -ne $window) '主窗口已出现（Toolbox · 插件化工具箱）'
    if ($null -eq $window) {
        Write-Host '主窗口未出现，后续步骤跳过。' -ForegroundColor Red
        return
    }

    # 等插件加载完（标签页出现）
    $tab = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and $null -eq $tab) {
        Start-Sleep -Milliseconds 500
        $tabs = @(Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::TabItem))
        foreach ($candidate in $tabs) {
            if ($candidate.Current.Name -like $tabNamePattern) { $tab = $candidate; break }
        }
        # 兜底：数据绑定的 TabItem 可能只暴露空名/类名，只要只有一个标签页就直接用它
        if ($null -eq $tab -and $tabs.Count -eq 1) { $tab = $tabs[0] }
    }

    Check ($null -ne $tab) "插件贡献的标签页已出现：$($tab.Current.Name)"

    # ── 2. 选中标签页并真实检测一次系统环境 ──────────────────────────────────
    Write-Host ''
    Write-Host '--- 2/4 在插件界面上执行一次真实的环境检测 ---' -ForegroundColor Cyan

    if ($null -ne $tab) {
        $selection = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
        $selection = $null
        Start-Sleep -Milliseconds 700
    }

    $expected = @{}
    $report = $null
    $titleValue = $null
    $probeButton = $null
    $statusText = ''

    if ($SkipProbe) {
        Write-Host '    （对照模式：跳过检测，不读取/不点击插件界面元素）' -ForegroundColor Yellow
    }
    else {
        # 插件界面的输入框/报告框都带 AutomationProperties.Name，可以稳定定位（不用猜第几个）
        $titleBox = Find-ByTypeAndName -Root $window -ControlType ([System.Windows.Automation.ControlType]::Edit) -Name $reportTitleBoxName
        $titleValue = Get-EditValue -Edit $titleBox
        Write-Host "    报告标题框内容：$titleValue"
        Check ([bool]$titleValue) '读到了插件界面里的报告标题（来自主程序配置 title）'

        # PowerShell 侧独立算出"应该出现的事实"，用来证明插件真的探测了本机
        $expected.MachineName = [Environment]::MachineName
        $expected.UserName = [Environment]::UserName
        $expected.ProcessorCount = [Environment]::ProcessorCount.ToString()
        $expected.SystemDirectory = [Environment]::SystemDirectory
        foreach ($pair in $expected.GetEnumerator()) {
            Write-Host "    PowerShell 侧独立取到：$($pair.Key) = $($pair.Value)"
        }

        $probeButton = Find-ButtonByName -Root $window -Name $probeButtonName
        Check ($null -ne $probeButton) "找到了插件界面里的「$probeButtonName」按钮"

        if ($null -ne $probeButton -and $null -ne $titleBox) {
            Invoke-Element -Element $probeButton
            Start-Sleep -Milliseconds 900
            $titleBox = $null
        }

        $reportBox = Find-ByTypeAndName -Root $window -ControlType ([System.Windows.Automation.ControlType]::Edit) -Name $reportBoxName
        $report = Get-EditValue -Edit $reportBox
        $hasReport = [bool]$report

        Check $hasReport '插件界面里的报告框已经产出内容'
        Check ($hasReport -and $titleValue -and $report.Contains([string]$titleValue)) `
            '报告标题与配置一致（plugin.json 默认值 < 主程序配置）'

        foreach ($key in @('MachineName', 'UserName', 'ProcessorCount', 'SystemDirectory')) {
            Check ($hasReport -and $report.Contains([string]$expected[$key])) `
                "报告里包含独立对照值 $key = $($expected[$key])"
        }

        # 状态行（插件界面里的 TextBlock）
        $statusText = ''
        foreach ($text in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::Text))) {
            if ($text.Current.Name -like '*检测完成*') { $statusText = $text.Current.Name; break }
        }
        Check ($statusText -like '*检测完成*') "插件界面状态栏反馈：$statusText"
    }

    # ── 3. 通过 UI 卸载插件 ──────────────────────────────────────────────────
    Write-Host ''
    Write-Host '--- 3/4 在界面上卸载 sysinfo 插件 ---' -ForegroundColor Cyan

    # ⚠ 自动化客户端持有的"插件界面元素"引用会经由 UIA provider 钉住插件 ALC，
    #    卸载前必须释放（这正是"宿主侧任何对插件元素的引用都算数"的实例）。
    $report = $null
    $probeButton = $null
    $titleValue = $null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    [GC]::Collect()
    Start-Sleep -Milliseconds 500

    $row = $null
    foreach ($item in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
        if ($item.Current.Name -eq $pluginId) { $row = $item; break }
    }
    Check ($null -ne $row) "在插件列表里找到 $pluginId 行"

    if ($null -ne $row) {
        $rowSelection = $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $rowSelection.Select()
        $rowSelection = $null
        Start-Sleep -Milliseconds 400

        # 真实右键 → 打开单插件上下文菜单
        Invoke-RightClick -Element $row
        $row = $null
        Start-Sleep -Milliseconds 900
    }

    # 菜单四项（打开 / 卸载 / 重载 / 删除）应该都在
    $menuOpen = Find-ProcessMenuItem -ProcessId $process.Id -NamePattern '打开（加载并显示界面）'
    $menuUnload = Find-ProcessMenuItem -ProcessId $process.Id -NamePattern '卸载（验证 ALC 回收）'
    $menuReload = Find-ProcessMenuItem -ProcessId $process.Id -NamePattern '重载（新 ALC）'
    $menuDelete = Find-ProcessMenuItem -ProcessId $process.Id -NamePattern '删除插件…*'
    Check ($null -ne $menuOpen -and $null -ne $menuUnload -and $null -ne $menuReload -and $null -ne $menuDelete) `
        "右键菜单提供 卸载/重载/删除/打开 四个单插件操作（打开=$(if ($menuOpen) { '√' } else { '×' }) 卸载=$(if ($menuUnload) { '√' } else { '×' }) 重载=$(if ($menuReload) { '√' } else { '×' }) 删除=$(if ($menuDelete) { '√' } else { '×' })）"

    if ($null -ne $menuUnload) {
        $menuUnload.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $menuUnload = $null
        $menuOpen = $null
        $menuReload = $null
        $menuDelete = $null
        Start-Sleep -Seconds 3
    }
    else {
        Check $false '无法执行卸载（右键菜单没弹出来）'
    }

    # 视图标签页应该消失（视图已移出视觉树）
    $tabStillThere = $false
    foreach ($candidate in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::TabItem))) {
        if ($candidate.Current.Name -like $tabNamePattern) { $tabStillThere = $true }
    }
    Check (-not $tabStillThere) '卸载后插件的界面标签页已消失（视图移出视觉树）'

    $reportBoxAfter = Find-ByTypeAndName -Root $window -ControlType ([System.Windows.Automation.ControlType]::Edit) -Name $reportBoxName
    Check ($null -eq $reportBoxAfter) '报告框也已从界面上消失（视图对象不再被界面持有）'

    # 结果历史：卸载结果应该留档（1.0.0-beta 新功能）
    $historyEntry = $null
    foreach ($text in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        if ($text.Current.Name -like "*卸载 $pluginId*") { $historyEntry = $text.Current.Name; break }
    }
    Check ($null -ne $historyEntry) "结果历史里留下卸载记录：$historyEntry"

    # ── 4. 校验回收日志 ──────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '--- 4/4 校验卸载回收结果 ---' -ForegroundColor Cyan

    $log = Get-ChildItem (Join-Path $logDir '*.log') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $logText = if ($log) { Get-Content $log.FullName -Raw -Encoding UTF8 } else { '' }

    $collectedLine = ([regex]::Matches($logText, "插件 $pluginId 已卸载并回收（(\d+) 轮 GC[^）]*）") |
        Select-Object -Last 1)
    Check ($collectedLine.Count -gt 0 -or $collectedLine.Success) `
        "日志里有卸载回收记录：$(if ($collectedLine.Success) { $collectedLine.Value } else { '<无>' })"

    $rounds = if ($collectedLine.Success) { [int]$collectedLine.Groups[1].Value } else { 999 }
    Check ($rounds -le 2) "回收轮数 ≤ 2（实际 $rounds 轮）"
    Check ($logText -notmatch '未被回收') '日志里没有"未被回收"告警'

    # 状态栏显示已加载插件数
    $statusLine = ''
    foreach ($text in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        if ($text.Current.Name -like '已加载插件：*') { $statusLine = $text.Current.Name; break }
    }
    Check ($statusLine -like '已加载插件：*') "状态栏显示已加载插件数：$statusLine"

    Write-Host ''
    Write-Host "合计 $script:Total 项断言，失败 $script:Failures 项。"
    Write-Host $(if ($script:Failures -eq 0) { 'GUI 验证：全部通过 ✅' } else { 'GUI 验证：存在失败 ❌' })
}
finally {
    if (-not $KeepOpen) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            Write-Host '（已关闭主程序）'
        }
    }
    else {
        Write-Host "（主程序保持打开，PID = $($process.Id)）"
    }
}
