#Requires -Version 5.1
<#
.SYNOPSIS
    一键验证 Toolbox 骨架：构建 → stage 示例插件 → 跑端到端冒烟测试（可选：启动主程序）。

.DESCRIPTION
    * 构建整个解决方案（需要 .NET 10 SDK；本机若只有 7/9，可用 -Dotnet 指定 10 的 dotnet.exe）。
    * 把示例插件 stage 成两种形态：目录插件（Hello）与 zip 插件包（Clock，含私有依赖）。
    * 运行 tests/Toolbox.PluginHost.SmokeTest，验证加载/隔离/DI/配置/卸载回收/文件锁释放。
    * -RunApp 会顺便把插件放到主程序输出目录并启动 WPF 主程序。

.EXAMPLE
    pwsh ./scripts/verify.ps1
    pwsh ./scripts/verify.ps1 -Configuration Debug -VerboseLogs
    pwsh ./scripts/verify.ps1 -RunApp
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 显式指定 .NET 10 的 dotnet.exe（例如 C:\Users\me\.dotnet10\dotnet.exe）
    [string]$Dotnet,

    [switch]$SkipBuild,
    [switch]$RunApp,
    [switch]$VerboseLogs
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$config = $Configuration.ToLowerInvariant()

function Get-DotnetSdkVersion {
    # 注意：Windows PowerShell 5.1 上原生程序往 stderr 写东西会被 EAP=Stop 变成终止错误，
    # 所以这里临时降级 EAP，并从输出里挑出版本号那一行。
    param([string]$Exe)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe --version 2>&1 | Out-String
        return ($output -split "`r?`n" | Where-Object { $_ -match '^\d+\.\d+\.\d+' } | Select-Object -First 1)
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Resolve-DotnetHost {
    param([string]$Explicit)

    if ($Explicit) {
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe')
    $candidates += 'C:\Program Files\dotnet\dotnet.exe'

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        if ((Get-DotnetSdkVersion -Exe $candidate) -like '10.*') { return $candidate }
    }

    throw '找不到 .NET 10 SDK。请安装 .NET 10，或用 -Dotnet 指定 dotnet.exe 的完整路径。'
}

$dotnetPath = Resolve-DotnetHost -Explicit $Dotnet
Write-Host "== Toolbox 验证 ==" -ForegroundColor Cyan
Write-Host "  仓库      : $repo"
Write-Host "  dotnet    : $dotnetPath (SDK $(Get-DotnetSdkVersion -Exe $dotnetPath))"
Write-Host "  配置      : $Configuration"

Push-Location $repo
try {
    if (-not $SkipBuild) {
        Write-Host ''
        Write-Host '--- 1/3 构建 ---' -ForegroundColor Cyan
        & $dotnetPath build Toolbox.slnx -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    }

    Write-Host ''
    Write-Host '--- 2/3 stage 示例插件 ---' -ForegroundColor Cyan

    $pluginsRoot = Join-Path $repo 'artifacts\plugins'
    Remove-Item -LiteralPath $pluginsRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $pluginsRoot -Force | Out-Null
    if (Test-Path -LiteralPath (Join-Path $pluginsRoot 'Toolbox.Plugin.SysInfo')) {
        throw "旧插件没清掉：$pluginsRoot（多半是主程序/冒烟测试还在跑，占着插件 dll）。请先关掉主程序再重跑。"
    }

    $sysInfoOutput = Join-Path $repo "artifacts\bin\Toolbox.Plugin.SysInfo\$config"
    if (-not (Test-Path -LiteralPath $sysInfoOutput)) { throw "找不到插件输出目录：$sysInfoOutput" }

    # ① 目录插件形态：plugins/Toolbox.Plugin.SysInfo/
    Copy-Item -LiteralPath $sysInfoOutput -Destination (Join-Path $pluginsRoot 'Toolbox.Plugin.SysInfo') -Recurse

    # ② zip 插件包形态（内容直接放在根，模拟发布包）：
    #    给冒烟测试覆盖"影子解包 + AssemblyDependencyResolver 解析私有依赖"这两条路径。
    #
    #    ⚠ 包里的插件 Id 故意改成 sysinfo-zip：两种形态同时待在同一个插件根时，
    #      如果共用同一个插件 Id，PluginManager 是"按 Id 找描述"的 —— 卸载 zip 会把
    #      目录那条描述的状态一起改掉，冒烟测试就分不清到底谁被卸载了。
    #      取不同 Id 让两条来源各自独立、状态可验证；应用侧只 stage 目录形态，不受影响。
    $zipStage = Join-Path $repo 'artifacts\zip-stage\Toolbox.Plugin.SysInfo'
    Remove-Item (Join-Path $repo 'artifacts\zip-stage') -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $zipStage -Force | Out-Null
    Copy-Item -Path (Join-Path $sysInfoOutput '*') -Destination $zipStage -Recurse

    $manifestPath = Join-Path $zipStage 'plugin.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifest.id = 'sysinfo-zip'
    $manifest.displayName = '系统环境检测（zip 形态）'
    $manifestJson = $manifest | ConvertTo-Json -Depth 6
    # 不带 BOM 写回，保持清单文件干净
    [System.IO.File]::WriteAllText($manifestPath, $manifestJson, (New-Object System.Text.UTF8Encoding($false)))

    $zipPath = Join-Path $pluginsRoot 'Toolbox.Plugin.SysInfo-1.0.0.zip'
    Compress-Archive -Path (Join-Path $zipStage '*') -DestinationPath $zipPath -Force
    Remove-Item (Join-Path $repo 'artifacts\zip-stage') -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "  $pluginsRoot"
    Get-ChildItem -LiteralPath $pluginsRoot | ForEach-Object { Write-Host "    - $($_.Name)" }

    # 主程序输出目录只放"目录插件"一份，保证应用里就是一个干净的初始测试插件。
    $appOutput = Join-Path $repo "artifacts\bin\Toolbox.App\$config"
    if (Test-Path -LiteralPath $appOutput) {
        $appPlugins = Join-Path $appOutput 'plugins'
        Remove-Item -LiteralPath $appPlugins -Recurse -Force -ErrorAction SilentlyContinue
        # ⚠ 必须确认真的清空了：如果还有一个 Toolbox.App 实例在跑，插件 dll 被占用，
        #    Remove-Item 会静默失败，于是"旧插件 + 新插件"混在一起，UI 验证会连到旧插件上，
        #    断言全部失去意义（这个坑真实踩过一次）。
        if (Test-Path -LiteralPath $appPlugins) {
            $leftover = @(Get-ChildItem -LiteralPath $appPlugins -Force -ErrorAction SilentlyContinue)
            if ($leftover.Count -gt 0) {
                throw "无法清空主程序插件目录（残留 $($leftover.Count) 项）：$appPlugins。多半还有 Toolbox.App 实例在运行并占用插件 dll，请先关闭主程序。"
            }
        }

        New-Item -ItemType Directory -Path $appPlugins -Force | Out-Null
        Copy-Item -LiteralPath $sysInfoOutput -Destination (Join-Path $appPlugins 'Toolbox.Plugin.SysInfo') -Recurse
        Write-Host "  已同步到主程序输出：$appPlugins"
    }

    Write-Host ''
    Write-Host '--- 3/3 端到端冒烟测试 ---' -ForegroundColor Cyan

    $smokeDll = Join-Path $repo "artifacts\bin\Toolbox.PluginHost.SmokeTest\$config\Toolbox.PluginHost.SmokeTest.dll"
    if (-not (Test-Path -LiteralPath $smokeDll)) { throw "找不到冒烟测试：$smokeDll（先构建）" }

    $smokeArgs = @($smokeDll, '--plugins', $pluginsRoot)
    if ($VerboseLogs) { $smokeArgs += '--verbose' }

    & $dotnetPath @smokeArgs
    $smokeExit = $LASTEXITCODE

    if ($RunApp) {
        Write-Host ''
        Write-Host '--- 启动主程序（关闭窗口后脚本结束）---' -ForegroundColor Cyan
        & $dotnetPath (Join-Path $appOutput 'Toolbox.App.dll')
    }

    Write-Host ''
    if ($smokeExit -eq 0) {
        Write-Host '验证结果：通过 ✅' -ForegroundColor Green
    }
    else {
        Write-Host "验证结果：失败 ❌（冒烟测试退出码 $smokeExit）" -ForegroundColor Red
    }

    exit $smokeExit
}
finally {
    Pop-Location
}
