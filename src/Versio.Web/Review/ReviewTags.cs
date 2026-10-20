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

    /// <summary>
    /// The body as posted for a comment placed on a drawn element: the person's text, the operation
    /// tag, and the anchor tag that lets Versio find the element again (docs/web/PLAN.md, C3).
    /// </summary>
    /// <remarks>
    /// <b>Only the server writes an anchor tag</b>, from an anchor it rendered itself — never from
    /// fields a page sent — so the format has one writer and a page cannot claim an element it was
    /// not shown. The JSON is base64url, so nothing in a key or a container (a member path, a title)
    /// can close the HTML comment around it.
    /// </remarks>
    public static string WithAnchor(string body, string key, Versio.Core.Review.SemanticAnchor anchor)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AnchorJson(anchor.Kind, anchor.SourcePath,
            anchor.Side == Versio.Core.Review.ReviewSide.New ? "new" : "old", anchor.Revision, anchor.Container, anchor.Key, anchor.Fingerprint));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return WithOperation(body, key) + "\n<!-- versio:anchor v1 " + encoded + " -->";
    }

    /// <summary>
    /// The anchor a body carries, or null: none, too long, malformed, or not every field present.
    /// Untrusted — anyone can type one on github.com — so it only ever positions a thread.
    /// </summary>
    public static Versio.Core.Review.SemanticAnchor? ReadAnchor(string? body)
    {
        if (body is null || AnchorTag().Match(body) is not { Success: true } match)
        {
            return null;
        }

        try
        {
            var text = match.Groups[1].Value.Replace('-', '+').Replace('_', '/');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=')));
            var a = System.Text.Json.JsonSerializer.Deserialize<AnchorJson>(json);
            return a is { Kind: { Length: > 0 }, Path: { Length: > 0 }, Side: "new" or "old", Revision: { Length: > 0 }, Container: not null, Key: { Length: > 0 }, Print: { Length: > 0 } }
                ? new Versio.Core.Review.SemanticAnchor(a.Kind, a.Path, a.Side == "new" ? Versio.Core.Review.ReviewSide.New : Versio.Core.Review.ReviewSide.Old,
                    a.Revision, a.Container, a.Key, a.Print)
                : null;
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record AnchorJson(string Kind, string Path, string Side, string Revision, string Container, string Key, string Print);

    /// <summary>The anchor tag; its payload capped at 1,024 characters, so a forged one costs nothing to refuse.</summary>
    [GeneratedRegex(@"<!--\s*versio:anchor v1 ([A-Za-z0-9_-]{1,1024})\s*-->")]
    private static partial Regex AnchorTag();

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
