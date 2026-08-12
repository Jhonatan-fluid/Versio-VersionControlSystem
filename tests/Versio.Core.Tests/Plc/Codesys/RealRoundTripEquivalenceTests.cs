using Versio.Core.Plc.Codesys;
using Xunit;

namespace Versio.Core.Tests.Plc.Codesys;

/// <summary>
/// Runs the normalizer against genuine CODESYS output captured from an
/// export → import → re-export cycle on 3.5.22.20.
/// </summary>
/// <remarks>
/// <para>
/// These fixtures are <b>committed</b> rather than regenerated, so the assertions run on
/// every machine and in CI without CODESYS installed. Unit cases written from remembered
/// symptoms drift; captured bytes do not.
/// </para>
/// <para>
/// Only the small files are committed. The three visualisation exports exhibit the same
/// three volatility classes but total 2.9 MB, which is not worth carrying in git — they are
/// covered synthetically in <see cref="NativeXmlNormalizerTests"/> and can be regenerated
/// with <c>tools/Test-RoundTrip.ps1</c>.
/// </para>
/// </remarks>
public class RealRoundTripEquivalenceTests
{
    private static string Baseline(string name) => TestPaths.Equivalence("baseline", name);

    private static string RoundTrip(string name) => TestPaths.Equivalence("roundtrip", name);

    [Theory]
    [InlineData("GlobalTextList.xml")]
    public void Native_files_that_differ_byte_wise_are_semantically_equivalent(string name)
    {
        var a = File.ReadAllText(Baseline(name));
        var b = File.ReadAllText(RoundTrip(name));

        // Byte-different: this is what makes the test meaningful.
        Assert.NotEqual(a, b);

        var na = NativeXmlNormalizer.Normalize(a);
        var nb = NativeXmlNormalizer.Normalize(b);

        Assert.True(na.Text == nb.Text,
            $"{name} is still not equivalent after normalisation. " +
            $"masked A: {na.GuidsMasked} guids / {na.CountersMasked} counters; " +
            $"B: {nb.GuidsMasked}/{nb.CountersMasked}. " +
            $"First difference: {FirstDifference(na.Text, nb.Text)}");
    }

    [Fact]
    public void The_textual_file_round_trips_byte_identically()
    {
        // ST needs no normalisation at all. Only native XML does.
        Assert.Equal(
            File.ReadAllText(Baseline("Simulation.st")),
            File.ReadAllText(RoundTrip("Simulation.st")));
    }

    [Fact]
    public void Task_configuration_import_mutates_Core2_and_ONLY_Core2()
    {
        // KNOWN DEVIATION, measured on CODESYS 3.5.22.20 and reproduced three times.
        //
        // import_native on a Task Configuration changes a task's CPU core affinity from
        // Core2=-1 to Core2=-2, sitting directly beside an unchanged Core=-1. That is NOT
        // volatile serialisation - it is a real value CODESYS altered without being asked -
        // so the normalizer deliberately does not mask it.
        //
        // The deviation is PINNED rather than tolerated: this fails the moment it grows
        // beyond that single field, which is what would tell us native import of task
        // configuration has become more damaging than we currently believe. Until it is
        // understood, task configuration stays diff-only (plan section 3.10, tier 3).
        var na = NativeXmlNormalizer.Normalize(
            File.ReadAllText(Baseline("Task Configuration.xml"))).Text.Split('\n');
        var nb = NativeXmlNormalizer.Normalize(
            File.ReadAllText(RoundTrip("Task Configuration.xml"))).Text.Split('\n');

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

    [Fact]
    public void EventPOUGuid_is_regenerated_on_import_and_masking_absorbs_it()
    {
        // The captured pair contains two EventPOUGuid values re-minted by CODESYS. Without
        // GUID masking the task configuration would show as changed on every import.
        var a = File.ReadAllText(Baseline("Task Configuration.xml"));
        var b = File.ReadAllText(RoundTrip("Task Configuration.xml"));

        Assert.Contains("EventPOUGuid", a);
        Assert.True(NativeXmlNormalizer.Normalize(a).GuidsMasked > 0);

        var rawDiffs = a.Split('\n').Zip(b.Split('\n')).Count(p => p.First != p.Second);
        Assert.True(rawDiffs > 1, "fixture should contain more than the Core2 difference");
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
