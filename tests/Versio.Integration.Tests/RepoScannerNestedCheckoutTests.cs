using Versio.Infrastructure.Bindings;
using Versio.Plc.Codesys;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The scanner stopping at a checkout nested inside the repository: a linked worktree, a
/// submodule, a clone somebody dropped into the tree. Its projects are that repository's to
/// track, not this one's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a test and not a footnote.</b> On 2026-10-27 the walk went into three
/// Claude Code worktrees under <c>.claude/worktrees/</c>, each a full checkout with the
/// fixtures and a <c>repo.json</c> of its own. Twelve fixture copies were auto-tracked under
/// fresh binding ids, every export tree among them reported a ManifestMismatch against the id
/// the <i>real</i> repository had given it, and the next commit through the app wrote the
/// twelve bindings and 129 ignore rules into git. The skip list caught a folder <i>named</i>
/// <c>.git</c>; what had to be skipped was the folder containing one — and for a linked
/// worktree that <c>.git</c> is a file, so a directory check would have missed it too.
/// </para>
/// <para>
/// Real disk, no git: a <c>.git</c> entry is a file or an empty folder here, which is all the
/// scanner reads.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RepoScannerNestedCheckoutTests(ITestOutputHelper output)
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "versio-nested-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string relative, string text = "x")
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task A_linked_worktree_inside_the_tree_is_not_walked_into()
    {
        var root = NewRoot();
        try
        {
            Write(root, "plc/Mine.project");
            Write(root, "plc/Mine/_manifest.json", """{"bindingId": "mine-1"}""");

            // The shape git writes for `git worktree add`: a one-line FILE, not a folder.
            Write(root, ".claude/worktrees/one/.git", "gitdir: C:/somewhere/.git/worktrees/one\n");
            Write(root, ".claude/worktrees/one/plc/Mine.project");
            Write(root, ".claude/worktrees/one/plc/Mine/_manifest.json", """{"bindingId": "mine-1"}""");
            Write(root, ".claude/worktrees/one/plc/Lib.fbsproj/Device.device.xml.v3");

            var logger = new TestLogger(output);
            var snapshot = await new RepoScanner(root, [CodesysProjectFormat.Instance], logger)
                .ScanAsync(git: null, CancellationToken.None);

            Assert.Equal(["plc/Mine.project"], snapshot.ProjectFiles);
            Assert.Empty(snapshot.SourceProjects);
            Assert.Equal(["plc/Mine"], snapshot.ExportDirectories);
            Assert.Equal("mine-1", Assert.Single(snapshot.ManifestBindingIds).Value);

            // Said in the log: a project that is not offered for tracking must be findable
            // there, or "why does it not see my project" has no answer.
            Assert.Contains(logger.Entries, e =>
                e.Message.Contains("not walking into .claude/worktrees/one", StringComparison.Ordinal) &&
                e.Message.Contains("checkout of its own", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_nested_clone_with_a_git_folder_is_not_walked_into_either()
    {
        var root = NewRoot();
        try
        {
            Write(root, "plc/Mine.project");
            Directory.CreateDirectory(Path.Combine(root, "vendor", "theirs", ".git"));
            Write(root, "vendor/theirs/plc/Theirs.project");

            var snapshot = await new RepoScanner(root, [CodesysProjectFormat.Instance], new TestLogger(output))
                .ScanAsync(git: null, CancellationToken.None);

            Assert.Equal(["plc/Mine.project"], snapshot.ProjectFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_repositorys_own_git_entry_does_not_stop_the_scan()
    {
        // The rule is for CHILDREN. The root always has a .git of its own — folder in the
        // main worktree, file in a linked one — and a scanner that applied the rule to the
        // root would find nothing anywhere.
        foreach (var asFile in new[] { false, true })
        {
            var root = NewRoot();
            try
            {
                if (asFile)
                {
                    Write(root, ".git", "gitdir: C:/somewhere/.git/worktrees/root\n");
                }
                else
                {
                    Directory.CreateDirectory(Path.Combine(root, ".git"));
                }

                Write(root, "plc/Mine.project");

                var snapshot = await new RepoScanner(root, [CodesysProjectFormat.Instance], new TestLogger(output))
                    .ScanAsync(git: null, CancellationToken.None);

                Assert.Equal(["plc/Mine.project"], snapshot.ProjectFiles);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
