<#
.SYNOPSIS
  Read-only Openness hardware probe on a COPY of the TIA fixture.

.DESCRIPTION
  Hardware is a coverage skip today because export walks Devices only to find
  PlcSoftware. This dump answers what those discarded DeviceItems actually are:
  article numbers, slots, ports, partners, and whether a write API exists.

  Two untouched runs of the same copy must be SHA-256 identical or the dump
  itself is churn and cannot become a catalog. A third run (-WriteTest) tries
  SetAttribute on Comment and restores it. The project is never saved.

  Close Versio AND TIA Portal first. Never the OneDrive original.
#>
[CmdletBinding()]
param(
    [string]$Project = "",
    [switch]$WriteTest,
    [switch]$SkipCopy
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}

if (-not $Project) {
    $committed = Join-Path $repo "fixtures\projects\CleaningStation_V20_V21"
    $Project = Get-ChildItem -Path $committed -Filter "*.ap21" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $Project -or -not (Test-Path $Project) -or ((Get-Item $Project).Length -lt 1024)) {
    Write-Error "No real .ap21 fixture. Expected fixtures/projects/CleaningStation_V20_V21/ (git lfs pull). Never write the OneDrive original."
}

$held = @(Get-Process -Name "Siemens.Automation.Portal","Portal","TiaPortal" -ErrorAction SilentlyContinue)
if ($held) {
    Write-Error ("Close TIA Portal first. Held: " + ($held.ProcessName -join ", "))
}
$versio = Get-Process -Name "Versio" -ErrorAction SilentlyContinue
if ($versio) {
    Write-Error "Close Versio.exe first; it locks the host binaries."
}

Write-Host "Building TIA host..."
& $dotnet build (Join-Path $repo "src\Versio.Plc.TiaPortal.Host\Versio.Plc.TiaPortal.Host.csproj") --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "host build failed" }

$hostExe = @(
    Join-Path $repo "src\Versio.Plc.TiaPortal.Host\bin\Debug\net48\Versio.Plc.TiaPortal.Host.exe"
    Join-Path $repo "src\Versio.App\bin\Debug\net9.0-windows\tia\Versio.Plc.TiaPortal.Host.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $hostExe) { Write-Error "Host executable not built." }

$api = "C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48"
if (-not (Test-Path $api)) { Write-Error "Openness API folder not found: $api" }

$stamp = [guid]::NewGuid().ToString("N").Substring(0, 8)
$work = Join-Path $env:TEMP ("versio-tia-hwprobe-" + $stamp)
New-Item -ItemType Directory -Force -Path $work | Out-Null

$targetProject = $Project
if (-not $SkipCopy) {
    $copyDir = Join-Path $work "project"
    Write-Host ("Copying fixture to {0}" -f $copyDir)
    New-Item -ItemType Directory -Force -Path $copyDir | Out-Null
    $srcDir = Split-Path -Parent $Project
    & robocopy $srcDir $copyDir /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    $rc = $LASTEXITCODE
    if ($rc -ge 8) { Write-Error "robocopy failed with $rc" }
    $targetProject = Join-Path $copyDir (Split-Path -Leaf $Project)
}

$utf8 = New-Object System.Text.UTF8Encoding $false
function Write-Job([string]$path, [hashtable]$body) {
    $json = $body | ConvertTo-Json -Depth 8 -Compress:$false
    [System.IO.File]::WriteAllText($path, $json, $utf8)
}

function Invoke-HwProbe([string]$outDir, [bool]$doWrite) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $job = Join-Path $work ("job-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + ".json")
    Write-Job $job @{
        op = "hwprobe"
        schema = 1
        operationId = "hwprobe"
        project = $targetProject
        out = $outDir
        apiFolder = $api
        save = $false
        writeTest = $doWrite
        timeoutSeconds = 1200
    }
    Write-Host ("hwprobe -> {0}  writeTest={1}" -f $outDir, $doWrite)
    $log = Join-Path $outDir "host.log"
    $err = Join-Path $outDir "host.err"
    $p = Start-Process -FilePath $hostExe -ArgumentList $job -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError $err
    if ($p.ExitCode -ne 0) {
        Write-Error "host exited $($p.ExitCode) for $outDir"
    }
    $dump = Join-Path $outDir "hardware.json"
    if (-not (Test-Path $dump)) { Write-Error "no hardware.json in $outDir" }
    return $dump
}

function Get-Sha256([string]$path) {
    (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
}

$out1 = Join-Path $work "run1"
$out2 = Join-Path $work "run2"
$dump1 = Invoke-HwProbe $out1 $false
$dump2 = Invoke-HwProbe $out2 $false
$h1 = Get-Sha256 $dump1
$h2 = Get-Sha256 $dump2
Write-Host ("SHA run1 {0}" -f $h1)
Write-Host ("SHA run2 {0}" -f $h2)
if ($h1 -ne $h2) {
    Write-Host "DUMPS DIFFER - the probe itself is churn and cannot become a catalog." -ForegroundColor Red
} else {
    Write-Host "Two untouched dumps are SHA-256 identical." -ForegroundColor Green
}

if ($WriteTest) {
    $out3 = Join-Path $work "write-test"
    $dump3 = Invoke-HwProbe $out3 $true
    Write-Host ("write-test dump: {0}" -f $dump3)
}

Write-Host ("work: {0}" -f $work)
Write-Host ("dump: {0}" -f $dump1)
Get-Content $dump1 -TotalCount 40
