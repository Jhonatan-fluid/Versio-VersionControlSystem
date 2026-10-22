<#
.SYNOPSIS
  Read-only probe of library-reference parameters on a COPY of a CODESYS project.

.DESCRIPTION
  Asks whether ScriptEngine's ref.parameters reports the resolved library's defaults or
  only what the project stores. See versio_probe_library_params.py for the question and
  why it came up (2026-10-16, MAX_CLIENTS on VisuElemXYChart).

  Does not save. Works on a copy under phase0/library-param-probe/. Close CODESYS first.
#>
[CmdletBinding()]
param(
    [string]$Project = "",
    [string]$Profile = "CODESYS V3.5 SP22 Patch 4",
    [string]$Codesys = "C:\Program Files\CODESYS 3.5.22.40\CODESYS\Common\CODESYS.exe"
)

$ErrorActionPreference = "Stop"
$tools = $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $tools)
if ([string]::IsNullOrEmpty($Project)) {
    $Project = Join-Path $repo "fixtures\projects\RefrigeratorControl.project"
}
$work = Join-Path $repo "phase0\library-param-probe"
$out = Join-Path $work "probe.json"
$script = Join-Path $tools "versio_probe_library_params.py"

if (-not (Test-Path $Project)) { throw "project not found: $Project" }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null
$copy = Join-Path $work "probe.project"
Copy-Item $Project $copy -Force

Write-Host "probing library parameters on a copy (read-only, no save)" -ForegroundColor Cyan
$r = & "$tools\Run-Codesys.ps1" -Script $script `
    -Job @{ project = $copy; out = $out } -Profile $Profile -Codesys $Codesys -TimeoutSec 300

Write-Host ("exit {0} in {1}s  log {2}" -f $r.ExitCode, $r.Seconds, $r.RawLog)
if (Test-Path $out) { Write-Host "report: $out" }
$r
