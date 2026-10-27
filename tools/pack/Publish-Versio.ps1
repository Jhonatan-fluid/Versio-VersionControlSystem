#Requires -Version 5.1
<#
.SYNOPSIS
    Build a Versio release and publish it to GitHub, where installed copies find it.

.DESCRIPTION
    Interactive, and run from Publish_Release.bat. In order: checks the source tree, finds a
    GitHub token, asks for the version, opens Notepad for the release notes, fetches the
    previous published release so vpk can build a delta, runs Pack-Versio.ps1, asks once more,
    uploads, checks that GitHub actually serves what was uploaded, then commits and tags the
    version in this repository.

    Everything a child process prints goes to artifacts\publish.log (the pack keeps its own
    artifacts\pack.log). The token never does: it reaches vpk through a response file that is
    deleted on the way out, and nothing here writes it anywhere else.

    Exit 0 published (or uploaded a draft), 1 failed, 2 quit before anything was uploaded.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Repo,

    [Parameter(Mandatory = $true)]
    [string] $Dotnet,

    # No spinner and no ANSI, for a captured run.
    [switch] $Plain
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'PackCommon.ps1')

$script:LogPath = Join-Path $Repo 'artifacts\publish.log'
$script:PhaseTotal = 4

# PowerShell 5.1 still offers TLS 1.0 first on some machines, and api.github.com refuses it
# with an error that reads like a network outage rather than a protocol mismatch.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ---------------------------------------------------------------------------- small helpers

function Write-Line([string] $Color, [string] $Text) {
    Write-Host "$(C $Color)   $Text$(C '0')"
}

# One keypress-style question. Returns the lower-cased first letter of the answer, or the
# default when the user just presses Enter. Anything outside $Allowed asks again rather
# than guessing - a stray key must not publish.
function Read-Choice([string] $Prompt, [string[]] $Allowed, [string] $Default) {
    while ($true) {
        $answer = Read-Host "   $Prompt"
        if ([string]::IsNullOrWhiteSpace($answer)) {
            return $Default
        }
        $key = $answer.Trim().Substring(0, 1).ToLowerInvariant()
        if ($Allowed -contains $key) {
            return $key
        }
        Write-Line '93' ("Not a choice: {0}" -f $answer.Trim())
    }
}

function Get-UpdateRepo {
    $props = Join-Path $Repo 'Directory.Build.props'
    $text = [System.IO.File]::ReadAllText($props, (New-Object System.Text.UTF8Encoding $false))
    if ($text -notmatch '<VersioUpdateRepo>([^<]+)</VersioUpdateRepo>') {
        throw "Directory.Build.props has no VersioUpdateRepo"
    }
    return $Matches[1].Trim().TrimEnd('/')
}

# owner/name out of https://github.com/owner/name - the API wants the pair, vpk the URL.
function Get-RepoSlug([string] $Url) {
    if ($Url -notmatch '^https://github\.com/([^/]+)/([^/]+?)(\.git)?$') {
        throw "VersioUpdateRepo is not a https://github.com/owner/name URL: $Url"
    }
    return "$($Matches[1])/$($Matches[2])"
}

function Invoke-Git([string[]] $Arguments) {
    # Continue, locally: under 'Stop', Windows PowerShell 5.1 turns every line a native
    # command writes to stderr into a terminating NativeCommandError when it is redirected -
    # and git writes its progress there, so a successful push would throw.
    $ErrorActionPreference = 'Continue'
    $output = & git -C $Repo @Arguments 2>&1
    $code = $LASTEXITCODE
    Log ("git " + ($Arguments -join ' ') + " -> exit $code")
    return [pscustomobject]@{ Code = $code; Output = ($output | Out-String).Trim() }
}

# ---------------------------------------------------------------------------- the token

# The token is held in $script:Token and goes nowhere else: not to the log, not to the
# console, not onto a command line. Each of those was a way for it to end up somewhere it
# outlives this window - pack.log is attached to bug reports, and a command line is visible
# to every process on the machine for as long as vpk runs.
$script:Token = $null

