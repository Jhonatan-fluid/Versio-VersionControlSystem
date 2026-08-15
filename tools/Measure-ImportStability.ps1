<#
.SYNOPSIS
  Phase 0 probe P7 of docs/CODESYS-IMPORT-PLAN.md: does an import -> export cycle on an
  UNCHANGED mirror ever settle, or does it churn forever?

.DESCRIPTION
  G10 claims the mirror never stops churning: import_native re-mints UniqueIdGenerator, the
  counter is a high-water mark, so every cycle bumps it again and every cycle leaves a diff.
  If that is true, Phase 5 (keep the existing bytes when the new export is normaliser-
  equivalent) is justified by measurement. If the counter settles after one cycle the way
  Core2 does, Phase 5 shrinks to almost nothing. Nobody has measured it; this measures it.

  ONE mirror directory, re-exported into, which is what the plan's acceptance test asks for -
  "three consecutive import->export cycles on an unchanged mirror must leave it
  byte-identical". Two fresh directories would answer a different and easier question. The
  exporter stages and swaps, so exporting over a populated mirror is its normal path.

    export(pristine) -> mirror -> snap 000
    repeat N times:  import(mirror) -> export -> mirror -> snap 00n, compare with 00n-1

  Nothing edits the mirror between cycles. Every difference found is therefore produced by
  the round trip itself and by nothing else, which is the whole point of holding it still.

  Runs entirely on a scratch COPY of the project. P7 must not share a copy with the other
  Phase 0 probes: rename, move, remove and create_pou all advance the same counters this
  measures, so a probe run before it would be indistinguishable from the churn.

.PARAMETER Cycles
  How many import->export rounds. Three is what the plan asks for and is the minimum that
  can tell "settles" from "climbs" - one cycle shows a change, two show it repeating, three
  show it is not a two-state flip-flop.

.EXAMPLE
  tools\Measure-ImportStability.ps1
#>
[CmdletBinding()]
param(
    [string]$Project = (Join-Path (Split-Path -Parent $PSScriptRoot) "fixtures\projects\RefrigeratorControl.project"),
    [string]$Profile = "CODESYS V3.5 SP22 Patch 2",
    [int]$Cycles = 3,
    [string]$WorkRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "phase0\p7"),
    [string]$Codesys = "C:\Program Files\CODESYS 3.5.22.20\CODESYS\Common\CODESYS.exe"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$tools    = $PSScriptRoot
$scripts  = Join-Path $repoRoot "src\Versio.Scripts.Codesys"

if (-not (Test-Path $Project)) { throw "project not found: $Project" }

# A stale work root is the one way this probe can lie: a mirror left by an earlier run would
# make cycle 1 compare against somebody else's export. Rebuilt from nothing every time.
if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

$copyProject = Join-Path $WorkRoot "work.project"
$mirror      = Join-Path $WorkRoot "mirror"
$snapRoot    = Join-Path $WorkRoot "snapshots"
New-Item -ItemType Directory -Force -Path $snapRoot | Out-Null

function Get-Layer([string]$relativePath) {
    # Same four layers Test-RoundTrip.ps1 judges by, and deliberately the same words. A
    # second vocabulary for the same distinction is how two harnesses start disagreeing
    # about what "expected" means.
    $leaf = [System.IO.Path]::GetFileName($relativePath)
    if ($leaf -in @("_manifest.json", "_coverage.md")) { return "bookkeeping" }
    if ($leaf -like "*.libraries.yaml")                { return "export-only" }
    if ($leaf -like "*.st")                            { return "textual" }
    return "native"
}

