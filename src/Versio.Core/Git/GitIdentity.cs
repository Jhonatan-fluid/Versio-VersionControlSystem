namespace Versio.Core.Git;

/// <summary>
/// Who git will attribute a commit to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity is not credentials, and conflating them is a real trap.</b> This is
/// <c>user.name</c> and <c>user.email</c>: local, never sent anywhere, and it affects
/// <c>commit</c> alone. Credentials are what proves who you are <i>to a remote</i> and affect
/// fetch, pull and push. A machine can have a perfect identity and no credentials, or the
/// reverse, and the remedies have nothing to do with each other — so they get separate types,
/// separate checks and separate sentences.
/// </para>
/// <para>
/// <b>Why this is checked before the commit rather than during it.</b> A commit with no
/// identity fails with a nine-line lecture from git about configuring your name — and in
/// Versio it fails <i>after</i> the PLC export gate has already run a 17–31 second CODESYS
/// export, because the gate runs first so that the mirror is never committed stale. A long wait
/// followed by a wall of text is the worst first-run experience the application can produce,
/// and it costs one <c>git config --get</c> at repository open to remove entirely.
/// </para>
/// <para>
/// <b>Deliberately not validated beyond emptiness.</b> Git itself accepts anything non-empty as
/// an email, and a client that refused <c>engineer@plc</c> or an intranet address with no dot
/// would be stricter than the tool it is driving — which is the kind of helpfulness that makes
/// people go back to the terminal. <see cref="LooksLikeEmail"/> exists so the UI can *advise*
/// without blocking.
/// </para>
/// </remarks>
public sealed record GitIdentity
{
    /// <summary>Nothing configured, which is what a fresh machine has.</summary>
    public static readonly GitIdentity None = new();

    public string Name { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public bool HasName => !string.IsNullOrWhiteSpace(Name);

    public bool HasEmail => !string.IsNullOrWhiteSpace(Email);

    /// <summary>True when git has everything it needs to record a commit.</summary>
    public bool IsComplete => HasName && HasEmail;

    /// <summary>
    /// Advisory only: does the email have the shape of one?
    /// </summary>
    /// <remarks>
    /// One <c>@</c> with something either side. Deliberately not a full RFC check — the point is
    /// to catch a name typed into the email box, not to adjudicate address syntax.
    /// </remarks>
    public bool LooksLikeEmail
    {
        get
        {
            var at = Email.IndexOf('@');
            return at > 0 && at < Email.Length - 1 && Email.IndexOf('@', at + 1) < 0;
        }
    }

    /// <summary>
    /// What is missing, in words, or empty when nothing is.
    /// </summary>
    /// <remarks>
    /// Names both when both are missing rather than reporting the first and making the user
    /// discover the second on the next attempt — which is how a one-step fix becomes two.
    /// </remarks>
    public string Explanation => (HasName, HasEmail) switch
    {
        (true, true) => string.Empty,
        (false, false) => "Git needs a name and an email address before it can record a commit.",
        (false, true) => "Git needs a name before it can record a commit.",
        (true, false) => "Git needs an email address before it can record a commit.",
    };

    /// <summary>What to put on a disabled commit button's tooltip.</summary>
    public string CommitBlockedReason =>
        IsComplete ? string.Empty : "Set a name and email for git first.";

    /// <summary>Build one from git's two answers, treating whitespace as absent.</summary>
    public static GitIdentity From(string? name, string? email) => new()
    {
        Name = (name ?? string.Empty).Trim(),
        Email = (email ?? string.Empty).Trim(),
    };
}
