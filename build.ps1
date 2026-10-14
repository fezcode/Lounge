<#
.SYNOPSIS
  Build, verify and publish Lounge and LoungeAgent into dist/Lounge.

.DESCRIPTION
  Runs the verification layers in order: unit tests (Core, the FFmpeg engine against
  generated clips), headless controller-flow checks driving the real screens with a
  virtual pad, and rendered previews of every screen in every theme. Then publishes
  the self-contained app and agent side by side (an extracted publish: Lounge starts
  from the couch often, and a single-file bundle unpacks to temp on every cold start),
  stages FFmpeg 8 + libass beside them, and proves the published binaries run.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -SkipTests      # local iteration only; never for a release
#>
param(
    [string] $Config = 'Release',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$rid = 'win-x64'
$distApp = Join-Path $PSScriptRoot 'dist/Lounge'
$preview = Join-Path $PSScriptRoot 'dist/preview'

function Write-Step([string] $text) { Write-Host "`n$text" -ForegroundColor Cyan }

function Invoke-Checked([string] $what, [scriptblock] $action) {
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

Write-Step '[1/7] Verifying versions agree...'
. (Join-Path $PSScriptRoot 'version.ps1')
$versions = Show-Report $PSScriptRoot $null
if (-not $versions.AllOk) { throw 'Versions disagree; run version.ps1 before building.' }
$version = $versions.Target

Write-Step '[2/7] Staging FFmpeg 8 + libass...'
& (Join-Path $PSScriptRoot 'tools/fetch-native.ps1')
if (-not (Test-Path (Join-Path $PSScriptRoot 'native/win-x64/avcodec-62.dll'))) { throw 'FFmpeg DLLs are not staged.' }

Write-Step "[3/7] Building ($Config)..."
Invoke-Checked 'dotnet build Lounge' { dotnet build Lounge.csproj -c $Config --nologo }
Invoke-Checked 'dotnet build LoungeAgent' { dotnet build Agent/LoungeAgent.csproj -c $Config --nologo }

if ($SkipTests) {
    Write-Host "`n[4/7] Skipping tests, checks and previews (-SkipTests)" -ForegroundColor Yellow
} else {
    Write-Step '[4/7] Running tests, controller-flow checks and previews...'
    Invoke-Checked 'dotnet test' { dotnet test tests/Lounge.Tests.csproj -c $Config --nologo }
    Invoke-Checked 'tools/Checks' { dotnet run --project tools/Checks -c $Config }
    Invoke-Checked 'tools/Preview' { dotnet run --project tools/Preview -c $Config -- $preview }
}

Write-Step '[5/7] Closing any running copy of this build...'
# Publishing over a running exe fails on Windows. Scope this to our own output.
foreach ($name in 'Lounge', 'LoungeAgent') {
    Get-Process -Name $name -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($distApp, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            Write-Host "  stopping $name pid $($_.Id)" -ForegroundColor DarkGray
            Stop-Process -Id $_.Id -Force
        }
}
Start-Sleep -Milliseconds 400

Write-Step "[6/7] Publishing self-contained $rid..."
if (Test-Path -LiteralPath $distApp) { Remove-Item -LiteralPath $distApp -Recurse -Force }
Invoke-Checked 'dotnet publish Lounge' {
    dotnet publish Lounge.csproj -c $Config -r $rid --self-contained true -p:PublishSingleFile=false --nologo -o $distApp
}
Invoke-Checked 'dotnet publish LoungeAgent' {
    dotnet publish Agent/LoungeAgent.csproj -c $Config -r $rid --self-contained true -p:PublishSingleFile=false --nologo -o $distApp
}

Write-Step '[7/7] Proving the published binaries actually run...'
$exe = Join-Path $distApp 'Lounge.exe'
$agent = Join-Path $distApp 'LoungeAgent.exe'
foreach ($required in @($exe, $agent, (Join-Path $distApp 'ffmpeg/avcodec-62.dll'), (Join-Path $distApp 'ffmpeg/libass-9.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Publish is missing $required" }
}
$symbols = @(Get-ChildItem -LiteralPath $distApp -Filter '*.pdb' -File -ErrorAction SilentlyContinue)
if ($symbols.Count) { throw "Payload contains $($symbols.Count) .pdb file(s); release builds embed symbols." }

# --selftest loads FFmpeg and libass from ffmpeg\, opens SQLite and writes settings,
# all of which resolve differently in the published folder than under dotnet run.
$log = Join-Path $env:TEMP 'lounge-build-selftest.txt'
$proc = Start-Process -FilePath $exe -ArgumentList '--selftest' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $log
$output = (Get-Content -LiteralPath $log -Raw).Trim()
if ($proc.ExitCode -ne 0) { throw "Self-test failed (exit $($proc.ExitCode)): $output" }

foreach ($bin in @($exe, $agent)) {
    $vlog = Join-Path $env:TEMP 'lounge-build-version.txt'
    $p = Start-Process -FilePath $bin -ArgumentList '--version' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $vlog
    $reported = (Get-Content -LiteralPath $vlog -Raw).Trim()
    if ($reported -notmatch [regex]::Escape($version)) {
        throw "$(Split-Path $bin -Leaf) reports '$reported' but the release version is $version."
    }
}

$item = Get-Item -LiteralPath $exe
$size = (Get-ChildItem -LiteralPath $distApp -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host "Done. Output: $distApp" -ForegroundColor Green
Write-Host ("  payload:   {0} MB in {1} files" -f [math]::Round($size / 1MB, 1), (Get-ChildItem -LiteralPath $distApp -Recurse -File).Count)
Write-Host "  version:   $version"
Write-Host "  self-test: $output"
Write-Host "  SHA256:    $((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash)"
