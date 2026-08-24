#Requires -Version 5.1
<#
.SYNOPSIS
    Publish a self-contained Versio payload and pack Setup.exe plus a per-machine MSI,
    as a staged launch sequence: banner, version menu, seven timed phases, liftoff.

.DESCRIPTION
    Child-process output (dotnet, vpk, the SFX, Fetch-Deps) goes to artifacts\pack.log
    rather than the console - the screen shows one line per phase with a spinner and
    elapsed time. Nothing is hidden: the log holds everything, its path is printed, and
    a failure replays the last lines automatically. -Plain drops the spinner, the
    countdown and the colors for scripted runs; -Version skips the bump menu.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Repo,

    [Parameter(Mandatory = $true)]
    [string] $Dotnet,

    # Skip the bump menu. Scripted packs and tests pass x.y.z; the bat does not.
    [string] $Version,

    # No spinner, no countdown, no ANSI: for CI logs and captured output.
    [switch] $Plain
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------- presentation

$Esc = [char]27
$script:UseAnsi = -not $Plain

# Color on, color off. Everything styled goes through this so -Plain produces clean text
# for a CI log instead of escape soup.
function C([string] $Code) {
    if ($script:UseAnsi) { return "$Esc[${Code}m" }
    return ''
}

function Write-Banner {
    Write-Host ""
    Write-Host "$(C '97')  ##   ##  #######  ######    #####   ####   #######  $(C '0')"
    Write-Host "$(C '96')  ##   ##  ##       ##   ##  ##        ##   ##     ## $(C '0')"
    Write-Host "$(C '96')  ##   ##  #####    ######    #####    ##   ##     ## $(C '0')"
    Write-Host "$(C '36')   ## ##   ##       ##   ##       ##   ##   ##     ## $(C '0')"
    Write-Host "$(C '34')    ###    #######  ##   ##   #####   ####   #######  $(C '0')"
    Write-Host "$(C '90')  ==================================================  $(C '0')"
    Write-Host "$(C '37')           PLC projects, versioned in git             $(C '0')"
}

function Format-Elapsed([TimeSpan] $Span) {
    if ($Span.TotalSeconds -ge 60) {
        return ('{0} m {1:00} s' -f [int][math]::Floor($Span.TotalMinutes), $Span.Seconds)
    }
    return ('{0:0.0} s' -f $Span.TotalSeconds)
}

function Format-Mb([long] $Bytes) {
    return ('{0:N1} MB' -f ($Bytes / 1MB))
}

# Labels arrive pre-dotted so every phase and check lines up, same as the bat's list.
function Format-Label([string] $Text) {
    return ($Text + ' ').PadRight(32, '.')
}

# ---------------------------------------------------------------------------- pack log

$script:LogPath = Join-Path $Repo 'artifacts\pack.log'

function Log([string] $Line) {
    Add-Content -LiteralPath $script:LogPath -Value ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Line) -Encoding UTF8
}

# ---------------------------------------------------------------------------- phases

$script:PhaseTotal = 7
$script:PhaseIndex = 0
$script:PhaseLabel = ''
$script:PhaseWatch = $null
$script:TickCount = 0

function Start-Phase([string] $Label) {
    $script:PhaseIndex++
    $script:PhaseLabel = Format-Label $Label
    $script:PhaseWatch = [System.Diagnostics.Stopwatch]::StartNew()
    Log ("=== [{0}/{1}] {2}" -f $script:PhaseIndex, $script:PhaseTotal, $Label)
    Write-Host -NoNewline ("$(C '90')  [{0}/{1}]$(C '0') $(C '37'){2}$(C '0') " -f `
        $script:PhaseIndex, $script:PhaseTotal, $script:PhaseLabel)
    if ($Plain) { Write-Host "" }
}

# One frame of the working animation: spinner plus live elapsed seconds, redrawn in
# place. -Plain skips it entirely - a redrawn line is escape soup in a captured log.
function Write-Tick {
    if ($Plain) { return }
    $spin = '|/-\'
    $frame = $spin[$script:TickCount % 4]
    $script:TickCount++
    Write-Host -NoNewline ("`r$(C '90')  [{0}/{1}]$(C '0') $(C '37'){2}$(C '0') $(C '36'){3}$(C '0') $(C '90'){4,4} s $(C '0')" -f `
        $script:PhaseIndex, $script:PhaseTotal, $script:PhaseLabel, $frame, [int]$script:PhaseWatch.Elapsed.TotalSeconds)
}