function Invoke-GitHub([string] $Path) {
    $headers = @{
        Authorization = "Bearer $($script:Token)"
        Accept = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    return Invoke-RestMethod -Uri "https://api.github.com/$Path" -Headers $headers -UseBasicParsing
}

# Asks Git Credential Manager for the github.com sign-in it already holds, without letting it
# open a window. GCM_INTERACTIVE=never is the part that matters: GIT_TERMINAL_PROMPT=0 alone
# does not stop GCM's own GUI, which would pop up in the middle of a console script.
#
# A Process with its own stdin rather than a PowerShell pipe into git. Windows PowerShell 5.1
# encodes a piped string with $OutputEncoding and git then refused it: "refusing to work with
# credential missing protocol field" - the first key arrived with something in front of it.
function Get-StoredGitHubToken {
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'git'
        $psi.Arguments = 'credential fill'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
        $psi.EnvironmentVariables['GCM_INTERACTIVE'] = 'never'

        $p = [System.Diagnostics.Process]::Start($psi)
        $stdin = New-Object System.IO.StreamWriter($p.StandardInput.BaseStream, (New-Object System.Text.UTF8Encoding $false))
        $stdin.NewLine = "`n"
        $stdin.Write("protocol=https`nhost=github.com`n`n")
        $stdin.Close()
        $reply = $p.StandardOutput.ReadToEnd()
        $null = $p.StandardError.ReadToEnd()
        if (-not $p.WaitForExit(15000)) {
            $p.Kill()
            return $null
        }
        if ($p.ExitCode -ne 0) {
            return $null
        }
        foreach ($line in ($reply -split "`r?`n")) {
            if ($line.StartsWith('password=')) {
                return $line.Substring('password='.Length)
            }
        }
        return $null
    }
    catch {
        return $null
    }
}

function Test-TokenCanPublish([string] $Slug) {
    try {
        $info = Invoke-GitHub "repos/$Slug"
        return [bool]$info.permissions.push
    }
    catch {
        Log ("token check against $Slug failed: " + $_.Exception.Message)
        return $false
    }
}

# Environment variable, then the stored sign-in, then ask. Each candidate is tried against the
# releases repository before it is believed: a token that reads but cannot write would get all
# the way through a seven-minute pack and fail at the upload.
function Resolve-Token([string] $Slug) {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:VERSIO_RELEASE_TOKEN)) {
        $candidates += , @('VERSIO_RELEASE_TOKEN', $env:VERSIO_RELEASE_TOKEN)
    }
    $stored = Get-StoredGitHubToken
    if ($null -ne $stored) {
        $candidates += , @('your Git sign-in for github.com', $stored)
    }

    foreach ($candidate in $candidates) {
        $script:Token = $candidate[1]
        if (Test-TokenCanPublish $Slug) {
            Log ("token: " + $candidate[0] + " can publish to $Slug")
            return $candidate[0]
        }
        Write-Line '93' ("{0} cannot publish to {1}." -f $candidate[0], $Slug)
        Log ("token: " + $candidate[0] + " cannot publish to $Slug")
    }

    Write-Host ""
    Write-Line '37' "Paste a GitHub token with write access to $Slug"
    Write-Line '90' "(fine-grained: repository $Slug, Contents read and write). Input is hidden."
    $secure = Read-Host "   Token" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $script:Token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    if ([string]::IsNullOrWhiteSpace($script:Token) -or -not (Test-TokenCanPublish $Slug)) {
        throw "that token cannot publish to $Slug"
    }
    Log "token: typed in, can publish to $Slug"
    return 'the token you typed'
}

# vpk takes the token only as --token. A response file keeps it off the command line, where
# Invoke-LoggedProcess would write it into publish.log and any process on the machine could
# read it while vpk runs. System.CommandLine expands @file itself; measured with vpk 1.2.0,
# which answered 401 for a bogus token passed this way rather than complaining about the
# argument - so the file was read and the token was used.
function New-TokenResponseFile {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("versio-vpk-{0}.rsp" -f [guid]::NewGuid().ToString('N'))
    [System.IO.File]::WriteAllText($path, "--token`n$($script:Token)`n", (New-Object System.Text.UTF8Encoding $false))
    return $path
}

# ---------------------------------------------------------------------------- releases

# tag -> [version], for tags shaped vX.Y.Z. Anything else on the releases page is somebody's
# manual experiment and takes no part in choosing the next number.
function ConvertTo-ReleaseVersion([string] $Tag) {
    if ($Tag -match '^v(\d+\.\d+\.\d+)$') {
        return [version]$Matches[1]
    }
    return $null
}

