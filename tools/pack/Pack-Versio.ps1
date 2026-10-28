#Requires -Version 5.1
<#
.SYNOPSIS
    Publish a self-contained Versio payload and pack Setup.exe plus an MSI, both per-user,
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
    [switch] $Plain,

    # Markdown notes baked into the package. Velopack carries them in the release feed, and
    # the app shows them in its update prompt - so they travel with the version they describe
    # instead of living in a GitHub page the user never sees. Publish-Versio.ps1 passes it.
    [string] $ReleaseNotes,

    # Called by Publish-Versio.ps1, which has more to do after the pack: no countdown, and no
    # Explorer window popping up over the publish's own prompts. Colours stay - unlike -Plain.
    [switch] $Unattended,

    # Where vpk writes, default artifacts\releases. The publish packs into a folder of its
    # own holding only the PUBLISHED previous release, because vpk builds the delta against
    # whatever newest package it finds there - and artifacts\releases keeps every local pack,
    # including ones that never shipped. A delta against one of those would not apply on any
    # user's machine.
    [string] $ReleasesDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The banner, the phase lines, the log and the version menu are shared with
# Publish-Versio.ps1, so a publish looks and behaves like a pack because it runs the same code.
. (Join-Path $PSScriptRoot 'PackCommon.ps1')

# The PortableGit denylist lives in its own file so it can be run against an extracted copy
# without seven minutes of publishing first - which is how its sizes were measured.
. (Join-Path $PSScriptRoot 'Trim-PortableGit.ps1')

$script:LogPath = Join-Path $Repo 'artifacts\pack.log'
$script:PhaseTotal = 7

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

    # Checked before seven minutes of publishing rather than by vpk at the very end, where a
    # mistyped path would throw away the whole build for a file that was never going to exist.
    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes) -and -not (Test-Path -LiteralPath $ReleaseNotes)) {
        throw "release notes not found: $ReleaseNotes"
    }

    $total = [System.Diagnostics.Stopwatch]::StartNew()
    $packVersion = Get-VersioVersion
    $publish = Join-Path $Repo 'artifacts\publish'
    $releases = Join-Path $Repo 'artifacts\releases'
    if (-not [string]::IsNullOrWhiteSpace($ReleasesDir)) {
        $releases = $ReleasesDir
    }
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

    # After the GCM poll, because that is the extraction-finished signal: deleting from a
    # tree the extractor is still writing would race it. Before every check below, so the
    # LFS, GCM, sh and helper-pin gates all run against the git that actually ships.
    $trim = Remove-UnusedPortableGit $gitOut { param($line) Log $line }
    # Every entry missing is not a smaller git, it is a trim that silently stopped working -
    # a path it could not match, or a PortableGit laid out differently - and the pack would
    # go green shipping all of it.
    if ($trim.After -ge $trim.Before) {
        throw "PortableGit trim removed nothing - see the trim: lines in artifacts\pack.log"
    }

    $lfs = (& $gitExe 'lfs' 'version' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "bundled git lfs version failed - this archive is not PortableGit"
    }
    Log $lfs

    # What the trim must not have taken. GCM is spawned by git as a helper, so a missing
    # DLL in its folder shows up here and not at the user's first Sign in. A `!` alias runs
    # under usr\bin\sh exactly as an LFS hook does, so this is the hook path without a repo.
    $gcmVersion = (& $gitExe 'credential-manager' '--version' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "bundled git credential-manager --version failed after the trim"
    }
    Log ("GCM " + $gcmVersion)
    $shell = (& $gitExe '-c' 'alias.versio-sh=!echo sh-ok' 'versio-sh' | Out-String).Trim()
    if ($shell -ne 'sh-ok') {
        throw "bundled git cannot run sh after the trim (got '$shell') - hooks would fail"
    }

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
    Complete-Phase ('{0} trimmed to {1} + LFS + GCM + pin' -f (Format-Mb $trim.Before), (Format-Mb $trim.After))

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
    #
    # PER-USER, and that is what makes self-update possible. A per-machine install lives
    # under Program Files, so applying an update needs elevation - a UAC prompt on every
    # release, and a flat failure on a managed laptop whose user is not an administrator,
    # which is most of them. Per-user installs to %LOCALAPPDATA%\Versio, where the user can
    # write, and settings already live beside it in VersioUser (AppUserPaths). The MSI is
    # still built for IT to push through Intune; it installs per-user too.
    $vpkArgs = ('--legacyConsole --yes --skip-updates pack --packId Versio --packVersion {0} --packTitle Versio --packAuthors Versio --packDir {1} --mainExe Versio.exe --icon {2} --msi --instLocation PerUser --outputDir {3}' -f `
        $packVersion, (Quote $publish), (Quote $icon), (Quote $releases))
    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
        $vpkArgs += (' --releaseNotes {0}' -f (Quote $ReleaseNotes))
    }
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
    Write-Check 'Per-user MSI' $msi.FullName (Format-Mb $msi.Length)
    Write-Host ""
    Write-Host ("$(C '90')   payload {0:N0} files - {1} - full log artifacts\pack.log$(C '0')" -f `
        $payload.Count, (Format-Mb $payload.Sum))
    Write-Host ""
    Log ("PACKED " + $setup.FullName)
    Log ("PACKED " + $msi.FullName)

    if ($Plain -or $Unattended) {
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
