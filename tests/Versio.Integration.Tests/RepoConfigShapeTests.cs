using Versio.Core.Bindings;
using Versio.Core.Logging;
using Versio.Infrastructure.Bindings;
using Versio.Infrastructure.Platform;
using Versio.Plc.Codesys;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// What <c>repo.json</c> may hold where its model says a value must be, and what the store says
/// when it does not (the 2026-10-25 review's sixth round, P2).
/// </summary>
/// <remarks>
/// <para>
/// <c>RepoConfig</c>'s lists are non-nullable and <c>ProjectBinding</c>'s paths are
/// <c>required</c>, and neither is a guarantee System.Text.Json keeps: <c>"bindings": null</c>
/// sets the list to null, <c>[null]</c> puts a null where a binding must be, and
/// <c>"projectPath": null</c> satisfies <c>required</c> (the property is present) while leaving
/// the path null. Each deserialised cleanly and crashed the first reader — <c>Adoption.Plan</c>,
/// the orphan scanner, the web viewer's model — with a <c>NullReferenceException</c> that named
/// none of them. The manifest scanner had the same defect a round earlier; this is the
/// configuration reader's copy of the fix.
/// </para>
/// <para>
/// <b>Two readings, one rule.</b> A null where the model has a default (the two lists, the
/// default branch) reads as that default, said once in the log: null is how many writers spell
/// "absent", and absent already means the default. A null where the model requires a value — a
/// binding, a binding's path, an ignore glob — refuses the whole file, through the corrupt-file
/// path that already exists, rather than dropping the one element: a dropped binding is a project
/// that is no longer protected while the rest looks healthy, and System.Text.Json already refuses
/// the whole file when the same path is <i>absent</i>, so the null spelling must not cost less.
/// </para>
/// <para>No CODESYS, no python, no git. Milliseconds.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RepoConfigShapeTests
{
    private const string SoundBinding =
        """{ "id": "b-1", "projectPath": "plc/Machine.project", "exportPath": "plc/Machine" }""";

    private static async Task<(RepoConfig Config, IReadOnlyList<LogEvent> Log)> LoadAsync(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "versio-shape-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, ".versio"));
        try
        {
            var sink = new InMemoryLogSink();
            var logger = new VersioLogger(new SystemClock()).AddSink(sink);
            var store = new RepoConfigStore(root, logger);
            await File.WriteAllTextAsync(store.Path, json);

            var config = await store.LoadAsync(CancellationToken.None);
            return (config, sink.Snapshot());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LogEvent SingleWarning(IReadOnlyList<LogEvent> log) =>
        Assert.Single(log, e => e.Level == LogLevel.Warning);

    [Fact]
    public async Task A_null_bindings_list_reads_as_empty_and_says_so()
    {
        var (config, log) = await LoadAsync("""{ "schema": 1, "bindings": null, "ignore": ["vendor/**"] }""");

        Assert.NotNull(config.Bindings);
        Assert.Empty(config.Bindings);
        // The rest of the file is kept: this is a default filled in, not a file refused.
        Assert.Equal(["vendor/**"], config.Ignore);

        var warning = SingleWarning(log);
        Assert.Contains("repo.json", warning.Message, StringComparison.Ordinal);
        Assert.Contains("bindings", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("corrupt", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_ignore_list_reads_as_empty_and_adoption_still_plans()
    {
        var (config, log) = await LoadAsync($$"""{ "schema": 1, "bindings": [{{SoundBinding}}], "ignore": null }""");

        Assert.NotNull(config.Ignore);
        Assert.Empty(config.Ignore);
        Assert.Single(config.Bindings);
        Assert.Contains("ignore", SingleWarning(log).Message, StringComparison.Ordinal);

        // The reader the reviewer named: Adoption.Plan matches the path against config.Ignore,
        // and a null list there was the crash. The store is the one gate, so Core's readers
        // stay written against the type they are given.
        var decision = Adoption.Plan("plc/Other.project", config, CodesysProjectFormat.Instance, "b-2");

        Assert.Equal(AdoptionVerdict.Ready, decision.Verdict);
    }

    [Fact]
    public async Task A_null_default_branch_reads_as_empty_and_says_so()
    {
        var (config, log) = await LoadAsync("""{ "schema": 1, "defaultBranch": null }""");

        Assert.Equal(string.Empty, config.DefaultBranch);
        Assert.Contains("defaultBranch", SingleWarning(log).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_where_a_binding_belongs_refuses_the_whole_file()
    {
        var (config, log) = await LoadAsync($$"""{ "schema": 1, "bindings": [{{SoundBinding}}, null] }""");

        // The sound binding goes too. Half a config is a project silently unprotected; an empty
        // one with a loud line is the state the corrupt-file path already gives.
        Assert.Empty(config.Bindings);

        var warning = SingleWarning(log);
        Assert.Contains("corrupt", warning.Message, StringComparison.Ordinal);
        Assert.Contains("repo.json", warning.Message, StringComparison.Ordinal);
        Assert.Contains("bindings[1]", warning.Exception?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binding_with_a_null_project_path_refuses_the_whole_file()
    {
        var (config, log) = await LoadAsync($$"""
            { "schema": 1, "bindings": [
                {{SoundBinding}},
                { "id": "b-2", "projectPath": null, "exportPath": "plc/Other" }
            ] }
            """);

        Assert.Empty(config.Bindings);

        var warning = SingleWarning(log);
        Assert.Contains("corrupt", warning.Message, StringComparison.Ordinal);
        Assert.Contains("bindings[1]", warning.Exception?.Message, StringComparison.Ordinal);
        Assert.Contains("projectPath", warning.Exception?.Message, StringComparison.Ordinal);
        // The id is the one thing a reader can search the file for.
        Assert.Contains("b-2", warning.Exception?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binding_missing_its_project_path_already_refused_the_whole_file()
    {
        // The premise the whole-file reading rests on, pinned so it cannot drift away from
        // under it: System.Text.Json honours `required` against an ABSENT property and refuses
        // the file. Were this ever to pass with a dropped element instead, the null spelling
        // above would be costing more than the absent one, and the argument would need redoing.
        var (config, log) = await LoadAsync($$"""
            { "schema": 1, "bindings": [ {{SoundBinding}}, { "id": "b-2", "exportPath": "plc/Other" } ] }
            """);

        Assert.Empty(config.Bindings);
        Assert.Contains("corrupt", SingleWarning(log).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binding_with_a_null_export_path_or_id_refuses_the_whole_file()
    {
        var noExport = await LoadAsync("""{ "bindings": [{ "id": "b-1", "projectPath": "a.project", "exportPath": null }] }""");
        var noId = await LoadAsync("""{ "bindings": [{ "id": null, "projectPath": "a.project", "exportPath": "a" }] }""");

        Assert.Empty(noExport.Config.Bindings);
        Assert.Contains("exportPath", SingleWarning(noExport.Log).Exception?.Message, StringComparison.Ordinal);
        Assert.Empty(noId.Config.Bindings);
        Assert.Contains("id", SingleWarning(noId.Log).Exception?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_ignore_glob_refuses_the_whole_file()
    {
        // PathGlob builds a regex from each glob; a null one is the crash in every reader of the
        // ignore list, and it is a value where the model requires one, so it costs what a null
        // binding costs rather than being quietly skipped.
        var (config, log) = await LoadAsync($$"""{ "bindings": [{{SoundBinding}}], "ignore": ["vendor/**", null] }""");

        Assert.Empty(config.Bindings);
        var warning = SingleWarning(log);
        Assert.Contains("corrupt", warning.Message, StringComparison.Ordinal);
        Assert.Contains("ignore[1]", warning.Exception?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_holding_null_refuses_and_says_so()
    {
        // `null` is valid JSON and deserialises to a null config. It used to read as an empty
        // one in silence - indistinguishable from a repository never adopted, which is the
        // lost-work shape LoadAsync's own remarks warn about.
        var (config, log) = await LoadAsync("null");

        Assert.Empty(config.Bindings);
        var warning = SingleWarning(log);
        Assert.Contains("corrupt", warning.Message, StringComparison.Ordinal);
        Assert.Contains("null", warning.Exception?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sound_file_reads_without_a_word()
    {
        // Quiet when nothing is wrong: the warnings above are worth reading only if a sound
        // file does not produce them too.
        var (config, log) = await LoadAsync($$"""{ "schema": 1, "bindings": [{{SoundBinding}}], "ignore": ["vendor/**"] }""");

        Assert.Single(config.Bindings);
        Assert.DoesNotContain(log, e => e.Level >= LogLevel.Warning);
    }
}
