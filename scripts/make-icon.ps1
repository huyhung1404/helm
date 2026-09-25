<#
.SYNOPSIS
  Regenerates Helm's icons in src/Helm.App/Assets from the vector description of the brand mark.

  The mark (traced from the design reference): two tall rounded rectangles side by side — a blue window on the
  left, a teal window on the right, separated by a thin gap — crossed by a rounded white bar. Every size is drawn
  natively on a pixel-snapped grid (never downscaled), with a 1 px minimum gap and 2 px minimum bar so 16 px stays
  readable.

  Outputs:
    helm-mark-1024.png / helm.png (256) / helm.ico   color mark, transparent (exe, window, taskbar, tray)
    helm-tile-1024.png / helm-tile.ico               mark on a dark rounded tile (installer)
#>
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Helm.App\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
$LeftTop = C '#0078F4'; $LeftBottom = C '#08B4FF'
$RightTop = C '#00C4DA'; $RightBottom = C '#03CEBA'
$TileTop = C '#252A36'; $TileBottom = C '#1A1E27'

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Max(0, [Math]::Min($r, [Math]::Min($w, $h) / 2))
    if ($r -lt 0.75) { $p.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h)); return $p }
    $d = 2 * $r
    $p.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
    $p.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
    $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
    $p.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Pixel-snapped geometry of the mark inside a square of $s pixels; $fill is the share of the height it occupies.
function Get-Geometry([int]$s, [double]$fill) {
    $g = @{}
    $H = [Math]::Max(8, [Math]::Round($s * $fill))
    $W = [Math]::Round($H * 0.835)
    $g.Gap = [Math]::Max(1, [Math]::Round($W * 0.063))
    $g.PillarW = [Math]::Floor(($W - $g.Gap) / 2)
    $W = 2 * $g.PillarW + $g.Gap
    $g.X = [Math]::Floor(($s - $W) / 2); $g.Y = [Math]::Floor(($s - $H) / 2)
    $g.W = $W; $g.H = $H
    $g.Radius = [Math]::Max(1, [Math]::Round($g.PillarW * 0.18))
    $g.BarH = [Math]::Max(2, [Math]::Round($H * 0.062))
    $g.BarY = $g.Y + [Math]::Round(($H - $g.BarH) / 2)
    $g.BarX = $g.X + [Math]::Max(1, [Math]::Round($W * 0.068))
    $g.BarW = ($g.X + $W - [Math]::Max(1, [Math]::Round($W * 0.068))) - $g.BarX
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

function Draw-Mark($g, $geo, [bool]$mono) {
    $left = New-RoundedPath $geo.X $geo.Y $geo.PillarW $geo.H $geo.Radius
    $rightX = $geo.X + $geo.PillarW + $geo.Gap
    $right = New-RoundedPath $rightX $geo.Y $geo.PillarW $geo.H $geo.Radius
    $bar = New-RoundedPath $geo.BarX $geo.BarY $geo.BarW $geo.BarH ($geo.BarH / 2)

    if ($mono) {
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $g.FillPath($white, $left); $g.FillPath($white, $right)
        # Cut the bar out so the monochrome mark still reads as "two windows + bar".
        $g.CompositingMode = 'SourceCopy'
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Transparent)), $bar)
        $g.CompositingMode = 'SourceOver'
        return
    }

    $lr = New-Object System.Drawing.RectangleF $geo.X, ($geo.Y - 1), $geo.PillarW, ($geo.H + 2)
    $rr = New-Object System.Drawing.RectangleF $rightX, ($geo.Y - 1), $geo.PillarW, ($geo.H + 2)
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $lr, $LeftTop, $LeftBottom, 90.0), $left)
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $rr, $RightTop, $RightBottom, 90.0), $right)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $bar)
}

function New-Mark([int]$s) { $bmp, $g = New-Canvas $s; Draw-Mark $g (Get-Geometry $s 0.90) $false; $g.Dispose(); return $bmp }

function New-Tile([int]$s) {
    $bmp, $g = New-Canvas $s
    $pad = [Math]::Round($s * 0.03)
    $tile = New-RoundedPath $pad $pad ($s - 2 * $pad) ($s - 2 * $pad) ([Math]::Round($s * 0.22))
    $full = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $full, $TileTop, $TileBottom, 90.0), $tile)
    Draw-Mark $g (Get-Geometry $s 0.62) $false
    $g.Dispose(); return $bmp
}

function Save-Png($bmp, [string]$name) { $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

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
Save-Png (New-Mark 256) 'helm.png'
Save-Ico ${function:New-Mark} 'helm.ico'
Save-Ico ${function:New-Tile} 'helm-tile.ico'

# Preview sheet (not shipped): each size on a dark and a light strip.
$preview = New-Object System.Drawing.Bitmap 820, 560
$pg = [System.Drawing.Graphics]::FromImage($preview)
$pg.Clear((C '#202020'))
$pg.FillRectangle((New-Object System.Drawing.SolidBrush (C '#F3F3F3')), 0, 280, 820, 280)
$x = 10
foreach ($sz in 16, 20, 24, 32, 48, 64, 128) {
    foreach ($row in 0, 1) {
        $y = 10 + $row * 280
        $pg.DrawImage((New-Mark $sz), $x, $y)
        $pg.DrawImage((New-Tile $sz), $x, $y + $sz + 6)
    }
    $x += $sz + 36
}
$pg.Dispose()
$preview.Save((Join-Path ([IO.Path]::GetTempPath()) 'helm-icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Icons written to $OutDir"
