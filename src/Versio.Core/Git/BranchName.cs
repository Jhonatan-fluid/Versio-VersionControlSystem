namespace Versio.Core.Git;

/// <summary>Whether a proposed branch name is one git will accept, and why not if it is not.</summary>
/// <remarks>
/// <see cref="Reason"/> is written to be shown to a person, not logged and forgotten: it names
/// the character or the rule, because "invalid branch name" sends somebody to a search engine
/// for a rule git already knows and could have stated.
/// </remarks>
public readonly record struct BranchNameCheck(bool IsValid, string Reason)
{
    public static readonly BranchNameCheck Ok = new(true, string.Empty);

    public static BranchNameCheck Rejected(string reason) => new(false, reason);
}

/// <summary>
/// The rules git applies to a branch name, applied before git is asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not just let git refuse?</b> Because it refuses <i>after</i> a process spawn, with a
/// message written for the command line — <c>fatal: 'feature branch' is not a valid branch
/// name</c> — which arrives in a status bar with no indication of which character was the
/// problem or what to type instead. Checking first turns that into a sentence beside the box
/// while it is still being typed.
/// </para>
/// <para>
/// <b>This does not replace git's check; it precedes it.</b> Git remains the authority — a name
/// that passes here can still be refused, most obviously when it already exists or collides
/// with an existing ref path (<c>feature</c> and <c>feature/x</c> cannot both exist). The
/// caller must still handle a failed <c>CreateBranchAsync</c>, and the tests say so.
/// </para>
/// <para>
/// Transcribed from <c>git check-ref-format</c>'s documented rules, with the ones that cannot
/// apply to a branch name left out — a branch is always <c>refs/heads/&lt;name&gt;</c>, so the
/// "must contain a slash" rule for full refs does not.
/// </para>
/// </remarks>
public static class BranchName
{
    /// <summary>Characters git rejects anywhere in a ref name.</summary>
    /// <remarks>
    /// <c>~^:?*[</c> and backslash are refused because they are ref-syntax operators or, in the
    /// backslash's case, a path separator on Windows that would silently create a nested ref.
    /// A space is refused too: git allows it in no ref, and it is the single most common thing
    /// somebody types.
    /// </remarks>
    private static readonly char[] Forbidden = [' ', '~', '^', ':', '?', '*', '[', '\\'];

    /// <summary>The longest name worth allowing.</summary>
    /// <remarks>
    /// Not a git rule — git's own limit is the filesystem's, since a loose ref is a file. 200
    /// is far past any real branch name and far short of <c>MAX_PATH</c> once a repository path
    /// is prefixed, which is the limit that would actually bite on Windows and would do it as
    /// an unreadable IO error rather than as a refusal.
    /// </remarks>
    public const int MaxLength = 200;

    /// <summary>Check a proposed branch name.</summary>
    public static BranchNameCheck Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BranchNameCheck.Rejected("A branch needs a name.");
        }

        // Trimming is the caller's job, and doing it here would silently accept a name with a
        // trailing space and then create a different one — the class of surprise this type
        // exists to prevent.
        if (name != name.Trim())
        {
            return BranchNameCheck.Rejected("A branch name cannot begin or end with a space.");
        }

        if (name.Length > MaxLength)
        {
            return BranchNameCheck.Rejected($"Too long — keep it under {MaxLength} characters.");
        }

        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                return BranchNameCheck.Rejected("A branch name cannot contain control characters.");
            }

            if (Array.IndexOf(Forbidden, c) >= 0)
            {
                return BranchNameCheck.Rejected(
                    c == ' '
                        ? "A branch name cannot contain spaces — try a dash or a slash."
                        : $"A branch name cannot contain '{c}'.");
            }
        }

        if (name.StartsWith('-'))
        {
            // Git refuses this because the name would be indistinguishable from an option on
            // its own command line — which is also why it must never be "fixed" by quoting.
            return BranchNameCheck.Rejected("A branch name cannot start with '-'.");
        }

        if (name is "@")
        {
            return BranchNameCheck.Rejected("'@' means HEAD to git, so it cannot be a branch name.");
        }

        if (name.Contains("@{", StringComparison.Ordinal))
        {
            return BranchNameCheck.Rejected("'@{' is reflog syntax, so it cannot appear in a name.");
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return BranchNameCheck.Rejected("'..' is range syntax, so it cannot appear in a name.");
        }

        if (name.StartsWith('/') || name.EndsWith('/'))
        {
            return BranchNameCheck.Rejected("A branch name cannot begin or end with '/'.");
        }

        if (name.Contains("//", StringComparison.Ordinal))
        {
            return BranchNameCheck.Rejected("A branch name cannot contain an empty path segment.");
        }

        if (name.EndsWith('.'))
        {
            return BranchNameCheck.Rejected("A branch name cannot end with '.'.");
        }

        // Per SEGMENT rather than for the whole name: refs are paths, and git applies these
        // rules to each component. "feature/.hidden" and "feature/x.lock" are both refused,
        // and neither would be caught by looking at the string as a whole.
        foreach (var segment in name.Split('/'))
        {
            if (segment.StartsWith('.'))
            {
                return BranchNameCheck.Rejected("No part of a branch name may start with '.'.");
            }

            if (segment.EndsWith(".lock", StringComparison.Ordinal))
            {
                // Git writes refs/heads/<name>.lock while updating a ref, so a branch of that
                // name would collide with the lock file for its own neighbour.
                return BranchNameCheck.Rejected("No part of a branch name may end with '.lock'.");
            }
        }

        return BranchNameCheck.Ok;
    }
}
