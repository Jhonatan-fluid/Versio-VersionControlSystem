#Requires -Version 5.1
<#
.SYNOPSIS
    Publish a self-contained Versio payload and pack Setup.exe plus a per-machine MSI.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Repo,

    [Parameter(Mandatory = $true)]
    [string] $Dotnet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Dotnet {
    param([string[]] $Arguments)
    Write-Host ("dotnet " + ($Arguments -join ' '))
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed (exit $LASTEXITCODE): $($Arguments -join ' ')"
    }
}

function Get-VersioVersion {
    $props = Join-Path $Repo 'Directory.Build.props'
    $text = Get-Content -LiteralPath $props -Raw -Encoding UTF8
    if ($text -notmatch '<VersioVersion>([^<]+)</VersioVersion>') {
        throw "Directory.Build.props has no VersioVersion"
    }
    return $Matches[1].Trim()
}

$fetch = Join-Path $Repo 'tools\pack\Fetch-Deps.ps1'
& $fetch -Repo $Repo -Dotnet $Dotnet

$version = Get-VersioVersion
$publish = Join-Path $Repo 'artifacts\publish'
$releases = Join-Path $Repo 'artifacts\releases'
$icon = Join-Path $Repo 'assets\versio.ico'

if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publish, $releases | Out-Null

$appProj = Join-Path $Repo 'src\Versio.App\Versio.App.csproj'
$tiaProj = Join-Path $Repo 'src\Versio.Plc.TiaPortal.Host\Versio.Plc.TiaPortal.Host.csproj'
$twinProj = Join-Path $Repo 'src\Versio.Plc.TwinCat.Host\Versio.Plc.TwinCat.Host.csproj'

Write-Host "Publishing Versio.App (self-contained win-x64)..."
# ReadyToRun pre-compiles to native at publish, which is startup time a desktop app pays
# on every cold open. Trimming and AOT stay off - WPF does not support them (plan 13.2) -
# but R2R is the supported middle: bigger managed images, less first-launch JIT.
Invoke-Dotnet @(
    'publish', $appProj,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $publish,
    '-p:PublishTrimmed=false',
    '-p:PublishAot=false',
    '-p:PublishSingleFile=false',
    '-p:PublishReadyToRun=true'
)

Write-Host "Publishing TwinCAT host (self-contained win-x64)..."
# R2R here too: the host is spawned per operation, so its cold start is paid on every
# TwinCAT export and import, not once per day.
$twinOut = Join-Path $publish 'twincat'
if (Test-Path -LiteralPath $twinOut) {
    Remove-Item -LiteralPath $twinOut -Recurse -Force
}
Invoke-Dotnet @(
    'publish', $twinProj,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $twinOut,
    '-p:PublishTrimmed=false',
    '-p:PublishAot=false',
    '-p:PublishSingleFile=false',
    '-p:PublishReadyToRun=true'
)

Write-Host "Building TIA host (net48)..."
# Into a fresh folder with -o, never a copy of bin\Release\net48: bin is incremental and
# nothing ever cleans it, so a renamed assembly or a dropped dependency lingers there from
# old builds and would ship in every installer from then on. The app and the TwinCAT host
# already publish into wiped folders; this is the same rule for the one net48 payload.
$tiaOut = Join-Path $publish 'tia'
if (Test-Path -LiteralPath $tiaOut) {
    Remove-Item -LiteralPath $tiaOut -Recurse -Force
}
Invoke-Dotnet @('build', $tiaProj, '-c', 'Release', '-o', $tiaOut)

