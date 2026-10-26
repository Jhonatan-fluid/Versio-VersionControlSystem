using Versio.Core.Bindings;
using Versio.Core.Plc;
using Versio.Infrastructure.Bindings;
using Versio.Plc.Codesys;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// Adoption through the infrastructure gate, on a real directory.
/// </summary>
/// <remarks>
/// The rule lives in <c>Adoption.Plan</c> and is tested there without a disk. What is checked
/// here is the half the pure test cannot: that the gate finds the untracked neighbours itself
/// when its caller hands it no scan, so no caller can leave the question unasked.
/// </remarks>
public sealed class RepoSetupAdoptionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task An_outer_project_is_refused_while_an_untracked_project_sits_under_its_export_root()
    {
        // Review 2026-10-25, sixth round, item 1: on a fresh scan A.project was adopted first,
        // A/B.project was then refused for sitting inside A's export root — and A's export
        // deleted it all the same. The outer one is the one refused now, whichever comes first.
        PlcFormats.Register(CodesysProjectFormat.Instance);
        var root = Path.Combine(Path.GetTempPath(), "versio-adopt-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "plc", "A"));
        File.WriteAllBytes(Path.Combine(root, "plc", "A.project"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "plc", "A", "B.project"), [4, 5, 6]);

        try
        {
            var setup = new RepoSetup(root, format: null, new TestLogger(output));

            // No scan handed over: the gate scans for itself.
            var outer = await setup.AdoptAsync("plc/A.project", git: null, profile: null, CancellationToken.None);
            Assert.False(outer.Adopted, outer.Decision.Explanation);
            Assert.Equal(AdoptionVerdict.ExportPathTaken, outer.Decision.Verdict);
            Assert.Contains("plc/A/B.project", outer.Decision.Explanation);
            Assert.False(File.Exists(Path.Combine(root, ".versio", "repo.json")), "a binding was written");

            // The inner project adopts on its own.
            var inner = await setup.AdoptAsync("plc/A/B.project", git: null, profile: null, CancellationToken.None);
            Assert.True(inner.Adopted, inner.Decision.Explanation);

            // And once B is tracked, A is refused by the binding check as well.
            var again = await setup.AdoptAsync("plc/A.project", git: null, profile: null, CancellationToken.None);
            Assert.False(again.Adopted, again.Decision.Explanation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
