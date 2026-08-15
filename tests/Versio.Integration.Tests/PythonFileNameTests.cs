using System.Diagnostics;
using System.Text.Json;
using Versio.Core.Plc.Codesys;
using Versio.Core.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

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
    private static readonly string Scripts = Path.Combine(
        Core.Tests.TestPaths.Root, "src", "Versio.Scripts.Codesys");

    /// <summary>Run <c>safe_file_stem</c> over one name and return what it produced.</summary>
    private string Stem(string name)
    {
        // Passed through a file rather than the command line: these names contain quotes,
        // backslashes and pipes, and arguing with two levels of shell quoting is how a test
        // ends up asserting against something it did not send.
        var input = Path.GetTempFileName();
        var result = Path.GetTempFileName();
        var script = Path.GetTempFileName() + ".py";

        try
        {
            File.WriteAllText(input, name, new System.Text.UTF8Encoding(false));
            File.WriteAllText(script, $"""
                import io, sys
                sys.path.insert(0, r"{Scripts}")
                import versio_text
                name = io.open(r"{input}", encoding="utf-8").read()
                out = versio_text.safe_file_stem(name)
                io.open(r"{result}", "w", encoding="utf-8").write(out)
                """);

            using var python = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            });

            var stderr = python!.StandardError.ReadToEnd();
            python.WaitForExit();

            Assert.True(python.ExitCode == 0, stderr);

            // Through a file, not stdout. Python on Windows writes stdout in the console
            // codepage, which turns "Kühlschrank Regelung" into "K?hlschrank Regelung" -
            // the same finding the README records for the C# side of this boundary, arriving
            // from the other direction.
            var produced = File.ReadAllText(result, new System.Text.UTF8Encoding(false));
            output.WriteLine($"{name} -> {produced}");
            return produced;
        }
        finally
        {
            File.Delete(input);
            File.Delete(result);
            File.Delete(script);
        }
    }

    /// <summary>Shared with <see cref="PythonFileNameParityTests"/>, which skips on the same
    /// condition for the same reason.</summary>
    internal static bool PythonIsAvailable()
    {
        try
        {
            using var python = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            python!.WaitForExit();
            return python.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

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
/// The file-name rule exists in two languages. This runs both over one corpus.
/// </summary>
/// <remarks>
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
    private static readonly string Scripts = Path.Combine(
        Core.Tests.TestPaths.Root, "src", "Versio.Scripts.Codesys");

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
        ExportForm.NativeWithChildren => "NATIVE_WITH_CHILDREN",
        ExportForm.TaskBoth => "TASK_BOTH",
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
            Core.Tests.TestPaths.Root, "fixtures", "projects", "RefrigeratorControl",
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

        Assert.Equal(
            [
                "key=tasks",
                "schema=1",
                "vendor=codesys",
                "count=2",
                "items=2",

                // Every value is TEXT, including coreBinding, which CODESYS wants as an int
                // on the way back. The reader never infers a type, because priority wants a
                // string and core binding wants an int and one guess would be wrong for one
                // of them — the caller converts, knowing which field it holds.
                //
                // interval is the reason the values are verbatim: "t#20ms" on one task and
                // "100" on the other, in one project. A normaliser would rewrite one of them
                // and put a diff in front of somebody that they did not make.
                "MainTask|1|t#20ms|-2|PLC_PRG,Simulation",
                "VISU_TASK|31|100|-1|VisuElems.Visu_Prg",
            ],
            lines);
    }

    /// <summary>
    /// Run a snippet against the real modules, with <c>payload</c> in and <c>out</c> out.
    /// </summary>
    /// <remarks>
    /// The snippet is inserted verbatim, so it must define <c>out</c> and may read
    /// <c>payload</c>. Everything else — the module path, the JSON plumbing, the encoding — is
    /// handled here so a new parity test is a few lines rather than a copy of this method.
    /// </remarks>
    private static IReadOnlyList<string> RunPython(string payloadJson, string body)
    {
        var input = Path.GetTempFileName();
        var result = Path.GetTempFileName();
        var script = Path.GetTempFileName() + ".py";
        var utf8 = new System.Text.UTF8Encoding(false);

        // The snippet is indented to sit inside the generated file's top level, so it must
        // arrive with no leading indentation of its own.
        var indented = string.Join("\n", body.Replace("\r\n", "\n").Split('\n'));

        try
        {
            File.WriteAllText(input, payloadJson, utf8);
            File.WriteAllText(script,
                "# -*- coding: utf-8 -*-\n" +
                "import io, json, sys\n" +
                $"sys.path.insert(0, r\"{Scripts}\")\n" +
                $"payload = json.load(io.open(r\"{input}\", encoding=\"utf-8\"))\n" +
                indented + "\n" +
                $"io.open(r\"{result}\", \"w\", encoding=\"utf-8\").write(json.dumps(out))\n",
                utf8);

            using var python = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            });

            var stderr = python!.StandardError.ReadToEnd();
            python.WaitForExit();
            Assert.True(python.ExitCode == 0, stderr);

            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(result, utf8))!;
        }
        finally
        {
            File.Delete(input);
            File.Delete(result);
            File.Delete(script);
        }
    }
}
