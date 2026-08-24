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
Invoke-Dotnet @(
    'publish', $appProj,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $publish,
    '-p:PublishTrimmed=false',
    '-p:PublishAot=false',
    '-p:PublishSingleFile=false'
)

Write-Host "Publishing TwinCAT host (self-contained win-x64)..."
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
    '-p:PublishSingleFile=false'
)

Write-Host "Building TIA host (net48)..."
Invoke-Dotnet @('build', $tiaProj, '-c', 'Release')
$tiaSrc = Join-Path $Repo 'src\Versio.Plc.TiaPortal.Host\bin\Release\net48'
$tiaOut = Join-Path $publish 'tia'
New-Item -ItemType Directory -Force -Path $tiaOut | Out-Null
Copy-Item -Path (Join-Path $tiaSrc '*') -Destination $tiaOut -Recurse -Force

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

Write-Host "Confirming git-lfs..."
& $gitExe 'lfs' 'version'
if ($LASTEXITCODE -ne 0) {
    throw "bundled git lfs version failed - this archive is not PortableGit"
}

$gcm = Get-ChildItem -LiteralPath $gitOut -Recurse -Filter 'git-credential-manager.exe' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $gcm) {
    throw "PortableGit extract has no git-credential-manager.exe"
}
Write-Host "GCM at $($gcm.FullName)"

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
    $gitExe
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "payload incomplete: missing $path"
    }
}

$vpk = Join-Path $Repo 'tools\pack\vpk\vpk.exe'
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

$setup = Get-ChildItem -LiteralPath $releases -Filter '*Setup.exe' | Select-Object -First 1
$msi = Get-ChildItem -LiteralPath $releases -Filter '*.msi' | Select-Object -First 1
if ($null -eq $setup -or $null -eq $msi) {
    throw "vpk pack did not produce both Setup.exe and an MSI in $releases"
}

Write-Host "PACKED"
Write-Host "  $($setup.FullName)"
Write-Host "  $($msi.FullName)"
