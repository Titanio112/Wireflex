<#
.SYNOPSIS
  Generates the Wireflex icon (wireflex.ico) from code: a blue rounded square with a white phone outline.
  Original drawing, no third-party artwork.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\icon\New-WireflexIcon.ps1
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\..\src\Wireflex.App\Assets\wireflex.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Background: blue rounded square.
    $s = [single]$size
    $bg = New-RoundedPath 0 0 ($s - 0.01) ($s - 0.01) ($s * 0.22)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 31, 111, 235))
    $g.FillPath($brush, $bg)

    # Phone: white rounded outline, centered.
    $stroke = [Math]::Max([single]1.5, $s * 0.085)
    $pw = $s * 0.44
    $ph = $s * 0.68
    $px = ($s - $pw) / 2
    $py = ($s - $ph) / 2
    $phone = New-RoundedPath $px $py $pw $ph ($s * 0.09)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $g.DrawPath($pen, $phone)

    # Home dot near the bottom of the phone.
    $dot = [Math]::Max([single]1.5, $s * 0.09)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillEllipse($white, ($s - $dot) / 2, $py + $ph - $stroke - $dot * 1.4, $dot, $dot)

    $g.Dispose()
    return $bmp
}

# DIB entry (BITMAPINFOHEADER + BGRA rows bottom-up + empty AND mask): read by every Windows version.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write([int]($size * $size * 4 + $maskRow * $size))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    return $ms.ToArray()
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    if ($size -ge 256) { , (Get-PngBytes $bmp) } else { , (Get-DibBytes $bmp) }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $w.Write([byte[]]$bytes) }
$w.Flush()

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
Write-Output "Icon written: $OutFile ($($out.Length) bytes)"
