using System.Text.RegularExpressions;

namespace Versio.Core.Git;

/// <summary>
/// Reads progress out of <c>git --progress</c> stderr.
/// </summary>
/// <remarks>
/// git writes progress to <b>stderr</b>, not stdout, and uses carriage returns to overwrite
/// the line in place — so a naive line reader sees one enormous line. Treating stderr as an
/// error channel here would report every successful push as a failure.
/// </remarks>
public static partial class GitProgressParser
{
    [GeneratedRegex(@"^(?<phase>[A-Za-z][A-Za-z ]*?):\s+(?<pct>\d{1,3})%")]
    private static partial Regex PhasePercent();

    [GeneratedRegex(@"^(?<phase>[A-Za-z][A-Za-z ]*?):\s+(?<n>\d+)\s*$")]
    private static partial Regex PhaseCount();

    /// <summary>Parse one chunk of stderr, which may contain several CR-separated updates.</summary>
    public static IReadOnlyList<GitProgress> Parse(string? chunk)
    {
        var results = new List<GitProgress>();
        if (string.IsNullOrEmpty(chunk))
        {
            return results;
        }

        foreach (var raw in chunk.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var pct = PhasePercent().Match(line);
            if (pct.Success)
            {
                results.Add(new GitProgress(
                    pct.Groups["phase"].Value.Trim(),
                    int.Parse(pct.Groups["pct"].Value),
                    line));
                continue;
            }

            var count = PhaseCount().Match(line);
            if (count.Success)
            {
                results.Add(new GitProgress(count.Groups["phase"].Value.Trim(), null, line));
                continue;
            }

            // Remote-side messages ("remote: Resolving deltas...") and anything else worth
            // showing, with no percentage to extract.
            if (line.StartsWith("remote:", StringComparison.Ordinal))
            {
                results.Add(new GitProgress("remote", null, line["remote:".Length..].Trim()));
            }
        }

        return results;
    }

    /// <summary>
    /// True when a line is progress chatter rather than a real error.
    /// </summary>
    /// <remarks>
    /// Needed because git uses stderr for both. Classifying progress as failure is the
    /// single most common way a git wrapper reports false errors on a successful push.
    /// </remarks>
    public static bool IsProgressNoise(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var trimmed = line.Trim();
        return PhasePercent().IsMatch(trimmed)
               || PhaseCount().IsMatch(trimmed)
               || trimmed.StartsWith("remote:", StringComparison.Ordinal)
               || trimmed.StartsWith("To ", StringComparison.Ordinal)
               || trimmed.StartsWith("From ", StringComparison.Ordinal)
               || trimmed.StartsWith("*", StringComparison.Ordinal)
               || trimmed.Contains("Everything up-to-date", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("Already up to date", StringComparison.OrdinalIgnoreCase);
    }
}