# Returned with the unary comma, and callers assign it to a variable before piping it.
# Windows PowerShell 5.1's Invoke-RestMethod emits a JSON array as ONE object, so @(...)
# around the call counted an empty releases page as one release - measured on the empty
# versio-releases, which reported "releases: 1".
function Get-Releases([string] $Slug) {
    $response = Invoke-GitHub "repos/$Slug/releases?per_page=100"
    $list = New-Object System.Collections.ArrayList
    foreach ($item in $response) {
        [void]$list.Add($item)
    }
    return , $list.ToArray()
}

# ---------------------------------------------------------------------------- release notes

$script:NotesMarker = '//'

# Notepad, prefilled. The commit subjects since the last release tag are a starting point to
# cut down, not the notes themselves: they are written for the people reading this repository,
# and the notes are read by the people about to install it.
function Edit-ReleaseNotes([string] $Version, [string] $Draft) {
    if (-not (Test-Path -LiteralPath $Draft)) {
        $last = Invoke-Git @('describe', '--tags', '--abbrev=0', '--match', 'v*')
        if ($last.Code -eq 0 -and $last.Output.Length -gt 0) {
            $range = "$($last.Output)..HEAD"
            $since = "since $($last.Output)"
        } else {
            $range = 'HEAD'
            $since = '(no earlier release tag, so the last 30)'
        }
        $log = Invoke-Git @('log', $range, '--no-merges', '-n', '30', '--format=- %s')

        $lines = @(
            "$($script:NotesMarker) Release notes for Versio $Version, shown to users in the update prompt.",
            "$($script:NotesMarker) Lines starting with $($script:NotesMarker) are removed. Markdown is fine.",
            "$($script:NotesMarker) Save and close Notepad to continue. Leave nothing but these lines to cancel.",
            "$($script:NotesMarker)",
            "$($script:NotesMarker) Commits $since - a starting point, not the notes:",
            ""
        )
        if ($log.Code -eq 0 -and $log.Output.Length -gt 0) {
            $lines += $log.Output -split "`r?`n"
        }
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($Draft, (($lines -join "`r`n") + "`r`n"), $utf8)
    }

    while ($true) {
        Write-Line '37' "Opening Notepad for the release notes. Save and close it to continue."
        Log "notes: editing $Draft"
        Start-Process -FilePath 'notepad.exe' -ArgumentList ('"{0}"' -f $Draft) -Wait

        # Notepad on Windows 11 saves UTF-8 without a BOM by default; an older one may add the
        # BOM, which ReadAllText strips either way.
        $raw = [System.IO.File]::ReadAllText($Draft, [System.Text.Encoding]::UTF8)
        $kept = @($raw -split "`r?`n" | Where-Object { -not $_.TrimStart().StartsWith($script:NotesMarker) })
        $notes = ($kept -join "`n").Trim()

        if ($notes.Length -eq 0) {
            return $null
        }

        Write-Host ""
        Write-Line '93' "RELEASE NOTES"
        foreach ($line in ($notes -split "`n")) {
            Write-Line '37' $line
        }
        Write-Host ""
        $choice = Read-Choice "[U]se these / [E]dit again / [Q]uit [U]" @('u', 'e', 'q') 'u'
        if ($choice -eq 'u') {
            return $notes
        }
        if ($choice -eq 'q') {
            return $null
        }
    }
}

# ---------------------------------------------------------------------------- run

New-Item -ItemType Directory -Force -Path (Join-Path $Repo 'artifacts') | Out-Null
if (Test-Path -LiteralPath $script:LogPath) {
    Remove-Item -LiteralPath $script:LogPath -Force
}
Log "publish starting"

if (-not $Plain) {
    Write-Banner
    Write-Host "$(C '90')           RELEASE PIPELINE$(C '0')"
}

$updateRepo = Get-UpdateRepo
$slug = Get-RepoSlug $updateRepo
$script:bumpedFrom = $null
$uploaded = $false
$rsp = $null