function Complete-Phase([string] $Note = '') {
    $elapsed = Format-Elapsed $script:PhaseWatch.Elapsed
    $suffix = ''
    if ($Note.Length -gt 0) { $suffix = "  $(C '90')$Note$(C '0')" }
    $line = ("  $(C '90')[{0}/{1}]$(C '0') $(C '37'){2}$(C '0') $(C '92')OK$(C '0')  $(C '90'){3,8}$(C '0'){4}" -f `
        $script:PhaseIndex, $script:PhaseTotal, $script:PhaseLabel, $elapsed, $suffix)
    if ($Plain) {
        Write-Host $line
    } else {
        Write-Host ("`r" + $line + (' ' * 12))
    }
    Log ("=== done in " + $elapsed)
}

# Run a child process with its output captured into pack.log, animating the current
# phase line while it works. The console stays readable; the log stays complete.
function Invoke-LoggedProcess {
    param(
        [string] $FilePath,
        [string] $Arguments
    )

    Log ("run: " + $FilePath + ' ' + $Arguments)
    $out = [System.IO.Path]::GetTempFileName()
    $err = [System.IO.Path]::GetTempFileName()

    $p = Start-Process -FilePath $FilePath -ArgumentList $Arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err

    # Touch the handle now or PowerShell 5.1 never caches it, and ExitCode reads $null
    # after the process exits - every failure would say "exit " with the number missing.
    $null = $p.Handle

    while (-not $p.HasExited) {
        Write-Tick
        Start-Sleep -Milliseconds 200
    }
    $p.WaitForExit()

    foreach ($f in @($out, $err)) {
        Get-Content -LiteralPath $f -ErrorAction SilentlyContinue |
            ForEach-Object { Log ('  ' + $_) }
        Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    }

    return $p.ExitCode
}

function Quote([string] $Value) {
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

# ---------------------------------------------------------------------------- version

function Get-VersioVersion {
    $props = Join-Path $Repo 'Directory.Build.props'
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $text = [System.IO.File]::ReadAllText($props, $utf8)
    if ($text -notmatch '<VersioVersion>([^<]+)</VersioVersion>') {
        throw "Directory.Build.props has no VersioVersion"
    }
    return $Matches[1].Trim()
}

function Assert-SemVer([string] $Value) {
    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "version '$Value' is not x.y.z (what vpk pack will accept)"
    }
}

function Set-VersioVersion([string] $NewVersion) {
    Assert-SemVer $NewVersion
    $props = Join-Path $Repo 'Directory.Build.props'
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $text = [System.IO.File]::ReadAllText($props, $utf8)
    $updated = [regex]::Replace(
        $text,
        '<VersioVersion>[^<]+</VersioVersion>',
        "<VersioVersion>$NewVersion</VersioVersion>",
        1)
    if ($updated -eq $text -and $text -notmatch "<VersioVersion>$NewVersion</VersioVersion>") {
        throw "Directory.Build.props VersioVersion was not replaced"
    }
    # File APIs, not Get-Content/Set-Content: those add a BOM and can corrupt the file.
    [System.IO.File]::WriteAllText($props, $updated, $utf8)
}

