using System.Windows;
using System.Windows.Controls;
using Versio.Core.Git;

namespace Versio.App.Views;

/// <summary>
/// Asks for a new branch name, and refuses the ones git would.
/// </summary>
/// <remarks>
/// <para>
/// The validation runs <b>while the name is typed</b>, using the same
/// <see cref="Versio.Core.Git.BranchName"/> rules the view model re-checks afterwards. Letting
/// git refuse instead would mean a process spawn, a failure written for a terminal, and a
/// dialog that has already closed by the time anybody reads it.
/// </para>
/// <para>
/// <b>Every refusal says which rule was broken.</b> A greyed-out button with no sentence beside
/// it leaves the user with nothing to change, which is worse than no validation at all —
/// they would at least have got git's message.
/// </para>
/// <para>
/// Purpose-built rather than a general "text prompt": it knows what a branch name is, and a
/// generic dialog with a validator passed in would be a larger thing that still only has one
/// caller.
/// </para>
/// </remarks>
public partial class BranchPromptWindow : Window
{
    /// <summary>The accepted name, or null when the user backed out.</summary>
    public string? BranchNameResult { get; private set; }

    public BranchPromptWindow()
    {
        InitializeComponent();

        // Once up front, because TextChanged does not fire for the box's initial empty value.
        // Without this the dialog opens with a blank message area under the field and a
        // disabled button, which reads as broken rather than as waiting — caught by a test,
        // not by looking at it, since a blank label looks like an empty label.
        Revalidate();

        // Focus on open: this dialog exists to receive one string, so anything the user has
        // to click first is a step that should not be there.
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => Revalidate();

    private void Revalidate()
    {
        var name = NameBox.Text;

        if (string.IsNullOrEmpty(name))
        {
            // Nothing typed yet is not an error. Saying "a branch needs a name" the instant
            // the dialog opens is scolding somebody for not having typed yet. The example
            // rides here rather than as a watermark: the box is focused on open, and a
            // watermark is the one hint focus hides.
            Message.Text = "Created from the current HEAD, and switched to straight away. "
                           + "A name like feature/what-this-changes says what it is for.";
            Message.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Secondary");
            CreateButton.IsEnabled = false;
            return;
        }

        var check = BranchName.Validate(name);

        Message.Text = check.IsValid
            ? "Created from the current HEAD, and switched to straight away."
            : check.Reason;

        Message.Foreground = (System.Windows.Media.Brush)FindResource(
            check.IsValid ? "Brush.Text.Secondary" : "Brush.Status.Deleted");

        CreateButton.IsEnabled = check.IsValid;
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        // Checked again rather than trusted from the button's state: IsDefault means Enter
        // can reach this, and a disabled button is a rendering detail rather than a guarantee.
        if (!BranchName.Validate(NameBox.Text).IsValid)
        {
            return;
        }

        BranchNameResult = NameBox.Text;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        BranchNameResult = null;
        DialogResult = false;
    }
}
