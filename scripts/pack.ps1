#Requires -Version 5.1
<#
.SYNOPSIS
    把主程序打包成可直接运行的 exe（多文件 + zip 分发）。

.DESCRIPTION
    * 默认**自包含**（self-contained）发布：目标机器不需要装 .NET，双击 Toolbox.App.exe 即可；
    * 显式关闭 PublishTrimmed / PublishAot / PublishSingleFile —— docs/DESIGN.md §10「已知边界」明确写了
      宿主不能用裁剪 / NativeAOT / 单文件方式发布（会破坏反射发现与动态加载），所以这里钉死这三个开关；
    * 把示例插件 stage 进 <dist>/plugins（目录插件 + zip 插件形态各来一个），并确认 appsettings.json 就位；
    * 默认额外打一个 zip，便于整包分发。

.EXAMPLE
    pwsh ./scripts/pack.ps1
    pwsh ./scripts/pack.ps1 -FrameworkDependent   # 体积小，但目标机要装 .NET 10 Desktop Runtime
    pwsh ./scripts/pack.ps1 -NoZip
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 显式指定 .NET 10 的 dotnet.exe
    [string]$Dotnet,

    [string]$RuntimeIdentifier = 'win-x64',

    # 输出目录（默认 artifacts/dist/Toolbox-<rid>[-fd]）
    [string]$OutputDirectory,

    # 依赖目标机已安装 .NET 10 Desktop Runtime（体积从 ~150MB 降到 ~2MB）
    [switch]$FrameworkDependent,

    [switch]$NoZip,

    # 只发布主程序，不 stage 示例插件
    [switch]$SkipPlugins
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$config = $Configuration.ToLowerInvariant()

function Resolve-DotnetHost {
    param([string]$Explicit)

    if ($Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }

    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe')
    $candidates += 'C:\Program Files\dotnet\dotnet.exe'

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

function Get-FolderSizeMb {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    $bytes = (Get-ChildItem -LiteralPath $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    return [math]::Round($bytes / 1MB, 1)
}

$dotnetPath = Resolve-DotnetHost -Explicit $Dotnet
$selfContained = -not $FrameworkDependent
$suffix = if ($selfContained) { '' } else { '-fd' }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo "artifacts\dist\Toolbox-$RuntimeIdentifier$suffix"
}

Write-Host '== Toolbox 打包 ==' -ForegroundColor Cyan
Write-Host "  仓库      : $repo"
Write-Host "  dotnet    : $dotnetPath (SDK $((& $dotnetPath --version 2>&1 | Out-String).Trim()))"
Write-Host "  配置      : $Configuration"
Write-Host "  RID       : $RuntimeIdentifier"
Write-Host "  自包含    : $selfContained"
Write-Host "  输出      : $OutputDirectory"

Push-Location $repo
try {
    Write-Host ''
    Write-Host '--- 1/4 构建解决方案（含插件工程）---' -ForegroundColor Cyan
    & $dotnetPath build Toolbox.slnx -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }

    Write-Host ''
    Write-Host '--- 2/4 发布主程序 ---' -ForegroundColor Cyan
    if (Test-Path -LiteralPath $OutputDirectory) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }

    # ⚠ 这三个开关必须保持 false：裁剪会砍掉反射发现的插件类型，单文件/NativeAOT 会破坏动态加载。
    $publishArgs = @(
        'publish', 'src\Toolbox.App\Toolbox.App.csproj',
        '-c', $Configuration,
        '-r', $RuntimeIdentifier,
        '--self-contained', $selfContained.ToString().ToLowerInvariant(),
        '--nologo',
        '-o', $OutputDirectory,
        '-p:PublishTrimmed=false',
        '-p:PublishAot=false',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false'
    )

    & $dotnetPath @publishArgs
    if ($LASTEXITCODE -ne 0) { throw '发布失败。' }

    $exePath = Join-Path $OutputDirectory 'Toolbox.App.exe'
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "发布目录里没有 Toolbox.App.exe：$OutputDirectory"
    }

    Write-Host ''
    Write-Host '--- 3/4 stage 示例插件 ---' -ForegroundColor Cyan
    $pluginsRoot = Join-Path $OutputDirectory 'plugins'

    if ($SkipPlugins) {
        Write-Host '  （-SkipPlugins：跳过）'
    }
    else {
        if (Test-Path -LiteralPath $pluginsRoot) { Remove-Item -LiteralPath $pluginsRoot -Recurse -Force }
        New-Item -ItemType Directory -Path $pluginsRoot -Force | Out-Null

        $sysInfoOutput = Join-Path $repo "artifacts\bin\Toolbox.Plugin.SysInfo\$config"
        if (-not (Test-Path -LiteralPath $sysInfoOutput)) { throw "找不到插件输出目录：$sysInfoOutput" }

        # 初始测试插件（系统环境检测）：目录形态随包发布。
        # 注意这里**不**再放第二份 zip 形态：两种形态是同一个插件 Id，
        # 同时待在 plugins/ 里会被加载器当成热替换顶掉前一份，界面上会出现重复行。
        Copy-Item -LiteralPath $sysInfoOutput -Destination (Join-Path $pluginsRoot 'Toolbox.Plugin.SysInfo') -Recurse

        Get-ChildItem -LiteralPath $pluginsRoot | ForEach-Object { Write-Host "    - $($_.Name)" }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'appsettings.json'))) {
        throw '发布目录里缺少 appsettings.json（插件宿主配置就靠它）。'
    }

    Write-Host ''
    Write-Host '--- 4/4 汇总 ---' -ForegroundColor Cyan
    $sizeMb = Get-FolderSizeMb -Path $OutputDirectory
    $fileCount = (Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File).Count
    Write-Host "  exe       : $exePath"
    Write-Host "  体积      : $sizeMb MB（$fileCount 个文件）"

    $archivePath = $null
    if (-not $NoZip) {
        $archivePath = Join-Path $repo "artifacts\dist\Toolbox-$RuntimeIdentifier$suffix.zip"
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
        Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $archivePath -Force
        Write-Host "  zip       : $archivePath ($([math]::Round((Get-Item $archivePath).Length / 1MB, 1)) MB)"
    }

    Write-Host ''
    Write-Host '打包完成 ✅' -ForegroundColor Green
    Write-Host ''
    Write-Host '运行方式：' -ForegroundColor Cyan
    Write-Host "  1) 双击：$exePath"
    Write-Host "  2) 命令行：& '$exePath'"
    if (-not $selfContained) {
        Write-Host '  ⚠ 这是框架依赖发布：目标机需要 .NET 10 Desktop Runtime（不加 -FrameworkDependent 即为自包含）。' -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}