$manifest = Get-Content -LiteralPath (Join-Path $Repo 'third_party\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$gitDep = $manifest.dependencies | Where-Object { $_.id -eq 'PortableGit' }
$sfx = Join-Path $Repo "third_party\cache\$($gitDep.filename)"
$gitOut = Join-Path $publish 'git'
if (Test-Path -LiteralPath $gitOut) {
    Remove-Item -LiteralPath $gitOut -Recurse -Force
}
Write-Host "Extracting PortableGit $($gitDep.version)..."
# The 7-Zip SFX is a GUI stub. PowerShell's call operator can return before extraction
# finishes, and -help opens a window that never exits. cmd /c with -y -o waits for the
# unpack; we still poll for git.exe because some builds spawn a child extractor.
$extractCmd = "`"$sfx`" -y `"-o$gitOut`""
cmd.exe /c $extractCmd
if ($LASTEXITCODE -ne 0) {
    throw "PortableGit SFX extract failed (exit $LASTEXITCODE)"
}

$gitExe = Join-Path $gitOut 'cmd\git.exe'
$deadline = (Get-Date).AddMinutes(2)
while (-not (Test-Path -LiteralPath $gitExe) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
}
if (-not (Test-Path -LiteralPath $gitExe)) {
    throw "PortableGit extract did not produce cmd\git.exe"
}

# Both existence polls run before anything from the payload is EXECUTED. git.exe lands
# early in the extraction; running `git lfs version` or the config pin against a tree the
# extractor is still writing would fail a perfectly good pack, and GCM is among the last
# files, so its appearance is the better "extraction finished" signal.
$gcm = $null
while ($null -eq $gcm -and (Get-Date) -lt $deadline) {
    $gcm = Get-ChildItem -LiteralPath $gitOut -Recurse -Filter 'git-credential-manager.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $gcm) {
        Start-Sleep -Seconds 1
    }
}
if ($null -eq $gcm) {
    throw "PortableGit extract has no git-credential-manager.exe"
}
Write-Host "GCM at $($gcm.FullName)"

Write-Host "Confirming git-lfs..."
& $gitExe 'lfs' 'version'
if ($LASTEXITCODE -ne 0) {
    throw "bundled git lfs version failed - this archive is not PortableGit"
}

# PortableGit's first-run helper is helper-selector (a GUI). GIT_TERMINAL_PROMPT=0
# does not suppress it, and "Always use this" cannot persist under Program Files.
# Pin manager into this copy's system gitconfig so Sign in is GCM, not the picker.
Write-Host "Pinning credential.helper=manager..."
& $gitExe config --system --unset-all credential.helper
& $gitExe config --system credential.helper manager
if ($LASTEXITCODE -ne 0) {
    throw "could not pin credential.helper=manager on bundled git"
}
$pinnedHelper = (& $gitExe config --system --get credential.helper | Out-String).Trim()
if ($pinnedHelper -ne 'manager') {
    throw "bundled git credential.helper is '$pinnedHelper', expected manager"
}

Copy-Item -LiteralPath (Join-Path $Repo 'third_party\THIRD-PARTY-NOTICES.txt') `
    -Destination (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') -Force

$required = @(
    (Join-Path $publish 'Versio.exe'),
    (Join-Path $publish 'tia\Versio.Plc.TiaPortal.Host.exe'),
    (Join-Path $publish 'twincat\Versio.Plc.TwinCat.Host.exe'),
    $gitExe,

    # GPL-2.0 compliance travels with the payload: distributing PortableGit's binaries
    # requires distributing its license text, and the notices file points at exactly this
    # path. If a future PortableGit moves it, this gate makes that a failed pack rather
    # than a quiet license violation.
    (Join-Path $gitOut 'LICENSE.txt')
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "payload incomplete: missing $path"
    }
}

$vpk = Join-Path $Repo 'tools\pack\vpk\vpk.exe'
$packStarted = Get-Date
Write-Host "Packing Setup.exe and per-machine MSI (version $version)..."
# Rebuilds of the same VersioVersion are expected until that number moves.
# --legacyConsole (-x) turns the overwrite question off; --yes (-y) is the
# answer. --yes alone still draws the prompt and waits for a timeout.
# Global flags belong before `pack` — System.CommandLine parses them on vpk itself.
& $vpk --legacyConsole --yes --skip-updates pack `
    --packId Versio `
    --packVersion $version `
    --packTitle Versio `
    --packAuthors Versio `
    --packDir $publish `
    --mainExe Versio.exe `
    --icon $icon `
    --msi `
    --instLocation PerMachine `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed (exit $LASTEXITCODE)"
}

# Written THIS run, not merely present: the releases folder is never cleaned, so an
# existence check would flash green on artifacts an earlier pack left behind.
$setup = Get-ChildItem -LiteralPath $releases -Filter '*Setup.exe' |
    Where-Object { $_.LastWriteTime -ge $packStarted } | Select-Object -First 1
$msi = Get-ChildItem -LiteralPath $releases -Filter '*.msi' |
    Where-Object { $_.LastWriteTime -ge $packStarted } | Select-Object -First 1
if ($null -eq $setup -or $null -eq $msi) {
    throw "vpk pack did not write both Setup.exe and an MSI to $releases this run"
}

Write-Host "PACKED"
Write-Host "  $($setup.FullName)"
Write-Host "  $($msi.FullName)"
