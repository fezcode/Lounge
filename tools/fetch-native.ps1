<#
.SYNOPSIS
  Stage the FFmpeg 8 + libass DLL closure Lounge's player loads, into native/win-x64.

.DESCRIPTION
  Lounge decodes with the same FFmpeg build tivi bundles: MSYS2 mingw64's FFmpeg 8.0
  (avcodec-62, avformat-62, avutil-60, swscale-9, swresample-6, avfilter-11) and
  libass-9. This walks the import tables with objdump and copies every dependency
  that lives in mingw64\bin, skipping Windows system DLLs. The result is gitignored;
  build.ps1 runs this when the folder is missing.

.EXAMPLE
  .\tools\fetch-native.ps1
  .\tools\fetch-native.ps1 -Force
#>
param(
    [string] $MingwBin = '',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root 'native\win-x64'

if (-not $MingwBin) {
    $gcc = Get-Command gcc -ErrorAction SilentlyContinue
    $MingwBin = if ($gcc) { Split-Path $gcc.Source } else { 'C:\msys64\mingw64\bin' }
}
$objdump = Join-Path $MingwBin 'objdump.exe'
if (-not (Test-Path $objdump)) { throw "objdump not found in $MingwBin. Install MSYS2 mingw64 FFmpeg + binutils or pass -MingwBin." }

$roots = 'avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swscale-9.dll',
         'swresample-6.dll', 'avfilter-11.dll', 'libass-9.dll'

if ((Test-Path (Join-Path $stage 'avcodec-62.dll')) -and -not $Force) {
    Write-Host "native/win-x64 already staged ($((Get-ChildItem $stage -Filter *.dll).Count) DLLs). Use -Force to refresh." -ForegroundColor DarkGray
    return
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$seen = @{}
function Resolve-Deps([string] $file) {
    $deps = & $objdump -p $file 2>$null | Select-String 'DLL Name:\s*(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
    foreach ($d in $deps) {
        $key = $d.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $src = Join-Path $MingwBin $d
        if (Test-Path $src) {
            Copy-Item $src $stage -Force
            Resolve-Deps $src
        }
    }
}

foreach ($r in $roots) {
    $src = Join-Path $MingwBin $r
    if (-not (Test-Path $src)) { throw "Missing $src - FFmpeg 8 / libass not installed in MSYS2 mingw64." }
    $seen[$r.ToLowerInvariant()] = $true
    Copy-Item $src $stage -Force
    Resolve-Deps $src
}

$n = (Get-ChildItem $stage -Filter *.dll).Count
$mb = [math]::Round(((Get-ChildItem $stage -Filter *.dll | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Staged $n DLLs ($mb MB) into $stage" -ForegroundColor Green
