using System.Text;
using Versio.Core.Bindings;
using Versio.Core.Plc;
using Versio.Infrastructure.Bindings;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// What a committed <c>repo.json</c> holds for a vendor, in the fast lane.
/// </summary>
/// <remarks>
/// <para>
/// <b>This guarantee already had a test, and it was behind a 42-minute gate.</b>
/// <c>AdoptAndExportTests</c> asserts <c>"vendor": "Codesys"</c> in the written bytes, which is
/// exactly right — but it is tagged for CODESYS, so it drives a real toolchain and only runs in
/// the full suite. The property it protects has nothing to do with CODESYS: it is that a shipped,
/// committed, hand-editable file keeps meaning what it meant.
/// </para>
/// <para>
/// That mattered the moment <c>PlcVendor</c> stopped being an enum. A struct serialises as an
/// object with a <c>Name</c> field unless something says otherwise, which would have rewritten
/// every binding in every clone into a shape older versions cannot read — and the only thing
/// standing in the way was a test nobody runs while working.
/// </para>
/// <para>
/// No CODESYS, no python, no git. Milliseconds.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RepoConfigVendorTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "versio-vendor-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, ".versio"));
        return root;
    }

    [Fact]
    public async Task A_binding_is_written_with_its_vendor_as_a_plain_string()
    {
        var root = NewTempRoot();
        try
        {
            var store = new RepoConfigStore(root);
            await store.SaveAsync(
                new RepoConfig
                {
                    Bindings =
                    [
                        new ProjectBinding
                        {
                            Id = "b-1",
                            ProjectPath = "plc/Machine.project",
                            ExportPath = "plc/Machine",
                            Vendor = PlcVendor.Codesys,
                        },
                    ],
                },
                CancellationToken.None);

            var text = await File.ReadAllTextAsync(store.Path, Encoding.UTF8);

            // The bytes, not the round trip. A converter that reads back whatever it wrote is
            // self-consistent and still wrong if what it wrote is a shape nothing else reads.
            Assert.Contains("\"vendor\": \"Codesys\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Name\"", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_binding_written_before_vendors_existed_still_reads_as_CODESYS()
    {
        // The backward-compatibility shape ProjectBinding.Vendor's own remarks promise: "a file
        // written by an older Versio must keep meaning what it meant". Every binding written
        // before the field existed was CODESYS, because CODESYS was all there was.
        //
        // Worth pinning separately from the default, because the two are not the same mechanism:
        // default(PlcVendor) is deliberately UNKNOWN now rather than Codesys, and it is the
        // property initialiser - not the struct's default - that carries this promise.
        var root = NewTempRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, ".versio", "repo.json"),
                """
                {
                  "schema": 1,
                  "bindings": [
                    { "id": "b-1", "projectPath": "plc/Machine.project", "exportPath": "plc/Machine" }
                  ]
                }
                """);

            var config = await new RepoConfigStore(root).LoadAsync(CancellationToken.None);

            Assert.Equal(PlcVendor.Codesys, Assert.Single(config.Bindings).Vendor);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_vendor_nobody_shipped_survives_a_round_trip()
    {
        // The point of opening the identity: a repository set up by a build that knows a vendor
        // this one does not must not lose that binding or silently repoint it at CODESYS.
        var root = NewTempRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, ".versio", "repo.json"),
                """
                {
                  "schema": 1,
                  "bindings": [
                    { "id": "b-9", "projectPath": "plc/M.gxw", "exportPath": "plc/M",
                      "vendor": "Mitsubishi GX Works3" }
                  ]
                }
                """);

            var store = new RepoConfigStore(root);
            var config = await store.LoadAsync(CancellationToken.None);
            var binding = Assert.Single(config.Bindings);

            Assert.Equal(new PlcVendor("Mitsubishi GX Works3"), binding.Vendor);
            Assert.NotEqual(PlcVendor.Codesys, binding.Vendor);

            await store.SaveAsync(config, CancellationToken.None);
            var text = await File.ReadAllTextAsync(store.Path, Encoding.UTF8);

            Assert.Contains("\"vendor\": \"Mitsubishi GX Works3\"", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
