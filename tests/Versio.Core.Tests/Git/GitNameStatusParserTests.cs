using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// The file list behind one commit, as git writes it.
/// </summary>
/// <remarks>
/// The shapes here are copied from real <c>git show --name-status -z</c> output rather than
/// invented, for the same reason the porcelain v2 tests are: what matters is whether the
/// parser reads what git actually emits, and a fixture written from the documentation would
/// only prove the parser agrees with our reading of it.
/// </remarks>
public class GitNameStatusParserTests
{
    /// <summary>Join fields the way <c>-z</c> does — every one terminated, none separated.</summary>
    private static string Z(params string[] fields) =>
        string.Concat(fields.Select(f => f + '\0'));

    [Fact]
    public void Reads_a_status_and_a_path_per_file()
    {
        var files = GitNameStatusParser.Parse(Z(
            "M", "src/app.st",
            "A", "src/new.st",
            "D", "src/gone.st"));

        Assert.Equal(3, files.Count);
        Assert.Equal(ChangeKind.Modified, files[0].Kind);
        Assert.Equal("src/app.st", files[0].Path);
        Assert.Equal(ChangeKind.Added, files[1].Kind);
        Assert.Equal(ChangeKind.Deleted, files[2].Kind);
        Assert.All(files, f => Assert.Null(f.OriginalPath));
    }

    /// <summary>
    /// A rename carries two paths, and reading one would corrupt everything after it.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is not a missing rename. It is that the destination
    /// path would be left in the stream and parsed as the <i>next</i> file's status, so every
    /// remaining row in the commit would come out wrong — a quiet, total corruption from one
    /// missing increment.
    /// </remarks>
    [Fact]
    public void A_rename_carries_both_paths_and_does_not_desynchronise_the_rest()
    {
        var files = GitNameStatusParser.Parse(Z(
            "R100", "old/name.st", "new/name.st",
            "M", "after.st"));

        Assert.Equal(2, files.Count);

        Assert.Equal(ChangeKind.Renamed, files[0].Kind);
        Assert.Equal("old/name.st", files[0].OriginalPath);
        Assert.Equal("new/name.st", files[0].Path);
        Assert.Equal(100, files[0].SimilarityScore);

        Assert.Equal(ChangeKind.Modified, files[1].Kind);
        Assert.Equal("after.st", files[1].Path);
    }

    [Fact]
    public void A_copy_is_read_the_same_way_as_a_rename()
    {
        var files = GitNameStatusParser.Parse(Z("C75", "template.st", "copy.st"));

        var file = Assert.Single(files);
        Assert.Equal(ChangeKind.Copied, file.Kind);
        Assert.Equal("template.st", file.OriginalPath);
        Assert.Equal("copy.st", file.Path);
        Assert.Equal(75, file.SimilarityScore);
    }

    /// <summary>
    /// The reason <c>-z</c> is used at all.
    /// </summary>
    /// <remarks>
    /// Without it git would render this path as <c>"K\303\274hlschrank Regelung.st"</c>,
    /// quotes and octal escapes included, and the panel would then ask git for the diff of a
    /// file that does not exist. The shape is taken from this repository's own fixture corpus.
    /// </remarks>
    [Fact]
    public void A_path_with_spaces_and_an_umlaut_survives_intact()
    {
        const string path = "Device/Plc Logic/Application/Kühlschrank Regelung.st";

        var file = Assert.Single(GitNameStatusParser.Parse(Z("M", path)));
        Assert.Equal(path, file.Path);
    }

    /// <summary>A merge, which git answers with nothing at all.</summary>
    [Fact]
    public void Empty_output_is_no_files_rather_than_a_failure()
    {
        Assert.Empty(GitNameStatusParser.Parse(string.Empty));
        Assert.Empty(GitNameStatusParser.Parse(null));
        Assert.Empty(GitNameStatusParser.Parse("\n"));
    }

    /// <summary>
    /// A record that arrives short is dropped, not thrown over.
    /// </summary>
    /// <remarks>
    /// Same tolerance as <see cref="GitLogParser"/>, and for the same reason: this drives a
    /// list somebody glances at, and taking the window down because one record was truncated
    /// would be a far worse failure than a missing row.
    /// </remarks>
    [Fact]
    public void A_status_with_no_path_is_dropped_and_the_rest_survive()
    {
        var files = GitNameStatusParser.Parse(Z("M", "kept.st") + "M");

        var file = Assert.Single(files);
        Assert.Equal("kept.st", file.Path);
    }

    /// <summary>
    /// The client appends a newline as it reassembles the process output.
    /// </summary>
    /// <remarks>
    /// It is not a field, and treating it as one would leave an empty trailing path on every
    /// commit ever expanded.
    /// </remarks>
    [Fact]
    public void The_trailing_newline_the_client_adds_is_not_a_record()
    {
        var files = GitNameStatusParser.Parse(Z("M", "only.st") + "\n");

        Assert.Single(files);
        Assert.Equal("only.st", files[0].Path);
    }

    /// <summary>A type change is a modification: the file is still there and still differs.</summary>
    [Fact]
    public void A_typechange_reads_as_a_modification()
    {
        var file = Assert.Single(GitNameStatusParser.Parse(Z("T", "link.st")));
        Assert.Equal(ChangeKind.Modified, file.Kind);
    }
}
