namespace Versio.Infrastructure.Git;

/// <summary>
/// Which <c>git.exe</c> Versio will actually run.
/// </summary>
/// <remarks>
/// <para>
/// A packaged install ships PortableGit at <c>git\cmd\git.exe</c> beside <c>Versio.exe</c>.
/// A development build has no such folder, so the name <c>git</c> is left for PATH. Preferring
/// the bundled copy when it exists is what makes behaviour identical on a machine that also
/// has Git for Windows, a vendor toolchain's git, or nothing at all.
/// </para>
/// <para>
/// Resolving from PATH used to be the only option, and the <see cref="Versio.Core.Git.GitPresence"/>
/// sentence still names PATH for a development build that cannot find it. The packaged case is
/// a missing payload, not a missing PATH entry.
/// </para>
/// </remarks>
public static class GitExecutable
{
    /// <summary>What PATH will resolve when no bundled copy is beside the app.</summary>
    public const string PathFallback = "git";

    /// <summary>
    /// The PortableGit entry point, relative to <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <c>cmd\git.exe</c> is the documented launcher: it puts mingw, usr/bin and the
    /// credential helper on PATH for that process. Calling <c>mingw64\bin\git.exe</c>
    /// directly is how LFS and GCM go missing while <c>--version</c> still works.
    /// </remarks>
    public static readonly string BundledRelativePath =
        Path.Combine("git", "cmd", "git.exe");

    /// <summary>
    /// The git this process should spawn: bundled if present, otherwise PATH.
    /// </summary>
    public static string Resolve(string? baseDirectory = null)
    {
        var root = string.IsNullOrEmpty(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
        var bundled = Path.GetFullPath(Path.Combine(root, BundledRelativePath));
        return File.Exists(bundled) ? bundled : PathFallback;
    }

    /// <summary>
    /// True when <paramref name="gitExecutable"/> is the PortableGit launcher, not a PATH name.
    /// </summary>
    public static bool IsBundled(string gitExecutable)
    {
        if (string.IsNullOrEmpty(gitExecutable) || !Path.IsPathRooted(gitExecutable))
        {
            return false;
        }

        var normalised = gitExecutable.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var suffix = Path.DirectorySeparatorChar + BundledRelativePath;
        return normalised.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PATH, credential helper, and anything else a bundled PortableGit process must inherit.
    /// </summary>
    /// <remarks>
    /// One call so a future spawn cannot forget the helper pin. Development builds that resolve
    /// <c>git</c> from PATH are left alone — their helper is the engineer's, on purpose.
    /// </remarks>
    public static void ApplyBundledDefaults(System.Diagnostics.ProcessStartInfo psi, string gitExecutable)
    {
        PrependHelperPath(psi, gitExecutable);
        PinCredentialHelper(psi, gitExecutable);
    }

    /// <summary>
    /// Put PortableGit's own bins ahead of PATH so <c>git lfs</c> and GCM resolve as they do
    /// in a Git for Windows install.
    /// </summary>
    /// <remarks>
    /// A rooted <c>FileName</c> is not enough: git still spawns helpers by name. Without this,
    /// a packaged Versio would run our git.exe and the engineer's (or nobody's) credential
    /// helper, which is the class of bug <c>GitPresence.ExecutablePath</c> exists to diagnose.
    /// </remarks>
    public static void PrependHelperPath(System.Diagnostics.ProcessStartInfo psi, string gitExecutable)
    {
        if (!IsBundled(gitExecutable))
        {
            return;
        }

        var cmd = Path.GetDirectoryName(gitExecutable);
        if (string.IsNullOrEmpty(cmd))
        {
            return;
        }

        var root = Path.GetFullPath(Path.Combine(cmd, ".."));
        var extras = new[]
        {
            cmd,
            Path.Combine(root, "mingw64", "bin"),
            Path.Combine(root, "usr", "bin"),
            Path.Combine(root, "usr", "lib", "git-core"),
        }.Where(Directory.Exists);

        var prefix = string.Join(Path.PathSeparator, extras);
        if (prefix.Length == 0)
        {
            return;
        }

        // Accessing Environment copies the current process environment. Prepend, do not
        // replace: ssh.exe from Windows OpenSSH and corporate proxies still have to resolve.
        var inherited = psi.Environment.TryGetValue("PATH", out var existing)
            ? existing
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        psi.Environment["PATH"] = prefix + Path.PathSeparator + inherited;
    }

    /// <summary>
    /// Make Git Credential Manager the helper for bundled git, and only for bundled git.
    /// </summary>
    /// <remarks>
    /// PortableGit ships <c>credential.helper=helper-selector</c> so the first credential use
    /// can ask. That window is a GUI, so <c>GIT_TERMINAL_PROMPT=0</c> does not stop it — every
    /// background fill (the poller, Verify, HasStored) pops <c>CredentialHelperSelector</c>
    /// again. Ticking Manager without "Always use this from now on" is per-process; ticking
    /// Always writes PortableGit's system gitconfig, which a per-machine MSI cannot because
    /// Program Files is not writable.
    /// <para>
    /// Empty helper then <c>manager</c>: git appends helpers, and an empty value is the
    /// documented reset. PATH git is not touched — a plant's corporate helper stays theirs.
    /// The same pin is written into the payload at pack time so a spawn we do not own still
    /// sees it.
    /// </para>
    /// </remarks>
    public static void PinCredentialHelper(System.Diagnostics.ProcessStartInfo psi, string gitExecutable)
    {
        if (!IsBundled(gitExecutable))
        {
            return;
        }

        // Options before the verb: git -c … credential fill, never git credential -c …
        psi.ArgumentList.Insert(0, "credential.helper=manager");
        psi.ArgumentList.Insert(0, "-c");
        psi.ArgumentList.Insert(0, "credential.helper=");
        psi.ArgumentList.Insert(0, "-c");
    }
}
