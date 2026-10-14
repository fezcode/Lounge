<#
.SYNOPSIS
  Read, set, or bump the Lounge release version everywhere it lives, then verify
  those places agree.

.DESCRIPTION
  The version appears in Lounge.csproj (<Version>), Agent/LoungeAgent.csproj
  (<Version>), Core/Product.cs (the Version constant the app and agent print), and
  in forge.toml (the [app] version, the wizard strings that name the version, and
  the registry Version data). This keeps them in lockstep so a release cannot ship
  mismatched numbers.

.EXAMPLE
  .\version.ps1                  # report the current version and consistency
  .\version.ps1 -Set 0.2.0       # set everywhere, then verify
  .\version.ps1 -Bump patch      # 0.1.0 -> 0.1.1 everywhere, then verify
  .\version.ps1 -Bump minor -DryRun
#>
param(
    [string] $Set,
    [ValidateSet('patch', 'minor', 'major')]
    [string] $Bump,
    [switch] $DryRun,
    [string] $RepoRoot = $PSScriptRoot
)

# ---- pure version helpers ---------------------------------------------------

function Parse-SemVer([string] $text) {
    if ($text -notmatch '^\s*(\d+)\.(\d+)\.(\d+)\s*$') {
        throw "Not a valid x.y.z version: '$text'"
    }
    [pscustomobject]@{ Major = [int]$Matches[1]; Minor = [int]$Matches[2]; Patch = [int]$Matches[3] }
}

function Format-SemVer($v) { "{0}.{1}.{2}" -f $v.Major, $v.Minor, $v.Patch }

function Step-SemVer([string] $current, [string] $kind) {
    $v = Parse-SemVer $current
    switch ($kind) {
        'patch' { $v.Patch++ }
        'minor' { $v.Minor++; $v.Patch = 0 }
        'major' { $v.Major++; $v.Minor = 0; $v.Patch = 0 }
        default { throw "Unknown bump kind: '$kind'" }
    }
    Format-SemVer $v
}

# ---- the places a version lives ---------------------------------------------

function Get-Places([string] $root) {
    [ordered]@{
        Csproj  = Join-Path $root 'Lounge.csproj'
        Agent   = Join-Path $root 'Agent/LoungeAgent.csproj'
        Product = Join-Path $root 'Core/Product.cs'
        Forge   = Join-Path $root 'forge.toml'
    }
}

function Read-Utf8([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing file: $path" }
    [System.IO.File]::ReadAllText($path)
}

function Write-Utf8([string] $path, [string] $text) {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

function Read-Versions([string] $root) {
    $p = Get-Places $root
    $csproj = Read-Utf8 $p.Csproj
    $agent = Read-Utf8 $p.Agent
    $product = Read-Utf8 $p.Product
    $forge = Read-Utf8 $p.Forge

    [ordered]@{
        'Lounge.csproj <Version>'          = if ($csproj -match '<Version>([^<]+)</Version>') { $Matches[1] } else { $null }
        'Agent/LoungeAgent.csproj <Version>' = if ($agent -match '<Version>([^<]+)</Version>') { $Matches[1] } else { $null }
        'Core/Product.cs Version'          = if ($product -match 'Version\s*=\s*"([^"]+)"') { $Matches[1] } else { $null }
        'forge.toml [app] version'         = if ($forge -match '(?m)^version\s*=\s*"([^"]+)"') { $Matches[1] } else { $null }
        'forge.toml registry Version'      = if ($forge -match '(?ms)value\s*=\s*"Version".*?data\s*=\s*"([^"]+)"') { $Matches[1] } else { $null }
    }
}

function Test-Consistent([string] $root, [string] $expected) {
    $found = Read-Versions $root
    $values = @($found.Values | Where-Object { $_ })
    $target = if ($expected) { $expected } elseif ($values.Count) { $values[0] } else { $null }

    $allOk = $target -and -not ($found.Values | Where-Object { $_ -ne $target })
    [pscustomobject]@{ Found = $found; Target = $target; AllOk = [bool]$allOk }
}

function Set-Version([string] $root, [string] $version) {
    $null = Parse-SemVer $version
    $p = Get-Places $root

    foreach ($proj in @($p.Csproj, $p.Agent)) {
        $text = Read-Utf8 $proj
        $text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$version</Version>")
        Write-Utf8 $proj $text
    }

    $product = Read-Utf8 $p.Product
    $product = [regex]::Replace($product, '(Version\s*=\s*)"[^"]+"', "`${1}`"$version`"")
    Write-Utf8 $p.Product $product

    $forge = Read-Utf8 $p.Forge
    $forge = [regex]::Replace($forge, '(?m)^version(\s*)=\s*"[^"]+"', "version`${1}= `"$version`"")
    # The wizard names the version in its welcome and folder steps.
    $forge = [regex]::Replace($forge, 'Lounge \d+\.\d+\.\d+', "Lounge $version")
    $forge = [regex]::Replace($forge, '(?ms)(value\s*=\s*"Version"\s*\r?\n\s*data\s*=\s*)"[^"]+"', "`${1}`"$version`"")
    Write-Utf8 $p.Forge $forge
}

function Show-Report([string] $root, [string] $expected) {
    $state = Test-Consistent $root $expected
    foreach ($key in $state.Found.Keys) {
        $value = $state.Found[$key]
        $shown = if ($value) { $value } else { '(not found)' }
        $ok = $value -eq $state.Target
        $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
        $colour = if ($ok) { 'DarkGray' } else { 'Red' }
        Write-Host ("  {0} {1,-36} {2}" -f $mark, $key, $shown) -ForegroundColor $colour
    }
    $state
}

# ---- entry point ------------------------------------------------------------
# Dot-sourcing (build.ps1 and installer.ps1 do this) must not run the body.

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'

    if ($Set -and $Bump) { throw 'Pass -Set or -Bump, not both.' }

    $current = (Test-Consistent $RepoRoot $null).Target
    if (-not $current) { throw 'Could not read a current version from any source.' }

    $target = if ($Set) { $Set } elseif ($Bump) { Step-SemVer $current $Bump } else { $current }

    if ($target -ne $current) {
        if ($DryRun) {
            Write-Host "Would set $current -> $target" -ForegroundColor Yellow
            return
        }
        Set-Version $RepoRoot $target
        Write-Host "Set $current -> $target" -ForegroundColor Cyan
    }

    Write-Host "Lounge version $target" -ForegroundColor Cyan
    $state = Show-Report $RepoRoot $target
    if (-not $state.AllOk) { throw 'Versions disagree. Fix the files above before releasing.' }
}
