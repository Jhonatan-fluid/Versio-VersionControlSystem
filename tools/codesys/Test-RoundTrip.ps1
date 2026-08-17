<#
.SYNOPSIS
  Phase 0.5 gate: export -> import -> export, judged per layer.

.DESCRIPTION
  The losslessness proof, run against a real project rather than argued about.

    1. export the pristine project            -> baseline
    2. copy the project to a scratch working copy
    3. import the baseline files into the copy
    4. export the copy                        -> roundtrip
    5. compare baseline against roundtrip, ONE LAYER AT A TIME

  Step 3 is the dangerous direction, so it never touches the original: the copy is what
  gets modified, exactly as the real import path will do.

  WHY STEP 5 IS NOT ONE VERDICT.

  It used to be: Compare-Exports byte-compared the whole tree and printed a single
  `Lossless` result. On a completely healthy project that printed FALSE, every time,
  because import_native legitimately perturbs native XML - it advances UniqueIdGenerator
  counters, reorders collections whose order carries no meaning, and re-mints internal
  reference GUIDs. PHASE0-FINDINGS gate 0.5b measured all three and states outright that
  the fidelity check "cannot be a byte comparison for native objects - it must be
  semantic".

  So the harness contradicted the document it was written to produce. A script that does
  not run gets investigated; a script that runs and cries wolf gets believed, and then
  ignored. The layers have genuinely different fidelity rules, so they get genuinely
  different verdicts:

    textual (.st)          MUST be byte-identical. This is the layer the round trip
                           proves, and the layer an engineer edits.
    export-only (.yaml)    MUST be byte-identical. The import never applies it, so a
                           re-export has nothing to perturb - a difference here means the
                           EXPORT is non-deterministic, which is a real failure.
    native (.xml)          EXPECTED to differ. Byte comparison is the wrong instrument.
                           Reported, never fatal, and explicitly NOT verified here.
    bookkeeping            _manifest.json records native hashes, so it differs whenever
                           the native layer does. Derived, so never fatal.
    added / removed        Fatal in any layer. A file that appeared or vanished is a
                           structural failure, not a serialisation one.

  The native layer's semantic check lives in C#, in
  tests/Versio.Core.Tests/Plc/Codesys/RealRoundTripEquivalenceTests.cs, which runs
  NativeXmlNormalizer over committed fixtures. It is not duplicated here on purpose:
  this repository has already been bitten twice by the same rule existing in both C# and
  python and drifting. Use -UpdateEquivalenceFixtures to refresh that test's inputs from
  a real run.

.PARAMETER MutateFirst
  Edits a comment into an exported .st before importing it. USE IT. Without a deliberate
  change the comparison can pass while the import silently did nothing, and a green
  result that cannot fail is worse than a red one.

.PARAMETER UpdateEquivalenceFixtures
  Copy the small baseline/roundtrip pairs into fixtures/equivalence/, which is where
  RealRoundTripEquivalenceTests reads them from. That test's own remarks have always said
  the fixtures "can be regenerated with tools/codesys/Test-RoundTrip.ps1" - this is the switch
  that makes the claim true. Refuses to run with -MutateFirst, which would bake the probe
  comment into a committed fixture.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Profile,
    # Derived from the script's own location so the tools work from any clone path.
    [string]$WorkRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "phase0\roundtrip"),
    [switch]$MutateFirst,
    [switch]$UpdateEquivalenceFixtures
)

$ErrorActionPreference = "Stop"
$tools = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $tools
$scripts = Join-Path $repoRoot "src\Versio.Scripts.Codesys"

if ($UpdateEquivalenceFixtures -and $MutateFirst) {
    throw ("-UpdateEquivalenceFixtures cannot be combined with -MutateFirst: the probe " +
           "comment would be committed into fixtures/equivalence/baseline/Simulation.st, " +
           "and the C# test asserting that file round-trips byte-identically would then " +
           "be asserting against a test artefact.")
}

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
    -Job @{ schema = 2; project = $copyProject; src = $baselineOut; save = $true;
            plan = (& "$tools\New-ImportPlan.ps1" -Mirror $baselineOut) } -Profile $Profile
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

