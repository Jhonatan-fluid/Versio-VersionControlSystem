<#
.SYNOPSIS
  Phase 0.5 gate: export -> import -> export must produce an identical tree.

.DESCRIPTION
  The losslessness proof, run against a real project rather than argued about.

    1. export the pristine project            -> baseline
    2. copy the project to a scratch working copy
    3. import the baseline files into the copy
    4. export the copy                        -> roundtrip
    5. byte-compare baseline vs roundtrip

  Step 3 is the dangerous direction, so it never touches the original: the copy is what
  gets modified, exactly as the real import path will do.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Profile,
    # Derived from the script's own location so the tools work from any clone path.
    [string]$WorkRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "phase0\roundtrip"),
    [switch]$MutateFirst
)

$ErrorActionPreference = "Stop"
$tools = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $tools
$scripts = Join-Path $repoRoot "src\Versio.Scripts.Codesys"

if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

$baselineOut = Join-Path $WorkRoot "baseline"
$copyProject = Join-Path $WorkRoot ([System.IO.Path]::GetFileName($Project))
$roundtripOut = Join-Path $WorkRoot "roundtrip"

Write-Host "`n[1/5] exporting the pristine project" -ForegroundColor Cyan
$r1 = & "$tools\Run-Codesys.ps1" -Script "$scripts\versio_export.py" `
    -Job @{ project = $Project; out = $baselineOut; bindingId = "rt" } -Profile $Profile
if ($r1.ExitCode -ne 0) { throw "baseline export failed (exit $($r1.ExitCode))" }
($r1.Events | Where-Object t -eq 'summary') | ConvertTo-Json -Compress | Write-Host

Write-Host "`n[2/5] copying the project to a scratch working copy" -ForegroundColor Cyan
Copy-Item $Project $copyProject -Force
Write-Host "  $copyProject"

if ($MutateFirst) {
    # Prove the harness can actually SEE a change - otherwise a comparison that always
    # passes tells you nothing. Edits one .st file before importing.
    $target = Get-ChildItem $baselineOut -Recurse -Filter *.st |
              Where-Object { $_.Name -notlike "*.gvl.st" } | Select-Object -First 1
    if ($target) {
        Write-Host "  MUTATING $($target.Name) to verify the harness detects change" -ForegroundColor Yellow
        $t = [System.IO.File]::ReadAllText($target.FullName)
        $t = $t -replace "// --- BEGIN IMPLEMENTATION ---",
                         "// --- BEGIN IMPLEMENTATION ---`n`n// versio round-trip probe"
        [System.IO.File]::WriteAllText($target.FullName, $t,
            (New-Object System.Text.UTF8Encoding $false))
    }
}

Write-Host "`n[3/5] importing the exported files into the copy" -ForegroundColor Cyan
$r2 = & "$tools\Run-Codesys.ps1" -Script "$scripts\versio_import.py" `
    -Job @{ project = $copyProject; src = $baselineOut; save = $true } -Profile $Profile
if ($r2.ExitCode -ne 0) { throw "import failed (exit $($r2.ExitCode))" }
($r2.Events | Where-Object t -eq 'summary') | ConvertTo-Json -Compress | Write-Host
$warned = $r2.Events | Where-Object t -eq 'warning'
if ($warned) {
    Write-Host "  warnings:" -ForegroundColor Yellow
    $warned | Group-Object code | ForEach-Object { "    {0} x{1}" -f $_.Name, $_.Count } | Write-Host
}

Write-Host "`n[4/5] re-exporting the imported copy" -ForegroundColor Cyan
$r3 = & "$tools\Run-Codesys.ps1" -Script "$scripts\versio_export.py" `
    -Job @{ project = $copyProject; out = $roundtripOut; bindingId = "rt" } -Profile $Profile
if ($r3.ExitCode -ne 0) { throw "round-trip export failed (exit $($r3.ExitCode))" }
($r3.Events | Where-Object t -eq 'summary') | ConvertTo-Json -Compress | Write-Host

Write-Host "`n[5/5] comparing baseline against round-trip" -ForegroundColor Cyan
# Under -MutateFirst the baseline manifest is stale BY CONSTRUCTION: the .st file was
# edited after the manifest recorded its hash. Excluding it keeps the signal clean; the
# probe assertion below is what actually proves the mutation survived.
$exclude = if ($MutateFirst) { @("_manifest.json") } else { @() }
$cmp = & "$tools\Compare-Exports.ps1" -A $baselineOut -B $roundtripOut -ShowFirstDiff -Exclude $exclude

if ($MutateFirst) {
    $rt = Get-ChildItem $roundtripOut -Recurse -Filter *.st |
          Where-Object { $_.Name -notlike "*.gvl.st" } | Select-Object -First 1
    $survived = $rt -and ([System.IO.File]::ReadAllText($rt.FullName)).Contains("versio round-trip probe")
    if ($survived) {
        Write-Host "  PROBE SURVIVED the round trip - the harness is not vacuous" -ForegroundColor Green
    } else {
        Write-Host "  PROBE LOST - import did not apply the edit" -ForegroundColor Red
    }
}

[pscustomobject]@{
    Lossless      = $cmp.Deterministic
    Differing     = $cmp.Differing
    ImportSummary = ($r2.Events | Where-Object t -eq 'summary')
    Warnings      = $warned
    WorkRoot      = $WorkRoot
}
