namespace Versio.Core.Guarding;

/// <summary>
/// A question asked before something is destroyed, with the words on its own buttons.
/// </summary>
/// <remarks>
/// <para>
/// <b>The label is part of the question, and until now there was nowhere to put it.</b> These
/// were <c>Func&lt;string, string, bool&gt;</c> answered by <c>MessageBox.Show</c>, so every
/// one of them ended in a pair of buttons reading OK and Cancel — and the captions are all
/// phrased as questions ("Discard changes to this file?", "Abandon this merge?"). OK is not an
/// answer to either. <c>GuardPromptWindow</c>'s own remarks make exactly this argument about
/// the three-way prompts; it applies no less to a two-way one.
/// </para>
/// <para>
/// <see cref="Confirm"/> has a default, so a caller with nothing better to say than "continue"
/// need not invent something. Every caller in the application does have something better.
/// </para>
/// </remarks>
/// <param name="Title">The question, as a question.</param>
/// <param name="Body">What it will do, and what cannot be undone about it.</param>
/// <param name="Confirm">
/// The affirmative button. A verb, matching the title — "Discard", "Abandon", "Import".
/// </param>
public sealed record DestructivePrompt(string Title, string Body, string Confirm = "Continue");
