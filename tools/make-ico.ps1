<#
.SYNOPSIS
  Pack the icon PNGs that tools/Preview renders into Assets/Lounge.ico.

.DESCRIPTION
  One PNG-compressed entry per size (16 to 256), the same layout Sir Word-a-lot's
  icon uses. Run after tools/Preview; the result is committed.

.EXAMPLE
  .\tools\make-ico.ps1
  .\tools\make-ico.ps1 -Preview dist\preview -Out Assets\Lounge.ico
#>
param(
    [string] $Preview = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\preview'),
    [string] $Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\Lounge.ico')
)

$ErrorActionPreference = 'Stop'
$sizes = 16, 24, 32, 48, 64, 128, 256
# The leading comma keeps each byte[] whole; a bare pipeline would flatten them.
$pngs = foreach ($s in $sizes) { , [IO.File]::ReadAllBytes((Join-Path $Preview "icon-$s.png")) }

$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter -ArgumentList $ms
$w.Write([uint16]0)               # reserved
$w.Write([uint16]1)               # type: icon
$w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
$w.Flush()
[IO.File]::WriteAllBytes($Out, $ms.ToArray())
Write-Host "Wrote $Out ($($ms.Length) bytes, $($sizes.Count) sizes)" -ForegroundColor Green
