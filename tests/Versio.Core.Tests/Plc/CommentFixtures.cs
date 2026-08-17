namespace Versio.Core.Tests.Plc.CommentFixtures;

/// <summary>
/// A type that talks about CODESYS, TwinCAT and Rockwell constantly, and leaks nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The negative control, and it needs a namespace of its own.</b> The first attempt asserted
/// this against the scanner's own test class, which was wrong in a way worth recording: those
/// tests pass vendor names as <i>assertion arguments</i> — <c>Assert.Contains("a TwinCAT project
/// would say this", …)</c> — and every one compiles to a real string literal. The test failed
/// immediately, correctly, and on its own premise rather than on the scanner.
/// </para>
/// <para>
/// So a negative control has to be a place where a brand appears in comments and in nothing
/// else. This is that place. Every mention below is in a comment or an XML doc, none of it is
/// compiled, and the scanner must find none of it — because reading IL rather than source is
/// exactly what makes it safe to explain the vendor rule in a comment above the code that
/// enforces it.
/// </para>
/// </remarks>
internal static class CommentOnlyFixture
{
    /// <summary>Mentions CODESYS in its documentation, and returns a neutral sentence.</summary>
    /// <remarks>
    /// A CODESYS export writes Structured Text; a TwinCAT one would too; Rockwell's Studio5000
    /// would not. None of that reaches the compiled assembly, and none of it should be a leak.
    /// </remarks>
    public static string Neutral() => "no PLC projects are tracked in this repository";

    // A plain comment naming Codesys, TwinCat, TiaPortal and Siemens, immediately above a
    // literal that names none of them. If the scanner ever starts reading source instead of IL,
    // this is the line that will catch it.
    public static string AlsoNeutral() => "untracked PLC project — track it to start exporting";
}
