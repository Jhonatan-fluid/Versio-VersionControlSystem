<#
.SYNOPSIS
  Byte-compares two export trees. This is the Phase 0.2 determinism gate.

.DESCRIPTION
  A byte comparison is exactly right for determinism - exporting an unchanged project
  twice must produce identical bytes - and exactly wrong for round-trip fidelity, where
  import_native legitimately perturbs native XML. Test-RoundTrip.ps1 therefore calls this
  for the raw per-file result and judges the layers itself.

.PARAMETER Quiet
  Return the result object without printing the banner. For callers that present their own
  verdict: a red "DIFFERING : 9" above a considered per-layer answer just reintroduces the
  alarm that answer exists to remove.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$A,
    [Parameter(Mandatory)][string]$B,
    [switch]$ShowFirstDiff,
    [string[]]$Exclude = @(),
    [switch]$Quiet
)

function Get-Tree($root, $exclude) {
    $map = @{}
    Get-ChildItem $root -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($root.Length).TrimStart('\')
        if ($exclude -contains $rel) { return }
        $sha = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        $map[$rel] = @{ Hash = $sha; Size = $_.Length; Path = $_.FullName }
    }
    $map
}

$ta = Get-Tree $A $Exclude
$tb = Get-Tree $B $Exclude

$onlyA = $ta.Keys | Where-Object { -not $tb.ContainsKey($_) }
$onlyB = $tb.Keys | Where-Object { -not $ta.ContainsKey($_) }
$both  = $ta.Keys | Where-Object { $tb.ContainsKey($_) }
$diff  = $both | Where-Object { $ta[$_].Hash -ne $tb[$_].Hash }

if (-not $Quiet) {

Write-Host ""
Write-Host "  files in A : $($ta.Count)"
Write-Host "  files in B : $($tb.Count)"
Write-Host "  only in A  : $($onlyA.Count)"
Write-Host "  only in B  : $($onlyB.Count)"
Write-Host "  identical  : $($both.Count - $diff.Count)"
if ($diff.Count -eq 0 -and $onlyA.Count -eq 0 -and $onlyB.Count -eq 0) {
    Write-Host "  DIFFERING  : 0" -ForegroundColor Green
    Write-Host ""
    Write-Host "  DETERMINISTIC - trees are byte-identical" -ForegroundColor Green
} else {
    Write-Host "  DIFFERING  : $($diff.Count)" -ForegroundColor Red
    $onlyA | ForEach-Object { Write-Host "    only A: $_" -ForegroundColor Yellow }
    $onlyB | ForEach-Object { Write-Host "    only B: $_" -ForegroundColor Yellow }
    $diff  | ForEach-Object { Write-Host "    differs: $_  ($($ta[$_].Size) vs $($tb[$_].Size) bytes)" -ForegroundColor Red }

    if ($ShowFirstDiff -and $diff.Count -gt 0) {
        # @() matters: PowerShell unwraps a single-element array, so $diff[0] on one
        # differing file would index into the STRING and yield its first character.
        $f = @($diff)[0]
        Write-Host ""
        Write-Host "  --- first differing lines in $f ---" -ForegroundColor Cyan
        $la = [System.IO.File]::ReadAllLines($ta[$f].Path)
        $lb = [System.IO.File]::ReadAllLines($tb[$f].Path)
        $shown = 0
        for ($i = 0; $i -lt [Math]::Max($la.Count, $lb.Count) -and $shown -lt 12; $i++) {
            $x = if ($i -lt $la.Count) { $la[$i] } else { "<eof>" }
            $y = if ($i -lt $lb.Count) { $lb[$i] } else { "<eof>" }
            if ($x -ne $y) {
                Write-Host ("    line {0}" -f ($i + 1)) -ForegroundColor Cyan
                Write-Host ("      A: {0}" -f $x.Trim()) -ForegroundColor Red
                Write-Host ("      B: {0}" -f $y.Trim()) -ForegroundColor Green
                $shown++
            }
        }
    }
}
Write-Host ""

}   # -not $Quiet

[pscustomobject]@{
    Deterministic = ($diff.Count -eq 0 -and $onlyA.Count -eq 0 -and $onlyB.Count -eq 0)
    Differing     = @($diff)
    OnlyA         = @($onlyA)
    OnlyB         = @($onlyB)
}
