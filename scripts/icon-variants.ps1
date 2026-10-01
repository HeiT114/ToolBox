#Requires -Version 5.1
<#
.SYNOPSIS
    N4「等距立方体」的优化批次（全部无渐变、纯色）——四款精修方向供对照选择。

.DESCRIPTION
    相对初版 N4 的优化点：
      * 占位放大（宽 0.68S、高约 0.69S），小尺寸更饱满；
      * 三面之间加"接缝"：透明底版本是真正镂空的缝（任何背景都读得出结构），
        带底色版本用底色描缝（同样是"缺口"观感，不会透穿到图形之外）；
      * 顶面加提手（胶囊形）；M3 额外加"盖缝 + 琥珀锁扣"，抽象方块变成真箱子。

      M1 靛蓝方底·白立方   靛蓝圆角底 + 白/浅蓝三面 + 琥珀提手
      M2 透明底·加缝立方   初版三色 + 透明接缝 + 琥珀提手
      M3 盖缝+锁扣         M2 基础上加横盖缝与琥珀锁扣（更像"箱子"）
      M4 深色方底·靛蓝立方 深夜蓝底 + 亮靛蓝三面（深色任务栏友好）

    输出（artifacts/icon-variants/）：
      * b3-<key>-256.png        单款 256 预览
      * contact-sheet-3.png     四款对照大图（含 48/32/16 浅底 / 深底效果）
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repo 'artifacts\icon-variants'

Add-Type -AssemblyName System.Drawing

$variants = @(
    @{ Key = 'cube-indigo-white'; Label = 'M1 靛蓝方底·白立方'; Kind = 'cube'
       Bg = '#4F46E5'; Top = '#FFFFFF'; Left = '#C7D2FE'; Right = '#A5B4FC'
       Handle = '#F59E0B'; SeamMode = 'paint'; WithLid = $false },
    @{ Key = 'cube-open-seams';   Label = 'M2 透明底·加缝立方'; Kind = 'cube'
       Bg = $null; Top = '#818CF8'; Left = '#4F46E5'; Right = '#3730A3'
       Handle = '#F59E0B'; SeamMode = 'erase'; WithLid = $false },
    @{ Key = 'cube-lid-latch';    Label = 'M3 盖缝+琥珀锁扣'; Kind = 'cube'
       Bg = $null; Top = '#818CF8'; Left = '#4F46E5'; Right = '#3730A3'
       Handle = '#F59E0B'; SeamMode = 'erase'; WithLid = $true },
    @{ Key = 'cube-dark';         Label = 'M4 深色方底·靛蓝立方'; Kind = 'cube'
       Bg = '#1B2333'; Top = '#A5B4FC'; Left = '#6366F1'; Right = '#4F46E5'
       Handle = '#F59E0B'; SeamMode = 'paint'; WithLid = $false }
)

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

# ── 优化版等距立方体 ───────────────────────────────────────────────────────
function New-CubeBitmap {
    param($Variant, [int]$Size)

    $s = [float]$Size
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # 底形（可选）
    $seamColor = $null
    if ($Variant.Bg) {
        $pad = [math]::Round($s * 0.055)
        $radius = [float]($s * 0.22)
        $bgPath = New-RoundRectPath -X ([float]$pad) -Y ([float]$pad) -W ([float]($s - 2 * $pad)) -H ([float]($s - 2 * $pad)) -Radius $radius
        $bgBrush = Get-Brush $Variant.Bg
        $g.FillPath($bgBrush, $bgPath)
        $bgPath.Dispose()
        $bgBrush.Dispose()
        $seamColor = $Variant.Bg
    }

    # ── 几何（相对放大版）──
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

    $topFace = [System.Drawing.PointF[]] @($T, $R, $B, $L)
    $leftFace = [System.Drawing.PointF[]] @($L, $B, $V, $L2)
    $rightFace = [System.Drawing.PointF[]] @($B, $R, $R2, $V)

    Add-Face -G $g -Points $topFace -Color $Variant.Top
    Add-Face -G $g -Points $leftFace -Color $Variant.Left
    Add-Face -G $g -Points $rightFace -Color $Variant.Right

    # ── 接缝：Y 形三条（L→B、B→R、B→V）──
    $seamWidth = [float]([math]::Max(1.5, $s * 0.026))
    if ($Variant.SeamMode -eq 'paint' -and $seamColor) {
        $seamPen = Get-Pen $seamColor $seamWidth
    }
    else {
        $seamPen = [System.Drawing.Pen]::new([System.Drawing.Color]::Transparent, $seamWidth)
    }
    $seamPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $seamPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
    if ($Variant.SeamMode -eq 'erase') { $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy }
    $g.DrawLine($seamPen, $L, $B)
    $g.DrawLine($seamPen, $B, $R)
    $g.DrawLine($seamPen, $B, $V)
    if ($Variant.SeamMode -eq 'erase') { $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver }
    $seamPen.Dispose()

    # ── 顶面提手（胶囊小横条，坐在顶点上）──
    $handleW = [float]($s * 0.17)
    $handleH = [float]($s * 0.055)
    $handleX = [float] (($s - $handleW) / 2)
    $handleY = [float] (($cy - $halfH) - $handleH * 0.8)
    $handlePath = New-RoundRectPath -X $handleX -Y $handleY -W $handleW -H $handleH -Radius ($handleH / 2)
    $handleBrush = Get-Brush $Variant.Handle
    $g.FillPath($handleBrush, $handlePath)
    $handlePath.Dispose()
    $handleBrush.Dispose()

    # ── 盖缝 + 锁扣（M3）──
    if ($Variant.WithLid) {
        $yBand = [float] ($cy + $halfH + ($bodyH * 0.25))
        $bandPen = Get-Pen '#312E81' ([float]([math]::Max(1.5, $s * 0.028)))
        $g.DrawLine($bandPen, ($cx - $halfW), $yBand, ($cx + $halfW), $yBand)
        $bandPen.Dispose()

        $lockW = [float]($s * 0.075)
        $lockH = [float]($s * 0.09)
        $lockX = [float] ($cx - ($lockW / 2))
        $lockY = [float] ($yBand - ($lockH / 2))
        $latchBrush = Get-Brush '#F59E0B'
        $g.FillRectangle($latchBrush, $lockX, $lockY, $lockW, $lockH)
        $latchBrush.Dispose()
    }

    $g.Dispose()
    return $bmp
}

