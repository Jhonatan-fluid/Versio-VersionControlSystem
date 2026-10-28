# Dot-sourced by Pack-Versio.ps1. Takes the parts of PortableGit that Versio never executes
# out of the payload before vpk packs it.
#
# WHY: a delta update downloads in about a second, and then Velopack rebuilds the whole
# package from the installed copy plus the delta - 91 s on 0.4.10 -> 0.4.11 (FINDINGS
# 2026-10-28, "The update card sat on 70%"). Most of what it recompresses is the bundled
# git, which was 405 MB of a 686 MB install. Every byte cut here is cut from every install
# and every update.
#
# A DENYLIST, NOT AN ALLOWLIST. An allowlist would have to name every DLL that git, curl,
# ssh, sh, git-lfs and Git Credential Manager load, plus every coreutil an LFS hook or the
# astextplain diff driver calls - and a PortableGit bump that added one more would ship a git
# that fails at the first push, on somebody else's machine. A denylist fails the other way:
# a new PortableGit that adds something just ships it, a few MB bigger. Each entry below is
# a whole subsystem whose only callers are commands Versio never runs.
#
# KEPT, ON PURPOSE - read this before adding to the list:
#   * mingw64\bin, mingw64\libexec\git-core: git, git-remote-https, curl, OpenSSL. The DLLs in
#     libexec\git-core DUPLICATE mingw64\bin, and that is deliberate upstream: Windows searches
#     the exe's own folder, then System32, and only then PATH, so without the local copy
#     git-remote-https would load whatever libcrypto some other installer left in System32.
#   * Git Credential Manager (git-credential-manager.exe, gcmcore, Avalonia, MSAL, SkiaSharp,
#     in both of those folders). Sign in is GCM; MinGit was rejected for lacking it (FINDINGS
#     2026-08-24, CredentialHelperSelector). Nothing GCM-shaped is on this list.
#   * git-lfs.exe. Versio probes `git lfs version`, and the TIA fixture is an LFS repository.
#   * usr\bin's MSYS runtime, sh/bash and coreutils. Every hook runs under sh - git lfs
#     install writes four of them - and so does every `!` alias and the astextplain textconv.
#   * file + usr\share\misc\magic.mgc, antiword, pdftotext: astextplain's tools. etc\gitattributes
#     routes *.doc/*.pdf diffs through it, and Versio runs plain `git diff`, so a missing
#     tool would turn a Word manual's diff into an error.
#   * ssh, ssh-keygen: SSH remotes. PATH puts usr\bin first, so this is the ssh git runs.
#   * gpg: commit.gpgsign in a user's global config calls it. Without it commits fail.
#   * every licenses folder and LICENSE.txt: GPL-2.0 compliance travels with the binaries.
#
# Pure ASCII, like everything under tools\pack (PackagingManifestTests).

$script:PortableGitDenylist = @(
    # Editors. Versio never opens one: every commit passes its message, and the one command
    # that would open an editor (rebase --continue) runs with GIT_EDITOR=":", git's spelling
    # for "none" (GitCliClient.RunAsync). An editor under a redirected console is a hang, so
    # a missing editor is the better failure anyway. Vim alone is six 3.9 MB copies of one
    # exe plus 44 MB of runtime.
    'usr\bin\vim.exe', 'usr\bin\vimdiff.exe', 'usr\bin\view.exe', 'usr\bin\rvim.exe',
    'usr\bin\rview.exe', 'usr\bin\ex.exe', 'usr\bin\vimtutor', 'usr\bin\xxd.exe',
    'usr\share\vim',
    'usr\bin\nano.exe', 'usr\bin\rnano.exe', 'usr\share\nano',

    # Perl. Its callers in this PortableGit are git send-email, OpenSSL's c_rehash and
    # docx2txt.pl - none of which Versio runs. The cost is one fallback: astextplain tries
    # docx2txt.pl for a .docx and falls back to `cat` (FINDINGS 2026-10-28).
    'usr\bin\perl.exe', 'usr\bin\perl5.*.exe', 'usr\bin\msys-perl5_*.dll',
    'usr\bin\core_perl', 'usr\bin\vendor_perl',
    'usr\lib\perl5', 'usr\share\perl5', 'mingw64\share\perl5',

    # Subversion's libraries. Their one consumer is git svn, and this PortableGit does not
    # even ship git-svn; they are dead weight from the MSYS package set.
    'usr\bin\msys-svn_*.dll',

    # Tcl/Tk and the two programs written in it, gitk and git gui. Versio is the GUI.
    # git-gui--askpass goes with them: it is only an askpass when SSH_ASKPASS or core.askPass
    # names it, and PortableGit's launcher sets neither.
    'mingw64\bin\gitk', 'mingw64\bin\tclsh*.exe', 'mingw64\bin\wish*.exe',
    'mingw64\bin\tcl86.dll', 'mingw64\bin\tk86.dll',
    'mingw64\lib\tcl8', 'mingw64\lib\tcl8.6', 'mingw64\lib\tk8.6', 'mingw64\lib\thread*',
    'mingw64\lib\reg1.3', 'mingw64\lib\tclConfig.sh', 'mingw64\lib\tkConfig.sh',
    'mingw64\share\git-gui',
    'mingw64\libexec\git-core\git-gui', 'mingw64\libexec\git-core\git-gui.tcl',
    'mingw64\libexec\git-core\git-gui--askpass', 'mingw64\libexec\git-core\git-gui--askyesno',
    'mingw64\libexec\git-core\git-citool',
    'mingw64\libexec\git-core\tcl86.dll', 'mingw64\libexec\git-core\tk86.dll',

    # git's HTML manual, for `git help`, which opens a browser. Only git-doc: the other
    # folders under share\doc carry upstream COPYING files.
    'mingw64\share\doc\git-doc',

    # The terminal. Versio spawns git with CreateNoWindow and redirected pipes; nothing here
    # ever draws a console for a human.
    'usr\bin\mintty.exe', 'usr\share\mintty', 'git-bash.exe', 'usr\bin\tig.exe'
)

