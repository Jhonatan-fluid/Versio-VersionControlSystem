using System.Windows;
using System.Windows.Controls;
using Versio.Core.Guarding;

namespace Versio.App.Views;

/// <summary>
/// Shows a <see cref="GuardVerdict"/> and returns what the user chose.
/// </summary>
/// <remarks>
/// <para>
/// A <c>MessageBox</c> cannot do this. The questions this asks have <b>three</b> answers —
/// stash, discard, cancel — and a message box offers a fixed set with fixed words, so the
/// choice would have to be squeezed into Yes/No/Cancel and the meaning carried entirely by a
/// paragraph the user is expected to map onto buttons labelled something else. "Yes" is not an
/// answer to "stash or discard".
/// </para>
/// <para>
/// <b>It renders a verdict; it does not decide anything.</b> Which options exist, their order,
/// and every word in them come from <see cref="InterruptionGuard"/> and
/// <see cref="GuardNarrative"/>. That is what keeps the decision testable without a window and
/// the wording testable without a scenario — and it is why this file has no <c>if</c> in it
/// about locks, exports or dirty trees.
/// </para>
/// <para>
/// A refusal has no choices, so the dialog shows a single <i>OK</i> that closes it. That case
/// is a statement rather than a question, and giving it a Cancel button beside an OK would
/// imply an alternative that does not exist.
/// </para>
/// </remarks>
public partial class GuardPromptWindow : Window
{
    /// <summary>What the user picked, or null when they dismissed it.</summary>
    /// <remarks>
    /// Null for a refusal too: there was nothing to choose, and reporting the OK as a decision
    /// would let a caller act on it.
    /// </remarks>
    public GuardChoice? ChoiceResult => Result as GuardChoice?;

    /// <summary>What the user picked, whatever kind of question was asked.</summary>
    /// <remarks>
    /// Untyped because this window now renders two unrelated decisions — the guard's, and
    /// what to do about a repository that has gone. The alternative was a second window with
    /// the same XAML, and duplicating a dialog to change the words in it is how two dialogs
    /// drift apart.
    /// </remarks>
    public object? Result { get; private set; }

    public GuardPromptWindow(GuardPrompt prompt)
        : this(prompt.Title, prompt.Body,
               [.. prompt.Choices.Select(c => new PromptOption(GuardNarrative.Label(c), c))],
               cancels: GuardChoice.Cancel)
    {
    }

    /// <param name="options">Best first. Empty means a statement, not a question.</param>
    /// <param name="cancels">
    /// The value Escape maps to, wherever it sits in the list. Null when nothing in this
    /// question is a way out — which is only the no-options case, where the OK is one.
    /// </param>
    public GuardPromptWindow(
        string title, string body, IReadOnlyList<PromptOption> options, object? cancels = null)
    {
        InitializeComponent();

        Heading.Text = title.ToUpperInvariant();
        Body.Text = body;

        if (options.Count == 0)
        {
            AddButton("OK", GuardChoice.Cancel, accent: true, isDefault: true, isCancel: true,
                      records: false);
            return;
        }

        // Reversed, because the panel lays out left to right and the recommended choice
        // belongs on the right where the default action sits in every other dialog here. The
        // caller's order is "best first"; the row's order is "best last".
        for (var i = options.Count - 1; i >= 0; i--)
        {
            var option = options[i];
            var recommended = i == 0;

            AddButton(
                option.Label,
                option.Value,
                accent: recommended,
                isDefault: recommended,

                // Escape maps to the way out, wherever it happens to sit in the list.
                isCancel: cancels is not null && Equals(option.Value, cancels),
                records: true);
        }
    }

    /// <param name="records">
    /// Whether pressing this sets <see cref="Result"/>. False for the OK on a refusal,
    /// which acknowledges rather than chooses.
    /// </param>
    private void AddButton(
        string text, object choice, bool accent, bool isDefault, bool isCancel, bool records)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(accent ? "AccentButton" : "GhostButton"),
            Padding = new Thickness(16, 4, 16, 4),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
        };

        button.Click += (_, _) =>
        {
            Result = records ? choice : null;

            // True even for Cancel. The dialog completed - what it returns is the choice, and
            // a caller that read DialogResult instead would be reading the wrong thing.
            DialogResult = true;
            Close();
        };

        Buttons.Children.Add(button);
    }
}
