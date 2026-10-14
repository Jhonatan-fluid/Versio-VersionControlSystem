namespace Versio.Core.Git;

/// <summary>
/// Parses <c>git status --porcelain=v2 --branch --untracked-files=all -z</c>.
/// </summary>
/// <remarks>
/// <para>
/// Porcelain v2 is git's machine-readable status format and is explicitly guaranteed
/// stable across versions — unlike the human output, which is localised and reflows.
/// </para>
/// <para>
/// <c>-z</c> matters for more than convenience: without it git <b>quotes and
/// escapes</b> paths containing spaces or non-ASCII, and CODESYS object names routinely
/// contain both. NUL-separated output needs no unquoting, so a POU called
/// <c>Kühlschrank Regelung</c> round-trips exactly.
/// </para>
/// </remarks>
public static class PorcelainV2Parser
{
    public static GitStatus Parse(string? output)
    {
        var branch = new BranchInfo { Name = "HEAD", IsCurrent = true, IsDetached = true };
        var changes = new List<FileChange>();
        var sawAheadBehind = false;

        if (string.IsNullOrEmpty(output))
        {
            return new GitStatus { Branch = branch };
        }

        // Entries are NUL-terminated. A rename entry contains an extra NUL separating the
        // new path from the old one, so the reader has to pull the next field itself.
        var fields = output.Split('\0');
        var i = 0;

        while (i < fields.Length)
        {
            var line = fields[i++];
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            switch (line[0])
            {
                case '#':
                    // Whether branch.ab appeared at all has to be remembered here, because
                    // git states "the upstream's ref is gone" by omitting the line entirely
                    // — there is nothing left to count against — and an omission cannot be
                    // seen from inside the per-line handler.
                    sawAheadBehind |= line.StartsWith("# branch.ab ", StringComparison.Ordinal);
                    branch = ApplyHeader(branch, line);
                    break;

                case '1':
                    if (TryParseOrdinary(line, out var ordinary))
                    {
                        changes.Add(ordinary);
                    }

                    break;

                case '2':
                    // Rename/copy: the original path is the following NUL-separated field.
                    var original = i < fields.Length ? fields[i++] : null;
                    if (TryParseRename(line, original, out var renamed))
                    {
                        changes.Add(renamed);
                    }

                    break;

                case 'u':
                    if (TryParseUnmerged(line, out var conflicted))
                    {
                        changes.Add(conflicted);
                    }

                    break;

                case '?':
                    // Length-checked like every other arm: a stream truncated right after
                    // the tag — git killed mid-status — leaves a bare "?" whose slice threw
                    // out of the whole parse, taking the refresh with it. The contract the
                    // '1' arm states holds here too: a truncated read must not take down
                    // the status refresh; the torn entry is dropped, never invented.
                    if (line.Length > 2)
                    {
                        changes.Add(new FileChange
                        {
                            Path = line[2..],
                            Staged = ChangeKind.Unmodified,
                            Unstaged = ChangeKind.Untracked,
                        });
                    }

                    break;

                case '!':
                    // Ignored entries only appear when explicitly requested; skip them.
                    break;
            }
        }

        // An upstream with no ahead/behind line is git saying the remote-tracking ref is
        // gone — the remote branch was deleted and the ref pruned. Without this flag the
        // zeros the omission leaves behind read as InSync, and the badge says "Up to date"
        // with a branch that no longer exists.
        if (branch.Upstream is { Length: > 0 } && !sawAheadBehind)
        {
            branch = branch with { UpstreamGone = true };
        }

        return new GitStatus { Branch = branch, Changes = changes };
    }

    private static BranchInfo ApplyHeader(BranchInfo branch, string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return branch;
        }

        switch (parts[1])
        {
            case "branch.oid":
                return branch with { IsUnborn = parts.Length > 2 && parts[2] == "(initial)" };

            case "branch.head":
                var head = parts.Length > 2 ? parts[2] : "HEAD";
                return head == "(detached)"
                    ? branch with { Name = "HEAD", IsDetached = true }
                    : branch with { Name = head, IsDetached = false };

            case "branch.upstream":
                return branch with { Upstream = parts.Length > 2 ? parts[2] : null };

            case "branch.ab":
                var ahead = 0;
                var behind = 0;
                foreach (var token in parts.Skip(2))
                {
                    if (token.StartsWith('+') && int.TryParse(token[1..], out var a))
                    {
                        ahead = a;
                    }
                    else if (token.StartsWith('-') && int.TryParse(token[1..], out var b))
                    {
                        behind = b;
                    }
                }

                return branch with { Ahead = ahead, Behind = behind };

            default:
                return branch;
        }
    }

    // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
    private static bool TryParseOrdinary(string line, out FileChange change)
    {
        change = null!;
        var parts = line.Split(' ', 9);
        if (parts.Length < 9 || parts[1].Length < 2)
        {
            return false;
        }

        change = new FileChange
        {
            Path = parts[8],
            Staged = FromCode(parts[1][0]),
            Unstaged = FromCode(parts[1][1]),
        };
        return true;
    }

    // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>
    private static bool TryParseRename(string line, string? originalPath, out FileChange change)
    {
        change = null!;
        var parts = line.Split(' ', 10);
        if (parts.Length < 10 || parts[1].Length < 2)
        {
            return false;
        }

        var scoreField = parts[8];      // e.g. R100 or C75
        int? score = scoreField.Length > 1 && int.TryParse(scoreField[1..], out var s) ? s : null;

        change = new FileChange
        {
            Path = parts[9],
            Staged = FromCode(parts[1][0]),
            Unstaged = FromCode(parts[1][1]),
            OriginalPath = originalPath,
            SimilarityScore = score,
        };
        return true;
    }

    // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
    private static bool TryParseUnmerged(string line, out FileChange change)
    {
        change = null!;
        var parts = line.Split(' ', 11);
        if (parts.Length < 11)
        {
            return false;
        }

        change = new FileChange
        {
            Path = parts[10],
            Staged = ChangeKind.Conflicted,
            Unstaged = ChangeKind.Conflicted,
        };
        return true;
    }

    private static ChangeKind FromCode(char c) => c switch
    {
        'M' => ChangeKind.Modified,
        'A' => ChangeKind.Added,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'U' => ChangeKind.Conflicted,
        '.' => ChangeKind.Unmodified,
        _ => ChangeKind.Unmodified,
    };
}