function Get-TreeBytes([string] $Path) {
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return [long] 0 }
    return [long] $sum
}

# Removes every denylisted path under $Root and says what it did. A pattern that matches
# nothing is logged rather than thrown: it means a PortableGit bump moved or dropped that
# piece, which is a reason to re-read this list, not a reason to fail the pack.
function Remove-UnusedPortableGit([string] $Root, [scriptblock] $Write = { param($line) Write-Host $line }) {
    # The long form, so the relative names in the log line up with FullName even when the
    # caller passed an 8.3 path such as C:\Users\JHONAT~1.
    $Root = (Get-Item -LiteralPath $Root).FullName.TrimEnd('\')
    $before = Get-TreeBytes $Root
    foreach ($entry in $script:PortableGitDenylist) {
        # Get-Item, not Get-ChildItem: on a folder Get-ChildItem would answer its contents
        # and leave the folder. -Path, not -LiteralPath, so perl5.*.exe can match a version.
        $found = @(Get-Item -Path (Join-Path $Root $entry) -Force -ErrorAction SilentlyContinue)
        if ($found.Count -eq 0) {
            & $Write ("trim: not present {0}" -f $entry)
            continue
        }
        foreach ($item in $found) {
            $bytes = if ($item.PSIsContainer) { Get-TreeBytes $item.FullName } else { [long] $item.Length }
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
            & $Write ("trim: removed {0} ({1:N1} MB)" -f $item.FullName.Substring($Root.Length + 1), ($bytes / 1MB))
        }
    }
    Set-WordDiffBinary $Root $Write
    $after = Get-TreeBytes $Root
    & $Write ("trim: PortableGit {0:N1} MB -> {1:N1} MB" -f ($before / 1MB), ($after / 1MB))
    return [pscustomobject] @{ Before = $before; After = $after }
}

# The price of dropping perl, paid honestly. PortableGit's etc\gitattributes sends *.docx and
# its three siblings through astextplain, whose extractor for them is docx2txt.pl; with perl
# gone it falls back to `cat`, and the diff panel would show a Word file's zip bytes as if
# they were text. Measured 2026-10-28 with PATH holding only System32 - on a machine that also
# has Git for Windows, that install's perl answers instead, which is how a quick check on a
# developer box says everything still works. `-diff` makes git print "Binary files differ",
# which UnifiedDiffParser already reads, as it does for every vendor binary project file.
# .doc and .pdf keep their extractors (antiword, pdftotext): those are not perl.
function Set-WordDiffBinary([string] $Root, [scriptblock] $Write) {
    $path = Join-Path $Root 'etc\gitattributes'
    if (-not (Test-Path -LiteralPath $path)) {
        & $Write "trim: not present etc\gitattributes"
        return
    }
    $text = [System.IO.File]::ReadAllText($path)
    $pattern = '(?im)^(\*\.(?:docx|docm|dotx|dotm))([ \t]+)diff=astextplain[ \t]*$'
    $count = [regex]::Matches($text, $pattern).Count
    if ($count -eq 0) {
        & $Write "trim: etc\gitattributes routes no Word 2007 pattern through astextplain"
        return
    }
    $text = [regex]::Replace($text, $pattern, '$1$2-diff')
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
    & $Write ("trim: etc\gitattributes - {0} Word 2007 patterns set to -diff (docx2txt is perl)" -f $count)
}
