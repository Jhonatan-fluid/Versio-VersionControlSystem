<#
.SYNOPSIS
  Read-only probe of ScriptDeviceObject / I/O on a COPY of RefrigeratorControl.

.DESCRIPTION
  Device YAML was specified in the founding plan (ScriptDeviceObject → parameters,
  connectors) and never measured. Libraries needed this same probe before the
  exporter was written: get_libraries() was the wrong API. Do not design devices.yaml
  until this log exists.

  Does not save. Work copy under phase0/device-probe/. Close Versio and CODESYS first.
#>
[CmdletBinding()]
param(
    [string]$Project = "",
    [string]$Profile = "CODESYS V3.5 SP22 Patch 2",
    [string]$Codesys = "C:\Program Files\CODESYS 3.5.22.20\CODESYS\Common\CODESYS.exe"
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
if ([string]::IsNullOrEmpty($here)) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$tools = $here
$repo = Split-Path -Parent (Split-Path -Parent $here)
if ([string]::IsNullOrEmpty($Project)) {
    $Project = Join-Path $repo "fixtures\projects\RefrigeratorControl.project"
}
$work = Join-Path $repo "phase0\device-probe"
$out = Join-Path $work "probe.json"
$script = Join-Path $tools "versio_probe_devices.py"

if (-not (Test-Path $Project)) { throw "project not found: $Project" }
if (-not (Test-Path $script)) { throw "probe script not found: $script" }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null
$copy = Join-Path $work "probe.project"
Copy-Item $Project $copy -Force

Write-Host "probing device/IO API on a copy (read-only, no save)" -ForegroundColor Cyan
$r = & "$tools\Run-Codesys.ps1" -Script $script `
    -Job @{ project = $copy; out = $out } -Profile $Profile -Codesys $Codesys -TimeoutSec 180

$r.Events | ForEach-Object {
    if ($_.t -eq "probe_result") {
        Write-Host ("  {0}: devices={1} unknownIo={2}" -f $_.title, $_.result.deviceCount, $_.result.unknownIoCount) -ForegroundColor Green
        $_.result | ConvertTo-Json -Depth 6 | Write-Host
    }
    elseif ($_.t -eq "fatal") {
        Write-Host ("  FATAL: {0}" -f $_.msg) -ForegroundColor Red
    }
}

Write-Host ("exit {0} in {1}s  log {2}" -f $r.ExitCode, $r.Seconds, $r.RawLog)
if (Test-Path $out) { Write-Host "report: $out" }
$r
