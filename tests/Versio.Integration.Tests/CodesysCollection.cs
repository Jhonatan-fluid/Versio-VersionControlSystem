using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// Every test class that launches a real CODESYS, serialised.
/// </summary>
/// <remarks>
/// <para>
/// xunit runs test <i>classes</i> in parallel by default, and a headless CODESYS is not a
/// thing there can usefully be two of: they contend on the profile and on the shared temp
/// state, and the loser exits in about a second with no useful message. The symptom is a test
/// that passes alone and fails in a full run - the most expensive kind to diagnose, and the
/// kind a newcomer meets on their first `dotnet test`.
/// </para>
/// <para>
/// Found by running the whole suite from a fresh clone rather than from the working tree it
/// was written in. Four classes already drove CODESYS and got away with it; adding a fifth
/// that launches five exports tipped it over, which means the race was always there and the
/// suite had simply been lucky.
/// </para>
/// <para>
/// The cost is wall-clock: these run one after another, and a CODESYS launch is 17-31 s. That
/// is the correct trade - a slow suite that is believed beats a fast one that is not.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class CodesysCollection
{
    public const string Name = "codesys";
}
