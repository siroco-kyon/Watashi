<#
.SYNOPSIS
  Generate color variants of the Watashi Torii icon as multi-size .ico files.

.DESCRIPTION
  Redraws the same Torii geometry as scripts/Generate-ToriiIcon.ps1 (which
  mirrors the WPF DrawingImage in src/Watashi.Client/Themes/Icons.xaml) in
  10 different hues, so that each distribution (= each central server / DB)
  can ship a visually distinct icon. Replace src/Watashi.Client/Watashi.ico
  with the chosen variant before publishing (see docs/BRANDING.md).

  The two shades of each variant are derived the same way as the original
  vermilion brand colors: accent = HSL(hue, 0.75, 0.45) and the darker
  second beam = HSL(hue, 0.79, 0.20). The original vermilion (hue 12) is
  intentionally NOT duplicated here; the default Watashi.ico stays as-is.

  We keep this script ASCII-only because Windows PowerShell 5.1 reads
  scripts in the system ANSI codepage when there is no UTF-8 BOM.

.PARAMETER OutDir
  Destination directory. Default: <repo>/assets/icons

.PARAMETER PreviewDir
  Optional directory to also write a 128px PNG preview per variant.
  Skipped when empty.

.EXAMPLE
  pwsh -File scripts/Generate-ToriiIconVariants.ps1
#>
param(
    [string]$OutDir,
    [string]$PreviewDir
)

$ErrorActionPreference = 'Stop'

if (-not $OutDir) {
    $repo = Split-Path -Parent $PSScriptRoot
    $OutDir = Join-Path $repo 'assets\icons'
}

Add-Type -AssemblyName System.Drawing

# 10 hues spread across the wheel, all clearly distinct from the original
# vermilion (hue 12) and from each other. Japanese reference names are in
# docs/BRANDING.md (kept out of this file to stay ASCII-only).
$variants = @(
    @{ Name = 'orange';  Hue = 30  },
    @{ Name = 'gold';    Hue = 48  },
    @{ Name = 'lime';    Hue = 90  },
    @{ Name = 'green';   Hue = 140 },
    @{ Name = 'teal';    Hue = 170 },
    @{ Name = 'cyan';    Hue = 200 },
    @{ Name = 'blue';    Hue = 225 },
    @{ Name = 'violet';  Hue = 262 },
    @{ Name = 'magenta'; Hue = 300 },
    @{ Name = 'rose';    Hue = 335 }
)

function Convert-HslToColor([double]$h, [double]$s, [double]$l) {
    $c = (1.0 - [Math]::Abs(2.0 * $l - 1.0)) * $s
    $hp = $h / 60.0
    $x = $c * (1.0 - [Math]::Abs(($hp % 2.0) - 1.0))
    switch ([int][Math]::Floor($hp) % 6) {
        0 { $r = $c; $g = $x; $b = 0.0 }
        1 { $r = $x; $g = $c; $b = 0.0 }
        2 { $r = 0.0; $g = $c; $b = $x }
        3 { $r = 0.0; $g = $x; $b = $c }
        4 { $r = $x; $g = 0.0; $b = $c }
        5 { $r = $c; $g = 0.0; $b = $x }
    }
    $m = $l - $c / 2.0
    return [System.Drawing.Color]::FromArgb(
        255,
        [int][Math]::Round(255.0 * ($r + $m)),
        [int][Math]::Round(255.0 * ($g + $m)),
        [int][Math]::Round(255.0 * ($b + $m)))
}

function New-ToriiPng([int]$size, [System.Drawing.Color]$accentColor, [System.Drawing.Color]$deepColor) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Source design is on a 64x64 viewbox; scale to target size.
    $scale = [float]($size / 64.0)
    $g.ScaleTransform($scale, $scale)

    $accent = New-Object System.Drawing.SolidBrush($accentColor)
    $accentDeep = New-Object System.Drawing.SolidBrush($deepColor)

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

function Write-Ico([string]$path, [System.Collections.ArrayList]$pngList) {
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

    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    $bw.Dispose()
    $out.Dispose()
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
if ($PreviewDir -and -not (Test-Path $PreviewDir)) { New-Item -ItemType Directory -Force -Path $PreviewDir | Out-Null }

$sizes = @(16, 24, 32, 48, 64, 128, 256)

foreach ($v in $variants) {
    $accentColor = Convert-HslToColor $v.Hue 0.75 0.45
    $deepColor = Convert-HslToColor $v.Hue 0.79 0.20

    $pngList = New-Object System.Collections.ArrayList
    foreach ($s in $sizes) {
        $png = New-ToriiPng $s $accentColor $deepColor
        [void]$pngList.Add(@{ Size = $s; Data = $png })
    }

    $icoPath = Join-Path $OutDir ("Watashi-{0}.ico" -f $v.Name)
    Write-Ico $icoPath $pngList

    if ($PreviewDir) {
        $previewPath = Join-Path $PreviewDir ("Watashi-{0}.png" -f $v.Name)
        $preview = New-ToriiPng 128 $accentColor $deepColor
        [System.IO.File]::WriteAllBytes($previewPath, $preview)
    }

    $fi = Get-Item $icoPath
    Write-Host ("{0,-8} hue={1,3}  accent=#{2:X2}{3:X2}{4:X2}  deep=#{5:X2}{6:X2}{7:X2}  {8} ({9} bytes)" -f `
        $v.Name, $v.Hue,
        $accentColor.R, $accentColor.G, $accentColor.B,
        $deepColor.R, $deepColor.G, $deepColor.B,
        $fi.Name, $fi.Length)
}

Write-Host ("Done. {0} variants written to {1}" -f $variants.Count, $OutDir)
