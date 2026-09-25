# Regenerates src/Helm.App/Assets/helm.ico (a ship's wheel on a gradient tile) and helm.png.
# Windows PowerShell 5.1 / PowerShell 7 on Windows (uses System.Drawing).
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Helm.App\Assets'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-HelmBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.04))
    $r = [int]($size * 0.22)
    $rect = New-Object System.Drawing.Rectangle $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), ([System.Drawing.Color]::FromArgb(255, 94, 92, 230)), 45.0
    $g.FillPath($brush, $path)

    $c = $size / 2.0
    $white = [System.Drawing.Color]::White
    $ringR = $size * 0.25
    $pen = New-Object System.Drawing.Pen $white, ([float][Math]::Max(1.5, $size * 0.07))
    $g.DrawEllipse($pen, [float]($c - $ringR), [float]($c - $ringR), [float](2 * $ringR), [float](2 * $ringR))

    $spokePen = New-Object System.Drawing.Pen $white, ([float][Math]::Max(1.2, $size * 0.055))
    $spokePen.StartCap = 'Round'; $spokePen.EndCap = 'Round'
    $outer = $size * 0.36
    $knob = [Math]::Max(1.5, $size * 0.055)
    $wb = New-Object System.Drawing.SolidBrush $white
    for ($i = 0; $i -lt 8; $i++) {
        $a = [Math]::PI / 4 * $i
        $x2 = $c + [Math]::Cos($a) * $outer; $y2 = $c + [Math]::Sin($a) * $outer
        $g.DrawLine($spokePen, [float]$c, [float]$c, [float]$x2, [float]$y2)
        if ($size -ge 32) { $g.FillEllipse($wb, [float]($x2 - $knob), [float]($y2 - $knob), [float](2 * $knob), [float](2 * $knob)) }
    }
    $hub = $size * 0.08
    $g.FillEllipse($wb, [float]($c - $hub), [float]($c - $hub), [float](2 * $hub), [float](2 * $hub))
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-HelmBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'helm.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'helm.ico'), $out.ToArray())
Write-Host "Wrote $(Join-Path $OutDir 'helm.ico')"
