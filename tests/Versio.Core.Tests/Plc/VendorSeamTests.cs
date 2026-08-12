using System.Reflection;
using Versio.Core.Plc;
using Xunit;

namespace Versio.Core.Tests.Plc;

/// <summary>
/// The vendor seam, enforced rather than documented.
/// </summary>
/// <remarks>
/// <para>
/// Versio started CODESYS-only and will grow to Rockwell, Siemens and TwinCAT. The README has
/// always said that adding a vendor must not require changing <c>Sync/</c>, <c>Git/</c>,
/// <c>Locking/</c>, <c>Watching/</c> or <c>Diff/</c> — and it was not true: the sync
/// orchestrator was built directly on <c>ICodesysRunner</c>, <c>CodesysJob</c> and
/// <c>CodesysResult</c>, so a second vendor would have meant rewriting the engine.
/// </para>
/// <para>
/// A rule nothing checks is a rule that decays. These tests read the assembly's own metadata,
/// so the next leak fails a build rather than surviving until somebody happens to grep.
/// </para>
/// </remarks>
public sealed class VendorSeamTests
{
    private static readonly Assembly Core = typeof(PlcJob).Assembly;

    /// <summary>Namespaces that must know nothing about any particular brand.</summary>
    private static readonly string[] NeutralNamespaces =
    [
        "Versio.Core.Sync",
        "Versio.Core.Git",
        "Versio.Core.Locking",
        "Versio.Core.Watching",
        "Versio.Core.Diff",
        "Versio.Core.Bindings",
        "Versio.Core.Projects",
        "Versio.Core.Abstractions",
        "Versio.Core.Hashing",
        "Versio.Core.Logging",
    ];

    /// <summary>Every brand name that must not appear outside its own corner.</summary>
    /// <remarks>
    /// The three unimplemented vendors are listed deliberately. They cost nothing today and
    /// they are the whole point: the seam is being kept honest for their sake, and whoever
    /// adds TwinCAT should find this failing before they get far enough to regret it.
    /// </remarks>
    private static readonly string[] VendorNames =
    [
        "Codesys", "TwinCat", "TwinCAT", "TiaPortal", "Siemens", "Rockwell", "Studio5000",
    ];

    public static TheoryData<string> Neutral => [.. NeutralNamespaces];

    [Theory]
    [MemberData(nameof(Neutral))]
    public void A_neutral_namespace_names_no_vendor_in_its_own_types(string ns)
    {
        var offenders = Core.GetTypes()
            .Where(t => t.Namespace == ns)
            .Where(t => VendorNames.Any(v => t.Name.Contains(v, StringComparison.Ordinal)))
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{ns} declares vendor-named types: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(Neutral))]
    public void A_neutral_namespace_does_not_depend_on_a_vendor_type(string ns)
    {
        // Signatures only - fields, properties, parameters and returns. That is what a second
        // vendor would actually have to satisfy, and it is what the compiler enforces.
        var offenders = new List<string>();

        foreach (var type in Core.GetTypes().Where(t => t.Namespace == ns))
        {
            foreach (var referenced in Referenced(type))
            {
                if (referenced.Namespace?.StartsWith("Versio.Core.Plc.", StringComparison.Ordinal)
                    == true)
                {
                    offenders.Add($"{type.Name} -> {referenced.FullName}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "vendor knowledge has leaked past the seam:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders.Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_neutral_toolchain_contract_is_what_a_new_vendor_implements()
    {
        // Named explicitly so that deleting or renaming one of these is a deliberate act
        // rather than something that happens during a refactor.
        Assert.NotNull(Core.GetType("Versio.Core.Plc.IPlcRunner"));
        Assert.NotNull(Core.GetType("Versio.Core.Plc.IPlcProjectFormat"));
        Assert.NotNull(Core.GetType("Versio.Core.Plc.IPlcToolchain"));
        Assert.NotNull(Core.GetType("Versio.Core.Plc.PlcJob"));
        Assert.NotNull(Core.GetType("Versio.Core.Plc.PlcResult"));
    }

    [Fact]
    public void Every_declared_vendor_is_either_implemented_or_honestly_absent()
    {
        // PlcVendor lists brands that do not exist yet on purpose - the abstraction is judged
        // against real second vendors rather than a hypothetical one. This asserts the list is
        // still there, so nobody "tidies" it down to the one that works.
        var vendors = Enum.GetNames<PlcVendor>();

        Assert.Contains("Codesys", vendors);
        Assert.Contains("TiaPortal", vendors);
        Assert.Contains("Studio5000", vendors);
        Assert.Contains("TwinCat", vendors);
    }

    private static IEnumerable<Type> Referenced(Type type)
    {
        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(All))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(All))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(All))
        {
            yield return method.ReturnType;

            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var ctor in type.GetConstructors(All))
        {
            foreach (var parameter in ctor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }
}
