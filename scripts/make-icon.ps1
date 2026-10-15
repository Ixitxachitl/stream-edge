<#
.SYNOPSIS
    Regenerates the app icons from the source art (icon.png at the repo root).

.DESCRIPTION
    Writes, under app/StreamEdge.App.Avalonia/Assets/:
      icon.ico  16-256 px, for the Windows executable and taskbar
      icon.png  512 px, the window icon on every OS (and for Linux/macOS packaging)
    The .ico holds PNG-compressed images, which Windows has read since Vista.
    Run after changing icon.png.

.EXAMPLE
    pwsh scripts/make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$source   = Join-Path $repoRoot 'icon.png'
$assets   = Join-Path $repoRoot 'app/StreamEdge.App.Avalonia/Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$art = [System.Drawing.Image]::FromFile($source)

function Resize([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($art, 0, 0, $size, $size)
    $g.Dispose()
    $bmp
}

function PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

try {
    # Window icon.
    $png = Resize 512
    $png.Save((Join-Path $assets 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $png.Dispose()

    # ICO: header, one directory entry per size, then the PNG data.
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $images = foreach ($s in $sizes) { $b = Resize $s; , (PngBytes $b); $b.Dispose() }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $dim = if ($s -ge 256) { 0 } else { $s }   # 0 means 256 in the ICO format
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $assets 'icon.ico'), $out.ToArray())
    Write-Host "Wrote icon.ico ($($sizes -join ', ') px) and icon.png (512 px) to $assets"
}
finally {
    $art.Dispose()
}
