#Requires -Version 5.1
<#
.SYNOPSIS
    生成应用图标 app.ico（1.0.0-beta 定稿样式 M4：深色方底·靖蓝立方）——纯代码绘制，无渐变。

.DESCRIPTION
    样式（由 scripts/icon-variants.ps1 三轮对照挑选定稿）：
      * 深夜蓝圆角方底 #1B2333（radius 22%）；
      * 等距立方体三面纯色：顶 #A5B4FC / 左 #6366F1 / 右 #4F46E5；
      * 三面之间用底色描 Y 形接缝（"缺口"观感）；
      * 顶点坐一枚琥珀色胶囊提手 #F59E0B（点睛）。
    全程 SolidBrush / Pen，没有任何渐变。

    多尺寸（16/24/32/48/64/128/256）打包成单个 .ico，每帧 PNG（Vista+ 标准做法）。
    同一个文件用作：ApplicationIcon（exe 图标）、窗口图标、托盘图标、Inno Setup 图标。

.EXAMPLE
    pwsh ./scripts/make-assets.ps1
#>
[CmdletBinding()]
param(
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutFile) { $OutFile = Join-Path $repo 'src\Toolbox.App\Assets\app.ico' }

Add-Type -AssemblyName System.Drawing

# ── M4 定稿配色（纯色）─────────────────────────────────────────────────────
$m4 = @{
    Bg     = '#1B2333'
    Top    = '#A5B4FC'
    Left   = '#6366F1'
    Right  = '#4F46E5'
    Handle = '#F59E0B'
}

function Get-Brush {
    param([string]$Hex)
    return [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($Hex))
}

function Get-Pen {
    param([string]$Hex, [float]$Width)
    return [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($Hex), $Width)
}

function New-RoundRectPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$Radius)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Add-Face {
    param($G, [System.Drawing.PointF[]]$Points, [string]$Color)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddPolygon($Points)
    $brush = Get-Brush $Color
    $G.FillPath($brush, $path)
    $path.Dispose()
    $brush.Dispose()
}

function New-M4Bitmap {
    param([int]$Size)

    $s = [float]$Size
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # 圆角方底（纯色）
    $pad = [float]([math]::Round($s * 0.055))
    $radius = [float]($s * 0.22)
    $bgPath = New-RoundRectPath -X $pad -Y $pad -W ($s - 2 * $pad) -H ($s - 2 * $pad) -Radius $radius
    $bgBrush = Get-Brush $m4.Bg
    $g.FillPath($bgBrush, $bgPath)
    $bgPath.Dispose()
    $bgBrush.Dispose()

    # ── 等距立方体 ──
    $cx = $s * 0.5
    $cy = $s * 0.36
    $halfW = $s * 0.34
    $halfH = $s * 0.17
    $bodyH = $s * 0.30

    $T = [System.Drawing.PointF]::new($cx, ($cy - $halfH))
    $L = [System.Drawing.PointF]::new(($cx - $halfW), $cy)
    $R = [System.Drawing.PointF]::new(($cx + $halfW), $cy)
    $B = [System.Drawing.PointF]::new($cx, ($cy + $halfH))
    $L2 = [System.Drawing.PointF]::new(($cx - $halfW), ($cy + $bodyH))
    $R2 = [System.Drawing.PointF]::new(($cx + $halfW), ($cy + $bodyH))
    $V = [System.Drawing.PointF]::new($cx, ($cy + $halfH + $bodyH))

    Add-Face -G $g -Points ([System.Drawing.PointF[]] @($T, $R, $B, $L)) -Color $m4.Top
    Add-Face -G $g -Points ([System.Drawing.PointF[]] @($L, $B, $V, $L2)) -Color $m4.Left
    Add-Face -G $g -Points ([System.Drawing.PointF[]] @($B, $R, $R2, $V)) -Color $m4.Right

    # Y 形接缝（底色描缝 = 缺口观感）
    $seamWidth = [float]([math]::Max(1.5, $s * 0.026))
    $seamPen = Get-Pen $m4.Bg $seamWidth
    $seamPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $seamPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $g.DrawLine($seamPen, $L, $B)
    $g.DrawLine($seamPen, $B, $R)
    $g.DrawLine($seamPen, $B, $V)
    $seamPen.Dispose()

    # 琥珀胶囊提手（坐在顶点上）
    $handleW = [float]($s * 0.17)
    $handleH = [float]($s * 0.055)
    $handleX = [float] (($s - $handleW) / 2)
    $handleY = [float] (($cy - $halfH) - $handleH * 0.8)
    $handlePath = New-RoundRectPath -X $handleX -Y $handleY -W $handleW -H $handleH -Radius ($handleH / 2)
    $handleBrush = Get-Brush $m4.Handle
    $g.FillPath($handleBrush, $handlePath)
    $handlePath.Dispose()
    $handleBrush.Dispose()

    $g.Dispose()
    return $bmp
}

function Save-Icon {
    param(
        [System.Drawing.Bitmap[]]$Frames,
        [int[]]$Sizes,
        [string]$Path
    )

    # ICONDIR(6) + ICONDIRENTRY[](16/个) + PNG 数据
    $blobs = @()
    for ($i = 0; $i -lt $Frames.Count; $i++) {
        $ms = New-Object System.IO.MemoryStream
        $Frames[$i].Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $blobs += , $ms.ToArray()
        $ms.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint16]0)                     # reserved
    $bw.Write([uint16]1)                     # type = icon
    $bw.Write([uint16]$Frames.Count)

    $offset = 6 + 16 * $Frames.Count
    for ($i = 0; $i -lt $Frames.Count; $i++) {
        $size = $Sizes[$i]
        $entrySize = if ($size -ge 256) { [byte]0 } else { [byte]$size }
        $bw.Write($entrySize)               # width
        $bw.Write($entrySize)               # height
        $bw.Write([byte]0)                  # palette colors
        $bw.Write([byte]0)                  # reserved
        $bw.Write([uint16]1)                # planes
        $bw.Write([uint16]32)               # bpp
        $bw.Write([uint32]$blobs[$i].Length) # data size
        $bw.Write([uint32]$offset)          # data offset
        $offset += $blobs[$i].Length
    }

    foreach ($blob in $blobs) { $bw.Write($blob) }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()

    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

# ── 主流程 ─────────────────────────────────────────────────────────────────
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) { $frames += New-M4Bitmap -Size $size }

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
Save-Icon -Frames $frames -Sizes $sizes -Path $OutFile

foreach ($f in $frames) { $f.Dispose() }

$len = (Get-Item -LiteralPath $OutFile).Length
Write-Host "已生成图标：$OutFile（$len 字节，尺寸：$($sizes -join '/')，样式：M4 深色方底·靖蓝立方，无渐变）" -ForegroundColor Green
