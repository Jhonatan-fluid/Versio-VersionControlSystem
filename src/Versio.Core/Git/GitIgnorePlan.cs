namespace Versio.Core.Git;

/// <summary>What adopting a project needs written into <c>.gitignore</c>, if anything.</summary>
public readonly record struct GitIgnorePlan(
    bool ChangeNeeded,
    string Content,
    IReadOnlyList<string> AddedPatterns)
{
    public static GitIgnorePlan NoChange(string content) => new(false, content, []);
}

/// <summary>
/// Plans the <c>.gitignore</c> rules a tracked PLC project needs.
/// </summary>
/// <remarks>
/// <para>
/// Toolchains write files beside a project that are nobody's business but the local machine's.
/// Measured on CODESYS 3.5.22.20: opening <c>Machine.project</c> creates <c>Machine.~u</c>
/// while it is held, and leaves <c>Machine-AllUsers.opt</c> and
/// <c>Machine-&lt;user&gt;-&lt;machine&gt;.opt</c> behind afterwards.
/// </para>
/// <para>
/// Untracked, those turn up in <c>git status</c>, in the project panel, and in anything that
/// stages everything. The per-user one is worse than noise: <b>its name carries the username
/// and the machine name</b>, so committing it puts both into history permanently, and it
/// differs for every engineer who opens the project — a file that can only ever conflict.
/// </para>
/// <para>
/// <b>Created when absent.</b> A repository with no <c>.gitignore</c> is the common case for
/// somebody who has only ever kept PLC projects in a folder, and telling them to write one
/// before the tool behaves is not an answer.
/// </para>
/// <para>
/// <b>An existing decision is never overridden.</b> A repository that already mentions a
/// pattern — including a negation like <c>!Machine-AllUsers.opt</c>, which means somebody
/// deliberately wants it tracked — is left exactly as it is.
/// </para>
/// </remarks>
public static class GitIgnore
{
    private const string Header =
        "# Added by Versio: files the PLC toolchain writes beside a project.";

    private const string Header2 =
        "# The per-user one carries a username and machine name in its filename.";

    /// <param name="existing">Current file contents, or empty when there is no file.</param>
    /// <param name="patterns">Globs to cover, e.g. <c>plc/Machine-*.opt</c>.</param>
    public static GitIgnorePlan Plan(string existing, IReadOnlyList<string> patterns)
    {
        var covered = ExistingPatterns(existing);

        var missing = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize)
            .Where(p => !covered.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            return GitIgnorePlan.NoChange(existing);
        }

        var builder = new System.Text.StringBuilder(existing);

        // A file that does not end in a newline would otherwise have our first rule welded to
        // its last one, silently changing a pattern somebody else wrote.
        if (builder.Length > 0 && !existing.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(Header).Append('\n');
        builder.Append(Header2).Append('\n');

        foreach (var pattern in missing)
        {
            builder.Append(pattern).Append('\n');
        }

        return new GitIgnorePlan(true, builder.ToString(), missing);
    }

    /// <summary>
    /// Every pattern the file already speaks about, negations included.
    /// </summary>
    /// <remarks>
    /// A leading <c>!</c> is stripped before comparing, so <c>!Machine-AllUsers.opt</c> counts
    /// as covered. That is the point: somebody wrote it on purpose, and re-adding the positive
    /// rule underneath would quietly reverse their decision.
    /// </remarks>
    private static HashSet<string> ExistingPatterns(string existing)
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in existing.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            patterns.Add(Normalize(line.StartsWith('!') ? line[1..] : line));
        }

        return patterns;
    }

    /// <summary>Forward slashes, no leading slash — the shape git compares.</summary>
    private static string Normalize(string pattern) =>
        pattern.Trim().Replace('\\', '/').TrimStart('/');
}
