using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The tag that lets this suite be run without CODESYS, and the rule that keeps it honest.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the tag exists.</b> Measured on 2026-08-16: <b>177 of the 228 tests here finish in
/// 16 seconds. The other 51 take half an hour.</b> The difference is entirely CODESYS — a cold
/// start is 17–31 s, every launch pays it in full, an import test needs three to five of them
/// (export, import, then a re-export to read the answer back out of the project), and
/// <see cref="CodesysCollection"/> serialises them because two headless instances fight over the
/// profile.
/// </para>
/// <para>
/// So <c>--filter "Toolchain!=Codesys"</c> is the loop to work in and the full run is the gate
/// before a commit. <c>CLAUDE.md</c> carries both commands.
/// </para>
/// <para>
/// <b>Why this test exists rather than a comment saying "remember the tag".</b> A filter that
/// silently stops excluding things is worse than no filter: the fast lane would quietly grow a
/// CODESYS launch and somebody would conclude the fast lane is slow, or — worse — a class would
/// be excluded from the fast lane while not needing to be, and 30 minutes of coverage would sit
/// behind a flag nobody runs. Both directions are asserted, from assembly metadata, which is the
/// same technique <c>VendorSeamTests</c> uses for the same reason: a rule nothing enforces is a
/// rule that has already drifted.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FastLaneTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_class_that_launches_codesys_is_tagged_and_nothing_else_is()
    {
        // CustomAttributeData, not GetCustomAttributes<T>(): xunit's CollectionAttribute and
        // TraitAttribute keep their values in constructor arguments and expose no properties
        // for them, so the instances are useless and the metadata is what carries the answer.
        static IEnumerable<IList<CustomAttributeTypedArgument>> ArgsOf(
            Type type, string attribute) =>
            CustomAttributeData.GetCustomAttributes(type)
                .Where(a => a.AttributeType.Name == attribute)
                .Select(a => a.ConstructorArguments);

        // MATCHED AS A PAIR, not by flattening every argument into one list. The flattened
        // version read `args.Contains("Toolchain")`, which is satisfied just as well by a
        // [Trait("Something", "Toolchain")] somewhere — a trait whose VALUE happens to be the
        // key this is looking for. Nothing in this assembly has one, which is exactly why the
        // sloppy version would have gone on passing.
        static bool HasTrait(Type type, string name, string value) =>
            ArgsOf(type, nameof(TraitAttribute)).Any(
                a => a.Count == 2
                     && (a[0].Value as string) == name
                     && (a[1].Value as string) == value);

        static bool InCollection(Type type, string name) =>
            ArgsOf(type, nameof(CollectionAttribute)).Any(
                a => a.Count == 1 && (a[0].Value as string) == name);

        var classes = typeof(FastLaneTests).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Select(t => new
            {
                Type = t,
                Serialised = InCollection(t, CodesysCollection.Name),
                Tagged = HasTrait(t, "Toolchain", "Codesys"),
            })
            .Where(x => x.Serialised || x.Tagged)
            .OrderBy(x => x.Type.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var c in classes)
        {
            output.WriteLine($"{(c.Serialised ? "collection" : "-"),-11} "
                             + $"{(c.Tagged ? "tagged" : "-"),-7} {c.Type.Name}");
        }

        var untagged = classes.Where(c => c.Serialised && !c.Tagged).Select(c => c.Type.Name);
        Assert.True(!untagged.Any(),
            "these classes are in the CODESYS collection and carry no [Trait(\"Toolchain\", "
            + "\"Codesys\")], so `--filter \"Toolchain!=Codesys\"` would try to launch CODESYS: "
            + string.Join(", ", untagged));

        var stray = classes.Where(c => c.Tagged && !c.Serialised).Select(c => c.Type.Name);
        Assert.True(!stray.Any(),
            "these classes are tagged as needing CODESYS but are not in the collection that "
            + "serialises it — either the tag is wrong and 30 minutes of coverage is hidden "
            + "from the fast lane, or the collection is missing and two CODESYS instances will "
            + "race: " + string.Join(", ", stray));

        // NON-VACUOUS. Both assertions above pass on an assembly with no CODESYS tests at all,
        // and this suite is one refactor away from looking like that to a reflection query
        // that has quietly stopped matching.
        Assert.True(classes.Count >= 8, $"only {classes.Count} CODESYS classes found");
    }
}