function Get-Counters([string]$root) {
    <#
      Every UniqueIdGenerator in the tree, by file. This is the actual measurement. "Nine
      files differ" cannot tell a counter that climbed 44 -> 47 -> 50 from one that went
      44 -> 47 and stopped while something else moved, and those two answers size Phase 5
      very differently.
    #>
    # Every value in the file, joined, NOT the last one. The fixture happens to carry exactly
    # one counter per file, so keeping only the last was indistinguishable from correct here
    # and would have silently dropped the others on any project that carries two.
    $found = @{}
    Get-ChildItem $root -Recurse -File -Filter *.xml | ForEach-Object {
        $rel  = $_.FullName.Substring($root.Length).TrimStart('\')
        $text = [System.IO.File]::ReadAllText($_.FullName)
        $vals = @([regex]::Matches($text, 'UniqueIdGenerator"[^>]*>(\d+)<') |
                  ForEach-Object { $_.Groups[1].Value })
        if ($vals.Count -gt 0) { $found[$rel] = ($vals -join ",") }
    }
    $found
}

function Save-Snapshot([string]$label) {
    $dest = Join-Path $snapRoot $label
    Copy-Item $mirror $dest -Recurse -Force
    $dest
}

Write-Host ""
Write-Host "P7 - import/export stability over $Cycles cycles" -ForegroundColor Cyan
Write-Host "  project : $Project"
Write-Host "  work    : $WorkRoot"

Write-Host "`n[setup] copying the project to a scratch working copy" -ForegroundColor Cyan
Copy-Item $Project $copyProject -Force

Write-Host "`n[cycle 0] exporting the pristine copy" -ForegroundColor Cyan
$r = & "$tools\Run-Codesys.ps1" -Script "$scripts\versio_export.py" `
    -Job @{ project = $copyProject; out = $mirror; bindingId = "p7" } -Profile $Profile -Codesys $Codesys
if ($r.ExitCode -ne 0) { throw "baseline export failed (exit $($r.ExitCode))" }
($r.Events | Where-Object t -eq 'summary') | ConvertTo-Json -Compress | Write-Host

$snaps    = @(Save-Snapshot "000")
$counters = @(Get-Counters $snaps[0])
$results  = @()

for ($n = 1; $n -le $Cycles; $n++) {
    Write-Host "`n[cycle $n] importing the mirror back into the copy" -ForegroundColor Cyan
    $ri = & "$tools\Run-Codesys.ps1" -Script "$scripts\versio_import.py" `
        -Job @{ schema = 2; project = $copyProject; src = $mirror; save = $true;
                plan = (& "$tools\New-ImportPlan.ps1" -Mirror $mirror) } -Profile $Profile -Codesys $Codesys
    if ($ri.ExitCode -ne 0) { throw "cycle $n import failed (exit $($ri.ExitCode))" }
    ($ri.Events | Where-Object t -eq 'summary') | ConvertTo-Json -Compress | Write-Host
    $warned = $ri.Events | Where-Object t -eq 'warning'
    if ($warned) {
        Write-Host "  warnings:" -ForegroundColor Yellow
        $warned | Group-Object code | ForEach-Object { "    {0} x{1}" -f $_.Name, $_.Count } | Write-Host
    }

    Write-Host "[cycle $n] re-exporting over the same mirror" -ForegroundColor Cyan
    $re = & "$tools\Run-Codesys.ps1" -Script "$scripts\versio_export.py" `
        -Job @{ project = $copyProject; out = $mirror; bindingId = "p7" } -Profile $Profile -Codesys $Codesys
    if ($re.ExitCode -ne 0) { throw "cycle $n export failed (exit $($re.ExitCode))" }

    $label   = "{0:D3}" -f $n
    $snaps  += Save-Snapshot $label
    $counters += ,(Get-Counters $snaps[$n])

    $cmp = & "$tools\Compare-Exports.ps1" -A $snaps[$n - 1] -B $snaps[$n] -Quiet
    $byLayer = @{ textual = @(); "export-only" = @(); native = @(); bookkeeping = @() }
    foreach ($f in $cmp.Differing) { $byLayer[(Get-Layer $f)] += $f }
    $structural = @($cmp.OnlyA) + @($cmp.OnlyB)

    $results += [pscustomobject]@{
        Cycle       = $n
        Textual     = $byLayer.textual.Count
        ExportOnly  = $byLayer."export-only".Count
        Native      = $byLayer.native.Count
        Bookkeeping = $byLayer.bookkeeping.Count
        Structural  = $structural.Count
        NativeFiles = $byLayer.native
    }

    Write-Host ("  vs cycle {0}: textual {1}, export-only {2}, native {3}, bookkeeping {4}, structural {5}" -f `
        ($n - 1), $byLayer.textual.Count, $byLayer."export-only".Count, `
        $byLayer.native.Count, $byLayer.bookkeeping.Count, $structural.Count)
    $byLayer.textual | ForEach-Object { Write-Host "    textual differs: $_" -ForegroundColor Red }
    $structural      | ForEach-Object { Write-Host "    structural:      $_" -ForegroundColor Red }
}

Write-Host "`n  PER-CYCLE DIFFERENCES" -ForegroundColor Cyan
$results | Format-Table Cycle, Textual, ExportOnly, Native, Bookkeeping, Structural -AutoSize | Out-String | Write-Host

Write-Host "  UNIQUEIDGENERATOR, BY CYCLE" -ForegroundColor Cyan
$allFiles = $counters | ForEach-Object { $_.Keys } | Sort-Object -Unique
foreach ($f in $allFiles) {
    $series = $counters | ForEach-Object { if ($_.ContainsKey($f)) { $_[$f] } else { "-" } }
    $trend  = if (($series | Select-Object -Unique).Count -eq 1) { "flat" }
              elseif ($series[-1] -eq $series[-2])              { "settled" }
              else                                              { "CLIMBING" }
    $colour = if ($trend -eq "CLIMBING") { "Red" } elseif ($trend -eq "settled") { "Yellow" } else { "Green" }
    Write-Host ("    {0,-9} {1,-58} {2}" -f $trend, $f, ($series -join " -> ")) -ForegroundColor $colour
}

# The verdict P7 exists to give. Phase 5 is written to stop the mirror churning; if the last
# two cycles are byte-identical it has already stopped on its own.
$last = $results[-1]
$settled = ($last.Textual -eq 0) -and ($last.ExportOnly -eq 0) -and ($last.Native -eq 0) -and
           ($last.Bookkeeping -eq 0) -and ($last.Structural -eq 0)

Write-Host ""
if ($settled) {
    Write-Host "  P7: SETTLES - the final cycle changed nothing. Phase 5 shrinks." -ForegroundColor Green
} else {
    Write-Host "  P7: CHURNS - the mirror still differs after $Cycles cycles. Phase 5 is justified." -ForegroundColor Yellow
}
Write-Host "  snapshots kept in $snapRoot for anyone who wants to diff them by hand."
Write-Host ""

[pscustomobject]@{
    Settled  = $settled
    Cycles   = $results
    Counters = $counters
    Snapshots = $snaps
}
