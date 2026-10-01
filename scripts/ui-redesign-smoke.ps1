#Requires -Version 5.1
<#
.SYNOPSIS
    UI 观感改版的端到端验证（UI Automation）：左侧工具栏 / 右侧工具输出 / 左上头像 / 小齿轮设置。

.DESCRIPTION
    与 scripts/ui-smoke.ps1（卸载 + ALC 回收验证的"权威"脚本）互补：
    * ui-smoke.ps1 负责证明「插件视图在卸载前被移出视觉树、ALC 能回收」——不因改版而降级；
    * 本脚本负责证明「改版要求真的落地了」，并且没有破坏 ui-smoke 依赖的观测点。

    验证项：
      1. 主窗口正常启动（标题仍是 ui-smoke 依赖的 "Toolbox · 插件化工具箱"）；
      2. 工具栏在**左侧**：刷新/加载全部/打开插件目录 三个动作按钮齐全且都在窗口左侧 12% 区域内；
      3. 「添加工具」按钮在左侧，点开浮层给出"插件包 / 插件文件夹"两条入口，再点收起；
      4. 用户头像在**左上角**；
      5. 设置是**右上角的小齿轮**，且能打开只读设置窗口；
      6. **工具输出（结果历史 + 日志）**在窗口右侧，且「复制全部 / 清空」入口齐全；
      7. 插件界面仍然挂在 TabControl 上（ui-smoke 靠 TabItem 判定"视图已移出视觉树"）；
      8. 插件列表行以 ListItem 暴露、名称就是插件 Id（无障碍 + 自动化可稳定定位）；
      9. ui-smoke 依赖的其它观测点仍然存在：状态栏 "已加载插件：…"；
     10. 设置窗口如实显示"生效中的宿主配置"（共享程序集不重复），并提供主题切换；
     11. **Ctrl+K 命令面板**（1.0.0-beta 新功能）：顶部搜索胶囊呼出，搜索框 + 命令列表齐全，
         过滤后仍有条目，再点一次收起。

.EXAMPLE
    pwsh ./scripts/ui-redesign-smoke.ps1
    pwsh ./scripts/ui-redesign-smoke.ps1 -Dotnet 'C:\Users\me\.dotnet10\dotnet.exe'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Dotnet,

    # 验证完不关闭窗口，方便手动体验
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repo = Split-Path -Parent $PSScriptRoot
$config = $Configuration.ToLowerInvariant()
$appDir = Join-Path $repo "artifacts\bin\Toolbox.App\$config"
$appDll = Join-Path $appDir 'Toolbox.App.dll'

$mainWindowTitle = 'Toolbox · 插件化工具箱'
$settingsWindowTitle = '设置 · Toolbox'

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

function Find-Buttons {
    param($Root)
    return Find-ByType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::Button)
}

function Find-ButtonByName {
    param($Root, [string]$Name)
    foreach ($button in (Find-Buttons -Root $Root)) {
        if ($button.Current.Name -eq $Name) { return $button }
    }
    return $null
}

