<#
.SYNOPSIS
  Read-only Automation Interface I/O probe on a COPY of the TwinCAT fixture.

.DESCRIPTION
  Product Export writes Devices/Devices.devices.yaml (form DEVICES, export-only).
  This dump is the measurement that led there: what LookupTreeItem("TIID") holds,
  whether Comment set-and-restore works in-session, and that Comment does not
  persist through Save. CreateChild is a separate switch so a PlugNew-class call
  cannot dirty the SHA dumps.

  Two untouched runs of the same copy must be SHA-256 identical or the dump
  itself is churn and cannot become a catalog. A third run (-WriteTest) tries
  Comment and restores it. A fourth copy (-CreateTest) tries CreateChild of an
  EtherCAT master. The project is never saved.

  Close Versio AND TcXaeShell first. Never the committed fixtures tree.
#>
[CmdletBinding()]
param(
    [string]$Project = "",
    [switch]$WriteTest,
    [switch]$CreateTest,
    [switch]$SkipCopy
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}

if (-not $Project) {
    $committed = Join-Path $repo "fixtures\projects\TC3_PlcSample_reStrText"
    $Project = Join-Path $committed "TC3_PlcSample_reStrText.tsproj"
}

if (-not $Project -or -not (Test-Path $Project)) {
    Write-Error "No TwinCAT fixture. Expected fixtures/projects/TC3_PlcSample_reStrText/TC3_PlcSample_reStrText.tsproj. Never write the committed tree."
}

$held = @(Get-Process -Name "TcXaeShell","devenv" -ErrorAction SilentlyContinue)
if ($held) {
    Write-Error ("Close TwinCAT XAE first. Held: " + ($held.ProcessName -join ", "))
}
$versio = Get-Process -Name "Versio" -ErrorAction SilentlyContinue
if ($versio) {
    Write-Error "Close Versio.exe first; it locks the host binaries."
}

Write-Host "Building TwinCAT host..."
& $dotnet build (Join-Path $repo "src\Versio.Plc.TwinCat.Host\Versio.Plc.TwinCat.Host.csproj") --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "host build failed" }

$hostExe = @(
    Join-Path $repo "src\Versio.Plc.TwinCat.Host\bin\Debug\net9.0-windows\Versio.Plc.TwinCat.Host.exe"
    Join-Path $repo "src\Versio.App\bin\Debug\net9.0-windows\twincat\Versio.Plc.TwinCat.Host.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $hostExe) { Write-Error "Host executable not built." }

$stamp = [guid]::NewGuid().ToString("N").Substring(0, 8)
$work = Join-Path $env:TEMP ("versio-tc-ioprobe-" + $stamp)
New-Item -ItemType Directory -Force -Path $work | Out-Null

$targetProject = $Project
if (-not $SkipCopy) {
    $copyDir = Join-Path $work "project"
    Write-Host ("Copying fixture to {0}" -f $copyDir)
    New-Item -ItemType Directory -Force -Path $copyDir | Out-Null
    $srcDir = Split-Path -Parent $Project
    & robocopy $srcDir $copyDir /E /NFL /NDL /NJH /NJS /nc /ns /np /XD .vs _Boot _CompileInfo | Out-Null
    $rc = $LASTEXITCODE
    if ($rc -ge 8) { Write-Error "robocopy failed with $rc" }
    $targetProject = Join-Path $copyDir (Split-Path -Leaf $Project)
}

$utf8 = New-Object System.Text.UTF8Encoding $false
function Write-Job([string]$path, [hashtable]$body) {
    $json = $body | ConvertTo-Json -Depth 8 -Compress:$false
    [System.IO.File]::WriteAllText($path, $json, $utf8)
}

function Invoke-IoProbe([string]$outDir, [bool]$doWrite, [bool]$doCreate) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $job = Join-Path $work ("job-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + ".json")
    Write-Job $job @{
        op = "ioprobe"
        schema = 1
        operationId = "ioprobe"
        project = $targetProject
        out = $outDir
        progId = "TcXaeShell.DTE.17.0"
        save = $false
        writeTest = $doWrite
        createTest = $doCreate
        allowConvert = $true
        timeoutSeconds = 1200
    }
    Write-Host ("ioprobe -> {0}  writeTest={1} createTest={2}" -f $outDir, $doWrite, $doCreate)
    $log = Join-Path $outDir "host.log"
    $err = Join-Path $outDir "host.err"
    $p = Start-Process -FilePath $hostExe -ArgumentList $job -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError $err
    if ($p.ExitCode -ne 0) {
        $tail = ""
        if (Test-Path $log) { $tail = Get-Content $log -Tail 20 | Out-String }
        Write-Error "host exited $($p.ExitCode) for $outDir`n$tail"
    }
    $dump = Join-Path $outDir "io.json"
    if (-not (Test-Path $dump)) { Write-Error "no io.json in $outDir" }
    return $dump
}

function Get-Sha256([string]$path) {
    (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
}

$out1 = Join-Path $work "run1"
$out2 = Join-Path $work "run2"
$dump1 = Invoke-IoProbe $out1 $false $false
$dump2 = Invoke-IoProbe $out2 $false $false
$h1 = Get-Sha256 $dump1
$h2 = Get-Sha256 $dump2
Write-Host ("SHA run1 {0}" -f $h1)
Write-Host ("SHA run2 {0}" -f $h2)
if ($h1 -ne $h2) {
    Write-Host "DUMPS DIFFER - the probe itself is churn and cannot become a catalog." -ForegroundColor Red
} else {
    Write-Host "Two untouched dumps are SHA-256 identical." -ForegroundColor Green
}

if ($WriteTest) {
    $out3 = Join-Path $work "write-test"
    $dump3 = Invoke-IoProbe $out3 $true $false
    Write-Host ("write-test dump: {0}" -f $dump3)
}

if ($CreateTest) {
    $createDir = Join-Path $work "create-project"
    New-Item -ItemType Directory -Force -Path $createDir | Out-Null
    $srcDir = Split-Path -Parent $targetProject
    if (-not $SkipCopy) {
        $srcDir = Join-Path $work "project"
    } else {
        $srcDir = Split-Path -Parent $Project
    }
    & robocopy $srcDir $createDir /E /NFL /NDL /NJH /NJS /nc /ns /np /XD .vs _Boot _CompileInfo | Out-Null
    $script:targetProject = Join-Path $createDir (Split-Path -Leaf $Project)
    $out4 = Join-Path $work "create-test"
    $dump4 = Invoke-IoProbe $out4 $false $true
    Write-Host ("create-test dump: {0}" -f $dump4)
}

Write-Host ("work: {0}" -f $work)
Write-Host ("dump: {0}" -f $dump1)
Get-Content $dump1 -TotalCount 80