# The bump menu, under the banner so choosing a version is part of the show rather than
# a bare prompt. Returns $null for a clean quit - the one exit that must not read as
# either success or failure to the bat.
function Read-VersionChoice([string] $Current) {
    Assert-SemVer $Current
    $parts = $Current.Split('.') | ForEach-Object { [int]$_ }
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]
    $asPatch = "$major.$minor.$($patch + 1)"
    $asMinor = "$major.$($minor + 1).0"
    $asMajor = "$($major + 1).0.0"

    Write-Host ""
    Write-Host "$(C '93')   MISSION PARAMETERS$(C '0')$(C '90') - current version $Current$(C '0')"
    Write-Host ""
    Write-Host "$(C '37')     1) Last number (patch)  -> $(C '96')$asPatch$(C '0')   $(C '90')[Enter]$(C '0')"
    Write-Host "$(C '37')     2) Middle (minor)       -> $(C '96')$asMinor$(C '0')"
    Write-Host "$(C '37')     3) First (major)        -> $(C '96')$asMajor$(C '0')"
    Write-Host "$(C '37')     4) Keep                 -> $(C '96')$Current$(C '0')"
    Write-Host "$(C '37')     5) Type one$(C '0')"
    Write-Host "$(C '90')     Q) Quit - touch nothing$(C '0')"
    Write-Host ""
    $choice = Read-Host "   Choice [1]"
    if ([string]::IsNullOrWhiteSpace($choice)) {
        $choice = '1'
    }
    switch ($choice.Trim().ToLowerInvariant()) {
        '1' { return $asPatch }
        '2' { return $asMinor }
        '3' { return $asMajor }
        '4' { return $Current }
        '5' {
            $typed = Read-Host "   Version (x.y.z)"
            if ([string]::IsNullOrWhiteSpace($typed)) {
                throw "no version typed"
            }
            Assert-SemVer $typed.Trim()
            return $typed.Trim()
        }
        'q' { return $null }
        default { throw "not a choice: $choice (1-5, Q)" }
    }
}

# ---------------------------------------------------------------------------- intro

New-Item -ItemType Directory -Force -Path (Join-Path $Repo 'artifacts') | Out-Null
if (Test-Path -LiteralPath $script:LogPath) {
    Remove-Item -LiteralPath $script:LogPath -Force
}
Log "pack starting"

if (-not $Plain) {
    Write-Banner
    Write-Host "$(C '90')           PACKAGING PIPELINE$(C '0')"
}

$current = Get-VersioVersion
if ([string]::IsNullOrWhiteSpace($Version)) {
    $chosen = Read-VersionChoice $current
    if ($null -eq $chosen) {
        Write-Host ""
        Write-Host "$(C '90')   Nothing touched.$(C '0')"
        exit 2
    }
} else {
    $chosen = $Version.Trim()
    Assert-SemVer $chosen
}

# A failed pack must not burn the number. The bump has to happen before the build so the
# version bakes into the assemblies - which means a pack that then fails has mutated
# Directory.Build.props for nothing, and the next menu compounds from the burned value
# (fail twice and 0.1.5 / 0.1.6 were silently skipped). The undo lives in the catch
# below: it restores the file only when THIS run wrote it, and the failure still reaches
# the caller as exit 1.
$script:bumpedFrom = $null