try {
    # -- preflight ------------------------------------------------------------------

    Write-Host ""
    Write-Line '93' "PREFLIGHT$(C '90') - releases go to $updateRepo"

    $branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')).Output
    $dirty = (Invoke-Git @('status', '--porcelain', '--untracked-files=no')).Output
    Write-Line '37' "source branch: $branch"

    # Warned, not refused. Releasing from a branch is sometimes exactly right - a hotfix - but
    # it should never be an accident, and uncommitted changes are code that ships in the
    # installer while no commit anywhere says what it was.
    if ($branch -ne 'main' -or $dirty.Length -gt 0) {
        if ($branch -ne 'main') {
            Write-Line '93' "Not on main. The release will be built from '$branch'."
        }
        if ($dirty.Length -gt 0) {
            Write-Line '93' "Uncommitted changes will be built into the release:"
            foreach ($line in ($dirty -split "`r?`n" | Select-Object -First 10)) {
                Write-Line '90' "  $line"
            }
        }
        if ((Read-Choice "[C]ontinue anyway / [Q]uit [Q]" @('c', 'q') 'q') -ne 'c') {
            Write-Line '90' "Nothing touched."
            exit 2
        }
    }

    $tokenSource = Resolve-Token $slug
    Write-Line '92' "Publishing as: $tokenSource"

    $releases = Get-Releases $slug
    $published = @($releases | Where-Object { -not $_.draft -and -not $_.prerelease } |
        ForEach-Object { [pscustomobject]@{ Tag = $_.tag_name; Version = (ConvertTo-ReleaseVersion $_.tag_name) } } |
        Where-Object { $null -ne $_.Version } | Sort-Object Version -Descending)
    $latest = $null
    if ($published.Count -gt 0) {
        $latest = $published[0]
        Write-Line '37' ("latest published release: {0}" -f $latest.Tag)
    } else {
        Write-Line '37' "no published release yet - this will be the first"
    }

    # -- version --------------------------------------------------------------------

    $propsVersion = Get-VersioVersion
    $base = [version]$propsVersion
    if ($null -ne $latest -and $latest.Version -gt $base) {
        $base = $latest.Version
    }

    $chosen = Read-VersionChoice $base.ToString()
    if ($null -eq $chosen) {
        Write-Line '90' "Nothing touched."
        exit 2
    }

    # Velopack offers an update only when the feed's version is HIGHER than the installed
    # one, so publishing an equal or lower number would succeed here and reach nobody.
    if ($null -ne $latest -and [version]$chosen -le $latest.Version) {
        throw "$chosen is not newer than the published $($latest.Tag); installed copies would never be offered it"
    }
    $tag = "v$chosen"
    $clash = @($releases | Where-Object { $_.tag_name -eq $tag })
    if ($clash.Count -gt 0) {
        $kind = 'a release'
        if ($clash[0].draft) { $kind = 'a draft' }
        throw "$slug already has $kind tagged $tag - delete it on GitHub first, or choose another version"
    }
    Log "version $chosen (props $propsVersion)"

    # -- release notes --------------------------------------------------------------

    $notesDir = Join-Path $Repo 'artifacts\release-notes'
    New-Item -ItemType Directory -Force -Path $notesDir | Out-Null
    $draft = Join-Path $notesDir "$tag.draft.md"
    $final = Join-Path $notesDir "$tag.md"

    Write-Host ""
    $notes = Edit-ReleaseNotes $chosen $draft
    if ($null -eq $notes) {
        Write-Line '90' "No release notes - nothing published. The draft is kept at $draft."
        exit 2
    }
    [System.IO.File]::WriteAllText($final, $notes + "`n", (New-Object System.Text.UTF8Encoding $false))
    Log "notes: $final"

    # -- previous release, then the pack --------------------------------------------

    Write-Host ""
    $outDir = Join-Path $Repo 'artifacts\publish-releases'
    if (Test-Path -LiteralPath $outDir) {
        Remove-Item -LiteralPath $outDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    # Fetch-Deps first: the download below needs vpk, and on a fresh clone the pack would be
    # the first thing to fetch it.
    $fetch = Join-Path $Repo 'tools\pack\Fetch-Deps.ps1'
    $code = Invoke-LoggedProcess 'powershell.exe' ('-NoProfile -ExecutionPolicy Bypass -File {0} -Repo {1} -Dotnet {2}' -f `
        (Quote $fetch), (Quote $Repo), (Quote $Dotnet))
    if ($code -ne 0) {
        throw "Fetch-Deps failed (exit $code) - see artifacts\publish.log"
    }
    $vpk = Join-Path $Repo 'third_party\cache\vpk\vpk.exe'
    $rsp = New-TokenResponseFile

    if ($null -ne $latest) {
        Start-Phase ("Previous release  " + $latest.Tag)
        $code = Invoke-LoggedProcess $vpk ('--legacyConsole --yes --skip-updates download github --outputDir {0} --repoUrl {1} @{2}' -f `
            (Quote $outDir), $updateRepo, (Quote $rsp))
        if ($code -eq 0) {
            Complete-Phase 'delta base ready'
        } else {
            # Not fatal. Without the previous package vpk packs a full release only, and
            # every user downloads the whole thing instead of the difference. Slower, correct.
            Complete-Phase "not fetched (exit $code) - full package only"
        }
    }

    Write-Host ""
    Write-Line '92' "PACKING Versio $chosen"
    $packArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Repo 'tools\pack\Pack-Versio.ps1'),
        '-Repo', $Repo, '-Dotnet', $Dotnet, '-Version', $chosen,
        '-ReleaseNotes', $final, '-ReleasesDir', $outDir, '-Unattended')
    if ($Plain) {
        $packArgs += '-Plain'
    }
    # In this console rather than through Invoke-LoggedProcess: the pack draws its own phase
    # lines, and they are the progress the user is waiting on for the next several minutes.
    & powershell.exe @packArgs
    $packCode = $LASTEXITCODE
    if ($packCode -ne 0) {
        # The pack reverts its own version bump when it fails, so there is nothing to undo.
        throw "the pack failed (exit $packCode) - see artifacts\pack.log"
    }
    if ($chosen -ne $propsVersion) {
        $script:bumpedFrom = $propsVersion
    }

    # -- confirm ----------------------------------------------------------------------

    $setup = Get-ChildItem -LiteralPath $outDir -Filter '*Setup.exe' | Select-Object -First 1
    $full = Get-ChildItem -LiteralPath $outDir -Filter "*-$chosen-full.nupkg" | Select-Object -First 1
    $delta = Get-ChildItem -LiteralPath $outDir -Filter "*-$chosen-delta.nupkg" | Select-Object -First 1
    if ($null -eq $setup -or $null -eq $full) {
        throw "the pack reported success but $outDir has no Setup.exe or $chosen full package"
    }

    Write-Host ""
    Write-Line '93' "READY TO PUBLISH"
    Write-Line '37' ("version     {0}" -f $chosen)
    Write-Line '37' ("to          {0}" -f $updateRepo)
    Write-Line '37' ("Setup.exe   {0}" -f (Format-Mb $setup.Length))
    Write-Line '37' ("full        {0}" -f (Format-Mb $full.Length))
    if ($null -ne $delta) {
        Write-Line '37' ("delta       {0}" -f (Format-Mb $delta.Length))
    } else {
        Write-Line '37' "delta       none - users download the full package"
    }
    Write-Host ""
    Write-Line '90' "Publish makes it live: every installed Versio is offered it at its next launch."
    Write-Line '90' "Draft uploads it where only repository writers can see it, to check first."
    $how = Read-Choice "[P]ublish / [D]raft / [Q]uit [Q]" @('p', 'd', 'q') 'q'
    if ($how -eq 'q') {
        throw 'quit before upload'
    }
    $publishNow = $how -eq 'p'

    # -- upload ------------------------------------------------------------------------

    Write-Host ""
    Start-Phase 'Upload to GitHub'
    $uploadArgs = ('--legacyConsole --yes --skip-updates upload github --outputDir {0} --repoUrl {1} --releaseName {2} --tag {3} @{4}' -f `
        (Quote $outDir), $updateRepo, (Quote "Versio $chosen"), $tag, (Quote $rsp))
    if ($publishNow) {
        $uploadArgs += ' --publish true'
    }
    $code = Invoke-LoggedProcess $vpk $uploadArgs
    if ($code -ne 0) {
        throw "vpk upload failed (exit $code) - see artifacts\publish.log; check $updateRepo/releases for a partial draft"
    }
    $uploaded = $true
    Complete-Phase

    # -- verify ------------------------------------------------------------------------

    # vpk's exit code says the upload ran. This says GitHub serves it - the release the app
    # will actually find, with the feed file the app actually reads.
    Start-Phase 'Verify what GitHub serves'
    $after = Get-Releases $slug
    $release = $after | Where-Object { $_.tag_name -eq $tag } | Select-Object -First 1
    if ($null -eq $release) {
        throw "the upload finished but $slug has no release tagged $tag"
    }
    $assetNames = @($release.assets | ForEach-Object { $_.name })
    foreach ($required in @('releases.win.json', 'Versio-win-Setup.exe', $full.Name)) {
        if ($assetNames -notcontains $required) {
            throw "release $tag is missing $required (it has: $($assetNames -join ', '))"
        }
    }
    if ($publishNow) {
        $latestNow = Invoke-GitHub "repos/$slug/releases/latest"
        if ($latestNow.tag_name -ne $tag) {
            throw "release $tag uploaded, but GitHub's latest release is $($latestNow.tag_name)"
        }
    }
    Complete-Phase ("{0} assets" -f $assetNames.Count)

    # -- record it in the source -------------------------------------------------------

    Write-Host ""
    if (-not $publishNow) {
        # A draft is a rehearsal, so the source tree goes back to how it was: committing and
        # tagging a version nobody can install would make the next real run refuse that tag.
        if ($null -ne $script:bumpedFrom) {
            Set-VersioVersion $script:bumpedFrom
            Log "draft: VersioVersion reverted to $($script:bumpedFrom)"
        }
        Write-Line '92' "DRAFT UPLOADED - $updateRepo/releases"
        Write-Line '37' "Installed copies cannot see it. To ship it for real, delete the draft on"
        Write-Line '37' "GitHub and run Publish_Release.bat again, choosing Publish."
        exit 0
    }

    if ($null -ne $script:bumpedFrom) {
        $null = Invoke-Git @('add', 'Directory.Build.props')
        $commit = Invoke-Git @('commit', '-m', "Release $chosen", '--', 'Directory.Build.props')
        if ($commit.Code -ne 0) {
            Write-Line '93' "Could not commit the version bump: $($commit.Output)"
        }
    }
    $tagged = Invoke-Git @('tag', '-a', $tag, '-m', "Versio $chosen")
    if ($tagged.Code -ne 0) {
        Write-Line '93' "Could not tag $($tag): $($tagged.Output)"
    }

    Write-Line '92' "PUBLISHED Versio $chosen - $updateRepo/releases/tag/$tag"
    Write-Host ""
    if ((Read-Choice "Push '$branch' and tag $tag to origin? [Y/n]" @('y', 'n') 'y') -eq 'y') {
        $push = Invoke-Git @('push', 'origin', $branch, $tag)
        if ($push.Code -eq 0) {
            Write-Line '92' "Pushed."
        } else {
            Write-Line '93' "Push failed - run: git push origin $branch $tag"
            Write-Line '90' $push.Output
        }
    } else {
        Write-Line '90' "Not pushed. When ready: git push origin $branch $tag"
    }
    exit 0
}
catch {
    $message = $_.Exception.Message

    # Before the upload, a failure leaves nothing behind but local files: put the version
    # back so the next run's menu starts from the number that is really published. After it,
    # the release EXISTS - reverting would make the source disagree with what users install.
    if (-not $uploaded -and $null -ne $script:bumpedFrom) {
        Set-VersioVersion $script:bumpedFrom
        Write-Line '93' "VersioVersion reverted to $($script:bumpedFrom) - nothing was published."
    }

    if ($message -eq 'quit before upload') {
        Write-Line '90' "Nothing uploaded."
        exit 2
    }

    Write-Host ""
    Write-Line '91' "PUBLISH FAILED - $message"
    if ($uploaded) {
        Write-Line '93' "The upload had already happened: check $updateRepo/releases."
    }
    Log ("FAILED: " + $message)
    Write-Line '90' "log: $($script:LogPath)"
    exit 1
}
finally {
    if ($null -ne $rsp -and (Test-Path -LiteralPath $rsp)) {
        Remove-Item -LiteralPath $rsp -Force
    }
    $script:Token = $null
}
