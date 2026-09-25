<#
.SYNOPSIS
  Regenerates Helm's icons in src/Helm.App/Assets.

  Concept: a bold "H" made of two tall rounded rectangles (two windows snapped side by side) joined by a bar,
  blue→teal gradient, white highlight on the bar. Every size is drawn natively on a pixel-snapped grid (not
  downscaled) so 16 px stays crisp.

  Outputs:
    helm-mark-1024.png / helm.png (256) / helm.ico   gradient mark, transparent (app, window, taskbar)
    helm-tile-1024.png / helm-tile.ico               rounded-square tile with a white mark (installer)
    helm-tray-white-1024.png / helm-tray-white.ico   monochrome white mark (tray on dark taskbars)
#>
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Helm.App\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

$Blue = [System.Drawing.Color]::FromArgb(255, 0x1F, 0x6F, 0xF2)
$Teal = [System.Drawing.Color]::FromArgb(255, 0x12, 0xC6, 0xB0)
$TileBlue = [System.Drawing.Color]::FromArgb(255, 0x16, 0x5B, 0xD9)
$TileTeal = [System.Drawing.Color]::FromArgb(255, 0x0E, 0xA8, 0x9A)

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Max(0, [Math]::Min($r, [Math]::Min($w, $h) / 2))
    if ($r -lt 0.5) { $p.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h)); return $p }
    $d = 2 * $r
    $p.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
    $p.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
    $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
    $p.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Geometry of the H on a unit square, snapped to whole pixels for the target size.
function Get-Geometry([int]$s, [double]$inset) {
    $snap = { param($v) [Math]::Round($v * $s) }
    $area = 1 - 2 * $inset
    $u = { param($v) & $snap ($inset + $v * $area) }
    $g = @{}
    $g.Top = & $u 0.08; $g.Bottom = & $u 0.92
    $g.LeftX = & $u 0.12; $g.PillarW = [Math]::Max(3, (& $u 0.37) - $g.LeftX)
    $g.RightX = $s - $g.LeftX - $g.PillarW
    $barH = [Math]::Max(2, (& $u 0.585) - (& $u 0.415))
    $g.BarY = [Math]::Round(($s - $barH) / 2); $g.BarH = $barH
    $g.BarX = $g.LeftX + $g.PillarW - [Math]::Max(1, [Math]::Round($g.PillarW * 0.25))
    $g.BarW = $g.RightX + [Math]::Max(1, [Math]::Round($g.PillarW * 0.25)) - $g.BarX
    $g.Radius = [Math]::Max(0, [Math]::Round($g.PillarW * 0.28))
    $g.BarRadius = [Math]::Max(0, [Math]::Round($barH * 0.30))
    return $g
}

function New-Canvas([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

function Draw-H($g, [int]$s, $geo, [string]$style) {
    $left = New-RoundedPath $geo.LeftX $geo.Top $geo.PillarW ($geo.Bottom - $geo.Top) $geo.Radius
    $right = New-RoundedPath $geo.RightX $geo.Top $geo.PillarW ($geo.Bottom - $geo.Top) $geo.Radius
    $bar = New-RoundedPath $geo.BarX $geo.BarY $geo.BarW $geo.BarH $geo.BarRadius
    $full = New-Object System.Drawing.RectangleF 0, 0, $s, $s

    if ($style -eq 'mark') {
        # One continuous diagonal blue→teal gradient across the whole letter.
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $full, $Blue, $Teal, 45.0
        $g.FillPath($grad, $left); $g.FillPath($grad, $right)
        # Subtle depth: the bar casts a soft shadow onto the pillars, so it reads as sitting in front of them.
        if ($s -ge 32) {
            $state = $g.Save()
            $clip = New-Object System.Drawing.Region $left; $clip.Union($right); $g.Clip = $clip
            foreach ($k in 1..3) {
                $m = New-Object System.Drawing.Drawing2D.Matrix; $m.Translate(0, [single]($s * 0.008 * $k))
                $shadow = $bar.Clone(); $shadow.Transform($m)
                $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(22, 6, 30, 70))), $shadow)
            }
            $g.Restore($state)
        }
        $g.FillPath($grad, $bar)
        # White highlight: a slim rounded line along the top of the bar, spanning the gap between the windows.
        $hlH = [Math]::Max(1, [Math]::Round($geo.BarH * 0.22))
        $hlY = $geo.BarY + [Math]::Max(1, [Math]::Round($geo.BarH * 0.20))
        $hlX = $geo.LeftX + $geo.PillarW + [Math]::Max(1, [Math]::Round($geo.PillarW * 0.10))
        $hlW = $geo.RightX - [Math]::Max(1, [Math]::Round($geo.PillarW * 0.10)) - $hlX
        $hl = New-RoundedPath $hlX $hlY $hlW $hlH ([Math]::Round($hlH / 2))
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 255, 255))), $hl)
    }
    else {
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $g.FillPath($white, $left); $g.FillPath($white, $right); $g.FillPath($white, $bar)
    }
}

