<#
.SYNOPSIS
  Runs a Versio python script inside headless CODESYS and captures everything.

.DESCRIPTION
  Phase 0 harness. Deliberately does what the plan says the real runner must do:
    - writes the job as JSON to a temp file (avoids --scriptargs quoting hell)
    - drains stdout/stderr asynchronously (never WaitForExit before draining)
    - hard timeout + process-tree kill (a --noUI modal is a hang, not an error)
    - separates the @@VERSIO@@ JSON-lines protocol from CODESYS's own chatter
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Script,
    [Parameter(Mandatory)][hashtable]$Job,
    [string]$Codesys = "C:\Program Files\CODESYS 3.5.22.20\CODESYS\Common\CODESYS.exe",
    [string]$Profile,
    [int]$TimeoutSec = 600,
    [string]$LogDir = "C:\VERSIO\phase0\logs"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$stamp   = Get-Date -Format "yyyyMMdd-HHmmss"
$tag     = [System.IO.Path]::GetFileNameWithoutExtension($Script)
$jobFile = Join-Path $LogDir "$tag-$stamp.job.json"
$rawLog  = Join-Path $LogDir "$tag-$stamp.raw.log"

# NOTE: must be UTF-8 *without* BOM. PowerShell 5.1's -Encoding utf8 emits a BOM,
# and IronPython 2.7's json.load chokes on it with "No JSON object could be decoded".
[System.IO.File]::WriteAllText($jobFile, ($Job | ConvertTo-Json -Depth 10),
                               (New-Object System.Text.UTF8Encoding $false))
Write-Host "job:    $jobFile"

$argList = @("--noUI", "--runscript=`"$Script`"", "--scriptargs:`"$jobFile`"")
if ($Profile) { $argList = @("--profile=`"$Profile`"") + $argList }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName               = $Codesys
$psi.Arguments              = ($argList -join " ")
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute        = $false
$psi.CreateNoWindow         = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi

# Async drain. Reading to completion before WaitForExit is the classic deadlock.
$sb = New-Object System.Text.StringBuilder
$onOut = { if ($EventArgs.Data -ne $null) { [void]$Event.MessageData.AppendLine($EventArgs.Data) } }
$outEvt = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action $onOut -MessageData $sb
$errEvt = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived  -Action $onOut -MessageData $sb

$sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "exec:   $($psi.FileName) $($psi.Arguments)"
[void]$proc.Start()
$proc.BeginOutputReadLine()
$proc.BeginErrorReadLine()

$timedOut = $false
if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    $timedOut = $true
    Write-Warning "TIMEOUT after $TimeoutSec s - killing process tree (PID $($proc.Id))"
    # Kill the whole tree: CODESYS spawns children, and a stuck modal keeps them alive.
    & taskkill /PID $proc.Id /T /F 2>&1 | Out-Null
    [void]$proc.WaitForExit(15000)
}
$sw.Stop()

Start-Sleep -Milliseconds 300      # let the async handlers flush
Unregister-Event -SourceIdentifier $outEvt.Name -ErrorAction SilentlyContinue
Unregister-Event -SourceIdentifier $errEvt.Name -ErrorAction SilentlyContinue

$all = $sb.ToString()
$all | Out-File -FilePath $rawLog -Encoding utf8

$versio = @()
$chatter = @()
foreach ($line in ($all -split "`r?`n")) {
    if ($line -like "*@@VERSIO@@*") {
        $json = $line.Substring($line.IndexOf("@@VERSIO@@") + 10)
        try { $versio += ($json | ConvertFrom-Json) } catch { $chatter += "UNPARSEABLE: $line" }
    } elseif ($line.Trim()) {
        $chatter += $line
    }
}

[pscustomobject]@{
    ExitCode  = $(if ($timedOut) { -1 } else { $proc.ExitCode })
    TimedOut  = $timedOut
    Seconds   = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    Events    = $versio
    Chatter   = $chatter
    RawLog    = $rawLog
    JobFile   = $jobFile
}
