using Xunit;

namespace Versio.Plc.TwinCat.Tests;

/// <summary>
/// Every test class that launches TcXaeShell, serialised.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TwinCatCollection
{
    public const string Name = "twincat";
}
