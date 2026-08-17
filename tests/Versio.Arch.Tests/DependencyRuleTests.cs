using System.Reflection;
using Versio.Core.Plc;
using Versio.TestSupport;
using Xunit;

namespace Versio.Arch.Tests;

/// <summary>
/// The five dependency rules <c>docs/PLAN.md</c> §12.2 has always said were enforced.
/// </summary>
/// <remarks>
/// <para>
/// <b>They were not.</b> §12.2 is headed *"Dependency rules — enforced, not documented"* and says
/// this project "fails the build if any of these are violated". The project had never existed:
/// its only trace anywhere in the repository was a comment in <c>Versio.Core.csproj</c> citing it
/// by name. Four of the five rules held anyway, by discipline; one was quietly violated.
/// </para>
/// <para>
/// That is the same shape as the rolling file sink recorded in <c>FINDINGS.md</c> — written,
/// reviewed, tested, and never instantiated, so nothing reached disk while the log panel made it
/// look as though logging worked. A rule nothing checks is a rule that has already drifted; a
/// rule nothing checks that a document *claims* is checked is worse, because it also spends the
/// reader's trust.
/// </para>
/// </remarks>
public sealed class DependencyRuleTests
{
    private static readonly Assembly Core = typeof(PlcJob).Assembly;
    private static readonly Assembly Infrastructure =
        typeof(Versio.Infrastructure.Platform.SystemClock).Assembly;

    private static readonly Assembly App = typeof(Versio.App.ViewModels.ShellViewModel).Assembly;

    [Fact]
    public void Rule1_Core_references_neither_the_UI_nor_the_infrastructure()
    {
        // Worth keeping even though the app is Windows-only: it is what keeps a future
        // UI-framework change, a CLI, or a test harness a view-layer job rather than a rewrite.
        var referenced = Core.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain("Versio.Infrastructure", referenced);
        Assert.DoesNotContain("PresentationFramework", referenced);
        Assert.DoesNotContain("PresentationCore", referenced);
        Assert.DoesNotContain("WindowsBase", referenced);
    }

    [Fact]
    public void Rule1b_Core_references_no_vendor_pack()
    {
        // Not in PLAN's original five, because when they were written the vendor lived INSIDE
        // Versio.Core and the rule could not have been stated. It is the same rule.
        var core = Core.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain("Versio.Plc.Codesys", core);
        Assert.DoesNotContain("Versio.Plc.Codesys.Infrastructure", core);

        // Versio.Infrastructure too, which the first version of this test forgot. It holds the
        // git client, the watchers, the filesystem and PlcToolchains — and PlcToolchains is
        // exactly where a vendor factory used to be hardcoded, so it is the assembly most
        // likely to grow the reference back.
        var infrastructure = Infrastructure.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain("Versio.Plc.Codesys", infrastructure);
        Assert.DoesNotContain("Versio.Plc.Codesys.Infrastructure", infrastructure);
    }

    [Fact]
    public void Rule3_the_views_do_not_reach_into_the_infrastructure()
    {
        // A view that constructs a WindowsFileSystem or a GitCliClient has bypassed the view
        // model, which is how a dialog ends up doing IO on the UI thread and how the panel
        // becomes untestable without a real repository.
        //
        // Signature-level, matching the seam scanner: it is what a reader of the type sees and
        // what the compiler would have to satisfy.
        var offenders = VendorSeamScanner
            .VendorTypeReferences(App, "Versio.App.Views", "Versio.Infrastructure")
            .Concat(VendorSeamScanner.VendorTypeReferences(
                App, "Versio.App.Views.Panels", "Versio.Infrastructure"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "a view reaches into Versio.Infrastructure:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Rule5_every_public_async_method_in_Core_can_be_cancelled()
    {
        // An await with no way out is a hang with no way out. Everything in Core is driven by
        // something the user can close, and an export that cannot be abandoned holds the
        // direction lock until the process dies.
        var offenders = new List<string>();

        foreach (var type in Core.GetTypes().Where(t => t.IsPublic))
        {
            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance |
                         BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!Returns(method) || method.IsSpecialName)
                {
                    continue;
                }

                if (!method.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)))
                {
                    offenders.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "a public async method in Versio.Core takes no CancellationToken:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static bool Returns(MethodInfo method) =>
        method.ReturnType == typeof(Task) ||
        (method.ReturnType.IsGenericType &&
         method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)) ||
        method.ReturnType == typeof(ValueTask) ||
        (method.ReturnType.IsGenericType &&
         method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>));

    [Fact]
    public void The_assemblies_under_test_really_were_loaded()
    {
        // Every rule above is an assertion that a set is EMPTY, which an assembly that failed to
        // load satisfies perfectly. This is the positive control - the same lesson the seam
        // scanner needed one phase earlier.
        Assert.NotEmpty(Core.GetTypes());
        Assert.NotEmpty(Infrastructure.GetTypes());
        Assert.Contains(App.GetTypes(), t => t.Namespace == "Versio.App.Views");
    }
}
