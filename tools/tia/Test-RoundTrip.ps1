# TIA Portal round-trip harness
#
# Close Versio AND TIA Portal before running. The host takes an exclusive Openness
# session, and a running Versio.exe locks its own binaries.
#
# Layered verdict: textual files must match; native XML is expected to differ.
# Never a single "Lossless: False" that collapses those two.
#
# -MutateFirst is mandatory. A round trip that writes nothing would pass vacuously.
# Default -Project is the committed fixture (Git LFS). That writes the committed
# tree. Pass a copy under .versio/local/tia/ to keep the fixture clean.

param(
    [switch]$MutateFirst,
    [string]$Project = "",
    [string]$Mirror = ""
)

$ErrorActionPreference = "Stop"

if (-not $MutateFirst) {
    Write-Error "-MutateFirst is required. A round trip that does not change the mirror is a vacuous pass."
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Project) {
    $committed = Join-Path $root "fixtures\projects\CleaningStation_V20_V21"
    $Project = Get-ChildItem -Path $committed -Filter "*.ap21" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $Project -or -not (Test-Path $Project) -or ((Get-Item $Project).Length -lt 1024)) {
    Write-Error "No real .ap21 fixture. Expected fixtures/projects/CleaningStation_V20_V21/ (git lfs pull). Never write the OneDrive original."
}

$hostExe = @(
    Join-Path $root "src\Versio.Plc.TiaPortal.Host\bin\Debug\net48\Versio.Plc.TiaPortal.Host.exe"
    Join-Path $root "src\Versio.App\bin\Debug\net9.0-windows\tia\Versio.Plc.TiaPortal.Host.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $hostExe) {
    Write-Error "Host executable not built. Build Versio.Plc.TiaPortal.Host first."
}

$api = "C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48"
if (-not (Test-Path $api)) {
    Write-Error "Openness API folder not found: $api"
}

if (-not $Mirror) {
    $Mirror = Join-Path $env:TEMP ("versio-tia-rt-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
}

New-Item -ItemType Directory -Force -Path $Mirror | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding $false

function Write-Job([string]$path, [hashtable]$body) {
    $json = $body | ConvertTo-Json -Depth 12 -Compress:$false
    [System.IO.File]::WriteAllText($path, $json, $utf8)
}

function Invoke-Host([string]$jobPath) {
    & $hostExe $jobPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "host exited $LASTEXITCODE for $jobPath"
    }
}

$jobExport = Join-Path $env:TEMP ("versio-tia-job-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + ".json")
Write-Host "Export 1 -> $Mirror"
Write-Job $jobExport @{
    op = "export"
    schema = 1
    operationId = "rt-export-1"
    project = $Project
    out = $Mirror
    apiFolder = $api
    save = $false
    timeoutSeconds = 1200
}
Invoke-Host $jobExport

$scl = Get-ChildItem -Path $Mirror -Filter "*.scl" -Recurse | Select-Object -First 1
if (-not $scl) { Write-Error "export wrote no .scl — cannot mutate textual" }

$original = [System.IO.File]::ReadAllText($scl.FullName)
$marker = "// versio-mutate " + (Get-Date -Format o)
$mutated = $original.TrimEnd() + "`r`n" + $marker + "`r`n"
[System.IO.File]::WriteAllText($scl.FullName, $mutated, (New-Object System.Text.UTF8Encoding $true))
Write-Host "Mutated $($scl.FullName)"

$rel = $scl.FullName.Substring($Mirror.TrimEnd('\', '/').Length).TrimStart('\', '/').Replace('\', '/')
$jobImport = Join-Path $env:TEMP ("versio-tia-job-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + ".json")
Write-Host "Import mutated $rel"
Write-Job $jobImport @{
    op = "import"
    schema = 1
    operationId = "rt-import"
    project = $Project
    src = $Mirror
    apiFolder = $api
    save = $true
    timeoutSeconds = 1200
    plan = @{
        files = @(@{ path = $rel; action = "ApplyText"; name = [IO.Path]::GetFileNameWithoutExtension($scl.Name) })
        deletions = @()
        renames = @()
    }
}
Invoke-Host $jobImport

$mirror2 = Join-Path $env:TEMP ("versio-tia-rt2-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $mirror2 | Out-Null
$jobExport2 = Join-Path $env:TEMP ("versio-tia-job-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + ".json")
Write-Host "Export 2 -> $mirror2"
Write-Job $jobExport2 @{
    op = "export"
    schema = 1
    operationId = "rt-export-2"
    project = $Project
    out = $mirror2
    apiFolder = $api
    save = $false
    timeoutSeconds = 1200
}
Invoke-Host $jobExport2

$second = Get-ChildItem -Path $mirror2 -Filter $scl.Name -Recurse | Select-Object -First 1
if (-not $second) { Write-Error "re-export did not write $($scl.Name)" }

$after = [System.IO.File]::ReadAllText($second.FullName)
$textualMatch = $after.Contains($marker)
$native1 = @(Get-ChildItem -Path $Mirror -Filter "*.xml" -Recurse)
$native2 = @(Get-ChildItem -Path $mirror2 -Filter "*.xml" -Recurse)

Write-Host "TEXTUAL marker present on re-export: $textualMatch"
Write-Host "NATIVE xml counts: $($native1.Count) then $($native2.Count) (expected to differ; do not fold into Lossless: False)"
if (-not $textualMatch) {
    Write-Host "Layered verdict: Textual: FAIL; Native: expected-churn; Probe: SURVIVED"
    Write-Error "textual layer lost the mutation"
}

Write-Host "Layered verdict: Textual: PASS; Native: expected-churn; Probe: SURVIVED because -MutateFirst was on"
