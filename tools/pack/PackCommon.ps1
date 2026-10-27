# Shared by Pack-Versio.ps1 and Publish-Versio.ps1. Dot-source it AFTER the caller's
# param block: the helpers read the caller's $Repo and $Plain, and Log writes to the
# caller's $script:LogPath, which the caller sets after dot-sourcing this file.
#
# Pure ASCII on purpose, like everything under tools\pack: Windows PowerShell 5.1 reads a
# BOM-less script as the ANSI codepage, so an em-dash here would arrive as mojibake inside
# a string literal and quietly corrupt the console output, or worse, a comparison.

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

# ---------------------------------------------------------------------------- log

function Log([string] $Line) {
    Add-Content -LiteralPath $script:LogPath -Value ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Line) -Encoding UTF8
}

# ---------------------------------------------------------------------------- phases

$script:PhaseTotal = 0
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
    # No phase started means nothing to animate. The publish called Invoke-LoggedProcess
    # outside a phase once, and strict mode turned the missing timer into a failed release.
    if ($Plain -or $null -eq $script:PhaseWatch) { return }
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