# ── 对照大图 ───────────────────────────────────────────────────────────────
function New-ContactSheet {
    param($Variants, [string]$FileName)

    $tileW = 300
    $tileH = 430
    $margin = 24
    $gutter = 16
    $cols = 2
    $rows = [int][math]::Ceiling($Variants.Count / $cols)
    $sheetW = ($margin * 2) + ($cols * $tileW) + (($cols - 1) * $gutter)
    $sheetH = ($margin * 2) + ($rows * $tileH) + (($rows - 1) * $gutter)

    $sheet = New-Object System.Drawing.Bitmap($sheetW, $sheetH)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#EEF1F7'))

    $font = New-Object System.Drawing.Font('Microsoft YaHei', [float]13, [System.Drawing.FontStyle]::Bold)
    $labelBrush = Get-Brush '#1B2333'
    $sf = [System.Drawing.StringFormat]::new()
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center

    $lightStrip = Get-Brush '#FFFFFF'
    $darkStrip = Get-Brush '#0E131B'
    $stripBorder = Get-Pen '#D3DAE6' 1

    for ($i = 0; $i -lt $Variants.Count; $i++) {
        $v = $Variants[$i]
        $tx = $margin + (($i % $cols) * ($tileW + $gutter))
        $ty = $margin + ([int][math]::Floor($i / $cols) * ($tileH + $gutter))

        $tileBrush = Get-Brush '#F7F9FC'
        $tilePath = New-RoundRectPath -X ([float]$tx) -Y ([float]$ty) -W ([float]$tileW) -H ([float]$tileH) -Radius 14
        $g.FillPath($tileBrush, $tilePath)
        $tilePath.Dispose()
        $tileBrush.Dispose()

        $big = New-CubeBitmap -Variant $v -Size 256
        $bigX = $tx + 22
        $bigY = $ty + 8
        $g.DrawImage($big, $bigX, $bigY, 256, 256)
        $big.Dispose()

        $lightX = $tx + 22
        $lightY = $ty + 272
        $g.FillRectangle($lightStrip, $lightX, $lightY, 256, 58)
        $g.DrawRectangle($stripBorder, $lightX, $lightY, 256, 58)
        $x1 = $tx + 46
        foreach ($sz in @(48, 32, 16)) {
            $small = New-CubeBitmap -Variant $v -Size $sz
            $yy = $lightY + [int]((58 - $sz) / 2)
            $g.DrawImage($small, $x1, $yy, $sz, $sz)
            $small.Dispose()
            $x1 += $sz + 44
        }

        $darkY = $ty + 334
        $g.FillRectangle($darkStrip, $lightX, $darkY, 256, 58)
        $x1 = $tx + 46
        foreach ($sz in @(48, 32, 16)) {
            $small = New-CubeBitmap -Variant $v -Size $sz
            $yy = $darkY + [int]((58 - $sz) / 2)
            $g.DrawImage($small, $x1, $yy, $sz, $sz)
            $small.Dispose()
            $x1 += $sz + 44
        }

        $labelX = [float]($tx + 8)
        $labelY = [float]($ty + 396)
        $labelW = [float]($tileW - 16)
        $labelRect = New-Object System.Drawing.RectangleF($labelX, $labelY, $labelW, 28)
        $g.DrawString($v.Label, $font, $labelBrush, $labelRect, $sf)
    }

    $stripBorder.Dispose()
    $lightStrip.Dispose()
    $darkStrip.Dispose()
    $labelBrush.Dispose()
    $font.Dispose()
    $sf.Dispose()
    $g.Dispose()

    $sheetPath = Join-Path $outDir $FileName
    $sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    return $sheetPath
}

# ── 主流程 ─────────────────────────────────────────────────────────────────
if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

foreach ($v in $variants) {
    $png = Join-Path $outDir "b3-$($v.Key)-256.png"
    $bmp = New-CubeBitmap -Variant $v -Size 256
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  - $png"
}

$sheetPath = New-ContactSheet -Variants $variants -FileName 'contact-sheet-3.png'
Write-Host ''
Write-Host "对照大图：$sheetPath" -ForegroundColor Green
