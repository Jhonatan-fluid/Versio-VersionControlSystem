using Versio.Core.Equivalence;
using Xunit;

namespace Versio.Core.Tests.Equivalence;

/// <summary>
/// Runs the normalizer against the actual export trees produced by
/// <c>tools\Test-RoundTrip.ps1</c> on a real CODESYS project.
/// </summary>
/// <remarks>
/// Skips silently when the trees are absent, so the unit suite still runs on a machine
/// without CODESYS. When they are present this is the test that matters: unit cases
/// written from remembered symptoms can drift from what the tool actually emits.
/// </remarks>
public class RealRoundTripEquivalenceTests
{
    private const string BaselineRoot = @"C:\VERSIO\phase0\roundtrip\baseline";
    private const string RoundTripRoot = @"C:\VERSIO\phase0\roundtrip\roundtrip";

    private static bool FixturesPresent =>
        Directory.Exists(BaselineRoot) && Directory.Exists(RoundTripRoot);

    public static TheoryData<string> NativeFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in new[]
        {
            @"GlobalTextList.xml",
            @"Device\Plc Logic\Application\Diagnosis.vis.xml",
            @"Device\Plc Logic\Application\Visualization.vis.xml",
            @"Device\Plc Logic\Application\Live_Visu.vis.xml",
        })
        {
            data.Add(f);
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(NativeFiles))]
    public void Native_files_that_differ_byte_wise_are_semantically_equivalent(string relative)
    {
        Skip.IfNot(FixturesPresent, "round-trip trees not present; run tools\\Test-RoundTrip.ps1");

        var a = Path.Combine(BaselineRoot, relative);
        var b = Path.Combine(RoundTripRoot, relative);
        Skip.IfNot(File.Exists(a) && File.Exists(b), $"missing fixture {relative}");

        var textA = File.ReadAllText(a);
        var textB = File.ReadAllText(b);

        var na = NativeXmlNormalizer.Normalize(textA);
        var nb = NativeXmlNormalizer.Normalize(textB);

        Assert.True(na.Text == nb.Text,
            $"{relative} is still not equivalent after normalisation. " +
            $"masked A: {na.GuidsMasked} guids / {na.CountersMasked} counters; " +
            $"B: {nb.GuidsMasked}/{nb.CountersMasked}. " +
            $"First difference: {FirstDifference(na.Text, nb.Text)}");
    }

    [SkippableFact]
    public void Task_configuration_import_mutates_Core2_and_ONLY_Core2()
    {
        Skip.IfNot(FixturesPresent, "round-trip trees not present");

        var rel = @"Device\Plc Logic\Application\Task Configuration.xml";
        var a = Path.Combine(BaselineRoot, rel);
        var b = Path.Combine(RoundTripRoot, rel);
        Skip.IfNot(File.Exists(a) && File.Exists(b), "missing Task Configuration.xml");

        // KNOWN DEVIATION, measured on CODESYS 3.5.22.20.
        //
        // import_native on a Task Configuration changes a task's CPU core affinity field
        // from Core2=-1 to Core2=-2, sitting directly beside an unchanged Core=-1. This is
        // NOT volatile serialisation - it is a real value CODESYS altered without being
        // asked, so it is deliberately NOT masked by the normalizer.
        //
        // The deviation is pinned rather than tolerated: this test fails the moment it
        // grows beyond that single field, which is what would tell us native import of
        // task configuration has become more damaging than we currently believe.
        var na = NativeXmlNormalizer.Normalize(File.ReadAllText(a)).Text.Split('\n');
        var nb = NativeXmlNormalizer.Normalize(File.ReadAllText(b)).Text.Split('\n');

        var differing = new List<(string A, string B)>();
        for (var i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            var x = i < na.Length ? na[i] : "<eof>";
            var y = i < nb.Length ? nb[i] : "<eof>";
            if (x != y)
            {
                differing.Add((x.Trim(), y.Trim()));
            }
        }

        Assert.True(differing.Count == 1,
            $"expected exactly one known deviation, found {differing.Count}: " +
            string.Join(" | ", differing.Take(5).Select(d => $"A='{d.A}' B='{d.B}'")));

        Assert.Contains("Core2", differing[0].A);
        Assert.Contains(">-1<", differing[0].A);
        Assert.Contains(">-2<", differing[0].B);
    }

    [SkippableFact]
    public void The_textual_file_is_byte_identical_and_needs_no_normalisation()
    {
        Skip.IfNot(FixturesPresent, "round-trip trees not present");

        var a = Path.Combine(BaselineRoot, @"Device\Plc Logic\Application\Simulation.st");
        var b = Path.Combine(RoundTripRoot, @"Device\Plc Logic\Application\Simulation.st");
        Skip.IfNot(File.Exists(a) && File.Exists(b), "missing Simulation.st");

        // The ST path round-trips exactly. Only native XML needs the normalizer.
        Assert.Equal(File.ReadAllText(a), File.ReadAllText(b));
    }

    private static string FirstDifference(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        for (var i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            var x = i < la.Length ? la[i] : "<eof>";
            var y = i < lb.Length ? lb[i] : "<eof>";
            if (x != y)
            {
                return $"line {i + 1}: A='{Truncate(x)}' B='{Truncate(y)}'";
            }
        }

        return "none";
    }

    private static string Truncate(string s) =>
        s.Length <= 120 ? s.Trim() : s[..120].Trim() + "…";
}
