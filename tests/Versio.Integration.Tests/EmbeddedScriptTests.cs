using Versio.Plc.Codesys.Infrastructure;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// That the python is still inside the assembly, checked in milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other test that touches <c>ScriptExtractor</c> is CODESYS-tagged.</b> So when the
/// scripts moved out of a loose <c>src/Versio.Scripts.Codesys/</c> folder and into the vendor
/// pack, a broken <c>EmbeddedResource</c> glob would have left the 19-second lane completely
/// green and shown up only in a 31-minute run — as CODESYS failing to start with an error about
/// a missing script, which is a long way from the cause.
/// </para>
/// <para>
/// <b>The count is asserted, not just "more than none".</b> A glob that matched one file would
/// satisfy a non-empty check and still break every export, and the fourteen modules are a set
/// that changes rarely and deliberately: if this number moves, somebody added or removed a script
/// and should say which in the same commit. <c>versio_devices.py</c> is the newest, added
/// 2026-08-20 so device connectors and empty IO-Link slots have a catalog of their own.
/// </para>
/// <para>
/// No CODESYS, no python interpreter, no git — it reads the manifest resources of an assembly
/// that is already loaded.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class EmbeddedScriptTests
{
    private static readonly string[] Expected =
    [
        "versio_decl.py",
        "versio_delete_objects.py",
        "versio_devices.py",
        "versio_export.py",
        "versio_import.py",
        "versio_libraries.py",
        "versio_native_equiv.py",
        "versio_probe.py",
        "versio_probe_native_import.py",
        "versio_tasks.py",
        "versio_text.py",
        "versio_tree.py",
        "versio_types.py",
        "versio_yaml.py",
    ];

    [Fact]
    public void Every_script_travels_inside_the_vendor_pack()
    {
        var embedded = typeof(ScriptExtractor).Assembly
            .GetManifestResourceNames()
            .Where(n => n.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            .Select(n => n[(n.LastIndexOf('.', n.Length - ".py".Length - 1) + 1)..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Expected, embedded);
    }

    [Fact]
    public void The_scripts_travel_with_the_code_that_runs_them()
    {
        // ScriptExtractor resolves through typeof(ScriptExtractor).Assembly rather than a
        // hardcoded assembly name or resource prefix, which is the only reason moving both into
        // the vendor pack needed no code change at all. This pins that: split them again and
        // the extractor would look in the wrong place while still compiling.
        var extractor = typeof(ScriptExtractor).Assembly.GetName().Name;

        Assert.Equal("Versio.Plc.Codesys.Infrastructure", extractor);
    }
}