function New-Mark([int]$s) {
    $bmp, $g = New-Canvas $s
    Draw-H $g $s (Get-Geometry $s 0.02) 'mark'
    $g.Dispose(); return $bmp
}

function New-Tray([int]$s) {
    $bmp, $g = New-Canvas $s
    Draw-H $g $s (Get-Geometry $s 0.02) 'white'
    $g.Dispose(); return $bmp
}

function New-Tile([int]$s) {
    $bmp, $g = New-Canvas $s
    $pad = [Math]::Round($s * 0.03)
    $tile = New-RoundedPath $pad $pad ($s - 2 * $pad) ($s - 2 * $pad) ([Math]::Round($s * 0.22))
    $full = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $full, $TileBlue, $TileTeal, 45.0), $tile)
    Draw-H $g $s (Get-Geometry $s 0.20) 'white'
    $g.Dispose(); return $bmp
}

function Save-Png($bmp, [string]$name) { $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png) }

function Save-Ico([scriptblock]$factory, [string]$name) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $pngs = foreach ($sz in $sizes) {
        $bmp = & $factory $sz
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        , $ms.ToArray()
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset); $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write($p) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir $name), $out.ToArray())
}

Save-Png (New-Mark 1024) 'helm-mark-1024.png'
Save-Png (New-Tile 1024) 'helm-tile-1024.png'
Save-Png (New-Tray 1024) 'helm-tray-white-1024.png'
Save-Png (New-Mark 256) 'helm.png'
Save-Ico ${function:New-Mark} 'helm.ico'
Save-Ico ${function:New-Tile} 'helm-tile.ico'
Save-Ico ${function:New-Tray} 'helm-tray-white.ico'

# Preview sheet: every size on dark and light backgrounds (not shipped).
$preview = New-Object System.Drawing.Bitmap 760, 560
$pg = [System.Drawing.Graphics]::FromImage($preview)
$pg.Clear([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
$pg.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 243, 243, 243))), 0, 280, 760, 280)
$x = 10
foreach ($sz in 16, 24, 32, 48, 64, 128) {
    foreach ($row in 0, 1) {
        $y = 10 + $row * 280
        $pg.DrawImage((New-Mark $sz), $x, $y)
        $pg.DrawImage((New-Tile $sz), $x, $y + ($sz + 4))
        if ($row -eq 0 -and $sz -le 32) { $pg.DrawImage((New-Tray $sz), $x + $sz + 4, $y) }
    }
    $x += $sz + 44
}
$pg.Dispose()
$preview.Save((Join-Path ([IO.Path]::GetTempPath()) 'helm-icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Icons written to $OutDir (preview: $(Join-Path ([IO.Path]::GetTempPath()) 'helm-icon-preview.png'))"