Write-Host "`n[5/5] comparing baseline against round-trip, per layer" -ForegroundColor Cyan

# Nothing is excluded. _manifest.json is stale by construction under -MutateFirst - the .st
# was edited after the manifest recorded its hash - but it is ALSO expected to differ on
# every run without -MutateFirst, because it records the hashes of native files that
# import_native perturbed. Excluding it hid the second case while explaining only the
# first. It is classified as derived bookkeeping below and reported rather than dropped.
# -Quiet: its banner is a byte verdict, and a red "DIFFERING : 9" printed above a
# considered per-layer answer just reintroduces the alarm that answer exists to remove.
$cmp = & "$tools\Compare-Exports.ps1" -A $baselineOut -B $roundtripOut -Quiet

function Get-Layer([string]$relativePath) {
    $leaf = [System.IO.Path]::GetFileName($relativePath)
    if ($leaf -in @("_manifest.json", "_coverage.md")) { return "bookkeeping" }
    if ($leaf -like "*.libraries.yaml")                { return "export-only" }
    if ($leaf -like "*.st")                            { return "textual" }
    return "native"
}

$byLayer = @{ textual = @(); "export-only" = @(); native = @(); bookkeeping = @() }
foreach ($f in $cmp.Differing) { $byLayer[(Get-Layer $f)] += $f }

$structural = @($cmp.OnlyA) + @($cmp.OnlyB)

Write-Host ""
Write-Host "  LAYER VERDICTS" -ForegroundColor Cyan

# Textual. The layer an engineer actually edits, and the one the round trip proves.
if ($byLayer.textual.Count -eq 0) {
    Write-Host "    textual (.st)        LOSSLESS - byte-identical" -ForegroundColor Green
} else {
    Write-Host "    textual (.st)        FAILED - $($byLayer.textual.Count) file(s) differ" -ForegroundColor Red
    $byLayer.textual | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
}

# Export-only. The import never applies it, so a re-export has nothing to perturb: a
# difference here is the EXPORT being non-deterministic, which is a real failure.
if ($byLayer."export-only".Count -eq 0) {
    Write-Host "    export-only (.yaml)  LOSSLESS - byte-identical" -ForegroundColor Green
} else {
    Write-Host "    export-only (.yaml)  FAILED - $($byLayer.'export-only'.Count) file(s) differ" -ForegroundColor Red
    $byLayer."export-only" | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
}

# Native. Byte comparison is the wrong instrument here and always was.
if ($byLayer.native.Count -eq 0) {
    Write-Host "    native (.xml)        byte-identical (import perturbed nothing)" -ForegroundColor Green
} else {
    Write-Host "    native (.xml)        $($byLayer.native.Count) file(s) differ - EXPECTED, not verified here" -ForegroundColor Yellow
    $byLayer.native | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
    Write-Host "      import_native advances counters, reorders unordered collections and" -ForegroundColor DarkGray
    Write-Host "      re-mints internal GUIDs. Semantic equivalence is asserted in C# by" -ForegroundColor DarkGray
    Write-Host "      RealRoundTripEquivalenceTests - run:" -ForegroundColor DarkGray
    Write-Host "        dotnet test tests\Versio.Core.Tests --filter RealRoundTripEquivalence" -ForegroundColor DarkGray
}

if ($byLayer.bookkeeping.Count -gt 0) {
    Write-Host "    bookkeeping          $($byLayer.bookkeeping.Count) file(s) differ - derived from the above" -ForegroundColor DarkGray
}

