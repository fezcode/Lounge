<#
.SYNOPSIS
  Package the published Lounge build into a Forge Setup executable.

.DESCRIPTION
  Requires the sibling Forge project to have been built with `gobake build`,
  producing ../Forge/build/forge.exe and ../Forge/build/uninstall.exe.

.EXAMPLE
  .\installer.ps1                 # build, verify, then package
  .\installer.ps1 -SkipBuild      # package the payload already in dist/
#>
param(
    [ValidateSet('win-x64')]
    [string] $Rid = 'win-x64',
    [string] $Config = 'Release',
    [string] $ForgeDir = '..\Forge',
    [switch] $SkipBuild,
    # Superseded installers are published as GitHub releases, so this prunes them.
    # Pass this to keep one that has not been released yet.
    [switch] $KeepOldInstallers
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')

$versions = Test-Consistent $PSScriptRoot $null
if (-not $versions.AllOk) { throw 'Version mismatch; run version.ps1 before packaging.' }
$version = $versions.Target

$forgeRoot   = Resolve-Path $ForgeDir
$forgeGui    = Join-Path $forgeRoot 'build\forge.exe'
$uninstaller = Join-Path $forgeRoot 'build\uninstall.exe'
$distApp     = Join-Path $PSScriptRoot 'dist\Lounge'
$outDir      = Join-Path $PSScriptRoot 'dist\installer'
$logDir      = Join-Path $outDir 'logs'
$setupPath   = Join-Path $outDir "Lounge-Setup-$version.exe"

function Assert-GuiExecutable([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid executable: $Path" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE header: $Path" }
        $stream.Position = $peOffset + 24 + 68
        if ($reader.ReadUInt16() -ne 2) {
            throw "Expected a GUI executable: $Path. Build Forge with gobake build (windowsgui subsystem)."
        }
    } finally { $reader.Dispose() }
}

if (-not $SkipBuild) {
    Write-Host "[1/4] Building Lounge ($Rid, $Config)..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1') -Config $Config
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed (exit $LASTEXITCODE)" }
} else {
    Write-Host '[1/4] Skipping rebuild (-SkipBuild)' -ForegroundColor DarkGray
}

Write-Host "`n[2/4] Checking the published payload..." -ForegroundColor Cyan
foreach ($relative in @('Lounge.exe', 'LoungeAgent.exe', 'ffmpeg\avcodec-62.dll', 'ffmpeg\libass-9.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $distApp $relative))) {
        throw "Missing payload: $relative. Run build.ps1 first."
    }
}
foreach ($relative in @('LICENSE.txt', 'Assets\Lounge.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $relative))) {
        throw "Missing installer input: $relative."
    }
}
$binaryVersion = (Get-Item -LiteralPath (Join-Path $distApp 'Lounge.exe')).VersionInfo.FileVersion
if ($binaryVersion -notmatch "^$([regex]::Escape($version))") {
    throw "Published binary is $binaryVersion but Forge expects $version. Run build.ps1 first."
}

# A release payload ships no loose debug symbols.
$symbols = @(Get-ChildItem -LiteralPath $distApp -Filter '*.pdb' -File -ErrorAction SilentlyContinue)
if ($symbols.Count) {
    throw "Payload contains $($symbols.Count) .pdb file(s). Release builds use DebugType=embedded."
}

# Every shortcut icon must be a path the installer actually writes, or the
# shortcut renders blank. Forge dedups [[files]] against [app] icon by source
# path, which has silently dropped an app.ico copy before.
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'forge.toml') -Raw
$written = @('${INSTALLDIR}/Lounge.exe')
foreach ($line in [regex]::Matches($manifest, '(?m)^\s*dst\s*=\s*"([^"]+)"')) {
    $written += $line.Groups[1].Value
}
foreach ($icon in [regex]::Matches($manifest, '(?m)^\s*icon\s*=\s*"(\$\{INSTALLDIR\}[^"]+)"')) {
    $path = $icon.Groups[1].Value
    if ($written -notcontains $path) {
        throw "Shortcut icon $path is never written by the installer. It would render blank."
    }
}
Write-Host "  payload ok: no symbols, shortcut icons resolve" -ForegroundColor DarkGray

Write-Host "`n[3/4] Ensuring a GUI-subsystem forge.exe..." -ForegroundColor Cyan
if (-not (Test-Path $forgeGui))    { throw "Missing $forgeGui. Run 'gobake build' in $forgeRoot first." }
if (-not (Test-Path $uninstaller)) { throw "Missing $uninstaller. Run 'gobake build' in $forgeRoot first." }

