<#
.SYNOPSIS
  Runs the Phase 0 import-API probes (P0-P3, P5, P6, P8, P9) of docs/CODESYS-IMPORT-PLAN.md,
  each against its own scratch copy of the project.

.DESCRIPTION
  ONE COPY PER PROBE, deliberately. Every probe here mutates: rename, move, remove, create,
  set_priority. Sharing a copy would make each probe's starting state depend on whichever
  probe ran before it, so a failure could not be attributed and a pass could not be trusted.
  It costs one CODESYS cold start each -- about 25 s -- which is the cheapest part of this.

  P3 is the reason this is not negotiable: it removes an object other objects reference, and
  whatever it leaves behind is not a project anything else should be measured against.

  P4 has no group of its own. It asks whether CODESYS can resolve a name that needs
  percent-encoding, which needs such an object to exist first -- so P5 creates them and
  looks them up in the same run.

  P7 is absent: it is the import/export churn measurement and lives in
  Measure-ImportStability.ps1, which has its own loop and its own verdict.

.PARAMETER Probes
  Which to run. Defaults to all of them, P0 first because the others depend on the API
  surface it reports.

.EXAMPLE
  tools\Probe-ImportApi.ps1
  tools\Probe-ImportApi.ps1 -Probes P0,P5
#>
[CmdletBinding()]
param(
    [string]$Project = (Join-Path (Split-Path -Parent $PSScriptRoot) "fixtures\projects\RefrigeratorControl.project"),
    [string]$Profile = "CODESYS V3.5 SP22 Patch 2",
    [string[]]$Probes = @("P0", "P1", "P2", "P3", "P5", "P6", "P8", "P9"),
    [string]$WorkRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "phase0\import-api"),
    [string]$Codesys = "C:\Program Files\CODESYS 3.5.22.20\CODESYS\Common\CODESYS.exe"
)

$ErrorActionPreference = "Stop"
$tools  = $PSScriptRoot
$script = Join-Path $tools "versio_probe_import_api.py"

if (-not (Test-Path $Project)) { throw "project not found: $Project" }
if (-not (Test-Path $script))  { throw "probe script not found: $script" }

if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

$results = @()

foreach ($p in $Probes) {
    $dir = Join-Path $WorkRoot $p
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $copy = Join-Path $dir "probe.project"
    Copy-Item $Project $copy -Force

    Write-Host "`n=== $p ===" -ForegroundColor Cyan
    $r = & "$tools\Run-Codesys.ps1" -Script $script `
        -Job @{ project = $copy; probes = @($p) } -Profile $Profile -Codesys $Codesys

    $done    = @($r.Events | Where-Object t -eq 'probe_result')
    $failed  = @($r.Events | Where-Object t -eq 'probe_failed')
    $fatal   = @($r.Events | Where-Object t -eq 'fatal')

    foreach ($e in $done) {
        Write-Host ("  {0}: {1}" -f $e.id, $e.title) -ForegroundColor Green
        # The whole result, not a summary. These are one-off measurements whose interesting
        # field is not known in advance -- that is what makes them probes rather than tests.
        $e.result | ConvertTo-Json -Depth 12 | Write-Host
    }
    foreach ($e in $failed) {
        Write-Host ("  {0} FAILED: {1}" -f $e.id, $e.error) -ForegroundColor Red
        Write-Host $e.trace -ForegroundColor DarkGray
    }
    foreach ($e in $fatal) {
        Write-Host ("  FATAL: {0}" -f $e.msg) -ForegroundColor Red
        Write-Host $e.trace -ForegroundColor DarkGray
    }

    $results += [pscustomobject]@{
        Probe    = $p
        ExitCode = $r.ExitCode
        Seconds  = $r.Seconds
        Results  = $done.Count
        Failed   = $failed.Count + $fatal.Count
        RawLog   = $r.RawLog
    }
}

Write-Host "`n  SUMMARY" -ForegroundColor Cyan
$results | Format-Table Probe, ExitCode, Seconds, Results, Failed -AutoSize | Out-String | Write-Host
Write-Host "  scratch copies and raw logs kept under $WorkRoot and phase0\logs"
$results
