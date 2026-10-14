<#
.SYNOPSIS
  Build Lounge's icon sizes and Assets/Lounge.ico from the rendered 3D master.

.DESCRIPTION
  The icon is a Vista-style object icon: a small 3D scene (glossy TV, film strip,
  controller, music note) rendered in Blender by tools/icon/lounge_icon.py into
  Assets/Icon/master.png (1024 px, transparent). This script adds a soft contact shadow
  under the objects, downsamples the master to every icon size, and packs
  Assets/Lounge.ico. Everything it writes is committed.

.PARAMETER Render
  Re-render the master with Blender first (needs Blender; path below or -Blender).

.EXAMPLE
  .\tools\make-icon.ps1
  .\tools\make-icon.ps1 -Render
#>
param(
    [switch] $Render,
    [string] $Blender = 'D:\Apps\Blender Foundation\Blender 5.2\blender.exe'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'Assets\Icon'
$master = Join-Path $dir 'master.png'

if ($Render) {
    if (-not (Test-Path -LiteralPath $Blender)) { throw "Blender not found at $Blender" }
    $script = Join-Path $PSScriptRoot 'icon\lounge_icon.py'
    & $Blender -b --factory-startup -P $script -- $master 1024 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Blender failed (exit $LASTEXITCODE)" }
    & $Blender -b --factory-startup -P $script -- (Join-Path $dir 'master-small.png') 512 small | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Blender failed (exit $LASTEXITCODE)" }
}
if (-not (Test-Path -LiteralPath $master)) { throw "No master render at $master; run with -Render." }

# A master with a soft elliptical contact shadow on its floor line, render on top.
function Shadowed([string] $file) {
    $src = [Drawing.Bitmap]::FromFile($file)
    $w = $src.Width; $h = $src.Height
    $minX = $w; $maxX = 0; $maxY = 0
    for ($y = 0; $y -lt $h; $y += 2) {
        for ($x = 0; $x -lt $w; $x += 2) {
            if ($src.GetPixel($x, $y).A -gt 40) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    $canvas = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($canvas)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)
    $cx = ($minX + $maxX) / 2; $sw = ($maxX - $minX) * 0.86; $sh = $h * 0.07
    for ($i = 0; $i -lt 24; $i++) {
        $k = $i / 24.0
        $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(7, 0, 0, 0))),
            [float]($cx - $sw / 2 * (1 - $k * 0.7)), [float]($maxY - $sh * 0.65 * (1 - $k * 0.6)),
            [float]($sw * (1 - $k * 0.7)), [float]($sh * (1 - $k * 0.6)))
    }
    $g.DrawImage($src, 0, 0, $w, $h)
    $g.Dispose(); $src.Dispose()
    $canvas
}

$full = Shadowed $master
$smallFile = Join-Path $dir 'master-small.png'
# Four objects turn to noise below 48 px; the small render (TV and note) is used there.
$small = if (Test-Path -LiteralPath $smallFile) { Shadowed $smallFile } else { $full }

foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
    $source = if ($size -le 32) { $small } else { $full }
    $bmp = New-Object Drawing.Bitmap $size, $size
    $sg = [Drawing.Graphics]::FromImage($bmp)
    $sg.InterpolationMode = 'HighQualityBicubic'
    $sg.PixelOffsetMode = 'HighQuality'
    $sg.CompositingQuality = 'HighQuality'
    $sg.Clear([Drawing.Color]::Transparent)
    $sg.DrawImage($source, 0, 0, $size, $size)
    $bmp.Save((Join-Path $dir "icon-$size.png"), [Drawing.Imaging.ImageFormat]::Png)
    $sg.Dispose(); $bmp.Dispose()
}
$full.Dispose(); if ($small -ne $full) { $small.Dispose() }& (Join-Path $PSScriptRoot 'make-ico.ps1') -Preview $dir -Out (Join-Path $root 'Assets\Lounge.ico')
Write-Host "Icon written to Assets\Icon and Assets\Lounge.ico"
