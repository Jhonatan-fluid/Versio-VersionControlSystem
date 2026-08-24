#Requires -Version 5.1
<#
.SYNOPSIS
    Download pinned pack-time dependencies. Hash mismatch fails the pack.

.DESCRIPTION
    PortableGit is cached under third_party/cache/. vpk is installed to
    third_party/cache/vpk/ so a global tool on the machine cannot silently
    drift - beside the other fetched pack dependency, and out of the source
    tree, where its ~450 files kept turning up in searches.
    Siemens, CODESYS and TwinCAT are never fetched.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Repo,

    [Parameter(Mandatory = $true)]
    [string] $Dotnet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$manifestPath = Join-Path $Repo 'third_party\manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "missing $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$cache = Join-Path $Repo 'third_party\cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Get-Dep([string] $Id) {
    $dep = $manifest.dependencies | Where-Object { $_.id -eq $Id }
    if ($null -eq $dep) {
        throw "manifest has no dependency '$Id'"
    }
    return $dep
}

function Assert-Sha256([string] $Path, [string] $Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $want = $Expected.ToLowerInvariant()
    if ($actual -ne $want) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        throw "SHA-256 mismatch for $(Split-Path $Path -Leaf): got $actual expected $want"
    }
}

$git = Get-Dep 'PortableGit'
$gitFile = Join-Path $cache $git.filename
if (Test-Path -LiteralPath $gitFile) {
    Assert-Sha256 $gitFile $git.sha256
    Write-Host "PortableGit cache ok ($($git.version))"
} else {
    Write-Host "Downloading PortableGit $($git.version)..."
    $tmp = "$gitFile.partial"
    & curl.exe -L --fail --retry 3 --retry-all-errors -o $tmp $git.url
    if ($LASTEXITCODE -ne 0) {
        throw "curl failed downloading PortableGit (exit $LASTEXITCODE)"
    }
    Assert-Sha256 $tmp $git.sha256
    Move-Item -LiteralPath $tmp -Destination $gitFile -Force
}

$vpk = Get-Dep 'vpk'
$toolDir = Join-Path $Repo 'third_party\cache\vpk'
New-Item -ItemType Directory -Force -Path $toolDir | Out-Null
$vpkExe = Join-Path $toolDir 'vpk.exe'
$havePinnedVpk = $false
if (Test-Path -LiteralPath $vpkExe) {
    $listed = & $Dotnet tool list --tool-path $toolDir | Out-String
    if ($listed -match ('(?m)^vpk\s+' + [regex]::Escape($vpk.version) + '\b')) {
        $havePinnedVpk = $true
        Write-Host "vpk $($vpk.version) already in third_party\cache\vpk"
    }
}
if (-not $havePinnedVpk) {
    Write-Host "Installing vpk $($vpk.version) into third_party\cache\vpk..."
    & $Dotnet tool update --tool-path $toolDir --version $vpk.version vpk
    if ($LASTEXITCODE -ne 0) {
        & $Dotnet tool install --tool-path $toolDir --version $vpk.version vpk
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet tool install vpk failed (exit $LASTEXITCODE)"
        }
    }
}

if (-not (Test-Path -LiteralPath $vpkExe)) {
    throw "vpk.exe was not installed at $vpkExe"
}

Write-Host "Pack deps ready."
