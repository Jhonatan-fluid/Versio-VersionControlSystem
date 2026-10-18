using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Versio.Web.Review;

/// <summary>
/// The marks Versio leaves in what it posts to GitHub, and the operation keys behind them
/// (docs/web/PLAN.md, *Pull request review*, C5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a body carries a tag.</b> A write can succeed at GitHub and still look failed here — the
/// response lost on the way back, the process killed before it recorded the success. Retrying then
/// would post the comment twice. Instead the body carries <c>&lt;!-- versio:op v1 KEY --&gt;</c>, and
/// an uncertain write is reconciled by looking for its own key among the person's comments. An HTML
/// comment, on a line of its own after a blank line: GitHub hides it, and so does Core's Markdown.
/// </para>
/// <para>
/// <b>Tags are untrusted on the way back.</b> Anyone can type one into a comment on github.com. They
/// are only ever used to recognise a write this person already asked for, under a key this person's
/// page generated; and they are stripped before anything is drawn, so a forged tag changes nothing
/// a reader sees.
/// </para>
/// </remarks>
public static partial class ReviewTags
{
    /// <summary>How long an operation key is honoured; after it, a retry must be a new, deliberate submission.</summary>
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether <paramref name="key"/> is a key the page could have made: <c>&lt;unix-ms&gt;-&lt;random&gt;</c>,
    /// with its time not in the future and not older than <see cref="KeyLifetime"/>.
    /// </summary>
    /// <remarks>
    /// <b>The time is in the key so expiry needs no storage.</b> A ledger entry that has expired is
    /// gone, and a store cannot tell an expired key from a new one; the key itself can. So an old key
    /// is refused even after every trace of it is gone, and expiry can never turn a retry into a fresh
    /// submission (C5).
    /// </remarks>
    public static KeyCheck CheckKey(string? key, DateTimeOffset now)
    {
        if (key is null || !Key().IsMatch(key))
        {
            return KeyCheck.Malformed;
        }

        var millis = long.Parse(key[..key.IndexOf('-', StringComparison.Ordinal)], CultureInfo.InvariantCulture);
        var issued = DateTimeOffset.FromUnixTimeMilliseconds(millis);
        if (issued > now + TimeSpan.FromMinutes(5))
        {
            return KeyCheck.Malformed;
        }

        return now - issued > KeyLifetime ? KeyCheck.Expired : KeyCheck.Ok;
    }

    /// <summary>The body as posted: the person's text, then the operation tag on its own line.</summary>
    public static string WithOperation(string body, string key) => Strip(body).TrimEnd() + "\n\n" + OperationTag(key);

    public static string OperationTag(string key) => $"<!-- versio:op v1 {key} -->";

    /// <summary>Whether a body GitHub returned carries this operation's tag.</summary>
    public static bool Carries(string? body, string key) =>
        body is not null && body.Contains(OperationTag(key), StringComparison.Ordinal);

    /// <summary>A body without any Versio tag, for drawing: a forged or stale tag never reaches a reader.</summary>
    public static string Strip(string body) => Tag().Replace(body, "").TrimEnd();

    /// <summary>The payload's fingerprint: the same key with a different payload is refused, never merged.</summary>
    public static string PayloadHash(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(p => p ?? "\u0000")))));

    [GeneratedRegex("^[0-9]{12,14}-[A-Za-z0-9]{16,64}$")]
    private static partial Regex Key();

    [GeneratedRegex(@"<!--\s*versio:(?:op|anchor)\b[^>]{0,1024}-->")]
    private static partial Regex Tag();
}

public enum KeyCheck
{
    Ok,
    Malformed,
    Expired,
}