function Find-ElementByName {
    param($Root, [string]$Name)
    foreach ($element in (Find-ByType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        if ($element.Current.Name -eq $Name) { return $element }
    }
    return $null
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

# 浮层（Popup）是独立的顶层 HWND，不是主窗口的后代节点，必须从 RootElement 按进程 + 名字找。
function Find-ProcessButtonByName {
    param([int]$ProcessId, [string]$Name)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-Rect {
    param($Element)
    $r = $Element.Current.BoundingRectangle
    return [pscustomobject]@{
        X      = $r.X
        Y      = $r.Y
        Width  = $r.Width
        Height = $r.Height
        Right  = $r.X + $r.Width
        Bottom = $r.Y + $r.Height
    }
}

function Get-AppWindow {
    param([int]$ProcessId, [string]$Title)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

    # 注意：ShowInTaskbar=False 的模态窗口不是 RootElement 的直接子节点，必须按后代找。
    # try/catch：启动早期窗口刚生刚灭，FindAll 偶发 ElementNotAvailable —— 视为"这次没找到"。
    try {
        foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and
                $element.Current.Name -eq $Title) {
                return $element
            }
        }
    }
    catch {
        return $null
    }
    return $null
}

if (-not (Test-Path -LiteralPath $appDll)) {
    throw "找不到主程序：$appDll（先运行 scripts/verify.ps1）"
}

$dotnetPath = Resolve-DotnetHost -Explicit $Dotnet

Write-Host '== Toolbox UI 改版验证（UI Automation）==' -ForegroundColor Cyan
Write-Host "  应用      : $appDll"
Write-Host "  dotnet    : $dotnetPath"

$process = Start-Process -FilePath $dotnetPath -ArgumentList @($appDll) -WorkingDirectory $appDir -PassThru
$exitCode = 1

try {
    # ── 1. 主窗口 ────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '--- 1/4 启动画面与主窗口骨架 ---' -ForegroundColor Cyan

    # 启动画面（splash）：插件加载期间显示，主窗口出现后淡出 —— 在等主窗口之前先抓它
    $splash = $null
    $splashDeadline = (Get-Date).AddSeconds(8)
    $splashHits = 0
    while ((Get-Date) -lt $splashDeadline -and $null -eq $splash) {
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 120
        $splashHits++
        $splash = Get-AppWindow -ProcessId $process.Id -Title 'Toolbox 启动中'
    }
    Check ($null -ne $splash) "启动画面先于主窗口出现（splash · Toolbox 启动中，轮询 $splashHits 次）"
    if ($null -eq $splash -and -not $process.HasExited) {
        # 诊断：列出该进程当时可见的顶层窗口名
        try {
            $rootEl = [System.Windows.Automation.AutomationElement]::RootElement
            $pidCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
            foreach ($el in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond)) {
                if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) {
                    Write-Host "    [当时窗口] '$($el.Current.Name)'" -ForegroundColor Yellow
                }
            }
        }
        catch { }
    }
    $splash = $null

    $window = $null
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and $null -eq $window) {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited) { break }
        $window = Get-AppWindow -ProcessId $process.Id -Title $mainWindowTitle
    }

    if ($process.HasExited) {
        Write-Host "  主程序启动即退出（退出码 0x$('{0:X8}' -f $process.ExitCode)）。" -ForegroundColor Red
        throw '主程序启动崩溃。最常见原因是 XAML 运行期错误（StaticResource / Style TargetType 与容器类型不匹配 / 资源字典路径）。'
    }

    if ($null -eq $window) {
        throw "主窗口未出现（标题应为「$mainWindowTitle」）。"
    }

    Check $true "主窗口已出现（标题仍为「$mainWindowTitle」）"

    # 等插件加载完（列表行 / 标签页出现）
    Start-Sleep -Seconds 4

    $windowRect = Get-Rect -Element $window
    Write-Host ("    窗口区域：{0},{1} {2}x{3}" -f `
        [int]$windowRect.X, [int]$windowRect.Y, [int]$windowRect.Width, [int]$windowRect.Height)

    # ── 2. 左工具栏 / 左上头像 / 右上齿轮 ────────────────────────────────────
    Write-Host ''
    Write-Host '--- 2/4 最左侧图文工具栏 · 窗口按钮 · 左下角设置 ---' -ForegroundColor Cyan

    # 1.0.0-beta 重排：工具栏 = 图文按钮（可折叠文字）；
    # 「重新扫描/重新加载」移进设置窗口；新增「日志」「插件市场」「关于」。
    $railActions = @('日志', '添加工具', '插件市场', '打开插件目录', '关于', '设置')
    $railFound = 0
    $railLeftMost = $true
    $leftBoundary = $windowRect.X + ($windowRect.Width * 0.12)

    foreach ($action in $railActions) {
        $button = Find-ButtonByName -Root $window -Name $action
        if ($null -eq $button) {
            Write-Host "    [--] 缺少工具栏按钮：$action" -ForegroundColor Yellow
            continue
        }

        $railFound++
        $rect = Get-Rect -Element $button
        if ($rect.Right -gt $leftBoundary) {
            $railLeftMost = $false
            Write-Host ("    [--] {0} 不在左侧：right={1:F0} > 边界 {2:F0}" -f $action, $rect.Right, $leftBoundary) -ForegroundColor Yellow
        }
    }

    Check ($railFound -eq $railActions.Count) "左侧工具栏 $($railActions.Count) 个图文按钮齐全（实际 $railFound / $($railActions.Count)）"
    Check $railLeftMost '工具栏全部位于窗口左侧 12% 区域内（工具栏在左）'
    Check ($null -eq (Find-ButtonByName -Root $window -Name '刷新目录')) '「刷新目录」已从工具栏移除（移入设置窗口）'
    Check ($null -eq (Find-ButtonByName -Root $window -Name '加载全部')) '「加载全部」已从工具栏移除（移入设置窗口）'

    # ── 图文工具栏：默认展开（带文字），可折叠成纯图标 ──
    $railLabel = Find-ElementByName -Root $window -Name '打开插件目录'
    Check ($null -ne $railLabel) '工具栏默认展开：按钮带文字标签'

    $marketBtn = Find-ButtonByName -Root $window -Name '插件市场'
    $widthBefore = 0
    if ($null -ne $marketBtn) { $widthBefore = [int](Get-Rect -Element $marketBtn).Width }

    $collapse = Find-ButtonByName -Root $window -Name '收起侧栏'
    Check ($null -ne $collapse) '存在折叠开关（收起侧栏）'
    if ($null -ne $collapse) {
        $collapse.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 900

        $railLabelAfter = Find-ElementByName -Root $window -Name '打开插件目录'
        Check ($null -eq $railLabelAfter) '折叠后文字标签隐藏（只剩图标）'

        $expand = Find-ButtonByName -Root $window -Name '展开侧栏'
        Check ($null -ne $expand) '折叠后开关改名为「展开侧栏」'

        $widthAfter = 0
        if ($null -ne $marketBtn) { $widthAfter = [int](Get-Rect -Element $marketBtn).Width }
        Check ($widthAfter -gt 0 -and $widthBefore -gt $widthAfter) `
            "折叠后栏体收窄（$widthBefore → $widthAfter 像素）"

        if ($null -ne $expand) {
            $expand.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Start-Sleep -Milliseconds 900
            $railLabelBack = Find-ElementByName -Root $window -Name '打开插件目录'
            Check ($null -ne $railLabelBack) '再次切换后文字标签恢复（可来回折叠）'
        }
    }

    # ── 无边框窗口的自定义窗口按钮（右上角）──
    $minBtn = Find-ButtonByName -Root $window -Name '最小化'
    $maxBtn = Find-ButtonByName -Root $window -Name '最大化或还原'
    $closeBtn = Find-ButtonByName -Root $window -Name '关闭'
    Check ($null -ne $minBtn -and $null -ne $maxBtn -and $null -ne $closeBtn) `
        '自定义标题栏有 最小化 / 最大化或还原 / 关闭 三个窗口按钮'
    if ($null -ne $closeBtn) {
        $closeRect = Get-Rect -Element $closeBtn
        $closeTopRight =
            $closeRect.X -gt ($windowRect.X + $windowRect.Width * 0.60) -and
            $closeRect.Y -lt ($windowRect.Y + $windowRect.Height * 0.14)
        Check $closeTopRight ('关闭按钮位于右上角标题栏区域（{0},{1}）' -f [int]$closeRect.X, [int]$closeRect.Y)
    }

    # 「添加工具」：左侧工具栏里新增的入口，点开浮层给出"插件包 / 插件文件夹"两条路径
    $addTool = Find-ButtonByName -Root $window -Name '添加工具'
    Check ($null -ne $addTool) '「添加工具」按钮存在（AutomationProperties.Name = 添加工具）'
    if ($null -ne $addTool) {
        $addRect = Get-Rect -Element $addTool
        Check ($addRect.Right -le $leftBoundary) '「添加工具」也在左侧工具栏内'

        # ToggleButton 暴露的是 TogglePattern（不是 InvokePattern）
        $addTool.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()

        # 弹层首次创建偶有延迟：轮询最多 ~4s 等两条入口出现
        $optionPackage = $null
        $optionFolder = $null
        $pollUntil = (Get-Date).AddSeconds(4)
        while ((Get-Date) -lt $pollUntil -and ($null -eq $optionPackage -or $null -eq $optionFolder)) {
            Start-Sleep -Milliseconds 300
            $optionPackage = Find-ProcessButtonByName -ProcessId $process.Id -Name '选择插件包'
            $optionFolder = Find-ProcessButtonByName -ProcessId $process.Id -Name '选择插件文件夹'
        }
        Check ($null -ne $optionPackage) '浮层提供「选择插件包」（.zip / .dll）入口'
        Check ($null -ne $optionFolder) '浮层提供「选择插件文件夹」（目录插件）入口'

        $addTool.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 700
        Check ($null -eq (Find-ProcessButtonByName -ProcessId $process.Id -Name '选择插件包')) '再次点击会收起浮层'
        $optionPackage = $null
        $optionFolder = $null
    }

    $avatar = Find-ButtonByName -Root $window -Name '用户头像'
    Check ($null -ne $avatar) '用户头像控件存在（AutomationProperties.Name = 用户头像）'
    if ($null -ne $avatar) {
        $avatarRect = Get-Rect -Element $avatar
        $avatarTopLeft =
            $avatarRect.X -lt ($windowRect.X + $windowRect.Width * 0.10) -and
            $avatarRect.Y -lt ($windowRect.Y + $windowRect.Height * 0.14) -and
            $avatarRect.Width -le 56 -and $avatarRect.Height -le 56
        Check $avatarTopLeft ('头像位于左上角且是小尺寸（{0},{1} {2}x{3}）' -f `
            [int]$avatarRect.X, [int]$avatarRect.Y, [int]$avatarRect.Width, [int]$avatarRect.Height)
    }

    # 1.0.0-beta：设置移到左下角，新增「关于」也在左下角区域
    $gear = Find-ButtonByName -Root $window -Name '设置'
    Check ($null -ne $gear) '设置入口存在且是「设置」按钮（AutomationProperties.Name = 设置）'
    if ($null -ne $gear) {
        $gearRect = Get-Rect -Element $gear
        $gearBottomLeft =
            $gearRect.X -lt ($windowRect.X + $windowRect.Width * 0.12) -and
            $gearRect.Y -gt ($windowRect.Y + $windowRect.Height * 0.70)
        Check $gearBottomLeft ('设置位于左下角（{0},{1}）' -f [int]$gearRect.X, [int]$gearRect.Y)
    }

    $aboutBtn = Find-ButtonByName -Root $window -Name '关于'
    Check ($null -ne $aboutBtn) '「关于」入口存在（1.0.0-beta 新增）'
    if ($null -ne $aboutBtn) {
        $aboutRect = Get-Rect -Element $aboutBtn
        $aboutLeft =
            $aboutRect.X -lt ($windowRect.X + $windowRect.Width * 0.12) -and
            $aboutRect.Y -gt ($windowRect.Y + $windowRect.Height * 0.60)
        Check $aboutLeft ('关于位于左侧栏下部（{0},{1}）' -f [int]$aboutRect.X, [int]$aboutRect.Y)
    }

    # ── 2.5 Ctrl+K 命令面板（1.0.0-beta 新功能）──────────────────────────────
    Write-Host ''
    Write-Host '--- 2.5/4 Ctrl+K 命令面板 ---' -ForegroundColor Cyan

    $searchButton = Find-ButtonByName -Root $window -Name '搜索（Ctrl K）'
    Check ($null -ne $searchButton) '顶部有「搜索（Ctrl K）」胶囊入口'
    if ($null -ne $searchButton) {
        $searchButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 900

        $paletteEdit = Find-ByTypeAndName -Root $window -ControlType ([System.Windows.Automation.ControlType]::Edit) -Name '命令搜索'
        Check ($null -ne $paletteEdit) '命令面板打开：出现「命令搜索」输入框'

        $paletteList = Find-ByTypeAndName -Root $window -ControlType ([System.Windows.Automation.ControlType]::List) -Name '命令列表'
        Check ($null -ne $paletteList) '命令面板打开：出现「命令列表」'

        $itemCount = 0
        if ($null -ne $paletteList) {
            $itemCount = (Find-ByType -Root $paletteList -ControlType ([System.Windows.Automation.ControlType]::ListItem)).Count
        }
        Check ($itemCount -gt 0) "命令面板列出了可执行命令（$itemCount 条）"

        if ($null -ne $paletteEdit) {
            $valuePattern = $paletteEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $valuePattern.SetValue('sysinfo')
            Start-Sleep -Milliseconds 600
            $filteredCount = 0
            if ($null -ne $paletteList) {
                $filteredCount = (Find-ByType -Root $paletteList -ControlType ([System.Windows.Automation.ControlType]::ListItem)).Count
            }
            Check ($filteredCount -gt 0 -and $filteredCount -le $itemCount) `
                "按关键字过滤生效（$filteredCount / $itemCount 条）"
            $valuePattern.SetValue('')
            Start-Sleep -Milliseconds 300
            $valuePattern = $null
        }

        # 再点一次胶囊收起
        $searchButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 800
        $paletteEditAfter = Find-ByTypeAndName -Root $window -ControlType ([System.Windows.Automation.ControlType]::Edit) -Name '命令搜索'
        Check ($null -eq $paletteEditAfter) '再次点击胶囊会收起命令面板'

        $searchButton = $null
        $paletteEdit = $null
        $paletteList = $null
    }

    # ── 3. 右侧工具输出 + 兼容 ui-smoke 的观测点 ─────────────────────────────
    Write-Host ''
    Write-Host '--- 3/4 右侧工具输出 · 插件界面标签页 · 状态栏 ---' -ForegroundColor Cyan

    $outputTitle = Find-ElementByName -Root $window -Name '工具输出'
    Check ($null -ne $outputTitle) '存在「工具输出」面板标题'
    if ($null -ne $outputTitle) {
        $outputRect = Get-Rect -Element $outputTitle
        Check ($outputRect.X -gt ($windowRect.X + $windowRect.Width * 0.60)) `
            ('工具输出面板位于窗口右侧（x={0:F0} > 60% 边界 {1:F0}）' -f `
                $outputRect.X, ($windowRect.X + $windowRect.Width * 0.60))
    }

    # 结果历史（1.0.0-beta 新功能）：区块标题 + 一键复制/清空
    $historyTitle = Find-ElementByName -Root $window -Name '结果历史'
    Check ($null -ne $historyTitle) '右侧存在「结果历史」区块'
    Check ($null -ne (Find-ButtonByName -Root $window -Name '复制全部结果')) '结果历史提供「复制全部」'
    Check ($null -ne (Find-ButtonByName -Root $window -Name '清空结果历史')) '结果历史提供「清空」'
    # 注：日志已从右侧面板移出，成为最左侧「日志」的独立视图（下面单独验证往返切换）

    $tabItem = $null
    foreach ($tab in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::TabItem))) {
        if ($tab.Current.Name -like '*系统环境*') { $tabItem = $tab; break }
    }
    Check ($null -ne $tabItem) "插件界面仍然是 TabControl 里的标签页：$($tabItem.Current.Name)"

    $pluginRow = $null
    foreach ($item in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
        if ($item.Current.Name -eq 'sysinfo') { $pluginRow = $item; break }
    }
    Check ($null -ne $pluginRow) '插件列表行以 ListItem 暴露且名称就是插件 Id（sysinfo）'

    # 插件过滤框（按名称/Id 过滤可见行）
    $filterEdit = Find-ByTypeAndName -Root $window -ControlType ([System.Windows.Automation.ControlType]::Edit) -Name '过滤插件'
    Check ($null -ne $filterEdit) '插件列表带过滤框（AutomationProperties.Name = 过滤插件）'
    if ($null -ne $filterEdit) {
        $valuePattern = $filterEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $valuePattern.SetValue('zzz-no-such-plugin')
        Start-Sleep -Milliseconds 700
        $rowsFiltered = 0
        foreach ($item in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
            if ($item.Current.Name -eq 'sysinfo') { $rowsFiltered++ }
        }
        Check ($rowsFiltered -eq 0) '过滤词不匹配时列表被清空'

        $valuePattern.SetValue('')
        Start-Sleep -Milliseconds 700
        $rowsBack = 0
        foreach ($item in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
            if ($item.Current.Name -eq 'sysinfo') { $rowsBack++ }
        }
        Check ($rowsBack -eq 1) '清空过滤词后列表恢复'
        $valuePattern = $null
        $filterEdit = $null
    }

    # 结果历史导出入口（只断言存在，不触发文件对话框）
    Check ($null -ne (Find-ButtonByName -Root $window -Name '导出结果历史')) '结果历史提供「导出…」（导出 txt）'

    $statusLine = ''
    foreach ($text in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        if ($text.Current.Name -like '已加载插件：*') { $statusLine = $text.Current.Name; break }
    }
    Check ($statusLine -like '已加载插件：*') "状态栏文本格式未变：$statusLine"

    # ── 日志独立功能：切到日志视图（插件界面隐藏）再切回 ──
    $logToggle = Find-ButtonByName -Root $window -Name '日志'
    Check ($null -ne $logToggle) '最左侧有「日志」独立功能入口'
    if ($null -ne $logToggle) {
        $logToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 900

        $logHeader = Find-ElementByName -Root $window -Name '日志（插件日志带 [pluginId] 前缀）'
        Check ($null -ne $logHeader) '切到日志视图：日志面板出现'

        $logList = Find-ByTypeAndName -Root $window -ControlType ([System.Windows.Automation.ControlType]::List) -Name '日志'
        Check ($null -ne $logList) '日志视图里能看到日志列表'

        $tabWhileLog = $false
        foreach ($candidate in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::TabItem))) {
            if ($candidate.Current.Name -like '*系统环境*') { $tabWhileLog = $true; break }
        }
        Check (-not $tabWhileLog) '日志视图下插件界面隐藏（Collapsed，逻辑树仍保留）'

        $logToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 900

        $tabBack = $false
        foreach ($candidate in (Find-ByType -Root $window -ControlType ([System.Windows.Automation.ControlType]::TabItem))) {
            if ($candidate.Current.Name -like '*系统环境*') { $tabBack = $true; break }
        }
        Check $tabBack '切回后插件界面标签页恢复可见'
        $logToggle = $null
        $logList = $null
    }

    # ── 4. 设置窗口：分类放左、内容放右 ────────────────────────────────────
    Write-Host ''
    Write-Host '--- 4/4 设置（左分类/右内容）· 新增开关与标签 ---' -ForegroundColor Cyan

    # 左侧分类导航切换（分类项的 UIA 名称 = Title）
    function Select-SettingsSection {
        param($Win, [string]$SectionName)
        $nav = Find-ByTypeAndName -Root $Win -ControlType ([System.Windows.Automation.ControlType]::List) -Name '设置分类导航'
        if ($null -eq $nav) { return $false }
        $item = $null
        foreach ($li in (Find-ByType -Root $nav -ControlType ([System.Windows.Automation.ControlType]::ListItem))) {
            if ($li.Current.Name -eq $SectionName) { $item = $li; break }
        }
        if ($null -eq $item) { return $false }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 700
        return $true
    }

    if ($null -eq $gear) {
        Check $false '无法验证设置窗口（找不到设置按钮）'
    }
    else {
        $gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 3

        $settingsWindow = Get-AppWindow -ProcessId $process.Id -Title $settingsWindowTitle
        Check ($null -ne $settingsWindow) "设置按钮打开了设置窗口（$settingsWindowTitle）"

        if ($null -ne $settingsWindow) {
            # 分类导航存在（分类放左、内容放右）
            $navList = Find-ByTypeAndName -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::List) -Name '设置分类导航'
            Check ($null -ne $navList) '设置窗口左侧有分类导航（分类放左、内容放右）'

            # ── 默认分类「外观」：主题 + 内测标签 + 字号缩放 ──
            # ⚠ WPF 的 RadioButton 在 UIA 里只暴露 SelectionItemPattern（没有 Toggle/Invoke）。
            $lightRadio = Find-ByTypeAndName -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::RadioButton) -Name '浅色主题'
            $darkRadio = Find-ByTypeAndName -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::RadioButton) -Name '深色主题'
            Check ($null -ne $lightRadio -and $null -ne $darkRadio) '默认分类「外观」提供浅色 / 深色主题切换'

            $neiceTag = $false
            foreach ($text in (Find-ByType -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::Text))) {
                if ($text.Current.Name -eq '内测') { $neiceTag = $true; break }
            }
            Check $neiceTag '「深色」选项旁带「内测」小标签'

            if ($null -ne $darkRadio) {
                $darkRadio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Milliseconds 700
                $darkSelected = $darkRadio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
                Check $darkSelected '选「深色主题」后该选项变为选中'

                if ($null -ne $lightRadio) {
                    $lightRadio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                    Start-Sleep -Milliseconds 700
                    $lightSelected = $lightRadio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
                    Check $lightSelected '选回「浅色主题」后选中态跟着切换（不留脏状态）'
                    $lightRadio = $null
                }

                $darkRadio = $null
            }

            # 字号缩放：125% 后主窗口放大，再切回 100% 恢复
            $scale125 = Find-ByTypeAndName -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::RadioButton) -Name '字号 125%'
            $scale100 = Find-ByTypeAndName -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::RadioButton) -Name '字号 100%'
            Check ($null -ne $scale125 -and $null -ne $scale100) '外观分类提供字号缩放（100% / 125% / 150%）'
            if ($null -ne $scale125) {
                $widthBefore = [int](Get-Rect -Element $window).Width
                $scale125.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Milliseconds 900
                $widthZoomed = [int](Get-Rect -Element $window).Width
                $scale125Selected = $scale125.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
                Check ($scale125Selected -and $widthZoomed -gt $widthBefore) `
                    "字号 125% 生效且主窗口同步放大（$widthBefore → $widthZoomed）"

                $scale100.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Milliseconds 900
                $widthBack = [int](Get-Rect -Element $window).Width
                Check ($widthBack -lt $widthZoomed) "切回 100% 恢复窗口尺寸（$widthZoomed → $widthBack）"
                $scale125 = $null
                $scale100 = $null
            }

            # ── 分类「目录」：路径 + 重新扫描/重新加载 ──
            Check (Select-SettingsSection -Win $settingsWindow -SectionName '目录') '分类导航可切换到「目录」'
            $texts = @()
            foreach ($text in (Find-ByType -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::Text))) {
                $texts += $text.Current.Name
            }
            $hasPluginPath = $false
            foreach ($text in $texts) { if ($text -like '*plugins*') { $hasPluginPath = $true; break } }
            Check $hasPluginPath '「目录」分类展示生效中的插件目录（只读）'
            Check ($null -ne (Find-ButtonByName -Root $settingsWindow -Name '重新扫描插件目录')) `
                '「目录」分类有「重新扫描插件目录」'
            Check ($null -ne (Find-ButtonByName -Root $settingsWindow -Name '重新加载全部插件')) `
                '「目录」分类有「重新加载全部插件」'

            # ── 分类「插件宿主」：共享程序集 ──
            Check (Select-SettingsSection -Win $settingsWindow -SectionName '插件宿主') '分类导航可切换到「插件宿主」'
            $texts = @()
            foreach ($text in (Find-ByType -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::Text))) {
                $texts += $text.Current.Name
            }
            Check ($texts -contains 'Toolbox.Contracts') '「插件宿主」分类显示共享程序集清单'
            $duplicated = $false
            foreach ($text in $texts) {
                if ($text -eq 'Toolbox.Contracts, Toolbox.Contracts') { $duplicated = $true }
            }
            Check (-not $duplicated) '共享程序集没有重复项（配置绑定后已归一化）'

            # ── 分类「启动与托盘」：两个开关（只来回切换内存态的关窗开关，开机自启只断言存在）──
            Check (Select-SettingsSection -Win $settingsWindow -SectionName '启动与托盘') '分类导航可切换到「启动与托盘」'
            $trayOn = Find-ByTypeAndName -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::RadioButton) -Name '关窗到托盘-开'
            $trayOff = Find-ByTypeAndName -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::RadioButton) -Name '关窗到托盘-关'
            $bootOn = Find-ByTypeAndName -Root $settingsWindow -ControlType ([System.Windows.Automation.ControlType]::RadioButton) -Name '开机自启-开'
            Check ($null -ne $trayOn -and $null -ne $trayOff) '「启动与托盘」提供关窗到托盘开关'
            Check ($null -ne $bootOn) '「启动与托盘」提供开机自启开关'

            if ($null -ne $trayOff -and $null -ne $trayOn) {
                $trayOff.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Milliseconds 500
                $offSelected = $trayOff.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
                Check $offSelected '关窗到托盘可切到「关」'
                $trayOn.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Milliseconds 500
                $onSelected = $trayOn.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
                Check $onSelected '切回「开」（不留脏状态）'
                $trayOn = $null
                $trayOff = $null
            }

            # 回到外观分类（保持默认状态），然后关闭
            Select-SettingsSection -Win $settingsWindow -SectionName '外观' | Out-Null

            $closeButton = Find-ButtonByName -Root $settingsWindow -Name '关闭'
            Check ($null -ne $closeButton) '设置窗口有「关闭」按钮'
            if ($null -ne $closeButton) {
                $closeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 800
                Check ($process.HasExited -or $null -eq (Get-AppWindow -ProcessId $process.Id -Title $settingsWindowTitle)) `
                    '「关闭」能正常关闭设置窗口'
            }

            $navList = $null
        }
    }

    # ── 关于窗口 ────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '--- 4.5 关于窗口 · 插件市场 ---' -ForegroundColor Cyan

    $aboutBtn2 = Find-ButtonByName -Root $window -Name '关于'
    if ($null -eq $aboutBtn2) {
        Check $false '找不到「关于」按钮'
    }
    else {
        $aboutBtn2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 2

        $aboutWindow = Get-AppWindow -ProcessId $process.Id -Title '关于 · Toolbox'
        Check ($null -ne $aboutWindow) '「关于」打开了关于窗口（关于 · Toolbox）'

        if ($null -ne $aboutWindow) {
            $aboutTexts = @()
            foreach ($text in (Find-ByType -Root $aboutWindow -ControlType ([System.Windows.Automation.ControlType]::Text))) {
                $aboutTexts += $text.Current.Name
            }

            $hasVersion = $false
            foreach ($text in $aboutTexts) { if ($text -like 'v1.0.0-beta*') { $hasVersion = $true; break } }
            Check $hasVersion '关于窗口展示版本号 v1.0.0-beta'

            $hasRuntime = $false
            foreach ($text in $aboutTexts) { if ($text -like '.NET 10*') { $hasRuntime = $true; break } }
            Check $hasRuntime '关于窗口展示运行时信息（.NET 10）'

            $aboutClose = Find-ButtonByName -Root $aboutWindow -Name '关闭'
            Check ($null -ne $aboutClose) '关于窗口有「关闭」按钮'
            if ($null -ne $aboutClose) {
                $aboutClose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 800
            }
            $aboutWindow = $null
            $aboutClose = $null
        }
    }

    # ── 插件市场 ────────────────────────────────────────────────────────────
    $marketBtn2 = Find-ButtonByName -Root $window -Name '插件市场'
    if ($null -eq $marketBtn2) {
        Check $false '找不到「插件市场」按钮'
    }
    else {
        $marketBtn2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $marketWindow = $null
        $marketDeadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $marketDeadline -and $null -eq $marketWindow) {
            Start-Sleep -Milliseconds 400
            $marketWindow = Get-AppWindow -ProcessId $process.Id -Title '插件市场 · Toolbox'
        }
        Check ($null -ne $marketWindow) '「插件市场」打开了市场窗口（插件市场 · Toolbox）'

        if ($null -ne $marketWindow) {
            # 「敬请期待」徽标（市场保留、能力受控）
            $comingSoon = $false
            foreach ($text in (Find-ByType -Root $marketWindow -ControlType ([System.Windows.Automation.ControlType]::Text))) {
                if ($text.Current.Name -eq '敬请期待') { $comingSoon = $true; break }
            }
            Check $comingSoon '市场标题旁带「敬请期待」徽标'

            # 空市场会自动播种；至少要能看到一个「安装 / 已安装」按钮或空态提示
            $installBtn = Find-ButtonByName -Root $marketWindow -Name '安装'
            $installedBtn = Find-ButtonByName -Root $marketWindow -Name '已安装'
            $emptyHint = $false
            foreach ($text in (Find-ByType -Root $marketWindow -ControlType ([System.Windows.Automation.ControlType]::Text))) {
                if ($text.Current.Name -like '市场里还没有插件*') { $emptyHint = $true; break }
            }
            Check (($null -ne $installBtn) -or ($null -ne $installedBtn) -or $emptyHint) `
                "市场窗口有内容或空态提示（安装=$(if ($installBtn) { '√' } else { '×' }) 已安装=$(if ($installedBtn) { '√' } else { '×' }) 空态=$(if ($emptyHint) { '√' } else { '×' })）"

            if ($null -ne $installBtn) {
                Check $true '市场列出可安装插件（播种/扫描生效）'
            }

            $fileInstall = Find-ButtonByName -Root $marketWindow -Name '从文件安装'
            Check ($null -ne $fileInstall) '市场提供「从文件安装」入口'

            $marketClose = Find-ButtonByName -Root $marketWindow -Name '关闭'
            Check ($null -ne $marketClose) '市场窗口有「关闭」按钮'
            if ($null -ne $marketClose) {
                $marketClose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 800
                Check ($process.HasExited -or $null -eq (Get-AppWindow -ProcessId $process.Id -Title '插件市场 · Toolbox')) `
                    '市场窗口可正常关闭'
            }
            $marketWindow = $null
            $marketClose = $null
        }
    }

    Write-Host ''
    Write-Host "合计 $script:Total 项断言，失败 $script:Failures 项。"
    if ($script:Failures -eq 0) {
        Write-Host 'UI 改版验证：全部通过 ✅' -ForegroundColor Green
        $exitCode = 0
    }
    else {
        Write-Host 'UI 改版验证：存在失败 ❌' -ForegroundColor Red
    }
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

exit $exitCode