try {
    if ($chosen -ne $current) {
        Set-VersioVersion $chosen
        $script:bumpedFrom = $current
        Log "VersioVersion $current -> $chosen"
    } else {
        Log "VersioVersion $chosen (unchanged)"
    }

    Write-Host ""
    Write-Host "$(C '92')   PACKING Versio $chosen$(C '0')$(C '90') - child output goes to artifacts\pack.log$(C '0')"
    Write-Host ""

    $total = [System.Diagnostics.Stopwatch]::StartNew()
    $packVersion = Get-VersioVersion
    $publish = Join-Path $Repo 'artifacts\publish'
    $releases = Join-Path $Repo 'artifacts\releases'
    $icon = Join-Path $Repo 'assets\versio.ico'
    $powershell = 'powershell.exe'

    # -- 1: pinned downloads --------------------------------------------------------

    Start-Phase 'Pack dependencies'
    $fetch = Join-Path $Repo 'tools\pack\Fetch-Deps.ps1'
    $code = Invoke-LoggedProcess $powershell ('-NoProfile -ExecutionPolicy Bypass -File {0} -Repo {1} -Dotnet {2}' -f `
        (Quote $fetch), (Quote $Repo), (Quote $Dotnet))
    if ($code -ne 0) {
        throw "Fetch-Deps failed (exit $code) - see artifacts\pack.log"
    }
    Complete-Phase 'PortableGit + vpk pinned'

    if (Test-Path -LiteralPath $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publish, $releases | Out-Null

    $appProj = Join-Path $Repo 'src\Versio.App\Versio.App.csproj'
    $tiaProj = Join-Path $Repo 'src\Versio.Plc.TiaPortal.Host\Versio.Plc.TiaPortal.Host.csproj'
    $twinProj = Join-Path $Repo 'src\Versio.Plc.TwinCat.Host\Versio.Plc.TwinCat.Host.csproj'

    # -- 2: the app -----------------------------------------------------------------

    # ReadyToRun pre-compiles to native at publish, which is startup time a desktop app
    # pays on every cold open. Trimming and AOT stay off - WPF does not support them
    # (plan 13.2) - but R2R is the supported middle: bigger managed images, less
    # first-launch JIT.
    Start-Phase 'Versio.App  R2R win-x64'
    $code = Invoke-LoggedProcess $Dotnet ('publish {0} -c Release -r win-x64 --self-contained true -o {1} -p:PublishTrimmed=false -p:PublishAot=false -p:PublishSingleFile=false -p:PublishReadyToRun=true' -f `
        (Quote $appProj), (Quote $publish))
    if ($code -ne 0) {
        throw "Versio.App publish failed (exit $code)"
    }
    Complete-Phase

    # -- 3: the TwinCAT host --------------------------------------------------------

    # R2R here too: the host is spawned per operation, so its cold start is paid on
    # every TwinCAT export and import, not once per day.
    Start-Phase 'TwinCAT host  R2R win-x64'
    $twinOut = Join-Path $publish 'twincat'
    if (Test-Path -LiteralPath $twinOut) {
        Remove-Item -LiteralPath $twinOut -Recurse -Force
    }
    $code = Invoke-LoggedProcess $Dotnet ('publish {0} -c Release -r win-x64 --self-contained true -o {1} -p:PublishTrimmed=false -p:PublishAot=false -p:PublishSingleFile=false -p:PublishReadyToRun=true' -f `
        (Quote $twinProj), (Quote $twinOut))
    if ($code -ne 0) {
        throw "TwinCAT host publish failed (exit $code)"
    }
    Complete-Phase

    # -- 4: the TIA host ------------------------------------------------------------

    # Into a fresh folder with -o, never a copy of bin\Release\net48: bin is incremental
    # and nothing ever cleans it, so a renamed assembly or a dropped dependency lingers
    # there from old builds and would ship in every installer from then on. The app and
    # the TwinCAT host already publish into wiped folders; this is the same rule for the
    # one net48 payload.
    Start-Phase 'TIA Portal host  net48'
    $tiaOut = Join-Path $publish 'tia'
    if (Test-Path -LiteralPath $tiaOut) {
        Remove-Item -LiteralPath $tiaOut -Recurse -Force
    }
    $code = Invoke-LoggedProcess $Dotnet ('build {0} -c Release -o {1}' -f (Quote $tiaProj), (Quote $tiaOut))
    if ($code -ne 0) {
        throw "TIA host build failed (exit $code)"
    }
    Complete-Phase

    # -- 5: PortableGit -------------------------------------------------------------

    $manifest = Get-Content -LiteralPath (Join-Path $Repo 'third_party\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $gitDep = $manifest.dependencies | Where-Object { $_.id -eq 'PortableGit' }
    $sfx = Join-Path $Repo "third_party\cache\$($gitDep.filename)"
    $gitOut = Join-Path $publish 'git'
    if (Test-Path -LiteralPath $gitOut) {
        Remove-Item -LiteralPath $gitOut -Recurse -Force
    }

    Start-Phase ("PortableGit " + $gitDep.version)

    # The 7-Zip SFX is a GUI stub. PowerShell's call operator can return before
    # extraction finishes, and -help opens a window that never exits. cmd /c with -y -o
    # waits for the unpack; we still poll for git.exe because some builds spawn a child
    # extractor.
    $code = Invoke-LoggedProcess 'cmd.exe' ('/c ""{0}" -y "-o{1}""' -f $sfx, $gitOut)
    if ($code -ne 0) {
        throw "PortableGit SFX extract failed (exit $code)"
    }

    # Both existence polls run before anything from the payload is EXECUTED. git.exe
    # lands early in the extraction; running `git lfs version` or the config pin against
    # a tree the extractor is still writing would fail a perfectly good pack, and GCM is
    # among the last files, so its appearance is the better "extraction finished" signal.
    $gitExe = Join-Path $gitOut 'cmd\git.exe'
    $deadline = (Get-Date).AddMinutes(2)
    while (-not (Test-Path -LiteralPath $gitExe) -and (Get-Date) -lt $deadline) {
        Write-Tick
        Start-Sleep -Seconds 1
    }
    if (-not (Test-Path -LiteralPath $gitExe)) {
        throw "PortableGit extract did not produce cmd\git.exe"
    }

    $gcm = $null
    while ($null -eq $gcm -and (Get-Date) -lt $deadline) {
        $gcm = Get-ChildItem -LiteralPath $gitOut -Recurse -Filter 'git-credential-manager.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $gcm) {
            Write-Tick
            Start-Sleep -Seconds 1
        }
    }
    if ($null -eq $gcm) {
        throw "PortableGit extract has no git-credential-manager.exe"
    }
    Log ("GCM at " + $gcm.FullName)

    $lfs = (& $gitExe 'lfs' 'version' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "bundled git lfs version failed - this archive is not PortableGit"
    }
    Log $lfs

    # PortableGit's first-run helper is helper-selector (a GUI). GIT_TERMINAL_PROMPT=0
    # does not suppress it, and "Always use this" cannot persist under Program Files.
    # Pin manager into this copy's system gitconfig so Sign in is GCM, not the picker.
    & $gitExe config --system --unset-all credential.helper
    & $gitExe config --system credential.helper manager
    if ($LASTEXITCODE -ne 0) {
        throw "could not pin credential.helper=manager on bundled git"
    }
    $pinnedHelper = (& $gitExe config --system --get credential.helper | Out-String).Trim()
    if ($pinnedHelper -ne 'manager') {
        throw "bundled git credential.helper is '$pinnedHelper', expected manager"
    }
    Complete-Phase 'LFS + GCM + helper pin'

    # -- 6: payload gates -----------------------------------------------------------

    Start-Phase 'Payload gates'
    Copy-Item -LiteralPath (Join-Path $Repo 'third_party\THIRD-PARTY-NOTICES.txt') `
        -Destination (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') -Force

    $required = @(
        (Join-Path $publish 'Versio.exe'),
        (Join-Path $publish 'tia\Versio.Plc.TiaPortal.Host.exe'),
        (Join-Path $publish 'twincat\Versio.Plc.TwinCat.Host.exe'),
        $gitExe,

        # GPL-2.0 compliance travels with the payload: distributing PortableGit's
        # binaries requires distributing its license text, and the notices file points at
        # exactly this path. If a future PortableGit moves it, this gate makes that a
        # failed pack rather than a quiet license violation.
        (Join-Path $gitOut 'LICENSE.txt')
    )
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "payload incomplete: missing $path"
        }
    }
    Complete-Phase ("{0} required files present" -f $required.Count)

    # -- 7: the installer -----------------------------------------------------------

    Start-Phase 'Setup.exe + MSI  vpk'
    $vpk = Join-Path $Repo 'third_party\cache\vpk\vpk.exe'
    $packStarted = Get-Date

    # Rebuilds of the same VersioVersion are expected until that number moves.
    # --legacyConsole (-x) turns the overwrite question off; --yes (-y) is the
    # answer. --yes alone still draws the prompt and waits for a timeout.
    # Global flags belong before `pack` - System.CommandLine parses them on vpk itself.
    $vpkArgs = ('--legacyConsole --yes --skip-updates pack --packId Versio --packVersion {0} --packTitle Versio --packAuthors Versio --packDir {1} --mainExe Versio.exe --icon {2} --msi --instLocation PerMachine --outputDir {3}' -f `
        $packVersion, (Quote $publish), (Quote $icon), (Quote $releases))
    $code = Invoke-LoggedProcess $vpk $vpkArgs
    if ($code -ne 0) {
        throw "vpk pack failed (exit $code)"
    }

    # Written THIS run, not merely present: the releases folder is never cleaned, so an
    # existence check would flash green on artifacts an earlier pack left behind.
    $setup = Get-ChildItem -LiteralPath $releases -Filter '*Setup.exe' |
        Where-Object { $_.LastWriteTime -ge $packStarted } | Select-Object -First 1
    $msi = Get-ChildItem -LiteralPath $releases -Filter '*.msi' |
        Where-Object { $_.LastWriteTime -ge $packStarted } | Select-Object -First 1
    if ($null -eq $setup -or $null -eq $msi) {
        throw "vpk pack did not write both Setup.exe and an MSI to $releases this run"
    }
    Complete-Phase

    # -- liftoff --------------------------------------------------------------------

    $payload = Get-ChildItem -LiteralPath $publish -Recurse -File |
        Measure-Object -Property Length -Sum

    function Write-Check([string] $Label, [string] $Path, [string] $Detail = '') {
        $tail = 'ready'
        if ($Detail.Length -gt 0) { $tail = $Detail }
        if (Test-Path -LiteralPath $Path) {
            Write-Host "$(C '92')   [ OK ]$(C '0') $(C '37')$(Format-Label $Label)$(C '0') $(C '92')$tail$(C '0')"
        } else {
            Write-Host "$(C '91')   [FAIL]$(C '0') $(C '37')$(Format-Label $Label)$(C '0') $(C '91')MISSING$(C '0')"
        }
    }

    Write-Banner
    Write-Host ""
    Write-Host ("$(C '92')   PACKAGE GREEN$(C '0')$(C '90') - Versio {0} - packed in {1}$(C '0')" -f `
        $packVersion, (Format-Elapsed $total.Elapsed))
    Write-Host ""
    Write-Check 'Self-contained Versio.exe' (Join-Path $publish 'Versio.exe')
    Write-Check 'Bundled git.exe' $gitExe
    Write-Check 'TIA Portal host' (Join-Path $publish 'tia\Versio.Plc.TiaPortal.Host.exe')
    Write-Check 'TwinCAT host' (Join-Path $publish 'twincat\Versio.Plc.TwinCat.Host.exe')
    Write-Check 'Setup.exe' $setup.FullName (Format-Mb $setup.Length)
    Write-Check 'Per-machine MSI' $msi.FullName (Format-Mb $msi.Length)
    Write-Host ""
    Write-Host ("$(C '90')   payload {0:N0} files - {1} - full log artifacts\pack.log$(C '0')" -f `
        $payload.Count, (Format-Mb $payload.Sum))
    Write-Host ""
    Log ("PACKED " + $setup.FullName)
    Log ("PACKED " + $msi.FullName)

    if ($Plain) {
        Write-Host "   LIFTOFF - $releases"
    } else {
        Write-Host -NoNewline "$(C '93')   LIFTOFF in 3$(C '0')"
        Start-Sleep -Seconds 1
        Write-Host -NoNewline "$(C '93').. 2$(C '0')"
        Start-Sleep -Seconds 1
        Write-Host -NoNewline "$(C '93').. 1$(C '0')"
        Start-Sleep -Seconds 1
        Write-Host "$(C '93')..$(C '0')"
        Write-Host "$(C '92')   artifacts\releases is go.$(C '0')"
        Write-Host "$(C '90')   Not launching Setup: that would install this machine.$(C '0')"
        Start-Process $releases
    }
    exit 0
}
catch {
    if ($null -ne $script:bumpedFrom) {
        Set-VersioVersion $script:bumpedFrom
        Write-Host ""
        Write-Host "$(C '93')   VersioVersion reverted to $($script:bumpedFrom) - the failed pack shipped nothing.$(C '0')"
    }

    # The reason and the evidence, right here: the child output lives in the log, so a
    # failure that only named the log would send somebody to open a file for a line
    # this can print itself. The bat draws the ABORT art after this and holds the window.
    Write-Host ""
    Write-Host "$(C '91')   PACK FAILED - $($_.Exception.Message)$(C '0')"
    if (Test-Path -LiteralPath $script:LogPath) {
        Write-Host "$(C '90')   last lines of artifacts\pack.log:$(C '0')"
        Get-Content -LiteralPath $script:LogPath -Tail 30 |
            ForEach-Object { Write-Host "$(C '90')     $_$(C '0')" }
    }
    Write-Host "$(C '90')   full log: $($script:LogPath)$(C '0')"
    exit 1
}