# Themes are go:embed-ed into forge.exe, so editing a theme and not rebuilding
# Forge silently packages the previous one. Check the theme tree, not just the
# Go sources.
$forgeStamp = (Get-Item $forgeGui).LastWriteTime
$newest = Get-ChildItem (Join-Path $forgeRoot 'themes'), (Join-Path $forgeRoot 'cmd'), (Join-Path $forgeRoot 'internal') `
    -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest -and $newest.LastWriteTime -gt $forgeStamp) {
    throw "forge.exe ($($forgeStamp.ToString('u'))) is older than $($newest.Name) ($($newest.LastWriteTime.ToString('u'))). Run 'gobake build' in $forgeRoot so the current themes are embedded."
}

Assert-GuiExecutable $forgeGui
Assert-GuiExecutable $uninstaller
Write-Host "  using: $forgeGui" -ForegroundColor DarkGray
Write-Host "  theme: $($manifest -replace '(?s).*?theme\s*=\s*"([^"]+)".*', '$1')" -ForegroundColor DarkGray

Write-Host "`n[4/4] Building Setup.exe..." -ForegroundColor Cyan

# forge.exe is a GUI-subsystem binary, so $LASTEXITCODE is not propagated through
# PowerShell's call operator. Start-Process -Wait -PassThru is the reliable route.
function Invoke-Forge {
    param([string[]] $ForgeArgs)
    $quoted = @($ForgeArgs | ForEach-Object {
        if ($_ -match '["\r\n]') { throw 'Invalid Forge argument.' }
        '"' + ($_ -replace '(\\+)$', '$1$1') + '"'
    })
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $stdout = Join-Path $logDir "forge-$($ForgeArgs[0]).stdout.log"
    $stderr = Join-Path $logDir "forge-$($ForgeArgs[0]).stderr.log"
    $p = Start-Process -FilePath $forgeGui -ArgumentList $quoted -WorkingDirectory $PSScriptRoot `
        -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout | Write-Host }
    if ($p.ExitCode -ne 0) {
        $detail = Get-Content -LiteralPath $stderr -Raw
        throw "forge $($ForgeArgs -join ' ') failed (exit $($p.ExitCode)): $detail"
    }
    if ((Get-Item -LiteralPath $stderr).Length) { Write-Warning (Get-Content -LiteralPath $stderr -Raw) }
}

Invoke-Forge @('validate', 'forge.toml')

# Forge names the output after the app, turning spaces into underscores, which
# gives "Lounge-Setup-x.y.z.exe". Keep the readable name in the wizard
# and rename the artifact to the clean one used for the release asset.
$forgeOutput = Join-Path $outDir "Lounge-Setup-$version.exe"

# Remove both names first, so a failed build cannot look successful.
foreach ($stale in @($setupPath, $forgeOutput)) {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }
}
Invoke-Forge @('build', '--out', $outDir)

if ((Test-Path -LiteralPath $forgeOutput) -and $forgeOutput -ne $setupPath) {
    Move-Item -LiteralPath $forgeOutput -Destination $setupPath -Force
}
if (-not (Test-Path -LiteralPath $setupPath)) { throw "Expected installer missing after build: $setupPath" }
$setup = Get-Item -LiteralPath $setupPath
Assert-GuiExecutable $setup.FullName

# Only now that this build is verified, drop the installers it supersedes; each
# was published as its own GitHub release. Logs stay under logs/.
$superseded = @(Get-ChildItem -LiteralPath $outDir -Filter '*Setup-*.exe' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -ne $setup.FullName })
if ($superseded.Count -and -not $KeepOldInstallers) {
    foreach ($old in $superseded) {
        Remove-Item -LiteralPath $old.FullName -Force
        Write-Host "  pruned:    $($old.Name)" -ForegroundColor DarkGray
    }
} elseif ($superseded.Count) {
    Write-Host "  kept:      $($superseded.Count) superseded installer(s) (-KeepOldInstallers)" -ForegroundColor DarkGray
}

Write-Host ''
Write-Host "Done. Output: $($setup.FullName)" -ForegroundColor Green
Write-Host ("  size:      {0} MB" -f [math]::Round($setup.Length / 1MB, 1))
Write-Host '  subsystem: GUI'
Write-Host "  version:   $version"
Write-Host "  SHA256:    $((Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash)"
