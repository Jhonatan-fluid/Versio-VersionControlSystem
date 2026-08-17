using System.Reflection;
using Versio.Core.Plc;
using Xunit;

namespace Versio.Arch.Tests;

/// <summary>
/// Rule 2: <c>Versio.Core</c> reaches nothing outside itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/PLAN.md</c> §12.2 forbids <c>File</c>, <c>Directory</c>, <c>Process</c>,
/// <c>DateTime.Now</c> and <c>Guid.NewGuid</c> in <c>Versio.Core</c>, because "all are behind
/// abstractions, which is what makes time, filesystem and identity controllable in tests". It is
/// the rule that makes the lock model, the loop defences and the state machine testable in
/// milliseconds without CODESYS, without git and without sleeping.
/// </para>
/// <para>
/// <b>Four of the five were true by discipline alone and one was quietly violated</b>, which is
/// what an unenforced rule looks like from the inside. See <see cref="Guid.NewGuid"/> below.
/// </para>
/// <para>
/// <b>Read from IL, and the walk is deliberately naive.</b> It scans for the <c>call</c> and
/// <c>callvirt</c> opcodes a byte at a time rather than decoding every instruction, so it can
/// land inside another instruction's operand — the same trade the vendor seam scanner makes, and
/// safe in the same one direction. A token that resolves to a real method means that method
/// really is referenced by this assembly; the cost of a stray hit is a misattributed method name
/// in the failure message. It cannot produce a false negative.
/// </para>
/// </remarks>
public sealed class PurityRuleTests
{
    private static readonly Assembly Core = typeof(PlcJob).Assembly;

    /// <summary>
    /// What <c>Versio.Core</c> may not call, and the abstraction that replaces each.
    /// </summary>
    private static readonly Dictionary<string, string> Forbidden = new(StringComparer.Ordinal)
    {
        ["System.IO.File"] = "IFileSystem",
        ["System.IO.Directory"] = "IFileSystem",
        ["System.Diagnostics.Process"] = "IPlcRunner / IProcessProbe",
    };

    /// <summary>
    /// <c>Guid.NewGuid</c>, exempted deliberately and narrowly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is used for one thing: the default value of an operation id, which correlates the log
    /// lines of a single run. Every one of those is an <c>init</c> property a caller can set, so
    /// a test that needs a deterministic id simply passes one — the identity is controllable
    /// exactly as the rule intends, without the ceremony of an <c>IIdentityProvider</c> nobody
    /// would inject anywhere else.
    /// </para>
    /// <para>
    /// <b>Listed rather than ignored.</b> The alternative was to leave the rule saying one thing
    /// and the code doing another, which is how §12.2 came to claim enforcement that did not
    /// exist. If this list grows, the exemption should be re-argued rather than extended.
    /// </para>
    /// </remarks>
    private static readonly string[] ExemptCallers =
    [
        "Versio.Core.Logging.LoggerExtensions.NewOperationId",
        "Versio.Core.Plc.PlcJob..ctor",
    ];

    [Fact]
    public void Rule2_the_core_does_not_touch_the_filesystem_or_start_a_process()
    {
        var offenders = Calls(Core)
            .Where(c => Forbidden.ContainsKey(c.Called))
            .Select(c => $"{c.From} calls {c.Called} (use {Forbidden[c.Called]})")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Versio.Core reached outside itself:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Rule2_the_core_never_asks_the_machine_what_time_it_is()
    {
        // IClock exists so a lock can be aged, a heartbeat fired and a day boundary crossed
        // without waiting for any of them. One DateTimeOffset.Now would make a recovery test
        // depend on the clock of whoever runs it.
        var offenders = Calls(Core)
            .Where(c => c.Called is "System.DateTime" or "System.DateTimeOffset")
            .Where(c => c.Member is "get_Now" or "get_UtcNow" or "get_Today")
            .Select(c => $"{c.From} calls {c.Called}.{c.Member} (use IClock)")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Versio.Core read the wall clock:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Rule2_Guid_NewGuid_is_used_only_where_the_exemption_says()
    {
        // The rule's one exception, pinned so it cannot spread quietly. A third caller appearing
        // fails here and has to be argued for rather than merely typed.
        var callers = Calls(Core)
            .Where(c => c.Called == "System.Guid" && c.Member == "NewGuid")
            .Select(c => c.From)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ExemptCallers.OrderBy(x => x, StringComparer.Ordinal), callers);
    }

    [Fact]
    public void The_scan_can_actually_find_something()
    {
        // The positive control. Every assertion above is "this set is empty", which a scanner
        // that reads nothing satisfies perfectly - and this file would then be decoration.
        var calls = Calls(Core).ToList();

        Assert.NotEmpty(calls);
        Assert.Contains(calls, c => c.Called == "System.Guid" && c.Member == "NewGuid");
    }

    private sealed record Call(string From, string Called, string Member);

    private const byte CallOp = 0x28;
    private const byte CallvirtOp = 0x6F;

    private static IEnumerable<Call> Calls(Assembly assembly)
    {
        var module = assembly.ManifestModule;

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in Members(type))
            {
                var il = Body(method);
                if (il is null)
                {
                    continue;
                }

                for (var i = 0; i + 5 <= il.Length; i++)
                {
                    if (il[i] != CallOp && il[i] != CallvirtOp)
                    {
                        continue;
                    }

                    var called = Resolve(module, BitConverter.ToInt32(il, i + 1));
                    if (called?.DeclaringType?.FullName is { } owner)
                    {
                        yield return new Call($"{type.FullName}.{method.Name}", owner, called.Name);
                    }
                }
            }
        }
    }

    private static IEnumerable<MethodBase> Members(Type type)
    {
        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var m in type.GetMethods(All))
        {
            yield return m;
        }

        foreach (var c in type.GetConstructors(All))
        {
            yield return c;
        }

        if (type.TypeInitializer is { } initializer)
        {
            yield return initializer;
        }
    }

    private static byte[]? Body(MethodBase method)
    {
        try
        {
            return method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static MethodBase? Resolve(Module module, int token)
    {
        try
        {
            return module.ResolveMethod(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
