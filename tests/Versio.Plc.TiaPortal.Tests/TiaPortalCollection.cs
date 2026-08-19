using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// Every test class that launches TIA Portal, serialised.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TiaPortalCollection
{
    public const string Name = "tia-portal";
}
