namespace Versio.App.Views;

/// <summary>One button on a prompt: what it says, and what picking it means.</summary>
/// <remarks>
/// The value is untyped so one dialog can ask questions with unrelated answers — a
/// <c>GuardChoice</c> and a <c>MissingRepositoryChoice</c> — without either enum having to
/// know the other exists. The caller casts back what it put in.
/// </remarks>
public sealed record PromptOption(string Label, object Value);
