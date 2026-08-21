using Versio.App.Mvvm;
using Versio.Core.Projects;

namespace Versio.App.ViewModels;

/// <summary>
/// One layer of the object the diff pane is showing — <c>ST | native</c>.
/// </summary>
/// <remarks>
/// <para>
/// An object is one row with several files: a GVL is <c>.gvl.st</c> and <c>.gvl.xml</c>, a
/// task configuration <c>.tasks.yaml</c> and <c>.xml</c>. The tree deliberately folds them
/// into one node, and the pane opens the readable layer — which left the other layer
/// reachable by nothing. These are the choices the pane's header offers when the selected
/// object has more than one.
/// </para>
/// <para>
/// Immutable, rebuilt when the shown path moves, with the command carried per item — the
/// same shape as a notification's dismiss — because <see cref="RelayCommand"/> takes no
/// parameter and threading one through XAML for a two-item strip is more machinery than
/// rebuilding two records.
/// </para>
/// </remarks>
public sealed class DiffLayerChoice
{
    public DiffLayerChoice(string path, bool isActive, Action show)
    {
        Path = path;
        IsActive = isActive;
        Label = LabelFor(path);
        ShowCommand = new RelayCommand(show);
    }

    /// <summary>Repo-relative path of this layer's file.</summary>
    public string Path { get; }

    /// <summary>What the chip says: the layer, not the file name.</summary>
    public string Label { get; }

    /// <summary>Whether the pane is showing this layer right now.</summary>
    public bool IsActive { get; }

    public RelayCommand ShowCommand { get; }

    /// <summary>
    /// The layer's name, derived from the suffix the exporter chose for it.
    /// </summary>
    /// <remarks>
    /// The catalogs are named for what they hold rather than lumped under "native", because
    /// a <c>.tasks.yaml</c> is the importable half and calling it what the read-only XML
    /// beside it is called would blur the one distinction that file exists to make. The
    /// file-name fallback is for a suffix this method has never met: wrong-but-honest beats
    /// a guessed category.
    /// </remarks>
    private static string LabelFor(string path) =>
        path.EndsWith(".st", StringComparison.OrdinalIgnoreCase) ? "ST"
        : path.EndsWith(LibraryCatalog.FileSuffix, StringComparison.OrdinalIgnoreCase) ? "libraries"
        : path.EndsWith(TaskCatalog.FileSuffix, StringComparison.OrdinalIgnoreCase) ? "tasks"
        : path.EndsWith(DeviceCatalog.FileSuffix, StringComparison.OrdinalIgnoreCase) ? "devices"
        : path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? "native"
        : System.IO.Path.GetFileName(path);
}
