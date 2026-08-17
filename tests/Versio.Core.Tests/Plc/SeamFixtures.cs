using Versio.Core.Plc.Codesys;

namespace Versio.Core.Tests.Plc.SeamFixtures;

/// <summary>
/// Deliberate leaks, in a namespace of their own, so the seam scanner can be proven to find them.
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because <c>Assert.Empty</c> passes just as well when the instrument is
/// broken.</b> The seam tests assert that neutral namespaces contain no vendor name — an
/// assertion a scanner that finds nothing at all satisfies perfectly. Every mutation check run
/// while the scanner was written was temporary and is long reverted, so without these fixtures
/// there would be no standing evidence that any of it still works.
/// </para>
/// <para>
/// This codebase has caught sixteen vacuous passes. Writing the seventeenth into the very test
/// that exists to stop vendor knowledge leaking would have been a poor way to end that run.
/// </para>
/// <para>
/// Kept in <c>Versio.Core.Tests</c>, which the real seam tests never scan — they read
/// <c>Versio.Core</c> and <c>Versio.Infrastructure</c> — so these cannot fail the rule they are
/// here to demonstrate.
/// </para>
/// </remarks>
internal static class VendorNamingFixture
{
    /// <summary>A vendor named in a compile-time constant.</summary>
    public const string Category = "Codesys";

    /// <summary>A vendor named in a plain string literal.</summary>
    public static string Plain() => "no CODESYS projects are tracked here either";

    /// <summary>
    /// A vendor named inside a lambda, which compiles into a generated nested type.
    /// </summary>
    /// <remarks>
    /// The shape most likely to be silently invisible: the literal does not live in this method
    /// at all, it lives in a compiler-generated class that happens to carry this namespace.
    /// </remarks>
    public static string InLambda() =>
        new[] { 1 }.Select(_ => "a TwinCAT project would say this").First();
}

/// <summary>A vendor type travelling inside a generic and inside an array.</summary>
/// <remarks>
/// The namespace of <c>IReadOnlyList&lt;CodesysProjectFormat&gt;</c> is
/// <c>System.Collections.Generic</c>, so a check that reads the declared type and stops sees
/// nothing to complain about. This is the hole that let a collection of vendor objects cross the
/// seam unnoticed.
/// </remarks>
internal sealed class GenericCarrier
{
    public IReadOnlyList<CodesysProjectFormat> Formats { get; init; } = [];

    public CodesysProjectFormat[] AsArray { get; init; } = [];

    public Dictionary<string, CodesysProjectFormat> ByName { get; init; } = [];
}
