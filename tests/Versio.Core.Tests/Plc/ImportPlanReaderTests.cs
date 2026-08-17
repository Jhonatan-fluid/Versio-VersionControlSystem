using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Plc.Codesys;
using Versio.Core.Tests.Fakes;
using Xunit;

namespace Versio.Core.Tests.Plc;

/// <summary>
/// What an import plan says in the log, before anybody is asked to agree to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists because nothing tested <see cref="ImportPlanReader"/> at all</b>, and
/// that is exactly how it came to log something false. Its missing-file line said deleting
/// objects "is not applied by this version, so they remain in the project" — written when it
/// was true, and left in place after the import learned to delete. The same stale-sentence
/// defect that had the confirmation dialog promising objects would stay while they were
/// removed, in the other channel, surviving for the same reason: no assertion had ever read it.
/// </para>
/// <para>
/// <b>A log line describing last version's behaviour is worse than no log line</b>, because it
/// is read as evidence. So the deletion lines are asserted here the way the dialog's sentences
/// are asserted in <c>ImportNarrativeTests</c> — they are part of the feature, not decoration.
/// </para>
/// <para>
/// Pure: a fake filesystem and an in-memory sink, no disk and no toolchain. That is what
/// <see cref="ImportPlanReader"/> being on <see cref="IFileSystem"/> buys, and the reason it
/// was put there.
/// </para>
/// </remarks>
public sealed class ImportPlanReaderTests
{
    private const string Root = @"C:\repo\plc\Machine";

    private static string Manifest(string objects) => $$"""
        {
          "schema": 5, "bindingId": "b1", "project": "Machine.project", "objectCount": 1,
          "objects": [ {{objects}} ],
          "warnings": [], "skipped": []
        }
        """;

    private static (ImportPlan Plan, IReadOnlyList<LogEvent> Log) Read(
        string manifest, params string[] presentFiles)
    {
        var fs = new FakeFileSystem();
        fs.Write(Path.Combine(Root, "_manifest.json"), manifest);
        foreach (var file in presentFiles)
        {
            fs.Write(Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar)), "x");
        }

        var sink = new InMemoryLogSink();
        var log = new VersioLogger(new FakeClock(DateTimeOffset.UnixEpoch)).AddSink(sink);

        var plan = ImportPlanReader.ReadAsync(
            fs, new CodesysProjectFormat(), Root, log, LogCategory.Sync, "b1",
            CancellationToken.None).GetAwaiter().GetResult();

        return (plan, sink.Snapshot());
    }

    private static string Lines(IReadOnlyList<LogEvent> log) =>
        string.Join("\n", log.Select(e => e.Message));

    [Fact]
    public void An_object_the_import_will_remove_is_named_in_the_log_before_anybody_is_asked()
    {
        // The one decision in a plan that takes work OUT of a project, taken ~20 s before
        // CODESYS starts. Until this line existed the log recorded only the removals the
        // script performed — so what had been decided, and what somebody agreed to, appeared
        // nowhere.
        var (plan, log) = Read(Manifest("""
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:a", "bytes": 1 }
            """));

        Assert.Single(plan.Deletions);

        var text = Lines(log);
        Assert.Contains("1 object(s) will be removed from the project: FB_Motor", text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_object_that_will_be_kept_says_so_and_says_why()
    {
        // A native object with no nhash: schema 5 is present but this record predates the
        // hash, so the executing side cannot check it and will refuse. The log has to carry
        // the same split the dialog does, or the two cannot be read against each other when
        // somebody asks why their GVL is still there.
        var (plan, log) = Read(Manifest("""
            { "guid": "g1", "name": "Glob_Var", "type": "GVL", "form": "NATIVE",
              "path": "App/Glob_Var.gvl.xml", "hash": "sha256:a", "bytes": 1 }
            """));

        Assert.NotNull(Assert.Single(plan.Deletions).CannotVerify);

        var text = Lines(log);
        Assert.Contains("will be kept: Glob_Var", text, StringComparison.Ordinal);
        Assert.Contains("export the project again", text, StringComparison.Ordinal);
        Assert.DoesNotContain("will be removed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_never_claims_again_that_this_version_does_not_delete()
    {
        // THE REGRESSION THIS CLASS WAS WRITTEN FOR. The exact sentence that survived the
        // commit which taught the import to delete, asserted absent rather than merely
        // replaced — a test that only checked the new wording would go green again the moment
        // somebody restored the old line beside it.
        var (_, log) = Read(Manifest("""
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:a", "bytes": 1 }
            """));

        var text = Lines(log);
        Assert.DoesNotContain("is not applied by this version", text, StringComparison.Ordinal);
        Assert.DoesNotContain("they remain in the project", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rename_is_logged_as_a_rename_and_not_as_a_deletion()
    {
        // The reassuring half of the plan, and it has to reach the log for the same reason it
        // has to reach the dialog: somebody who renamed a file goes looking for its name, and
        // finding it only in a deletion line is how a correct import gets cancelled.
        var body = "PROGRAM New\nVAR\nEND_VAR\n\nx();";
        var fs = new FakeFileSystem();
        fs.Write(Path.Combine(Root, "_manifest.json"), $$"""
            {
              "schema": 5, "bindingId": "b1", "project": "Machine.project", "objectCount": 1,
              "objects": [
                { "guid": "g1", "name": "Old", "type": "POU", "form": "ST_FULL",
                  "path": "App/Old.st", "hash": "{{Versio.Core.Hashing.ContentHash.Of(body)}}",
                  "bytes": 1 }
              ],
              "warnings": [], "skipped": []
            }
            """);
        fs.Write(Path.Combine(Root, "App", "New.st"), body);

        var sink = new InMemoryLogSink();
        var log = new VersioLogger(new FakeClock(DateTimeOffset.UnixEpoch)).AddSink(sink);

        var plan = await ImportPlanReader.ReadAsync(
            fs, new CodesysProjectFormat(), Root, log, LogCategory.Sync, "b1",
            CancellationToken.None);

        Assert.Single(plan.Renames);

        var text = Lines(sink.Snapshot());
        Assert.Contains("will be renamed or moved", text, StringComparison.Ordinal);
        Assert.DoesNotContain("will be removed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_that_deletes_nothing_says_nothing_about_deletions()
    {
        // Quiet when idle. A line that fires on every import is a line nobody reads, and the
        // deletion lines have to be the ones that stand out when they do appear.
        var (plan, log) = Read(Manifest("""
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:a", "bytes": 1 }
            """), "App/FB_Motor.st");

        Assert.Empty(plan.Deletions);

        var text = Lines(log);
        Assert.DoesNotContain("will be removed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("will be kept", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer in the mirror", text, StringComparison.Ordinal);
    }
}
