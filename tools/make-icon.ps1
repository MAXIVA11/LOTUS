# Draws the LOTUS mark and writes it as a multi-size .ico plus a 256 px .png.
# Run from the repository root:  powershell -File tools\make-icon.ps1
# The same shapes are used in the app (src\Lotus.App\Controls\LotusMark.cs).

param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\Lotus.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Shapes live in a 100 x 100 box.
$tile   = @(4,4, 78,4, 96,22, 96,96, 4,96)
$shapes = @(
    @{ Color = '#FF2D95'; Points = @(10,52, 28,60, 44,77, 21,76) },   # outer left petal
    @{ Color = '#FF2D95'; Points = @(90,52, 72,60, 56,77, 79,76) },   # outer right petal
    @{ Color = '#00F0FF'; Points = @(24,28, 41,44, 45,75, 28,60) },   # inner left petal
    @{ Color = '#00F0FF'; Points = @(76,28, 59,44, 55,75, 72,60) },   # inner right petal
    @{ Color = '#F5F5F5'; Points = @(50,14, 62,44, 50,77, 38,44) },   # centre petal
    @{ Color = '#FCEE0A'; Points = @(22,82, 78,82, 73,89, 27,89) }    # water line
)

function New-Points([int[]]$flat, [double]$scale) {
    $pts = New-Object 'System.Drawing.PointF[]' ($flat.Length / 2)
    for ($i = 0; $i -lt $pts.Length; $i++) {
        $pts[$i] = New-Object System.Drawing.PointF ([single]($flat[2*$i] * $scale)), ([single]($flat[2*$i+1] * $scale))
    }
    return ,$pts
}

function New-Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 100.0

    $back = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#0B0B10'))
    $g.FillPolygon($back, (New-Points $tile $scale))
    $back.Dispose()

    foreach ($shape in $shapes) {
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($shape.Color))
        $g.FillPolygon($brush, (New-Points $shape.Points $scale))
        $brush.Dispose()
    }
    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

# Classic icon image: BITMAPINFOHEADER, 32-bit pixels bottom-up, then a 1-bit mask.
function Get-DibBytes($bmp) {
    $size = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $rowBytes = [int]([math]::Ceiling($size / 32.0) * 4)
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $rowBytes
        for ($x = 0; $x -lt $size; $x++) {
            if ($bmp.GetPixel($x, $y).A -eq 0) {
                $row[[int][math]::Floor($x / 8)] = $row[[int][math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8))
            }
        }
        $bw.Write($row)
    }
    $bw.Flush()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = @()
foreach ($s in $sizes) {
    $bmp = New-Mark $s
    if ($s -ge 256) { $bytes = Get-PngBytes $bmp } else { $bytes = Get-DibBytes $bmp }
    $images += ,@{ Size = $s; Bytes = $bytes }
    if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'lotus.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$img.Bytes.Length); $w.Write([int]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'lotus.ico'), $ico.ToArray())

Write-Host "Wrote $(Join-Path $OutDir 'lotus.ico') and lotus.png"
