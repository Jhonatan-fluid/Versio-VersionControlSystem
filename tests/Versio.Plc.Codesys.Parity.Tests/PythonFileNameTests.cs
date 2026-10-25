using System.Diagnostics;
using System.Text.Json;
using Versio.Core.Diff;
using Versio.Core.Iec;
using Versio.Core.Projects;
using Versio.Core.Serialization;
using Versio.Plc.Codesys;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// The exporter's filename rule, exercised by running the module itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>versio_text.py</c> runs inside CODESYS on IronPython 2.7, and its pure half — no
/// <c>scriptengine</c> import — runs on CPython too, which is what this uses. It is the first
/// automated coverage the python modules have had; until now the only way to exercise them was
/// a 17–31 second CODESYS launch, so the rules in them were tested by whichever export happened
/// to touch them.
/// </para>
/// <para>
/// <b>The case that prompted it.</b> CODESYS gives an external-file reference an identity like
/// <c>{00000000-…}|C:\Users\m.lamp\Documents\license.txt</c>, and percent-encoding that
/// produced a 120-character filename carrying a username and a directory layout into a shared
/// repository — the same class of leak as the <c>-&lt;user&gt;-&lt;machine&gt;.opt</c> files
/// adoption gitignores, arriving through a different door. It was found in a real mirror, not
/// in a test.
/// </para>
/// <para>
/// Skips cleanly without python, exactly as the CODESYS tests skip without CODESYS. No python
/// is shipped or required at run time — CODESYS brings its own interpreter.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PythonFileNameTests(ITestOutputHelper output)
{
    private static readonly string Scripts = TestPaths.Scripts;

    /// <summary>Run <c>object_path</c> over one directory and name.</summary>
    /// <remarks>
    /// Tab-separated rather than JSON because the inputs are two plain strings and the
    /// output is one — the same file-passing argument as <see cref="Stem"/>, since a
    /// <c>rel_dir</c> is exactly the thing that carries backslashes.
    /// </remarks>
    private string ObjectPath(string relDir, string? name) =>
        CPythonHarness.Run(relDir + "	" + name, (input, result) => $"""
            import io, sys
            sys.path.insert(0, r"{Scripts}")
            import versio_text
            rel_dir, name = io.open(r"{input}", encoding="utf-8").read().split("	")
            out = versio_text.object_path(rel_dir, name)
            io.open(r"{result}", "w", encoding="utf-8").write(out)
            """);

    [SkippableFact]
    public void An_object_at_the_project_root_has_no_leading_slash()
    {
        // rel_dir is EMPTY at the root, so the naive spelling produces "/Device" — a path
        // that matches nothing in the manifest and reads, to anybody who knows unix, as
        // absolute. This is the case the two warning sites got wrong for as long as they
        // existed, and it is the one the panel now prints when an export fails.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("Device", ObjectPath(string.Empty, "Device"));
    }

    [SkippableFact]
    public void A_windows_separator_from_os_path_join_is_normalised_to_a_forward_slash()
    {
        // rel_dir is built with os.path.join, so on Windows it arrives with backslashes
        // while the literal joining it to the name is a forward slash. Mixed, the same
        // object is spelled two ways in one file — and _manifest.json, _coverage.md, the
        // log and the panel are all supposed to agree on one string.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("Device/Plc Logic/Application",
                     ObjectPath("Device\\Plc Logic", "Application"));
    }

    /// <summary>Run one of the two G9 path guards and return what it said.</summary>
    private string Guard(string function, string path) =>
        CPythonHarness.Run(path, (input, result) => $"""
            import io, sys
            sys.path.insert(0, r"{Scripts}")
            import versio_text
            path = io.open(r"{input}", encoding="utf-8").read()
            out = versio_text.{function}(path) or u""
            io.open(r"{result}", "w", encoding="utf-8").write(out)
            """);

    [SkippableFact]
    public void An_ordinary_mirror_path_passes_both_guards_without_a_word()
    {
        // THE HALF THAT MATTERS MOST, because a guard that fires on normal work is worse
        // than no guard: it teaches people to read past the line that eventually matters.
        // This is a real path from the committed fixture.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        const string ordinary = "Device/Plc Logic/Application/Library Manager.libraries.yaml";

        Assert.Equal(string.Empty, Guard("too_long_for_any_machine", ordinary));
        Assert.Equal(string.Empty,
                     Guard("too_long_for_this_machine", @"C:\GIT\Versio\plc\Machine\" + ordinary));
    }

    [SkippableFact]
    public void A_mirror_path_no_machine_could_check_out_is_named_as_such()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        // Boundary, both sides: 200 is allowed and 201 is not. A guard tested only far past
        // its threshold cannot tell a correct comparison from an off-by-one.
        Assert.Equal(string.Empty, Guard("too_long_for_any_machine", new string('x', 200)));

        var said = Guard("too_long_for_any_machine", new string('x', 201));

        Assert.Contains("201", said, StringComparison.Ordinal);

        // Says what follows from it rather than reporting a number. "201 characters" is a
        // fact nobody can act on; "cannot be checked out on any machine" is the reason this
        // is worth interrupting somebody for.
        Assert.Contains("any machine", said, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void A_checkout_that_is_too_deep_blames_the_checkout_and_not_the_object()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        Assert.Equal(string.Empty, Guard("too_long_for_this_machine", new string('x', 260)));

        var said = Guard("too_long_for_this_machine", new string('x', 261));

        Assert.Contains("261", said, StringComparison.Ordinal);

        // The remedy is the point. This guard fires on paths the object itself is not
        // responsible for, so a message about the object would send somebody to rename a
        // POU that is not the problem.
        Assert.Contains("repository", said, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void An_object_whose_name_could_not_be_read_is_spelled_the_same_way()
    {
        // UNNAMED_OBJECT fires when get_name() itself raises, so the directory is the whole
        // description of the object that will ever exist — and it is the site that was STILL
        // passing a raw os.path.join result after the other four had been converted. Caught by
        // re-reading this change rather than by any of its own tests.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("Device/Plc Logic", ObjectPath("Device\\Plc Logic", null));

        // And at the project root there is no directory either. The empty string has to come
        // back cleanly rather than as "/" — ExportCoverage prints this.
        Assert.Equal(string.Empty, ObjectPath(string.Empty, null));
    }

    [SkippableFact]
    public void An_ordinary_nested_object_is_left_alone()
    {
        // The non-vacuous half. A rule that rewrote every path would satisfy both tests
        // above and quietly repoint every record in the manifest.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("Device/Plc Logic/Application/FB_Pump",
                     ObjectPath("Device/Plc Logic/Application", "FB_Pump"));
    }

    /// <summary>Run <c>safe_file_stem</c> over one name and return what it produced.</summary>
    /// <remarks>
    /// Through files at both ends — the names contain quotes, backslashes and pipes, and
    /// the encoding trap on stdout is the harness's own remark.
    /// </remarks>
    private string Stem(string name)
    {
        var produced = CPythonHarness.Run(name, (input, result) => $"""
            import io, sys
            sys.path.insert(0, r"{Scripts}")
            import versio_text
            name = io.open(r"{input}", encoding="utf-8").read()
            out = versio_text.safe_file_stem(name)
            io.open(r"{result}", "w", encoding="utf-8").write(out)
            """);

        output.WriteLine($"{name} -> {produced}");
        return produced;
    }

    /// <summary>Shared with <see cref="PythonFileNameParityTests"/>, which skips on the same
    /// condition for the same reason.</summary>
    internal static bool PythonIsAvailable() => CPythonHarness.PythonIsAvailable();

    [SkippableTheory]
    [InlineData("clsLight")]
    [InlineData("Main")]
    [InlineData("Kühlschrank Regelung")]
    [InlineData("Task Configuration")]
    public void An_ordinary_object_name_is_its_own_filename(string name)
    {
        // No churn. Every existing mirror was written with these rules, and a change here
        // would rename every file in every export somebody has already committed.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        Assert.Equal(name, Stem(name));
    }

    [SkippableFact]
    public void A_name_that_is_really_a_path_does_not_put_a_username_in_the_repository()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var stem = Stem(@"{00000000-0000-0000-0000-000000000000}|C:\Users\m.lamp\Documents\license.txt");

        Assert.DoesNotContain("m.lamp", stem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", stem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C%3A", stem, StringComparison.OrdinalIgnoreCase);

        // What survives is the part a human would recognise it by.
        Assert.StartsWith("license.txt", stem, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Two_external_files_with_the_same_name_do_not_collide()
    {
        // The reason the shortened form carries a digest at all: one file per object is what
        // makes the manifest's path mapping meaningful, and two objects landing on one file
        // would silently lose one of them.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var first = Stem(@"{0000}|C:\Users\a\Documents\license.txt");
        var second = Stem(@"{0000}|D:\shared\legal\license.txt");

        Assert.NotEqual(first, second);
        Assert.StartsWith("license.txt", first, StringComparison.Ordinal);
        Assert.StartsWith("license.txt", second, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void A_name_nobody_could_open_is_shortened_rather_than_written()
    {
        // Windows still enforces 260 characters in most configurations, and a deep CODESYS
        // folder chain plus a percent-encoded name reaches it. A file nobody can open is
        // worse than a filename that is abbreviated.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var stem = Stem(new string('A', 300));

        Assert.True(stem.Length < 100, $"stem was {stem.Length} characters");
    }

    [SkippableFact]
    public void The_windows_rules_that_were_already_there_still_hold()
    {
        // The percent-encoding is not replaced, only bypassed for names that are paths. A
        // reserved device name and an illegal character must still round-trip into something
        // Windows will accept.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        Assert.Equal("CON%20", Stem("CON"));
        Assert.Equal("weird%3Cname%3E", Stem("weird<name>"));
    }

    [SkippableFact]
    public void A_trailing_space_and_a_trailing_dot_do_not_collapse_onto_one_file()
    {
        // This was a real collision. safe_file_stem used to rstrip(" ."), so "Pump " and
        // "Pump." both became "Pump" - two distinct CODESYS objects landing on one file,
        // which is exactly what the encoding exists to prevent. Windows does strip these
        // characters, so they cannot be left alone; they are encoded instead, as the C#
        // PathMapper always did.
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var space = Stem("Pump ");
        var dot = Stem("Pump.");

        Assert.NotEqual(space, dot);
        Assert.False(space.EndsWith(' ') || space.EndsWith('.'));
        Assert.False(dot.EndsWith(' ') || dot.EndsWith('.'));
    }
}

/// <summary>
/// The rules that exist in two languages. This runs both over one corpus and compares
/// their <b>outputs</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It has outgrown the name.</b> It started as the file-name rule and now also covers
/// the export-form decision, the YAML emitter against its own reader, the committed
/// <c>.tasks.yaml</c>, and <c>versio_native_equiv</c> against
/// <see cref="NativeXmlNormalizer"/>. They belong in one class because they share
/// <see cref="RunPython"/> and one skip condition, and because they are one check asked
/// five times.
/// </para>
/// <para>
/// <c>safe_file_stem</c> in <c>versio_text.py</c> is the implementation that runs;
/// <c>PathMapper.EncodeStem</c> is the C# statement of the same rule. They had drifted in
/// both directions at once — the python shortened path-like names and the C# did not, while
/// the C# encoded a trailing space and the python collapsed it — and neither side noticed,
/// because each was tested only against itself.
/// </para>
/// <para>
/// That is the same failure as the GVL suffix bug, where the C# was right, the python was
/// wrong, and the parity test compared only GUID tables. Comparing outputs rather than
/// intentions is the only check that closes it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PythonFileNameParityTests(ITestOutputHelper output)
{
    private static readonly string Scripts = TestPaths.Scripts;

    /// <summary>
    /// Names chosen to hit every branch on both sides.
    /// </summary>
    /// <remarks>
    /// Ordinary names are here on purpose as well as the awkward ones: the rule that matters
    /// most is that a normal object name is its own file name, because a change there would
    /// rename every file in every mirror anybody has already committed.
    /// </remarks>
    private static readonly string[] Names =
    [
        "Main", "clsLight", "Conveyor_FB", "With Space", "Task Configuration",
        "Kühlschrank Regelung", "Кириллица", "dotted.name", "emoji_\U0001F600",
        "CON", "con", "PRN", "LPT9", "NUL",
        "a|b", "a*b", "a?b", "a\"b", "a<b>c",
        "a/b", "a\\b", "a:b", "C:", "x/", "/x",
        "trailing ", "trailing.", "a  ", "..", " ",
        new string('A', 81), new string('A', 300), "ünïcödé" + new string('z', 90),
        @"{00000000-0000-0000-0000-000000000000}|C:\Users\m.lamp\Documents\license.txt",
        @"{0000}|D:\shared\legal\license.txt",
    ];

    public static TheoryData<string> Corpus
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Names)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Corpus))]
    public void The_python_and_the_csharp_produce_the_same_stem(string name)
    {
        Skip.IfNot(PythonFileNameTests.PythonIsAvailable(), "python is not on PATH");

        var fromPython = PythonStems([name])[0];
        var fromCsharp = PathMapper.EncodeStem(name);

        output.WriteLine($"{name.Length,4} chars | python: {fromPython}");
        output.WriteLine($"{string.Empty,4}       | c#    : {fromCsharp}");

        Assert.Equal(fromPython, fromCsharp);
    }

    [SkippableFact]
    public void No_two_names_in_the_corpus_share_a_stem_on_either_side()
    {
        // Parity is not enough on its own: the two halves agreeing on a COLLISION would pass
        // every assertion above and still lose an object. The corpus deliberately contains
        // names that differ only in the characters most likely to be normalised away.
        Skip.IfNot(PythonFileNameTests.PythonIsAvailable(), "python is not on PATH");

        var names = Names.Distinct(StringComparer.Ordinal).ToList();

        var python = PythonStems(names);
        var csharp = names.Select(PathMapper.EncodeStem).ToList();

        foreach (var (side, stems) in new[] { ("python", python), ("c#", csharp) })
        {
            var duplicates = stems
                .Select((stem, i) => (stem, name: names[i]))
                .GroupBy(x => x.stem, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key} <- {string.Join(" / ", g.Select(x => $"'{x.name}'"))}")
                .ToList();

            Assert.True(duplicates.Count == 0,
                $"{side} maps distinct names onto one stem: {string.Join(" ; ", duplicates)}");
        }
    }

    /// <summary>
    /// The per-type export decision also exists twice. This runs both over every type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C1 in the audit: <c>ExportFormRules.Decide</c> returned <c>NativeWithChildren</c> for an
    /// unmapped type with children — with a comment explaining that anything else "quietly
    /// destroys the tree shape" — and <c>decide_form</c> had no such form, so the exporter that
    /// actually runs did the flattening the C# was documented to prevent. A GUID-table
    /// comparison could never catch that: both tables were identical and the DECISION differed.
    /// </para>
    /// <para>
    /// So this compares outputs across every type and every combination of the three inputs,
    /// which is 8 cases per type and the only shape of test that would have failed.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public void The_python_and_the_csharp_decide_the_same_export_form()
    {
        Skip.IfNot(PythonFileNameTests.PythonIsAvailable(), "python is not on PATH");

        var cases = new List<(string Guid, bool Impl, bool Decl, bool Children)>();
        var guids = CodesysObjectTypes.All.Keys
            .Append("00000000-0000-0000-0000-000000000000")   // a type no table knows
            .ToList();

        foreach (var guid in guids)
        {
            foreach (var impl in new[] { false, true })
            {
                foreach (var decl in new[] { false, true })
                {
                    foreach (var children in new[] { false, true })
                    {
                        cases.Add((guid, impl, decl, children));
                    }
                }
            }
        }

        var fromPython = PythonForms(cases);

        var mismatches = new List<string>();
        for (var i = 0; i < cases.Count; i++)
        {
            var (guid, impl, decl, children) = cases[i];
            var type = CodesysObjectTypes.FromGuid(guid);
            var csharp = FormName(ExportFormRules.Decide(type, impl, decl, children));

            if (!string.Equals(csharp, fromPython[i], StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"{type} (impl={impl} decl={decl} children={children}): " +
                    $"c#={csharp} python={fromPython[i]}");
            }
        }

        output.WriteLine($"{cases.Count} cases compared across {guids.Count} type GUIDs");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} disagreement(s): {string.Join(" ; ", mismatches.Take(10))}");
    }

    /// <summary>The python spelling of an <see cref="ExportForm"/>. Kept as an explicit map
    /// rather than a name transform, so adding a form makes this fail to compile.</summary>
    private static string FormName(ExportForm form) => form switch
    {
        ExportForm.Skip => "SKIP",
        ExportForm.Container => "CONTAINER",
        ExportForm.StFull => "ST_FULL",
        ExportForm.StDeclaration => "ST_DECL",
        ExportForm.StImplementation => "ST_IMPL",
        ExportForm.GvlBoth => "GVL_BOTH",
        ExportForm.Native => "NATIVE",
        ExportForm.NativeRecursive => "NATIVE_R",
        ExportForm.Libraries => "LIBRARIES",
        ExportForm.Devices => "DEVICES",
        ExportForm.NativeWithChildren => "NATIVE_WITH_CHILDREN",
        ExportForm.TaskBoth => "TASK_BOTH",
        ExportForm.ExternalNative => "EXTERNAL_NATIVE",
        ExportForm.PropertyBoth => "PROPERTY_BOTH",
        ExportForm.PropertyText => "PROPERTY_TEXT",
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "unmapped export form"),
    };

    private static IReadOnlyList<string> PythonForms(
        IReadOnlyList<(string Guid, bool Impl, bool Decl, bool Children)> cases)
    {
        var payload = cases
            .Select(c => new { guid = c.Guid, impl = c.Impl, decl = c.Decl, children = c.Children })
            .ToList();

        return RunPython(JsonSerializer.Serialize(payload), """
            import versio_types
            out = [versio_types.decide_form(versio_types.type_of(c["guid"]),
                                            c["impl"], c["decl"], c["children"])
                   for c in payload]
            """);
    }

    [SkippableFact]
    public void The_walk_descends_into_a_property_now_that_its_accessors_are_files()
    {
        // THE HALF WITH NO C# TWIN. ExportFormRules decides a property's own form and stops
        // there; whether the exporter then WALKS its accessors is decided only here, and the
        // two answers have to agree or the accessors are never written at all.
        //
        // NATIVE_R and TASK_BOTH are excluded because their children are already inside
        // what they write — a property's accessors in its recursive payload, a task
        // configuration's tasks in its .tasks.yaml. Walking either would write a second copy.
        //
        // PROPERTY is in this list because it moved twice on 2026-08-18: off NATIVE_R to
        // plain text, back when the round trip emptied every getter, and then to
        // PROPERTY_BOTH — which descends, because the accessors' readable bodies are files of
        // their own even though nothing applies them back. Each move changed the walk in one
        // language only, and this is what says so.
        Skip.IfNot(PythonFileNameTests.PythonIsAvailable(), "python is not on PATH");

        var answers = RunPython(
            JsonSerializer.Serialize(
                new[] { "PROPERTY", "PROPERTY_METHOD", "TASK_CONFIGURATION", "POU", "FOLDER" }),
            """
            import versio_types
            out = ["%s=%s" % (t, versio_types.descends_into_children(t)) for t in payload]
            """);

        Assert.Equal(
            ["PROPERTY=True", "PROPERTY_METHOD=True", "TASK_CONFIGURATION=False",
             "POU=True", "FOLDER=True"],
            answers);
    }

    [SkippableFact]
    public void The_yaml_emitter_and_its_reader_agree_about_every_shape_they_support()
    {
        // versio_yaml gained a reader for the import half of Phase 4, and until now nothing
        // python-side had ever read a document it wrote: .libraries.yaml is export-only and
        // LibraryCatalog.Parse (C#) was the only parser in existence. A .tasks.yaml is the
        // IMPORTABLE half of a task configuration, so a python reader exists now, and an
        // emitter and a parser that disagree would corrupt a task on the way back in.
        //
        // Emitting and re-reading in one process is a stronger check than either half has
        // alone: it covers the quoting rules, the scalar list added for a task's POUs, the
        // nested mapping, and the values chosen specifically to be misread — a name that
        // would parse as a bool, one that would parse as a number, and one carrying the
        // quotes and backslashes the escaper handles.
        var values = new[]
        {
            "MainTask", "No", "3.14", "t#20ms", "-2", "", "has \"quotes\" and \\slash",
        };

        var lines = RunPython(JsonSerializer.Serialize(values), """
            import versio_yaml as y

            items = [
                {"name": payload[0], "note": payload[1], "num": payload[2],
                 "pous": [payload[0], payload[3]], "odd": payload[6]},
                {"name": payload[3], "num": payload[4], "pous": []},
            ]
            top = {"schema": 1, "vendor": "codesys", "note": payload[1]}
            keys = ["name", "note", "num", "odd", "pous"]

            text = y.document("h", top, ["schema", "vendor", "note"], "tasks", items, keys)
            back_top, back_key, back_items = y.parse_document(text)

            out = [
                "key=%s" % back_key,
                "top.schema=%s" % back_top.get("schema"),
                "top.note=%s" % back_top.get("note"),
                "0.name=%s" % back_items[0].get("name"),
                "0.note=%s" % back_items[0].get("note"),
                "0.num=%s" % back_items[0].get("num"),
                "0.odd=%s" % back_items[0].get("odd"),
                "0.pous=%s" % ",".join(back_items[0].get("pous") or []),
                "1.name=%s" % back_items[1].get("name"),
                "1.num=%s" % back_items[1].get("num"),
                "count=%d" % len(back_items),
            ]
            """);

        Assert.Equal(
            [
                "key=tasks",
                "top.schema=1",
                "top.note=No",
                "0.name=MainTask",
                // "No" survives as the string it was rather than coming back a bool, and
                // "3.14" as text rather than a float. The emitter quotes both; the reader
                // must not helpfully undo that by inferring a type.
                "0.note=No",
                "0.num=3.14",
                "0.odd=has \"quotes\" and \\slash",
                "0.pous=MainTask,t#20ms",
                "1.name=t#20ms",
                "1.num=-2",
                // An EMPTY scalar list is omitted by the emitter, so it must not come back
                // as an empty item or shift the ones after it.
                "count=2",
            ],
            lines);
    }

    /// <summary>
    /// Run <c>safe_file_stem</c> over every name in one python process.
    /// </summary>
    /// <remarks>
    /// JSON in and JSON out, through files. Not the command line, because these names contain
    /// quotes, backslashes and pipes and arguing with two levels of shell quoting is how a test
    /// ends up asserting against something it did not send. Not stdout either: python on
    /// Windows writes it in the console codepage, which turns "Kühlschrank" into "K?hlschrank".
    /// </remarks>
    private static IReadOnlyList<string> PythonStems(IReadOnlyList<string> names) =>
        RunPython(JsonSerializer.Serialize(names), """
            import versio_text
            out = [versio_text.safe_file_stem(n) for n in payload]
            """);

    [SkippableFact]
    public void The_committed_task_file_parses_with_the_reader_that_ships_beside_it()
    {
        // The counterpart of the library test in WorkspaceTreeFromCommittedExportTests, and
        // the cheap half of the coverage the typed import path is missing. It needs no
        // CODESYS: it reads bytes a real export committed and parses them with the reader the
        // importer itself calls, so it catches an emitter/reader divergence for free.
        //
        // That divergence is not hypothetical. The emitter has been wrong twice on this file
        // already — a POU list rendered as python's repr, and before that a GVL suffix falling
        // through to a default — and both produced a file whose contents lied about their own
        // type while every existing assertion passed.
        //
        // It could not exist until 2026-08-15, because the committed mirror had no
        // .tasks.yaml: the fixture was left unregenerated when Phase 4 landed.
        var path = Path.Combine(
            TestPaths.Root, "fixtures", "projects", "RefrigeratorControl",
            "Device", "Plc Logic", "Application", "Task Configuration.tasks.yaml");

        Skip.If(!File.Exists(path), "the fixture's export is not committed");

        var lines = RunPython(
            JsonSerializer.Serialize(new { path = path.Replace("\\", "/") }),
            """
            import io
            import versio_yaml as y

            text = io.open(payload["path"], encoding="utf-8").read()
            top, key, items = y.parse_document(text)

            out = ["key=%s" % key,
                   "schema=%s" % top.get("schema"),
                   "vendor=%s" % top.get("vendor"),
                   "count=%s" % top.get("taskCount"),
                   "items=%d" % len(items)]
            for it in items:
                out.append("%s|%s|%s|%s|%s" % (
                    it.get("name"), it.get("priority"), it.get("interval"),
                    it.get("coreBinding"), ",".join(it.get("pous") or [])))
            """);

        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        // The header's own count, so adding a task to the project does not fail this. What
        // is being checked is that the reader returns as many tasks as the emitter declared —
        // a reader that dropped one fails, and a third task does not. It read `count=2` until
        // 2026-08-18, when the owner added one.
        var declaredTasks = File.ReadAllLines(path)
            .First(l => l.StartsWith("taskCount:", StringComparison.Ordinal))
            .Split(':')[1].Trim();

        Assert.Equal(
            [
                "key=tasks",
                "schema=1",
                "vendor=codesys",
                $"count={declaredTasks}",
                $"items={declaredTasks}",

                // Every value is TEXT, including coreBinding, which CODESYS wants as an int
                // on the way back. The reader never infers a type, because priority wants a
                // string and core binding wants an int and one guess would be wrong for one
                // of them — the caller converts, knowing which field it holds.
                //
                // interval is the reason the values are verbatim: "t#20ms" on one task and
                // "100" on the other, in one project. A normaliser would rewrite one of them
                // and put a diff in front of somebody that they did not make.
                //
                // These two are asserted by CONTAINMENT below rather than by listing every
                // row, because the project gained a third task on 2026-08-18 and will gain
                // more. What must not change is that these two arrive verbatim.
            ],
            lines.Take(5));

        Assert.Contains("MainTask|1|t#20ms|-2|PLC_PRG,Simulation", lines);
        Assert.Contains("VISU_TASK|31|100|-1|VisuElems.Visu_Prg", lines);

        // And every row is five verbatim fields — a reader that inferred a type or dropped a
        // field shows up here whichever tasks the project happens to hold.
        foreach (var row in lines.Skip(5))
        {
            Assert.Equal(5, row.Split('|').Length);
            Assert.False(string.IsNullOrWhiteSpace(row.Split('|')[0]), $"nameless task: {row}");
        }
    }

    [SkippableFact]
    public void The_two_native_normalisers_agree_on_every_measured_volatility_case()
    {
        // versio_native_equiv.py is the python half of NativeXmlNormalizer, and this is the
        // check that keeps the pair honest. CODESYS.md section 9 counts four drifts across
        // the four pairs that came before it - two with the C# right and two with the python
        // right - and NOT ONE was catchable by comparing the two sources. The GUID tables
        // were byte-identical every time; the behaviour differed. So this compares outputs.
        //
        // The corpus is NativeXmlNormalizerTests' own, case for case, because every entry in
        // it is a real measured difference between an export and its re-export on 3.5.22.20
        // rather than a difference somebody imagined. Anything added there belongs here too.
        //
        // The normalised TEXT is compared, not a verdict. Two implementations can agree on
        // "equivalent" for every pair in a corpus while masking different things - a
        // false-true on both sides looks identical to agreement, which is the failure mode a
        // pair of booleans cannot see.
        string[] corpus =
        [
            // 1. Counters.
            """<Single Name="UniqueIdGenerator" Type="string">26</Single>""",
            """<Single Name="UniqueIdGenerator" Type="string">29</Single>""",
            """<Single Name="Timestamp" Type="long">638600000000000000</Single>""",

            // 2. Reference GUIDs, including one inside a counter's value - the case that
            //    pins the ORDER of the two maskings. Counters go first on both sides, so
            //    this is masked once and the guid count reports what survived into the
            //    comparison rather than what was in the file.
            """<Single Name="EventPOUGuid" Type="System.Guid">ac6c9991-dfa0-4172-9770-3ebcbacdf556</Single>""",
            """<Single Name="UniqueIdGenerator" Type="string">288f98d1-b9d3-4ed7-99e7-12de96a16159</Single>""",

            // 3. Unordered containers, one per name, so dropping any one of the three from
            //    either table shows up here rather than in a real file six months later.
            "<List>\n<Single Type=\"string\">UseInitValue</Single>\n<Single Type=\"string\">DescriptionTextId</Single>\n</List>",
            "<Properties>\n<b/>\n<a/>\n</Properties>",
            "<Dictionary Count=\"2\">\n<z>1</z>\n<y>2</y>\n</Dictionary>",
            "<List>\n<Properties>\n<b/>\n<a/>\n</Properties>\n</List>",

            // Two container names on ONE line, closing at different depths. This is the only
            // case in the corpus where the ORDER of the two container tables is observable:
            // taking List first stops at </List> and sorts two lines, taking Properties first
            // runs on to </Properties> and sorts four - including the </List> in between.
            // Both sides take the first match, so the tables have to agree on order as well
            // as on membership, and without this case they could disagree silently.
            "<Properties><List>\n<b/>\n<a/>\n</List>\n<c/>\n</Properties>",

            // Content OUTSIDE a container keeps its order, or a real reordering of things
            // that ARE ordered - statements, task priorities - would be sorted away.
            "<Body>\n<b/>\n<a/>\n</Body>",

            // 3b. The sort ORDER itself, above the BMP - and this case had to be added,
            //     because the first version of this corpus did not have it and deleting the
            //     python's ordinal sort key left the whole suite green. C# sorts by UTF-16
            //     code unit and CPython 3 by code point, and the two disagree exactly here:
            //     U+1F600 is a surrogate pair starting D83D, which sorts BELOW U+E000 as
            //     code units and ABOVE it as code points. So the two normalisers emit these
            //     two lines in opposite orders unless the python encodes to UTF-16 first.
            //
            //     Not a hypothetical corner of the format. GlobalTextList.xml is where a
            //     project's translated HMI strings live, it is one of the four files that
            //     churn on every cycle, and an emoji in an operator message is an ordinary
            //     thing for it to contain.
            "<List>\n<t>\U0001F600</t>\n<t>\uE000</t>\n</List>",

            // 3c. Siblings are sorted as WHOLE elements, cut by tag depth (review item 20,
            //     2026-10-24): two multi-line children keep their own lines together and in
            //     order, a container nested inside a sibling is sorted on its own, a line
            //     holding several tags counts each, an unterminated child is kept whole, and
            //     a close with no open degrades to the old line sort. Any of these cut
            //     differently on one side gives different TEXT while every verdict agrees.
            "<List>\n<Single Name=\"B\">\n<Id>2</Id>\n<Value>y</Value>\n</Single>\n<Single Name=\"A\">\n<Id>1</Id>\n<Value>x</Value>\n</Single>\n</List>",
            "<List>\n<Single Name=\"A\">\n<Properties>\n<b/>\n<a/>\n</Properties>\n</Single>\n</List>",
            "<List>\n<b><c/></b>\n<a><d>\n</d></a>\n</List>",
            "<List>\n<b>\n<a/>\n</List>",
            "<List>\n</z>\n<a/>\n</List>",
            "<List>\n<!-- a comment -->\n<?pi x?>\n<a/>\n</List>",

            // A self-closing container has no body to sort, and the test is on the whole
            // line rather than the tag, so this one is left alone by both sides.
            "<List/>",
            "<List>\n<a/>\n<b/><c/>\n</List>",

            // Degrade rather than hang or throw: a truncated payload costs one unsorted
            // block, not the rest of the file.
            "<List>\n<b/>\n<a/>",

            // Line-ending and empty handling, where the C# tolerates null and python None.
            "<a/>\r\n<b/>",
            "",

            // Nothing volatile at all: the pair must agree on leaving a file alone.
            "<Signals>\n<Single Name=\"Core2\" Type=\"int\">-1</Single>\n</Signals>",
        ];

        // Both maskings, because they are different questions. Masking is required for an
        // import round trip and must be OFF for export-to-export, where GUIDs are stable and
        // a difference in one is genuinely meaningful. A parity test that only ever ran the
        // default would leave the export-to-export path - Phase 5's path - uncompared.
        foreach (var maskGuids in new[] { true, false })
        {
            var expected = corpus.Select(x =>
            {
                var n = NativeXmlNormalizer.Normalize(x, maskGuids);
                return $"{n.GuidsMasked}|{n.CountersMasked}|{n.Text}";
            }).ToList();

            var actual = RunPython(
                JsonSerializer.Serialize(new { cases = corpus, mask = maskGuids }),
                """
                import versio_native_equiv as ne

                out = []
                for case in payload["cases"]:
                    text, guids, counters = ne.normalize(case, payload["mask"])
                    out.append(u"%d|%d|%s" % (guids, counters, text))
                """);

            // Counts first. Indexing straight into actual would report a divergence as an
            // IndexOutOfRange from inside the assertion loop, which says nothing about which
            // side came up short.
            Assert.Equal(expected.Count, actual.Count);

            for (var i = 0; i < expected.Count; i++)
            {
                output.WriteLine($"[mask={maskGuids}] {i}: {actual[i].Replace("\n", "\\n")}");
                Assert.True(expected[i] == actual[i],
                    $"case {i} (maskGuids: {maskGuids}) diverges.\n"
                    + $"  C#     : {expected[i].Replace("\n", "\\n")}\n"
                    + $"  python : {actual[i].Replace("\n", "\\n")}\n"
                    + $"  input  : {corpus[i].Replace("\n", "\\n")}");
            }
        }

        // None is python's null, and the C# tolerates one. Kept out of the corpus above so
        // the JSON carries a real absence rather than an empty string standing in for it.
        Assert.Equal(
            ["0|0|"],
            RunPython("null", """
                import versio_native_equiv as ne
                text, guids, counters = ne.normalize(payload)
                out = [u"%d|%d|%s" % (guids, counters, text)]
                """));
    }

    [SkippableFact]
    public void The_two_native_normalisers_agree_on_real_CODESYS_output()
    {
        // The synthetic corpus above is every case somebody thought to write down. This one
        // is 2.9 MB of genuine CODESYS output that nobody curated: the equivalence captures
        // from a real export -> import -> re-export cycle, and every native file in the
        // committed mirror - Live_Visu.vis.xml among them, at 2.5 MB, which is where the
        // unordered-container reordering actually occurs at scale.
        //
        // It is the half of this pair that would have caught the section 9 drifts, all four
        // of which were behaviours no unit case covered rather than rules anybody disagreed
        // about.
        //
        // Compared by hash rather than by text: the alternative is deserialising 2.9 MB of
        // JSON per file for an assertion that is almost always true. The mask counts and the
        // length travel beside it so a failure still says something - equal lengths with
        // different hashes is a substitution, unequal counts names which masking diverged.
        string[] files =
        [
            TestPaths.Equivalence("baseline", "GlobalTextList.xml"),
            TestPaths.Equivalence("baseline", "Task Configuration.xml"),
            TestPaths.Equivalence("roundtrip", "GlobalTextList.xml"),
            TestPaths.Equivalence("roundtrip", "Task Configuration.xml"),
            .. Directory.EnumerateFiles(
                    TestPaths.Project("RefrigeratorControl"),
                    "*.xml", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal),
        ];

        // Existence, not a count: the four equivalence paths are named rather than globbed, so
        // a count test would pass with all four missing and then throw out of ReadAllText.
        Skip.If(files.Length < 6 || files.Any(f => !File.Exists(f)),
                "the committed fixtures are not present");

        var expected = files.Select(path =>
        {
            var n = NativeXmlNormalizer.Normalize(File.ReadAllText(path));
            return $"{Path.GetFileName(path)}|{Sha256(n.Text)}|{n.GuidsMasked}|"
                   + $"{n.CountersMasked}|{n.Text.Length}";
        }).ToList();

        var actual = RunPython(
            JsonSerializer.Serialize(files.Select(f => f.Replace("\\", "/")).ToList()),
            """
            import hashlib, io, os
            import versio_native_equiv as ne

            out = []
            for path in payload:
                text, guids, counters = ne.normalize(
                    io.open(path, encoding="utf-8").read())
                digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
                out.append(u"%s|%s|%d|%d|%d" % (
                    os.path.basename(path), digest, guids, counters, len(text)))
            """);

        foreach (var line in actual)
        {
            output.WriteLine(line);
        }

        Assert.Equal(expected, actual);

        // Non-vacuous: a normaliser that returned its input unchanged would agree with
        // another one that did the same. These files really do carry all three volatility
        // classes, so both sides really did have work to do.
        Assert.Contains(expected, e => e.Split('|')[2] != "0");
        Assert.Contains(expected, e => e.Split('|')[3] != "0");
    }

    [SkippableFact]
    public void The_python_normaliser_reaches_the_same_verdicts_the_C_sharp_one_is_pinned_to()
    {
        // RealRoundTripEquivalenceTests asserts three things about the committed captures,
        // and they are the measurements this whole layer rests on. The python side has to
        // reach them too, because Phase 2 will take the verdict from python and delete an
        // object on the strength of it.
        //
        // The Core2 case is the one that matters most. import_native changed a task's CPU
        // core affinity from -1 to -2 and it is deliberately NOT masked - it is a real value
        // CODESYS altered without being asked. A python normaliser that masked it would call
        // the file equivalent, and Phase 2 would then be willing to delete an object whose
        // live state had genuinely changed.
        var files = new[]
        {
            TestPaths.Equivalence("baseline", "GlobalTextList.xml"),
            TestPaths.Equivalence("roundtrip", "GlobalTextList.xml"),
            TestPaths.Equivalence("baseline", "Task Configuration.xml"),
            TestPaths.Equivalence("roundtrip", "Task Configuration.xml"),
        };

        Skip.If(files.Any(f => !File.Exists(f)), "the equivalence captures are not committed");

        var lines = RunPython(
            JsonSerializer.Serialize(files.Select(f => f.Replace("\\", "/")).ToList()),
            """
            import io
            import versio_native_equiv as ne

            texts = [io.open(p, encoding="utf-8").read() for p in payload]

            out = [
                u"raw text list differs=%s" % (texts[0] != texts[1]),
                u"text list equivalent=%s" % ne.are_equivalent(texts[0], texts[1]),
                u"raw tasks differ=%s" % (texts[2] != texts[3]),
                u"tasks equivalent=%s" % ne.are_equivalent(texts[2], texts[3]),
            ]

            # Exactly one surviving difference, and it is Core2 -1 -> -2.
            a = ne.normalize(texts[2])[0].split(u"\n")
            b = ne.normalize(texts[3])[0].split(u"\n")
            diff = [(x, y) for x, y in zip(a, b) if x != y]
            out.append(u"task diffs=%d" % len(diff))
            out.append(u"lines=%d/%d" % (len(a), len(b)))
            if diff:
                out.append(u"A=%s" % diff[0][0].strip())
                out.append(u"B=%s" % diff[0][1].strip())
            """);

        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        Assert.Equal(
            [
                // Byte-different, or the equivalence result below would mean nothing.
                "raw text list differs=True",
                "text list equivalent=True",
                "raw tasks differ=True",

                // NOT equivalent, and that is the correct answer: the Core2 deviation is a
                // real change and masking it away is the one thing this normaliser must not
                // do. It is pinned on the C# side by
                // Task_configuration_import_mutates_Core2_and_ONLY_Core2.
                "tasks equivalent=False",
                "task diffs=1",
                "lines=188/188",
                """A=<Single Name="Core2" Type="int">-1</Single>""",
                """B=<Single Name="Core2" Type="int">-2</Single>""",
            ],
            lines);
    }

    /// <summary>
    /// The planner's prediction of a refusal and the script's actual refusal, per form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pair this repository paid for.</b> The confirmation dialog is shown ~20 s before
    /// CODESYS starts, so the planner has to say which objects an import will really remove —
    /// and if it says "will be removed" for a form the script then refuses, the dialog is
    /// lying about a destructive operation. That exact lie shipped once already, pointed the
    /// other way: the script grew <c>remove()</c> while the dialog went on saying objects
    /// <i>will stay</i>, and a full green suite did not notice because nothing compared the
    /// two sides.
    /// </para>
    /// <para>
    /// Compared by <b>output over a corpus of forms</b> rather than by reading both rules,
    /// for the reason the class remarks give: two implementations can agree in intention and
    /// disagree on a case neither author thought about.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public void The_planner_and_the_script_agree_about_which_deletions_can_be_checked()
    {
        Skip.If(!Directory.Exists(Scripts), "the scripts are not present");

        // Every importable form the exporter can record. The two export-only forms are
        // deliberately absent: PlanDeletions drops them before verifiability is asked, because
        // a file rewritten by every export and applied by nothing is not evidence of anything.
        // Each form twice: once with a recorded normalised hash and once without. The second
        // half is not padding — it is the whole of the schema-5 condition, and a corpus of
        // forms alone would have both sides answering from the form and agreeing about a rule
        // neither was applying.
        (string Form, string NHash)[] cases =
        [
            ("ST_FULL", ""), ("ST_DECL", ""), ("ST_IMPL", ""),
            ("NATIVE", ""), ("NATIVE_R", ""), ("NATIVE_WITH_CHILDREN", ""),
            ("NATIVE", "sha256:n"), ("NATIVE_R", "sha256:n"),
            ("NATIVE_WITH_CHILDREN", "sha256:n"),
            ("TASK_BOTH", ""), ("TASK_BOTH", "sha256:n"),
        ];

        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects = [.. cases.Select((c, i) => new ProjectObject
            {
                Guid = $"g{i}", Name = $"{c.Form}#{i}", Type = "POU", Form = c.Form,
                Path = $"App/{c.Form}{i}.bin", Hash = "sha256:0", NormalizedHash = c.NHash,
            })],
        };

        // An empty mirror: every recorded object has lost every file, so each one is a
        // deletion candidate and each one's verifiability is answered.
        var plan = CodesysImportPlanner.Plan(manifest, []);

        var fromPlanner = plan.Deletions
            .ToDictionary(d => d.Name, d => d.CannotVerify is null, StringComparer.Ordinal);

        var csharp = cases
            .Select((c, i) => $"{c.Form}/{(c.NHash.Length > 0 ? "nhash" : "none")}"
                              + $"={fromPlanner[$"{c.Form}#{i}"]}")
            .ToList();

        var python = RunPython(
            JsonSerializer.Serialize(cases.Select(c => new { form = c.Form, nhash = c.NHash })),
            """
            import versio_types
            out = ["%s/%s=%s" % (c["form"], "nhash" if c["nhash"] else "none",
                                 versio_types.can_verify(c["form"], c["nhash"]))
                   for c in payload]
            """);

        foreach (var line in csharp)
        {
            output.WriteLine(line);
        }

        Assert.Equal(csharp, python);

        // And the corpus is not vacuous in any direction — a rule that answered the same way
        // for everything would pass the comparison above while checking nothing. All three
        // outcomes are asserted: always-yes, yes-only-with-a-hash, and never.
        Assert.Contains("ST_FULL/none=True", csharp);
        Assert.Contains("NATIVE/none=False", csharp);
        Assert.Contains("NATIVE/nhash=True", csharp);
        Assert.Contains("TASK_BOTH/nhash=False", csharp);
    }

    /// <summary>
    /// The line sketch, written by the exporter and read by the planner.
    /// </summary>
    /// <remarks>
    /// <b>A disagreement here would not fail — it would go quiet.</b> The exporter records the
    /// sketch and the planner compares an arrived file against it; two implementations that
    /// hashed or ordered differently would share no values at all, estimate every similarity as
    /// zero, and simply stop detecting renames. Nothing would throw, no warning would fire, and
    /// the fallback looks exactly like "these files are not alike".
    /// </remarks>
    [SkippableFact]
    public void The_two_line_sketch_implementations_produce_the_same_fingerprint()
    {
        Skip.If(!Directory.Exists(Scripts), "the scripts are not present");

        // Chosen to hit what the sketch is defined by: blank lines and indentation dropped,
        // duplicates collapsed to one value, non-ASCII hashed as UTF-8, and more distinct
        // lines than the sketch keeps so the BOTTOM-K truncation is exercised rather than
        // assumed.
        string[] corpus =
        [
            string.Empty,
            "PROGRAM A\nVAR\nEND_VAR\nx := 1;",
            "  PROGRAM A  \n\n\tVAR\nEND_VAR\n\n  x := 1;  ",
            "END_IF\nEND_IF\nEND_IF",
            "Kühlschrank := TRUE;\nКириллица();",
            string.Join("\n", Enumerable.Range(1, 60).Select(i => $"step_{i}();")),
        ];

        // A LAMBDA, NOT A METHOD GROUP. `Select(LineSketch.Of)` compiles and binds to Select's
        // INDEX overload — Func<string,int,string> — so the element's position is passed as the
        // sketch size: index 1 produced a one-value sketch, index 5 a five-value one. Nothing
        // warns, and the sketches look plausible. This parity test is what caught it, which is
        // the argument for comparing two implementations by output rather than reading either.
        var csharp = corpus.Select(t => LineSketch.Of(t)).ToList();

        var python = RunPython(JsonSerializer.Serialize(corpus), """
            import versio_text
            out = [versio_text.line_sketch(t) for t in payload]
            """);

        foreach (var line in csharp)
        {
            output.WriteLine(line.Length == 0 ? "(empty)" : line);
        }

        Assert.Equal(csharp, python);

        // Non-vacuous: the third case must really have collapsed to fewer values than lines,
        // and the last must really have been truncated to the sketch size.
        Assert.Single(csharp[3].Split(','));
        Assert.Equal(LineSketch.Size, csharp[5].Split(',').Length);

        // And whitespace must genuinely not matter, or the two sides could agree on a rule
        // that makes the sketch useless.
        Assert.Equal(csharp[1], csharp[2]);
    }

    [SkippableFact]
    public void The_two_sketch_similarity_estimators_agree()
    {
        Skip.If(!Directory.Exists(Scripts), "the scripts are not present");

        var a = LineSketch.Of("a();\nb();\nc();\nd();");
        var b = LineSketch.Of("a();\nb();\nc();\nz();");
        var unrelated = LineSketch.Of("q();\nr();\ns();\nt();");

        var pairs = new[] { (a, a), (a, b), (a, unrelated), (a, string.Empty) };
        var csharp = pairs.Select(p => $"{LineSketch.Similarity(p.Item1, p.Item2):F3}").ToList();

        var python = RunPython(
            JsonSerializer.Serialize(pairs.Select(p => new { a = p.Item1, b = p.Item2 })),
            """
            import versio_text
            out = ["%.3f" % versio_text.sketch_similarity(p["a"], p["b"]) for p in payload]
            """);

        output.WriteLine(string.Join(" ", csharp));
        Assert.Equal(csharp, python);

        // Ordered as a similarity measure must be, or agreement would be worthless.
        Assert.Equal("1.000", csharp[0]);
        Assert.Equal("0.000", csharp[3]);
        Assert.True(double.Parse(csharp[1]) > double.Parse(csharp[2]));
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Run a snippet against the real modules, with <c>payload</c> in and <c>out</c> out.
    /// </summary>
    /// <remarks>
    /// The snippet is inserted verbatim, so it must define <c>out</c> and may read
    /// <c>payload</c>. Everything else — the module path, the JSON plumbing, the encoding — is
    /// handled here so a new parity test is a few lines rather than a copy of this method.
    /// </remarks>
    // ── The ST codec ──────────────────────────────────────────

    /// <summary>
    /// Every ST payload these tests can think of, packed and unpacked by both codecs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pair the plan calls the highest-consequence uncompared thing in the repository,
    /// and it was right.</b> <c>StTextCodec</c> and <c>versio_text</c> both split a
    /// <c>.st</c> file into a declaration and an implementation. A disagreement between them
    /// does not fail, warn or throw: it silently moves code from one half of a POU into the
    /// other, and the result is a project that compiles into something nobody wrote.
    /// </para>
    /// <para>
    /// <b>Outputs, not intentions.</b> Each side already has its own round-trip tests and
    /// each passes them; that is exactly the arrangement <c>safe_file_stem</c> was in when it
    /// had drifted from <c>EncodeStem</c> in two directions at once. The only check that
    /// closes it is running both over the same input and comparing what comes out.
    /// </para>
    /// <para>
    /// <b>Real files are in the corpus as well as adversarial ones</b>, taken from the two
    /// committed fixtures: a POU, a member method whose file carries the delimiter, an action
    /// with no declaration at all, and a DUT with no delimiter. The awkward cases are what
    /// finds a bug; the ordinary ones are what stops a "fix" for it renaming or re-splitting
    /// every file anybody has already committed.
    /// </para>
    /// <para>
    /// <b>One caveat this cannot cover, stated rather than left implicit.</b> The code that
    /// actually runs inside CODESYS is IronPython 2.7 and this harness runs whatever
    /// <c>python</c> on PATH is — CPython 3. Where the two Pythons agree, which is
    /// everything the codec does with <c>strip</c>, <c>replace</c> and <c>find</c>, that is
    /// sound. It is not a substitute for the round trip through a real CODESYS, which
    /// <c>AdoptAndExportTests</c> provides.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public void The_st_codec_and_versio_text_agree_about_every_payload_either_can_produce()
    {
        Skip.IfNot(PythonFileNameTests.PythonIsAvailable(), "python is not on PATH");

        var corpus = StCorpus();

        // Enough shapes that a green run means something. If this number falls, a case was
        // deleted rather than a rule proven.
        Assert.True(corpus.Count >= 20, $"only {corpus.Count} payloads in the corpus");

        // ── one: JOIN, declaration + implementation into one file ──────────────────
        var joinPayload = corpus.Select(c => new[] { c.Declaration, c.Implementation });
        var pythonJoined = RunPython(JsonSerializer.Serialize(joinPayload), """
            import versio_text
            out = [versio_text.join_st(p[0], p[1]) for p in payload]
            """);

        var csharpJoined = corpus
            .Select(c => StTextCodec.Join(c.Declaration, c.Implementation))
            .ToList();

        for (var i = 0; i < corpus.Count; i++)
        {
            Assert.True(csharpJoined[i] == pythonJoined[i],
                $"join disagrees on '{corpus[i].Name}':\n"
                + $"  C#     {Visible(csharpJoined[i])}\n"
                + $"  python {Visible(pythonJoined[i])}");
        }

        // ── two: SPLIT, the direction that corrupts source when it is wrong ────────
        //
        // Fed the JOINED text and every raw payload as well: a file on disk was not
        // necessarily written by Join. It may have been hand-edited, checked out with
        // different line endings, or written by an older Versio.
        var toSplit = csharpJoined
            .Concat(corpus.Select(c => c.Declaration))
            .Concat(corpus.Select(c => c.Implementation))
            .ToList();

        var pythonSplit = RunPython(JsonSerializer.Serialize(toSplit), """
            import json
            import versio_text
            out = [json.dumps(list(versio_text.split_st(t))) for t in payload]
            """);

        for (var i = 0; i < toSplit.Count; i++)
        {
            var (declaration, implementation) = StTextCodec.Split(toSplit[i]);
            var fromPython = JsonSerializer.Deserialize<string[]>(pythonSplit[i])!;

            Assert.True(declaration == fromPython[0],
                $"split disagrees about the DECLARATION of payload {i}:\n"
                + $"  C#     {Visible(declaration)}\n"
                + $"  python {Visible(fromPython[0])}");

            Assert.True(implementation == fromPython[1],
                $"split disagrees about the IMPLEMENTATION of payload {i} — which is code "
                + $"moving between halves:\n"
                + $"  C#     {Visible(implementation)}\n"
                + $"  python {Visible(fromPython[1])}");
        }

        // ── three: the declaration-only path, which a GVL and a DUT take ───────────
        var pythonDeclOnly = RunPython(
            JsonSerializer.Serialize(corpus.Select(c => c.Declaration)), """
            import versio_text
            out = [versio_text.normalize_decl_only(d) for d in payload]
            """);

        for (var i = 0; i < corpus.Count; i++)
        {
            Assert.True(StTextCodec.DeclarationOnly(corpus[i].Declaration) == pythonDeclOnly[i],
                $"normalize_decl_only disagrees on '{corpus[i].Name}'");
        }

        // ── three b: a declaration-only object's file is read as it was written ────
        //
        // The export writes a DUT with normalize_decl_only and never escapes it; the import
        // read every plain .st through split_st, which unescapes and cuts at a delimiter.
        // A DUT holding a literal escape marker came back changed and one holding the
        // literal delimiter line was truncated (the 2026-10-24 review's item 5). The rule
        // lives in read_textual and is chosen by the object: no implementation, no decoding.
        var declOnly = corpus.Select(c => c.Declaration)
            .Concat(
            [
                "TYPE Marker :\nSTRUCT\n  // a literal //!VERSIO-ESC!D in a comment\n  x : INT;\nEND_STRUCT\nEND_TYPE",
                "TYPE Delim :\nSTRUCT\n  // --- BEGIN IMPLEMENTATION ---\n  y : INT;\nEND_STRUCT\nEND_TYPE",
                "TYPE Both :\nSTRUCT\n  // //!VERSIO-ESC!E and //!VERSIO-ESC!D\nEND_STRUCT\nEND_TYPE",
            ])
            .ToList();

        var pythonRead = RunPython(JsonSerializer.Serialize(declOnly), """
            import json
            import versio_text
            out = [json.dumps(list(versio_text.read_textual(t, False))) for t in payload]
            """);

        for (var i = 0; i < declOnly.Count; i++)
        {
            var fromPython = JsonSerializer.Deserialize<string[]>(pythonRead[i])!;
            var expected = StTextCodec.DeclarationOnly(declOnly[i]).TrimEnd('\n');

            Assert.True(expected == fromPython[0],
                $"read_textual(decl-only) disagrees with DeclarationOnly on payload {i}:\n"
                + $"  C#     {Visible(expected)}\n"
                + $"  python {Visible(fromPython[0])}");
            Assert.Equal(string.Empty, fromPython[1]);

            // And it is NOT what split_st would have made of it, for the three adversarial
            // cases: that difference is the defect.
            if (i >= corpus.Count)
            {
                var (split, _) = StTextCodec.Split(declOnly[i]);
                Assert.True(split != fromPython[0],
                    $"payload {i} reads the same through Split as through the declaration-only "
                    + "reader, so this case no longer pins the asymmetry");
            }
        }

        // With an implementation the file is the escaped two-part form, exactly as Split.
        var pythonReadFull = RunPython(JsonSerializer.Serialize(csharpJoined), """
            import json
            import versio_text
            out = [json.dumps(list(versio_text.read_textual(t, True))) for t in payload]
            """);

        for (var i = 0; i < csharpJoined.Count; i++)
        {
            var fromPython = JsonSerializer.Deserialize<string[]>(pythonReadFull[i])!;
            var (declaration, implementation) = StTextCodec.Split(csharpJoined[i]);
            Assert.True(declaration == fromPython[0] && implementation == fromPython[1],
                $"read_textual(full) disagrees with Split on '{corpus[i].Name}'");
        }

        // ── four: the round trip, on BOTH sides, over the same corpus ──────────────
        //
        // Agreeing with each other and being wrong together is a real outcome, and the two
        // checks above cannot see it. This is the invariant each side already claims.
        for (var i = 0; i < corpus.Count; i++)
        {
            var (declaration, implementation) = StTextCodec.Split(csharpJoined[i]);

            // Visible(), because every difference this can find is an invisible character:
            // a bare Assert.Equal prints two strings that look identical to each other.
            Assert.True(Normalized(corpus[i].Declaration) == declaration,
                $"round trip lost the declaration of '{corpus[i].Name}':\n"
                + $"  in  {Visible(Normalized(corpus[i].Declaration))}\n"
                + $"  out {Visible(declaration)}");

            Assert.True(Normalized(corpus[i].Implementation) == implementation,
                $"round trip lost the implementation of '{corpus[i].Name}':\n"
                + $"  in  {Visible(Normalized(corpus[i].Implementation))}\n"
                + $"  out {Visible(implementation)}");
        }

        // ── five: the RULE, not a sample of it ────────────────────────────
        //
        // The four cases above can only find a divergence they happen to contain, and the
        // one they DID find - U+001C-U+001F, which python calls whitespace and .NET does not
        // - was in the corpus because somebody guessed it might be. This compares the trim
        // sets themselves, so the next disagreement does not need to be guessed at first.
        var pythonTrimSet = RunPython("[]", """
            import versio_text
            out = sorted(u"%04X" % ord(c) for c in versio_text.WS)
            """);

        Assert.Equal(
            StTextCodec.TrimmedCharacters.Select(c => ((int)c).ToString("X4"))
                .OrderBy(h => h, StringComparer.Ordinal),
            pythonTrimSet);

        output.WriteLine($"{corpus.Count} payloads joined, {toSplit.Count} split, "
                         + $"{pythonTrimSet.Count} trim characters, "
                         + "both codecs agreeing on every one");
    }

    /// <summary>What <c>Trim</c> and <c>strip</c> are both expected to have done.</summary>
    /// <remarks>
    /// Trimmed with the codec's OWN set rather than with <c>string.Trim()</c>. Writing the
    /// default here made this round-trip check fail against a payload the two codecs had just
    /// agreed on — the expectation was asserting .NET's 25 characters against an implementation
    /// that trims python's 29, which is the very divergence this test exists to catch,
    /// restated as a bug in the test.
    /// </remarks>
    private static string Normalized(string s) =>
        s.Replace("\r\n", "\n").Trim([.. StTextCodec.TrimmedCharacters]);

    /// <summary>Control characters made visible, so a failure names the byte.</summary>
    private static string Visible(string s) =>
        string.Concat(s.Select(c => c switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ when char.IsControl(c) => $"\\x{(int)c:x2}",
            _ => c.ToString(),
        }));

    private sealed record StPayload(string Name, string Declaration, string Implementation);

    /// <summary>
    /// Every ST payload shape worth asking both codecs about.
    /// </summary>
    /// <remarks>
    /// The delimiter and escape-marker cases are the ones that corrupt source when they are
    /// wrong: a declaration containing the delimiter made an earlier version of this codec
    /// cut in the wrong place and move code between halves, which is what the escape marker
    /// exists to prevent and what the pair of implementations has to agree about exactly.
    /// </remarks>
    private static List<StPayload> StCorpus()
    {
        const string delimiter = StTextCodec.ImplementationDelimiter;
        const string escape = "//!VERSIO-ESC!";

        List<StPayload> corpus =
        [
            new("ordinary", "PROGRAM Main\nVAR\n\tx : INT;\nEND_VAR", "x := x + 1;"),
            new("empty implementation", "PROGRAM Empty\nVAR\nEND_VAR", ""),
            new("empty declaration", "", "x := 1;"),
            new("both empty", "", ""),
            new("delimiter in the declaration", $"PROGRAM D\n{delimiter}\nVAR\nEND_VAR", "x := 1;"),
            new("delimiter in the implementation", "PROGRAM D\nVAR\nEND_VAR", $"x := 1;\n{delimiter}\ny := 2;"),
            new("delimiter twice", $"{delimiter}\n{delimiter}", $"{delimiter}"),
            new("escape marker present", $"PROGRAM E\n{escape}\nVAR\nEND_VAR", $"{escape}D"),
            new("escape marker doubled", $"{escape}{escape}", $"{escape}E{escape}D"),
            new("crlf throughout", "PROGRAM C\r\nVAR\r\nEND_VAR", "x := 1;\r\ny := 2;"),
            new("leading and trailing blank lines", "\n\n\nPROGRAM B\n\n\n", "\n\nx := 1;\n\n"),
            new("tabs and spaces around", "\t  PROGRAM T  \t", "  \tx := 1;\t  "),
            new("trailing whitespace only", "   ", "\t\n "),
            new("unicode in a comment", "PROGRAM U\nVAR\nEND_VAR\n(* em—dash, box │ char *)", "x := 1; (* café *)"),
            new("very long single line", "PROGRAM L\nVAR\n\ts : STRING := '" + new string('a', 500) + "';\nEND_VAR", "x := 1;"),
            new("no trailing newline", "PROGRAM N\nVAR\nEND_VAR", "x := 1;"),
            new("only a delimiter", delimiter, ""),
            new("blank lines inside", "PROGRAM I\n\nVAR\n\n\tx : INT;\n\nEND_VAR", "x := 1;\n\n\ny := 2;"),

            // The separator characters CPython's str.strip() removes and char.IsWhiteSpace
            // does NOT consider whitespace. If the two codecs ever disagree about the edge
            // of a payload, this is where it shows.
            new("ascii separators at the edges", "\u001cPROGRAM S\nVAR\nEND_VAR\u001f", "\u001ex := 1;\u001d"),
            new("non-breaking space at the edges", "\u00a0PROGRAM NB\u00a0", "\u00a0x := 1;\u00a0"),
            new("next-line character", "\u0085PROGRAM NL", "x := 1;\u0085"),
        ];

        // AND THE REAL FILES, from both committed fixtures. The adversarial cases above are
        // what finds a bug; these are what says the fix did not change how an ordinary POU,
        // a member method, an action or a DUT is packed - which would re-split every file in
        // every mirror anybody has committed.
        foreach (var relative in new[]
                 {
                     "RefrigeratorControl/Device/Plc Logic/Application/Simulation.st",
                     "RefrigeratorControl/Device/Plc Logic/Application/Glob_Var.gvl.st",
                     "MachineWithMembers/Device/Plc Logic/Application/E_Mode.st",
                     "MachineWithMembers/Device/Plc Logic/Application/Pumps/FB_Pump.st",
                     "MachineWithMembers/Device/Plc Logic/Application/Pumps/FB_Pump.Start.st",
                     "MachineWithMembers/Device/Plc Logic/Application/Pumps/FB_Pump.Stop.action.st",
                 })
        {
            var path = Path.Combine(
                TestPaths.Fixtures, "projects",
                relative.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                continue;
            }

            // Split with the C# codec and feed BOTH halves back in as a payload of their
            // own. Whatever the exporter wrote is a real declaration and a real
            // implementation, and this is how they get into the corpus without a fixture
            // author having to retype them.
            var (declaration, implementation) = StTextCodec.Split(File.ReadAllText(path));
            corpus.Add(new StPayload(relative, declaration, implementation));
        }

        return corpus;
    }

    private static IReadOnlyList<string> RunPython(string payloadJson, string body) =>
        JsonSerializer.Deserialize<List<string>>(
            CPythonHarness.RunJson(payloadJson, body, jsonOut: true))!;
}
