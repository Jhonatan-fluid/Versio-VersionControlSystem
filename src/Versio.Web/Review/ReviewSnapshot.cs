using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Versio.Core.Abstractions;

namespace Versio.Web.Review;

/// <summary>
/// One revision of one pull request, fixed for the length of a review: every drawing, patch and
/// thread the page shows is computed from these SHAs and never from a branch name
/// (docs/web/PLAN.md, *Pull request review*, C1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The page makes many reads per pull request — files, renders, patches,
/// threads — and a push can land between any two of them. Read by ref, the drawing could show
/// commit A while the comment positions came from commit B, and a reviewer would comment on lines
/// that are not where they appear. Fixing the SHAs once, and carrying them on every read, makes
/// every answer describe the same revision.
/// </para>
/// <para>
/// <b><see cref="BaseSha"/> is the base branch's tip when the snapshot was made</b>, not
/// GitHub's stored <c>base.sha</c>: a move of the base alone changes the merge-base and the hunks
/// even with the head unchanged, so the base is part of the revision.
/// </para>
/// </remarks>
public sealed record ReviewSnapshot(
    string Owner,
    string Repo,
    int Number,
    string BaseRef,
    string BaseSha,
    string MergeBaseSha,
    string HeadSha,
    DateTimeOffset IssuedAt,
    string Viewer);

/// <summary>Why a snapshot token was not accepted.</summary>
public enum SnapshotRefusal
{
    None,

    /// <summary>Not a token this server sealed, or altered since.</summary>
    Invalid,

    /// <summary>Sealed for another repository, pull request or viewer than this request's.</summary>
    Mismatch,

    /// <summary>Older than <see cref="ReviewSnapshots.Lifetime"/>: the page reloads the pull request.</summary>
    Expired,
}

/// <summary>
/// Seals a <see cref="ReviewSnapshot"/> into a token the page carries, and opens it again.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seal proves integrity, never access.</b> Data Protection encrypts and authenticates the
/// fields, so the page can neither forge a snapshot nor alter its SHAs. But a valid token says only
/// that this server once made it. Every request carrying one is re-authorised in full by
/// <c>AccessResolver</c> first, and the token must name this request's repository, pull request and
/// viewer (<see cref="Open"/>); a token that was valid for somebody else, or for another pull
/// request, is refused rather than believed. Nothing is read from it that the URL could not be
/// checked against.
/// </para>
/// <para>
/// <b>It is self-contained</b>: the fields travel inside the token, so no server keeps snapshot
/// state and any instance can open one sealed by another (they share the Data Protection key ring,
/// as sessions already require).
/// </para>
/// <para>
/// <b>It grants nothing</b>, which is why it may sit in a compressed response body (the BREACH
/// argument in WebHost): without a session that may read the repository, a stolen token reads
/// nothing.
/// </para>
/// <para>
/// <b>It expires</b> after <see cref="Lifetime"/>, the life of a user token. An expired token is
/// "reload the pull request", never a silent re-resolve: re-resolving would quietly move the review
/// to a revision the reviewer never loaded.
/// </para>
/// </remarks>
public sealed class ReviewSnapshots(IDataProtectionProvider protection, IClock clock)
{
    /// <summary>How long a snapshot is accepted: a GitHub user token's life.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    private readonly IDataProtector _seal = protection.CreateProtector("Versio.Web.ReviewSnapshot.v1");

    public DateTimeOffset Now => clock.UtcNow;

    public string Seal(ReviewSnapshot snapshot) =>
        _seal.Protect(JsonSerializer.Serialize(snapshot, Json));

    /// <summary>
    /// The snapshot in <paramref name="token"/>, if it was sealed here, is unexpired, and names
    /// exactly this repository, pull request and viewer.
    /// </summary>
    /// <remarks>Owner and repository compare without case, as GitHub's names do.</remarks>
    public (ReviewSnapshot? Snapshot, SnapshotRefusal Refusal) Open(string? token, string owner, string repo, int number, string viewer)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return (null, SnapshotRefusal.Invalid);
        }

        ReviewSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ReviewSnapshot>(_seal.Unprotect(token), Json);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return (null, SnapshotRefusal.Invalid);
        }

        if (snapshot is null)
        {
            return (null, SnapshotRefusal.Invalid);
        }

        if (!string.Equals(snapshot.Owner, owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.Repo, repo, StringComparison.OrdinalIgnoreCase)
            || snapshot.Number != number
            || !string.Equals(snapshot.Viewer, viewer, StringComparison.Ordinal))
        {
            return (null, SnapshotRefusal.Mismatch);
        }

        // A token from the future by more than a clock's drift between instances was not made by
        // a clock this server trusts; within it, another instance simply runs a little ahead.
        var age = clock.UtcNow - snapshot.IssuedAt;
        return age < -ClockSkew || age > Lifetime ? (null, SnapshotRefusal.Expired) : (snapshot, SnapshotRefusal.None);
    }

    /// <summary>
    /// An EXPIRED snapshot, if it was sealed here and names this repository, pull request and
    /// viewer — for settling an operation already recorded under it, and for nothing else.
    /// </summary>
    /// <remarks>
    /// Operation keys live a week; snapshots eight hours. A retry of a write whose answer was lost
    /// carries the snapshot it was first sent under (so it can never be applied to another
    /// revision), and that snapshot may have expired meanwhile. It may still find out how its own
    /// operation ended — read-only — but never start one: that needs an unexpired snapshot.
    /// </remarks>
    public ReviewSnapshot? OpenExpired(string? token, string owner, string repo, int number, string viewer)
    {
        var (_, refusal) = Open(token, owner, repo, number, viewer);
        if (refusal != SnapshotRefusal.Expired)
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<ReviewSnapshot>(_seal.Unprotect(token!), Json);
            return snapshot is not null && clock.UtcNow - snapshot.IssuedAt > Lifetime ? snapshot : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Far above a real token (a few hundred characters), far below anything costly to try.</summary>
    internal const int MaxTokenLength = 4096;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
