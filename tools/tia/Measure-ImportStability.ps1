# P7 SETTLES — second import of an unchanged TIA mirror.
#
# Close Versio AND TIA Portal first.
# A project that keeps changing under a no-op import never settles; that is the
# measurement, not a single Lossless flag.

param(
    [string]$Project = "",
    [int]$Cycles = 3
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $Project) {
    $committed = Join-Path $root "fixtures\projects\CleaningStation_V20_V21"
    $Project = Get-ChildItem -Path $committed -Filter "*.ap21" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $Project -or -not (Test-Path $Project) -or ((Get-Item $Project).Length -lt 1024)) {
    Write-Error "No real .ap21 fixture under fixtures/projects/CleaningStation_V20_V21/ (git lfs pull)."
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

$utf8 = New-Object System.Text.UTF8Encoding $false

function Write-Job([string]$path, [hashtable]$body) {
    [System.IO.File]::WriteAllText($path, ($body | ConvertTo-Json -Depth 12), $utf8)
}

function Invoke-Host([string]$jobPath) {
    & $hostExe $jobPath
    if ($LASTEXITCODE -ne 0) { Write-Error "host exited $LASTEXITCODE" }
}

function Get-TextualFingerprint([string]$dir) {
    $files = Get-ChildItem -Path $dir -Include *.scl,*.udt,*.s7dcl -Recurse | Sort-Object FullName
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in $files) {
        [void]$sb.AppendLine($f.Name)
        [void]$sb.AppendLine([System.IO.File]::ReadAllText($f.FullName))
    }
    return $sb.ToString()
}

$previous = $null
for ($i = 1; $i -le $Cycles; $i++) {
    $mirror = Join-Path $env:TEMP ("versio-tia-p7-" + $i + "-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $mirror | Out-Null
    $job = Join-Path $env:TEMP ("versio-tia-p7-job-" + $i + ".json")
    Write-Host "Cycle $i export -> $mirror"
    Write-Job $job @{
        op = "export"
        schema = 1
        operationId = "p7-$i"
        project = $Project
        out = $mirror
        apiFolder = $api
        save = $false
        timeoutSeconds = 1200
    }
    Invoke-Host $job

    if ($i -gt 1) {
        $jobImport = Join-Path $env:TEMP ("versio-tia-p7-import-" + $i + ".json")
        Write-Host "Cycle $i no-op import (empty apply plan)"
        Write-Job $jobImport @{
            op = "import"
            schema = 1
            operationId = "p7-import-$i"
            project = $Project
            src = $mirror
            apiFolder = $api
            save = $true
            timeoutSeconds = 1200
            plan = @{
                files = @(@{ path = "_manifest.json"; action = "Skip"; skip = "Bookkeeping" })
                deletions = @()
                renames = @()
            }
        }
        Invoke-Host $jobImport
    }

    $fp = Get-TextualFingerprint $mirror
    if ($null -ne $previous -and $fp -ne $previous) {
        Write-Host "P7: CHURNS on textual files at cycle $i"
        Write-Error "textual layer did not settle"
    }
    $previous = $fp
}

Write-Host "P7: SETTLES on textual files after $Cycles cycles"
Write-Host "Native XML is not part of this verdict."
