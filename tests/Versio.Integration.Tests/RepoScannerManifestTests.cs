using Versio.Core.Logging;
using Versio.Infrastructure.Bindings;
using Versio.Plc.Codesys;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The scanner reading a binding id back out of an export tree's <c>_manifest.json</c>, and
/// what one manifest of the wrong shape may cost the rest of the repository: nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>One bad manifest used to abort the whole scan.</b> <c>TryReadBindingId</c> caught the
/// two exceptions a damaged file raises — <c>IOException</c> for a file mid-write and
/// <c>JsonException</c> for one that is not JSON — and nothing else. A file that IS valid JSON
/// but not a JSON object (<c>null</c>, <c>[]</c>), or whose <c>bindingId</c> is a number, is
/// neither: <c>TryGetProperty</c> on a non-object root and <c>GetString()</c> on a number
/// throw <c>InvalidOperationException</c>, which went straight up through <c>ScanAsync</c>.
/// Every project in the repository then disappeared from the picture, including the ones
/// whose manifests were fine, and the user saw a scan that never finished rather than the
/// one file that was wrong. Reviewed 2026-10-25 as a P2.
/// </para>
/// <para>
/// Real disk, the real CODESYS format, no git and no CODESYS.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RepoScannerManifestTests(ITestOutputHelper output)
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "versio-manifest-" + Path.GetRandomFileName());
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
    public async Task A_manifest_of_the_wrong_shape_is_unidentified_and_named_and_the_scan_completes()
    {
        var root = NewRoot();
        try
        {
            // The one tracked project, whose manifest is exactly what the exporter writes.
            Write(root, "plc/Good.project");
            Write(root, "plc/Good.export/_manifest.json", """{"bindingId": "good-1"}""");

            // Three manifests that parse as JSON and are still the wrong shape. Each is a
            // different throw site: the first two fail on reading a property from a root
            // that has none, the third on reading a string out of a number.
            Write(root, "plc/Null.export/_manifest.json", "null");
            Write(root, "plc/Array.export/_manifest.json", "[]");
            Write(root, "plc/Number.export/_manifest.json", """{"bindingId": 7}""");

            var logger = new TestLogger(output);
            var snapshot = await new RepoScanner(root, [CodesysProjectFormat.Instance], logger)
                .ScanAsync(git: null, CancellationToken.None);

            // The valid project and its id survive untouched.
            Assert.Equal(["plc/Good.project"], snapshot.ProjectFiles);
            Assert.Equal("good-1", Assert.Single(snapshot.ManifestBindingIds).Value);

            // Each bad tree is still an export tree — a directory holding a manifest is one,
            // whatever the manifest says — but none of them has an id, so OrphanScanner sees
            // them as unidentified rather than as a mismatch against something invented.
            Assert.Equal(
                ["plc/Array.export", "plc/Good.export", "plc/Null.export", "plc/Number.export"],
                snapshot.ExportDirectories.Order(StringComparer.Ordinal));

            // One warning per bad file, naming the file, so "why does it want to adopt
            // something it already tracks" has a trace behind it.
            var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
            Assert.Equal(3, warnings.Count);
            Assert.Contains(warnings, w => w.Message.Contains(Path.Combine("Null.export", "_manifest.json"), StringComparison.Ordinal));
            Assert.Contains(warnings, w => w.Message.Contains(Path.Combine("Array.export", "_manifest.json"), StringComparison.Ordinal));
            Assert.Contains(warnings, w => w.Message.Contains(Path.Combine("Number.export", "_manifest.json"), StringComparison.Ordinal));
            Assert.DoesNotContain(warnings, w => w.Message.Contains("Good.export", StringComparison.Ordinal));

            // The warnings say what was wrong, not just that something was.
            Assert.Contains(warnings, w => w.Message.Contains("Null", StringComparison.Ordinal) && w.Message.Contains("not a JSON object", StringComparison.Ordinal));
            Assert.Contains(warnings, w => w.Message.Contains("Array", StringComparison.Ordinal) && w.Message.Contains("not a JSON object", StringComparison.Ordinal));
            Assert.Contains(warnings, w => w.Message.Contains("Number", StringComparison.Ordinal) && w.Message.Contains("not a string", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_manifest_without_a_binding_id_is_unidentified_quietly()
    {
        // Pre-binding manifests have no bindingId at all, and an explicit null is the
        // exporter's way of saying the same thing. Neither is damage, so neither is a warning:
        // the scan's warnings have to stay worth reading.
        var root = NewRoot();
        try
        {
            Write(root, "plc/Old.export/_manifest.json", """{"schema": 1}""");
            Write(root, "plc/Unset.export/_manifest.json", """{"bindingId": null}""");

            var logger = new TestLogger(output);
            var snapshot = await new RepoScanner(root, [CodesysProjectFormat.Instance], logger)
                .ScanAsync(git: null, CancellationToken.None);

            Assert.Equal(
                ["plc/Old.export", "plc/Unset.export"],
                snapshot.ExportDirectories.Order(StringComparer.Ordinal));
            Assert.Empty(snapshot.ManifestBindingIds);
            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