# Structure. A file that appeared or vanished is a real failure in any layer.
if ($structural.Count -eq 0) {
    Write-Host "    structure            intact - nothing added or removed" -ForegroundColor Green
} else {
    Write-Host "    structure            FAILED - $($structural.Count) file(s) added or removed" -ForegroundColor Red
    $cmp.OnlyA | ForEach-Object { Write-Host "      lost:  $_" -ForegroundColor Red }
    $cmp.OnlyB | ForEach-Object { Write-Host "      added: $_" -ForegroundColor Red }
}

$probeSurvived = $null
if ($MutateFirst) {
    $rt = Get-ChildItem $roundtripOut -Recurse -Filter *.st |
          Where-Object { $_.Name -notlike "*.gvl.st" } | Select-Object -First 1
    $probeSurvived = [bool]($rt -and ([System.IO.File]::ReadAllText($rt.FullName)).Contains("versio round-trip probe"))
    if ($probeSurvived) {
        Write-Host "    probe                SURVIVED - the harness is not vacuous" -ForegroundColor Green
    } else {
        Write-Host "    probe                LOST - the import did not apply the edit" -ForegroundColor Red
    }
}

# The overall answer. Native and bookkeeping differences are deliberately not part of it.
$pass = ($byLayer.textual.Count -eq 0) -and
        ($byLayer."export-only".Count -eq 0) -and
        ($structural.Count -eq 0) -and
        (-not $MutateFirst -or $probeSurvived)

Write-Host ""
if ($pass) {
    Write-Host "  VERDICT: PASS" -ForegroundColor Green
    if (-not $MutateFirst) {
        Write-Host "  Re-run with -MutateFirst. Without a deliberate change this cannot fail." -ForegroundColor Yellow
    }
} else {
    Write-Host "  VERDICT: FAIL" -ForegroundColor Red
}
Write-Host ""

if ($UpdateEquivalenceFixtures) {
    # Refresh the inputs RealRoundTripEquivalenceTests reads. Its remarks have always said
    # these can be regenerated here; until now nothing in this script did it, so the
    # fixtures were whatever somebody copied by hand in August.
    $equivalence = Join-Path $repoRoot "fixtures\equivalence"
    $updated = 0
    foreach ($side in @(@{ Name = "baseline"; Root = $baselineOut },
                        @{ Name = "roundtrip"; Root = $roundtripOut })) {
        $target = Join-Path $equivalence $side.Name
        New-Item -ItemType Directory -Force -Path $target | Out-Null

        # By name, not by path: the committed fixtures are flat, and the export layout is
        # free to move an object into a different folder without invalidating them.
        foreach ($wanted in Get-ChildItem $target -File) {
            $source = Get-ChildItem $side.Root -Recurse -File |
                      Where-Object { $_.Name -eq $wanted.Name } | Select-Object -First 1
            if ($source) {
                Copy-Item $source.FullName $wanted.FullName -Force
                $updated++
            } else {
                Write-Warning "no $($wanted.Name) in the $($side.Name) export; fixture left as it was"
            }
        }
    }
    Write-Host "  refreshed $updated equivalence fixture(s) under $equivalence" -ForegroundColor Cyan
    Write-Host "  now run: dotnet test tests\Versio.Core.Tests --filter RealRoundTripEquivalence" -ForegroundColor Cyan
    Write-Host ""
}

[pscustomobject]@{
    # Deliberately NOT called "Lossless". One boolean over two layers with different
    # fidelity rules is the defect this replaced.
    Pass              = $pass
    TextualLossless   = ($byLayer.textual.Count -eq 0)
    ExportOnlyLossless = ($byLayer."export-only".Count -eq 0)
    StructureIntact   = ($structural.Count -eq 0)
    ProbeSurvived     = $probeSurvived
    NativeDiffering   = $byLayer.native
    TextualDiffering  = $byLayer.textual
    ImportSummary     = ($r2.Events | Where-Object t -eq 'summary')
    Warnings          = $warned
    WorkRoot          = $WorkRoot
}
