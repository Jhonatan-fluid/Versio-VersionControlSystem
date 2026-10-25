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

        // Every unchanged object passes; every edited copy is still refused — the older forms
        // widen what a RECORDED hash can be, not what a live object may have become.
        Assert.All(lines, l => Assert.Contains("|verdict=ok|", l));
        Assert.All(lines, l => Assert.EndsWith("|edited=DELETE_CONTENT_CHANGED", l));

        // And each passed only through an older form, which is said on the removed event.
        Assert.All(lines, l => Assert.Contains("|older=True|", l));
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

        Assert.Equal(
            [
                "recorded-0|verdict=ok|older=False|forms=0|edited=DELETE_CONTENT_CHANGED",
                "recorded-1|verdict=ok|older=True|forms=1|edited=DELETE_CONTENT_CHANGED",
                "recorded-2|verdict=ok|older=True|forms=2|edited=DELETE_CONTENT_CHANGED",
                "recorded-3|verdict=DELETE_CONTENT_CHANGED|older=False|forms=|edited=DELETE_CONTENT_CHANGED",
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
            from versio_text import content_hash

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
                forms = [k for k, t in enumerate(ne.normalized_forms(xml)) if content_hash(t) == nhash]
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
