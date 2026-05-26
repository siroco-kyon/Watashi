<#
.SYNOPSIS
  Generate the Watashi Torii brand mark as a multi-size .ico file.

.DESCRIPTION
  Redraws the same geometry as the WPF DrawingImage in
  src/Watashi.Client/Themes/Icons.xaml using System.Drawing, at sizes
  16/24/32/48/64/128/256 px, and packs them all into a single .ico file
  (each size as PNG inside the ICO container -- supported by Vista+).

  We keep this as ASCII-only because Windows PowerShell 5.1 reads scripts
  in the system ANSI codepage when there is no UTF-8 BOM, and previous
  Japanese-comment versions ended up garbled and merging lines.

.PARAMETER OutPath
  Absolute destination path. Default: <repo>/src/Watashi.Client/Watashi.ico

.EXAMPLE
  pwsh -File scripts/Generate-ToriiIcon.ps1
#>
param(
    [string]$OutPath
)

$ErrorActionPreference = 'Stop'

if (-not $OutPath) {
    $repo = Split-Path -Parent $PSScriptRoot
    $OutPath = Join-Path $repo 'src\Watashi.Client\Watashi.ico'
}

Add-Type -AssemblyName System.Drawing

# Mirrors Colors.xaml: AccentColor (vermilion / shu-iro) and AccentDeepColor.
$ACCENT      = [System.Drawing.Color]::FromArgb(0xFF, 0xC7, 0x3E, 0x1D)
$ACCENT_DEEP = [System.Drawing.Color]::FromArgb(0xFF, 0x5C, 0x1A, 0x0B)

function New-ToriiPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Source design is on a 64x64 viewbox; scale to target size.
    $scale = [float]($size / 64.0)
    $g.ScaleTransform($scale, $scale)

    $accent = New-Object System.Drawing.SolidBrush($ACCENT)
    $accentDeep = New-Object System.Drawing.SolidBrush($ACCENT_DEEP)

    # Top beam (kasagi) with upturned ends.
    $kasagi = New-Object System.Drawing.Drawing2D.GraphicsPath
    $kasagi.AddBezier(2, 15, 2, 12, 2, 4, 14, 5)
    $kasagi.AddLine(14, 5, 50, 5)
    $kasagi.AddBezier(50, 5, 58, 2, 62, 4, 62, 15)
    $kasagi.AddLine(62, 15, 2, 15)
    $kasagi.CloseFigure()
    $g.FillPath($accent, $kasagi)
    $kasagi.Dispose()

    # Second beam (shimaki) in the darker shade.
    $g.FillRectangle($accentDeep, 6, 17, 52, 5)

    # Left pillar (hashira), trapezoid widening slightly downward.
    $leftP = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(14, 22)),
        (New-Object System.Drawing.PointF(22, 22)),
        (New-Object System.Drawing.PointF(23, 60)),
        (New-Object System.Drawing.PointF(13, 60))
    )
    $g.FillPolygon($accent, $leftP)

    # Right pillar.
    $rightP = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(42, 22)),
        (New-Object System.Drawing.PointF(50, 22)),
        (New-Object System.Drawing.PointF(51, 60)),
        (New-Object System.Drawing.PointF(41, 60))
    )
    $g.FillPolygon($accent, $rightP)

    # Crossbeam (nuki).
    $g.FillRectangle($accent, 10, 29, 44, 5)

    $accent.Dispose()
    $accentDeep.Dispose()
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

# Generate one PNG per target size.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngList = New-Object System.Collections.ArrayList
foreach ($s in $sizes) {
    $png = New-ToriiPng $s
    [void]$pngList.Add(@{ Size = $s; Data = $png })
}

# Pack the PNGs into an ICO container.
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)

# ICONDIR header (6 bytes).
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type = icon
$bw.Write([uint16]$pngList.Count) # number of images

$headerSize = 6 + (16 * $pngList.Count)
$offset = $headerSize

# ICONDIRENTRY (16 bytes per image).
foreach ($e in $pngList) {
    $w = $e.Size
    if ($w -ge 256) { $w = 0 }   # 0 means 256 in the 1-byte field
    $bw.Write([byte]$w)          # width
    $bw.Write([byte]$w)          # height
    $bw.Write([byte]0)           # color count (0 for >256 colors)
    $bw.Write([byte]0)           # reserved
    $bw.Write([uint16]1)         # planes
    $bw.Write([uint16]32)        # bits per pixel
    $bw.Write([uint32]$e.Data.Length) # bytes in res
    $bw.Write([uint32]$offset)   # offset in file
    $offset = $offset + $e.Data.Length
}

# Image data blocks (PNG bytes for each size).
foreach ($e in $pngList) {
    $bw.Write($e.Data)
}

$outDir = Split-Path -Parent $OutPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())

$bw.Dispose()
$out.Dispose()

$fi = Get-Item $OutPath
Write-Host ("Wrote {0} ({1} bytes, {2} sizes)" -f $fi.FullName, $fi.Length, $pngList.Count)
