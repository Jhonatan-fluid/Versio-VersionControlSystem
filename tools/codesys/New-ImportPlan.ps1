<#
.SYNOPSIS
  Builds the schema-2 import plan for a SELF-EXPORTED mirror, from its _manifest.json.

.DESCRIPTION
  versio_import.py executes a plan and nothing else (job schema 2, Phase 1 of
  docs/phase1/IMPORT-PLAN.md) - a planless job is refused before the project is opened.
  The tools in this directory drive the script raw, without LocalCodesysRunner, so they
  need a plan from somewhere, and this is it.

  DELIBERATELY THE SUBSET, not a port of CodesysImportPlanner. These tools only ever
  import a mirror the export just wrote: every file is in the manifest, nothing is a
  create, nothing is a stray, no object is a sub-POU member in the fixture. So this maps
  the manifest's per-file form to an action, decodes percent escapes in directory names,
  and stops. The C# planner remains the authority - if a tool ever needs creates, owner
  chains or stray detection, route it through the runner rather than growing this.

  A file the plan does not mention draws a NOT_IN_PLAN warning from the script, which is
  why the bookkeeping files are included as explicit skips rather than left out.

.OUTPUTS
  A hashtable shaped like CodesysJob's plan payload: @{ files = @( ... ) }.
#>
param(
    [Parameter(Mandatory)][string]$Mirror
)

$manifestPath = Join-Path $Mirror '_manifest.json'
if (-not (Test-Path $manifestPath)) {
    throw "no _manifest.json in $Mirror - these tools only import self-exported mirrors"
}

$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json

function Decode([string]$s) {
    [regex]::Replace($s, '%([0-9A-Fa-f]{2})', {
        param($m) [string][char][Convert]::ToInt32($m.Groups[1].Value, 16)
    })
}

$files = @()
foreach ($obj in $manifest.objects) {
    $segments = $obj.path -split '/'
    $resolve = @()
    if ($segments.Count -gt 1) {
        $resolve = @($segments[0..($segments.Count - 2)] | ForEach-Object { Decode $_ })
    }
    $resolve += $obj.name

    # The same dispatch as CodesysImportPlanner.PlanKnown, minus everything a fresh
    # self-export cannot contain. An unknown form is refused there and here alike.
    #
    # AN EXPORT-ONLY FORM HAS THREE HOMES, NOT TWO, and this is the third:
    # versio_types.EXPORT_ONLY_FORMS, ExportCoverage.IsExportOnly, and the line below.
    # CodesysObjectTypeTests compares all three, which it did not until GVL_NATIVE was added
    # for G12 and broke every tool in this directory - Measure-ImportStability.ps1 died on
    # `form 'GVL_NATIVE' is not one this helper knows` the first time it was run afterwards.
    # The refusal was correct; the omission was not.
    switch -Regex ($obj.form) {
        '^ST_'      { $action = 'text';   $skip = $null }
        '^TASK_BOTH$' { $action = 'typed'; $skip = $null }
        '^NATIVE'   { $action = 'native'; $skip = $null }
        '^(LIBRARIES|TASK_NATIVE|GVL_NATIVE|EXTERNAL_NATIVE|PROPERTY_TEXT)$' { $action = 'skip'; $skip = 'exportOnly' }
        default { throw "form '$($obj.form)' is not one this helper knows - use the runner" }
    }

    $entry = @{ path = $obj.path; action = $action; name = $obj.name }
    if ($skip) { $entry.skip = $skip } else { $entry.resolve = $resolve }
    $files += $entry
}

foreach ($book in '_manifest.json', '_coverage.md') {
    if (Test-Path (Join-Path $Mirror $book)) {
        $files += @{ path = $book; action = 'skip'; skip = 'bookkeeping' }
    }
}

@{ files = $files }
