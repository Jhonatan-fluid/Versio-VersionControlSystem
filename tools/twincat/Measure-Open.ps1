# Step-0 measurement: open a COPY of the fixture under TcXaeShell.DTE.17.0.
# Writes a JSON log; never opens the committed fixtures tree.
$ErrorActionPreference = 'Stop'
$tmp = (Get-Content (Join-Path $env:TEMP 'versio-tc-measure-path.txt') -Raw).Trim()
$sln = Join-Path $tmp 'TC3_PlcSample_reStrText.sln'
$log = Join-Path $tmp 'measure.json'
$sw = [Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{
  tmp = $tmp
  started = (Get-Date).ToString('o')
  progId = 'TcXaeShell.DTE.17.0'
}

function Invoke-Retry([scriptblock]$Block, [int]$Times = 20) {
  for ($i = 0; $i -lt $Times; $i++) {
    try { return & $Block }
    catch {
      $msg = $_.Exception.Message
      if ($msg -match '0x80010001|0x8001010A|rejected|RPC') {
        Start-Sleep -Milliseconds 400
        continue
      }
      throw
    }
  }
  throw "retry exhausted: $Block"
}

try {
  $dte = New-Object -ComObject TcXaeShell.DTE.17.0
  $result.createdMs = $sw.ElapsedMilliseconds
  $dte.SuppressUI = $true
  try { $dte.UserControl = $false } catch { }
  try { $dte.MainWindow.Visible = $false } catch { $result.mainWindow = $_.Exception.Message }

  Invoke-Retry { $dte.Solution.Open($sln) }
  $result.openedMs = $sw.ElapsedMilliseconds

  $sidecar = Get-ChildItem $tmp -Force | Where-Object { $_.Name -like '*.~u*' -or $_.Name -like '*.suo' -or $_.Name -eq '.vs' }
  $result.sidecars = @($sidecar | ForEach-Object { $_.Name })
  $u = Join-Path $tmp 'TC3_PlcSample_reStrText.~u'
  $result.tildeUExists = Test-Path $u
  if (Test-Path $u) { $result.tildeU = Get-Content $u -Raw }

  $proj = Invoke-Retry { $dte.Solution.Projects.Item(1) }
  $result.projectName = $proj.Name
  $sys = Invoke-Retry { $proj.Object }
  $result.hasSysMan = $null -ne $sys

  $tipc = Invoke-Retry { $sys.LookupTreeItem('TIPC') }
  $result.tipcName = $tipc.Name
  $result.tipcChildren = $tipc.ChildCount
  $children = @()
  for ($i = 1; $i -le $tipc.ChildCount; $i++) {
    $c = $tipc.Child($i)
    $children += [ordered]@{ name = $c.Name; subType = $c.ItemSubType; childCount = $c.ChildCount }
  }
  $result.tipcChildNames = $children

  $plcPath = 'TIPC^LibraryDocumentationSample^LibraryDocumentationSample Project'
  $loaded = $false
  for ($n = 0; $n -lt 30; $n++) {
    try {
      $plc = $sys.LookupTreeItem($plcPath)
      if ($null -ne $plc) {
        $result.plcPath = $plcPath
        $result.plcChildren = $plc.ChildCount
        $loaded = $true
        $result.lazyWaitMs = $sw.ElapsedMilliseconds
        break
      }
    } catch { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 500
  }
  $result.plcLoaded = $loaded

  if ($loaded) {
    $fb = $null
    try {
      $fb = $sys.LookupTreeItem($plcPath + '^LibPOUs^A_Samples^FB_DocuSample_FunctionBlock')
    } catch { $result.fbLookup = $_.Exception.Message }
    if ($null -ne $fb) {
      $result.fbName = $fb.Name
      try { $result.decl = $fb.DeclarationText.Substring(0, [Math]::Min(120, $fb.DeclarationText.Length)) } catch { $result.declErr = $_.Exception.Message }
      try { $result.impl = $fb.ImplementationText } catch { $result.implErr = $_.Exception.Message }
    }
  }

  $result.tsprojAfter = Select-String -Path (Join-Path $tmp 'TC3_PlcSample_reStrText.tsproj') -Pattern 'TcVersion=' | ForEach-Object { $_.Line.Trim() }
  $result.elapsedMs = $sw.ElapsedMilliseconds
  $result.ok = $true
}
catch {
  $result.ok = $false
  $result.error = $_.Exception.ToString()
  $result.elapsedMs = $sw.ElapsedMilliseconds
}
finally {
  try { if ($dte) { $dte.Quit() } } catch { $result.quit = $_.Exception.Message }
}

$result | ConvertTo-Json -Depth 6 | Set-Content $log -Encoding utf8
Write-Host "wrote $log"
Get-Content $log
