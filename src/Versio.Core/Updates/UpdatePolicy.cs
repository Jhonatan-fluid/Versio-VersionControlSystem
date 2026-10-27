namespace Versio.Core.Updates;

/// <summary>The update question as the user sees it.</summary>
/// <param name="Current">The version running now.</param>
/// <param name="Offered">The version on offer.</param>
/// <param name="Notes">The release notes, already cut to fit a dialog.</param>
public sealed record UpdatePrompt(string Current, string Offered, string Notes);

/// <summary>What the user said to an offered update. No answer means later.</summary>
public enum UpdateChoice
{
    /// <summary>Download now, then close and restart into the new version.</summary>
    UpdateNow,

    /// <summary>Not now. The launch check stops asking about this version.</summary>
    Later,
}

/// <summary>
/// When to ask about an update, and how much of the notes to show.
/// </summary>
/// <remarks>
/// Pure, so the rules are tested with strings rather than with a published release.
/// </remarks>
public static class UpdatePolicy
{
    /// <summary>Lines of release notes a prompt shows before saying how many it left out.</summary>
    /// <remarks>
    /// The prompt window sizes to its content and has no scroll bar, by design - it is a
    /// question, not a document. Notes long enough to push the buttons off a laptop screen
    /// would leave a modal nobody can answer.
    /// </remarks>
    public const int PromptNoteLines = 14;

    /// <summary>
    /// Whether the launch check should put the question, given what was dismissed before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A dismissal is for one version, not for updates.</b> Later on 0.5.0 silences the
    /// launch prompt for 0.5.0 and nothing newer, so publishing 0.5.1 asks again. A dismissal
    /// that silenced everything would leave a user on an old build for good after one busy
    /// morning, which is the opposite of what publishing is for.
    /// </para>
    /// <para>
    /// Only the launch check honours it. Check for updates in the gear menu is the user asking,
    /// and answering "nothing new" while 0.5.0 sits there would be a lie.
    /// </para>
    /// </remarks>
    public static bool ShouldAskAtLaunch(UpdateOffer offer, string dismissedVersion) =>
        !string.Equals(offer.Version, dismissedVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The release notes as plain lines for a prompt: markdown markers off, blank runs folded,
    /// and capped at <see cref="PromptNoteLines"/>.
    /// </summary>
    /// <remarks>
    /// The prompt body is a plain text block, so a heading's <c>#</c> and a bold's <c>**</c>
    /// would show as characters. Only the markers that read as noise go; a bullet's dash stays,
    /// because a dash reads as a bullet in plain text too.
    /// </remarks>
    public static string NotesForPrompt(string markdown, int maxLines = PromptNoteLines)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('#'))
            {
                line = trimmed.TrimStart('#').Trim();
            }

            line = line.Replace("**", string.Empty, StringComparison.Ordinal)
                       .Replace("__", string.Empty, StringComparison.Ordinal)
                       .Replace("`", string.Empty, StringComparison.Ordinal);

            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0))
            {
                continue;
            }

            lines.Add(line);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count <= maxLines)
        {
            return string.Join('\n', lines);
        }

        var left = lines.Count - maxLines;
        var shown = lines.Take(maxLines).ToList();
        shown.Add(left == 1 ? "…and 1 more line" : $"…and {left} more lines");
        return string.Join('\n', shown);
    }
}
