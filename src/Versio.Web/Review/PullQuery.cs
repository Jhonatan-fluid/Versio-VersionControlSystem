using System.Text;
using System.Text.RegularExpressions;
using Versio.Web.Api;

namespace Versio.Web.Review;

/// <summary>
/// GitHub's pull request search grammar, the subset the list page takes (docs/web/FINISH.md, B10):
/// <c>is:open|closed|merged|draft</c>, <c>author:</c>, <c>assignee:</c>, <c>label:</c>,
/// <c>milestone:</c>, <c>review:none|required|approved|changes_requested</c>,
/// <c>reviewed-by:</c>, <c>review-requested:</c>, <c>base:</c>, <c>head:</c>,
/// <c>sort:created|updated|comments(-asc|-desc)</c>, and plain words or quoted phrases matched
/// against the title and body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every token is checked here, and only known tokens reach GitHub</b>, re-emitted in a shape this
/// class wrote. A free-text search is sent to GitHub's search API in the person's name; a token
/// that is not in the grammar is refused (400) rather than passed on, so the query can never name
/// another repository, another type than a pull request, or an operator this page does not show.
/// </para>
/// <para>
/// The REST fallback (an anonymous reader has no GraphQL) takes only the state and the sort: the
/// rest of a query is answered with the page's own filter over the rows it got, and the page says
/// the count is of what was listed.
/// </para>
/// </remarks>
public static partial class PullQuery
{
    /// <summary>What a query asks for: GitHub's search string, the state for a REST fallback, and the sort.</summary>
    public sealed record Parsed(string Search, string State, string Sort, string Direction, string Normalized);

    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "is", "author", "assignee", "label", "milestone", "review", "reviewed-by", "review-requested", "base", "head", "sort", "no",
    };

    /// <summary>The longest search GitHub takes; the route refuses a longer one by name.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// <paramref name="search"/> with its <c>is:open</c> / <c>is:closed</c> / <c>is:merged</c> token
    /// replaced by <c>is:<paramref name="state"/></c>, or that token added when there is none —
    /// by token, never by substring: a quoted value may contain the words (<c>label:"is:closed"</c>).
    /// </summary>
    /// <remarks>
    /// The tokens are the tokenizer's own (<see cref="Token"/>), the same walk <see cref="Parse"/>
    /// makes, so a quoted phrase is one token however many spaces it holds. A boundary regex
    /// (<c>(?&lt;=\s)is:closed(?=\s)</c>) was tried first and fixed only a value touching its quotes:
    /// <c>label:"foo is:closed bar"</c> still had its label rewritten and its real state kept, so the
    /// counts' queries named a label nobody has (the 2026-10-24 review's item 8).
    /// </remarks>
    public static string WithState(string search, string state)
    {
        ArgumentNullException.ThrowIfNull(search);
        foreach (Match m in Token().Matches(search))
        {
            if (!m.Groups["key"].Success || !m.Groups["key"].Value.Equals("is", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["value"].Value;
            if (value.ToLowerInvariant() is "open" or "closed" or "merged")
            {
                return string.Concat(search.AsSpan(0, m.Index), "is:", state, search.AsSpan(m.Index + m.Length));
            }
        }

        return $"{search} is:{state}";
    }

    /// <summary>The parsed query, or null with the word that was refused.</summary>
    public static (Parsed? Query, string? Refused) Parse(string? q, string owner, string repo)
    {
        var tokens = new List<string>();
        var state = "open";
        var stateSaid = false;
        var sort = "updated";
        var direction = "desc";
        foreach (Match m in Token().Matches(q ?? ""))
        {
            var key = m.Groups["key"].Success ? m.Groups["key"].Value.ToLowerInvariant() : null;
            var raw = m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["value"].Value;
            if (key is null)
            {
                if (!Word(raw))
                {
                    return (null, raw);
                }

                tokens.Add(Quoted(raw));
                continue;
            }

            if (!Keys.Contains(key))
            {
                return (null, $"{key}:");
            }

            var value = raw;
            switch (key)
            {
                case "is":
                    value = value.ToLowerInvariant();
                    if (value == "pr")
                    {
                        continue;
                    }

                    if (value is not ("open" or "closed" or "merged" or "draft"))
                    {
                        return (null, $"is:{raw}");
                    }

                    if (value is "open" or "closed" or "merged")
                    {
                        state = value == "open" ? "open" : "closed";
                        stateSaid = true;
                    }

                    break;
                case "review":
                    value = value.ToLowerInvariant();
                    if (value is not ("none" or "required" or "approved" or "changes_requested"))
                    {
                        return (null, $"review:{raw}");
                    }

                    break;
                case "sort":
                    value = value.ToLowerInvariant();
                    var dash = value.LastIndexOf('-');
                    var field = dash > 0 && value[(dash + 1)..] is "asc" or "desc" ? value[..dash] : value;
                    if (field is not ("created" or "updated" or "comments"))
                    {
                        return (null, $"sort:{raw}");
                    }

                    sort = field;
                    direction = dash > 0 && value.EndsWith("-asc", StringComparison.Ordinal) ? "asc" : "desc";
                    value = $"{sort}-{direction}";
                    break;
                case "author" or "assignee" or "reviewed-by" or "review-requested":
                    if (!Login().IsMatch(value) && !(value.StartsWith('@') && value is "@me"))
                    {
                        return (null, $"{key}:{raw}");
                    }

                    break;
                case "base" or "head":
                    if (!ApiEndpoints.ValidRef(value) || value.Contains(':', StringComparison.Ordinal))
                    {
                        return (null, $"{key}:{raw}");
                    }

                    break;
                case "no":
                    value = value.ToLowerInvariant();
                    if (value is not ("label" or "assignee" or "milestone" or "project"))
                    {
                        return (null, $"no:{raw}");
                    }

                    break;
                default:
                    // label, milestone: a name, bounded, nothing that could close the quotes.
                    if (!Name(value))
                    {
                        return (null, $"{key}:{raw}");
                    }

                    break;
            }

            tokens.Add($"{key}:{Quoted(value)}");
        }

        if (!stateSaid)
        {
            tokens.Insert(0, "is:open");
        }

        if (!tokens.Any(t => t.StartsWith("sort:", StringComparison.Ordinal)))
        {
            tokens.Add($"sort:{sort}-{direction}");
        }

        var normalized = string.Join(' ', tokens);
        var search = new StringBuilder("repo:").Append(owner).Append('/').Append(repo).Append(" is:pr ").Append(normalized).ToString();
        return (new Parsed(search, state, sort, direction, normalized), null);
    }

    /// <summary>A value as GitHub's grammar takes it: quoted when it has a space, and only then.</summary>
    private static string Quoted(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private static bool Word(string value) => value.Length is >= 1 and <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ' or '#' or '/');

    private static bool Name(string value) => value.Length is >= 1 and <= 64 && !value.Any(c => char.IsControl(c) || c == '"');

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex Login();

    /// <summary>One token: an optional key, then a quoted phrase or a run of non-spaces.</summary>
    [GeneratedRegex("(?:(?<key>[A-Za-z-]+):)?(?:\"(?<quoted>[^\"]*)\"|(?<value>\\S+))")]
    private static partial Regex Token();
}
