using System.IO;
using System.Text.Json;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// The delete check still reads the <c>nhash</c> an older Versio recorded.
/// </summary>
/// <remarks>
/// <para>
/// <c>why_not_delete</c> re-exports a native object, normalises it and compares the hash
/// against the manifest's <c>nhash</c>. The normaliser changed twice in two days — the
/// sibling sort replaced the line sort (2026-10-24, item 20) and the GUID placeholder
/// changed shape (2026-10-25, item 7) — and each change made every hash recorded before it
/// unreproducible, so an unchanged object exported by an older Versio was refused as
/// <c>DELETE_CONTENT_CHANGED</c>: "edited since the export", when nothing had touched it
/// (the 2026-10-25 review's third round, item 4). Both committed fixture mirrors are such
/// older exports, every native record of them, which is what the first test runs the real
/// check over.
/// </para>
/// <para>
/// The importer is a script whose trailer runs <c>main()</c>, so it is loaded here as the
/// text above that trailer, with a stub <c>scriptengine</c> for the one class that inherits
/// from it. That is the real <c>why_not_delete</c> and the real <c>reexported_native_of</c>,
/// with only <c>export_native</c> faked to write the bytes a fixture holds.
/// </para>
/// </remarks>
public sealed class PythonNativeHashCompatibilityTests(ITestOutputHelper output)
{
    /// <summary>
    /// The fixture mirrors were exported before either change to the normaliser, so every
    /// native record's <c>nhash</c> is an older form's; each still passes the check, and an
    /// edited copy of each is still refused.
    /// </summary>
    /// <remarks>
    /// The "older" assertions hold while the fixtures stay as exported in 2026-08 to 10. A
    /// re-export with a current Versio retires them (every record would then match the
    /// current form first); the test below pins the three forms on a capture that never
    /// changes, so the compatibility itself stays tested when that day comes.
    /// </remarks>
    [SkippableFact]
    public void Every_native_record_of_the_committed_mirrors_still_passes_the_delete_check()
    {
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        string[] manifests =
        [
            Path.Combine(TestPaths.Project("RefrigeratorControl"), "_manifest.json"),
            Path.Combine(TestPaths.Project("MachineWithMembers"), "_manifest.json"),
        ];
        Skip.If(manifests.Any(m => !File.Exists(m)), "the committed fixtures are not present");

        var lines = RunDeleteCheck(
            JsonSerializer.Serialize(new { manifests = manifests.Select(m => m.Replace("\\", "/")) }),
            """
            for manifest in payload["manifests"]:
                root = os.path.dirname(manifest)
                records = json.load(io.open(manifest, encoding="utf-8")).get("objects") or []
                for rec in records:
                    if rec.get("form") not in versio_types.NATIVE_FORMS or not rec.get("nhash"):
                        continue
                    path = os.path.join(root, rec["path"].replace("/", os.sep))
                    xml = io.open(path, encoding="utf-8", newline="").read()
                    out.append(check(rec["path"], rec["form"], rec["nhash"], xml))
            """);

        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        // Eighteen on 2026-10-25; a count, so a fixture that lost its native records would
        // not pass this vacuously.
        Assert.True(lines.Count >= 10, $"only {lines.Count} native records with an nhash");

        // Every unchanged object is either vouched for by an older form that can tell an edit
        // from a reordering (the chunker, form 1) or waits for the next export: none is called
        // changed, and none is deleted on the line sort's word alone, which for a container
        // with multi-line children cannot tell the two apart (the fifth round's item 1). Every
        // edited copy is still refused — the older forms widen what a RECORDED hash can be,
        // not what a live object may have become.
        Assert.All(lines, l => Assert.True(
            l.Contains("|verdict=ok|older=True|forms=1,2|", StringComparison.Ordinal)
            || l.Contains("|verdict=DELETE_UNVERIFIABLE|older=False|forms=2|", StringComparison.Ordinal),
            l));
        Assert.All(lines, l => Assert.EndsWith("|edited=DELETE_CONTENT_CHANGED", l));

        // Both outcomes occur in the fixtures: the flat property files are vouched for, the
        // visualisations and text lists wait. If a re-export ever makes every record current,
        // these two lines retire and the pinned test below keeps the rule.
        Assert.Contains(lines, l => l.Contains("|verdict=ok|older=True|", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("|verdict=DELETE_UNVERIFIABLE|", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("|forms=0", StringComparison.Ordinal));
    }

    /// <summary>
    /// The three normalisations an <c>nhash</c> may have been recorded under, each pinned
    /// by the hash it gives one capture that never changes.
    /// </summary>
    /// <remarks>
    /// The capture is the baseline <c>GlobalTextList.xml</c> from the equivalence pair. The
    /// three values were taken on 2026-10-25 — the current form from <c>normalize</c>, the
    /// two older ones from an independent re-implementation of the line sort and the old
    /// placeholder written for the probe, and then from <c>normalized_forms</c>; they agreed.
    /// Each is accepted by the real check with the right "older" flag, a fourth is refused,
    /// and the three differ from one another, or this would be pinning one form three times.
    /// </remarks>
    [SkippableFact]
    public void Each_normalisation_an_nhash_was_ever_recorded_under_is_reproduced()
    {
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        var capture = TestPaths.Equivalence("baseline", "GlobalTextList.xml");
        Skip.IfNot(File.Exists(capture), "the equivalence captures are not committed");

        string[] recorded =
        [
            // Current: whole siblings sorted, {masked-guid}.
            "sha256:5174138f31475b27d7bdcdc52fe574cb03fc9a8441bfbbb7a0b7dae4638600c5",
            // 2026-10-24 to 2026-10-25: whole siblings sorted, <masked-guid>.
            "sha256:d92287cdc2b868cb46f4bd9b44d964694804a98f7f5c985ffffb0ff956a1e7ec",
            // Before 2026-10-24: lines sorted, <masked-guid>.
            "sha256:623a169838fd50c07135bc1933db9b13383f710600969e5ae682a5f313617931",
            // Nobody's: a changed object.
            "sha256:0000000000000000000000000000000000000000000000000000000000000000",
        ];
        Assert.Equal(4, recorded.Distinct().Count());

        var lines = RunDeleteCheck(
            JsonSerializer.Serialize(new { capture = capture.Replace("\\", "/"), recorded }),
            """
            xml = io.open(payload["capture"], encoding="utf-8", newline="").read()
            for k, nhash in enumerate(payload["recorded"]):
                out.append(check("recorded-%d" % k, "NATIVE", nhash, xml))
            """);

        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        // The line-sort hash is reproduced (forms=2) and does not vouch: a text list has
        // multi-line children, so that hash would also match a copy with two texts swapped
        // between their owners. Unverifiable until the next export, not changed.
        Assert.Equal(
            [
                "recorded-0|verdict=ok|older=False|forms=0|edited=DELETE_CONTENT_CHANGED",
                "recorded-1|verdict=ok|older=True|forms=1|edited=DELETE_CONTENT_CHANGED",
                "recorded-2|verdict=DELETE_UNVERIFIABLE|older=False|forms=2|edited=DELETE_CONTENT_CHANGED",
                "recorded-3|verdict=DELETE_CONTENT_CHANGED|older=False|forms=|edited=DELETE_CONTENT_CHANGED",
            ],
            lines);
    }

    /// <summary>
    /// The line sort's hash vouches for an object only where that sort could not have hidden an
    /// edit: every unordered container holding single-line siblings and nothing else.
    /// </summary>
    /// <remarks>
    /// The 2026-10-25 review's fifth round, item 1: the third round taught the delete check to
    /// accept the hash an older export recorded, and the oldest normalisation sorted a
    /// container's LINES. Swap two values between two multi-line children and the bag of lines
    /// is the same, so the old hash is reproduced by a modified object — and the reviewer
    /// watched that object deleted with <c>olderHash: true</c> on the log. For single-line
    /// siblings the bag of lines is the set of siblings, so a swap there is a reordering, which
    /// an unordered container is allowed, and an edit to a value is a different bag.
    /// </remarks>
    [SkippableFact]
    public void The_line_sort_vouches_only_where_it_could_not_have_hidden_an_edit()
    {
        Skip.IfNot(CPythonHarness.PythonIsAvailable(), "python is not on PATH");

        var lines = RunDeleteCheck(
            "null",
            """
            # What the exporter recorded: the hash over the SCRUBBED text, as versio_export
            # writes it and as reexported_native_of reads the live object back.
            def old(xml):
                return content_hash(ne.normalized_forms(scrub_native_xml(xml)[0])[ne.LEGACY_LINE_SORT_FORM])

            # Two multi-line children: the line sort cannot tell their values apart.
            lossy = u"<Root>\n<List>\n<A>\n<v>1</v>\n</A>\n<B>\n<v>2</v>\n</B>\n</List>\n</Root>"
            swapped = u"<Root>\n<List>\n<A>\n<v>2</v>\n</A>\n<B>\n<v>1</v>\n</B>\n</List>\n</Root>"
            out.append(check("lossy-unchanged", "NATIVE", old(lossy), lossy))
            out.append(check("lossy-swapped", "NATIVE", old(lossy), swapped))

            # Single-line siblings carrying GUIDs: the bag of lines is the set of siblings.
            flat = (u"<Root>\n<List>\n"
                    u"<S id=\"11111111-2222-3333-4444-555555555555\" v=\"1\"/>\n"
                    u"<S id=\"66666666-7777-8888-9999-000000000000\" v=\"2\"/>\n"
                    u"</List>\n</Root>")
            reordered = (u"<Root>\n<List>\n"
                         u"<S id=\"66666666-7777-8888-9999-000000000000\" v=\"2\"/>\n"
                         u"<S id=\"11111111-2222-3333-4444-555555555555\" v=\"1\"/>\n"
                         u"</List>\n</Root>")
            edited = flat.replace(u"v=\"2\"", u"v=\"3\"")
            out.append(check("flat-unchanged", "NATIVE", old(flat), flat))
            out.append(check("flat-reordered", "NATIVE", old(flat), reordered))
            out.append(check("flat-edited", "NATIVE", old(flat), edited))
            """);

        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        Assert.Equal(
            [
                "lossy-unchanged|verdict=DELETE_UNVERIFIABLE|older=False|forms=2|edited=DELETE_CONTENT_CHANGED",
                "lossy-swapped|verdict=DELETE_UNVERIFIABLE|older=False|forms=2|edited=DELETE_CONTENT_CHANGED",
                "flat-unchanged|verdict=ok|older=True|forms=1,2|edited=DELETE_CONTENT_CHANGED",
                // Only the line sort reproduces a reordering here: the old chunker could not
                // cut a body holding a GUID and kept file order. So this is the case where the
                // line sort alone vouches, and may, because every sibling is one line.
                "flat-reordered|verdict=ok|older=True|forms=2|edited=DELETE_CONTENT_CHANGED",
                "flat-edited|verdict=DELETE_CONTENT_CHANGED|older=False|forms=|edited=DELETE_CONTENT_CHANGED",
            ],
            lines);
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the real importer loaded and a <c>check</c> that
    /// puts one record through <c>why_not_delete</c> twice: as exported, and with one
    /// element added before the closing tag.
    /// </summary>
    private static IReadOnlyList<string> RunDeleteCheck(string payloadJson, string body) =>
        JsonSerializer.Deserialize<List<string>>(CPythonHarness.RunJson(
            payloadJson,
            $$"""
            import os, types
            import versio_native_equiv as ne
            import versio_types
            from versio_text import content_hash, scrub_native_xml

            # The importer is a script: everything above its trailer is the module.
            stub = types.ModuleType("scriptengine")
            stub.NativeImportHandler = object
            sys.modules["scriptengine"] = stub
            scripts = r"{{TestPaths.Scripts}}"
            src = io.open(os.path.join(scripts, "versio_import.py"), encoding="utf-8").read()
            src = src[:src.index(u"\ntry:\n    rc = main()")]
            versio_import = types.ModuleType("versio_import")
            versio_import.__file__ = os.path.join(scripts, "versio_import.py")
            exec(compile(src, "versio_import.py", "exec"), versio_import.__dict__)

            class Obj(object):
                def __init__(self, text):
                    self.text = text

                def export_native(self, path, recursive):
                    io.open(path, "w", encoding="utf-8", newline="").write(self.text)

            def check(label, form, nhash, xml):
                imp = versio_import.Importer.__new__(versio_import.Importer)
                files = [{"form": form, "path": label, "hash": "sha256:0", "nhash": nhash}]
                verdict = imp.why_not_delete(Obj(xml), label, files)
                older = imp.older_nhash
                # Over the scrubbed text, as the check itself reads the live object back.
                forms = [k for k, t in enumerate(ne.normalized_forms(scrub_native_xml(xml)[0])) if content_hash(t) == nhash]
                at = xml.rfind(u"</")
                edited = imp.why_not_delete(Obj(xml[:at] + u"<Edited/>" + xml[at:]), label, files)
                return u"%s|verdict=%s|older=%s|forms=%s|edited=%s" % (
                    label, verdict[0] if verdict else "ok", older,
                    ",".join(str(k) for k in forms), edited[0] if edited else "ok")

            out = []
            {{body}}
            """,
            jsonOut: true))!;
}
