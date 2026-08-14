using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// Reading the copies of a project that survive an import.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name is the whole record</b>, so this is where a restore list becomes possible at all.
/// The orchestrator has written these before every import since the import path existed and kept
/// no index beside them — deliberately, because an index is a second thing that can disagree
/// with the directory.
/// </para>
/// <para>
/// The two rules worth breaking on purpose: an ordinal sort of the names must be a
/// chronological sort, and a file this cannot read must still appear. The second matters at
/// exactly the moment somebody is hunting for a copy of their project.
/// </para>
/// </remarks>
public sealed class BackupCatalogTests
{
    private const string Folder = ".versio/local/backups/b1";

    [Fact]
    public void A_backup_name_yields_its_time_and_its_project()
    {
        var backup = BackupCatalog.Parse($"{Folder}/20260814-093000-Machine.project");

        Assert.True(backup.IsRecognised);
        Assert.Equal("Machine.project", backup.ProjectFileName);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.Zero), backup.TakenUtc);
    }

    [Fact]
    public void A_project_whose_own_name_has_hyphens_still_parses()
    {
        // The parser splits by POSITION, because the stamp is fixed width. One that counted
        // hyphens would take "Line" as part of the timestamp and lose the rest.
        var backup = BackupCatalog.Parse($"{Folder}/20260814-093000-Line-2-Packaging.project");

        Assert.True(backup.IsRecognised);
        Assert.Equal("Line-2-Packaging.project", backup.ProjectFileName);
    }

    [Fact]
    public void The_newest_copy_is_first()
    {
        var list = BackupCatalog.Read([
            $"{Folder}/20260812-101500-Machine.project",
            $"{Folder}/20260814-093000-Machine.project",
            $"{Folder}/20260813-235959-Machine.project",
        ]);

        Assert.Equal(
            ["20260814-093000-Machine.project",
             "20260813-235959-Machine.project",
             "20260812-101500-Machine.project"],
            list.Select(b => b.Path.Split('/').Last()));
    }

    [Fact]
    public void An_ordinal_sort_of_the_names_is_a_sort_by_time()
    {
        // The pruner keeps the first ten by DESCENDING NAME and deletes the rest, so if this
        // ever stopped being true it would start deleting the wrong ten — silently, and only
        // noticed by somebody who went looking for a copy that should have been there.
        var names = new[]
        {
            "20260814-093000-M.project",
            "20260814-090000-M.project",
            "20260813-235959-M.project",
            "20251231-000000-M.project",
        };

        var byName = names.OrderByDescending(n => n, StringComparer.Ordinal);
        var byTime = names.OrderByDescending(n => BackupCatalog.Parse(n).TakenUtc);

        Assert.Equal(byTime, byName);
    }

    [Fact]
    public void A_file_the_parser_cannot_read_is_still_listed()
    {
        // Somebody's own copy, dropped in by hand, or one written by a future version. It is
        // very possibly the thing they are looking for, and dropping it from the list would be
        // indistinguishable from Versio having lost it.
        var list = BackupCatalog.Read([
            $"{Folder}/20260814-093000-Machine.project",
            $"{Folder}/before-the-upgrade.project",
        ]);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, b => !b.IsRecognised && b.ProjectFileName == "before-the-upgrade.project");
    }

    [Fact]
    public void An_unreadable_name_sorts_below_every_dated_one()
    {
        // It has no time to compare, and an unknown at the top of a list somebody is scanning
        // for "the one from before lunch" is the wrong end.
        var list = BackupCatalog.Read([
            $"{Folder}/zzz-not-a-backup.project",
            $"{Folder}/20251231-000000-Machine.project",
        ]);

        Assert.True(list[0].IsRecognised);
        Assert.False(list[1].IsRecognised);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short.project")]
    [InlineData("2026081-093000-M.project")]
    [InlineData("20261314-093000-M.project")]
    [InlineData("20260814x093000-M.project")]
    public void A_name_that_is_not_one_of_ours_is_read_as_undated_rather_than_thrown_over(
        string name)
    {
        var backup = BackupCatalog.Parse(name);

        Assert.False(backup.IsRecognised);
        Assert.Null(backup.TakenUtc);
    }

    [Fact]
    public void Nothing_at_all_is_an_empty_list()
    {
        Assert.Empty(BackupCatalog.Read(null));
        Assert.Empty(BackupCatalog.Read([]));
        Assert.Empty(BackupCatalog.Read(["", "   "]));
    }

    [Fact]
    public void Backslashes_read_the_same_as_forward_slashes()
    {
        var backup = BackupCatalog.Parse(@".versio\local\backups\b1\20260814-093000-M.project");

        Assert.True(backup.IsRecognised);
        Assert.Equal(".versio/local/backups/b1/20260814-093000-M.project", backup.Path);
    }

    [Fact]
    public void One_place_knows_where_the_copies_live()
    {
        Assert.Equal(".versio/local/backups/b1", BackupCatalog.FolderFor("b1"));
    }
}
