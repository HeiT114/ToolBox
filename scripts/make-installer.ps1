#Requires -Version 5.1
<#
.SYNOPSIS
    编译 1.0.0-beta 安装包（Inno Setup 6）→ artifacts/dist/Toolbox-Setup-<版本>.exe。

.DESCRIPTION
    * 前置条件：scripts/pack.ps1 已经跑过（安装包直接复用它的自包含发布目录）；
    * 用法：winget 安装的官方 Inno Setup（JRSoftware.InnoSetup），编译器是 ISCC.exe；
    * 版本号从仓库根的 Directory.Build.props 里读（唯一事实来源）。

.EXAMPLE
    pwsh ./scripts/make-installer.ps1
#>
[CmdletBinding()]
param(
    # 直接指定已发布的目录（默认 artifacts/dist/Toolbox-win-x64）
    [string]$SourceDirectory,

    # 显式指定 ISCC.exe（默认自动探测官方安装位置）
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# ── 版本号（唯一事实来源：Directory.Build.props）────────────────────────────
$propsPath = Join-Path $repo 'Directory.Build.props'
$propsText = [System.IO.File]::ReadAllText($propsPath)
$versionMatch = [regex]::Match($propsText, '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) {
    throw "在 Directory.Build.props 里找不到 <Version>。"
}
$version = $versionMatch.Groups[1].Value.Trim()

# ── 源目录（pack.ps1 的发布产物）────────────────────────────────────────────
if (-not $SourceDirectory) {
    $SourceDirectory = Join-Path $repo 'artifacts\dist\Toolbox-win-x64'
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory 'Toolbox.App.exe'))) {
    throw "发布目录里没有 Toolbox.App.exe：$SourceDirectory（先运行 scripts/pack.ps1）"
}

# ── ISCC.exe 探测 ───────────────────────────────────────────────────────────
function Find-Iscc {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "找不到 ISCC：$Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        # winget 按用户安装（非提升）落在这里
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 5\ISCC.exe')
    )
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }

    throw '找不到 ISCC.exe。先安装 Inno Setup 6：winget install --id JRSoftware.InnoSetup'
}

$isccPath = Find-Iscc -Explicit $Iscc
$issPath = Join-Path $repo 'installer\toolbox.iss'
$iconPath = Join-Path $repo 'src\Toolbox.App\Assets\app.ico'
$outputDir = Join-Path $repo 'artifacts\dist'
$outputExe = Join-Path $outputDir "Toolbox-Setup-$version.exe"

Write-Host '== Toolbox 安装包 ==' -ForegroundColor Cyan
Write-Host "  版本      : $version"
Write-Host "  源目录    : $SourceDirectory"
Write-Host "  编译器    : $isccPath"
Write-Host "  输出      : $outputExe"

if (Test-Path -LiteralPath $outputExe) {
    Remove-Item -LiteralPath $outputExe -Force
}

& $isccPath "/DSourceDir=$SourceDirectory" "/DMyAppVersion=$version" "/DSetupIcon=$iconPath" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC 编译失败（退出码 $LASTEXITCODE）。"
}

if (-not (Test-Path -LiteralPath $outputExe)) {
    throw "编译后没有找到输出：$outputExe"
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $outputExe).Length / 1MB, 1)
Write-Host ''
Write-Host "安装包已生成：$outputExe（$sizeMb MB）" -ForegroundColor Green
