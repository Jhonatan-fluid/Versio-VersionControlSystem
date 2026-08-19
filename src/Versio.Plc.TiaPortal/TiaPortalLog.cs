using Versio.Core.Logging;

namespace Versio.Plc.TiaPortal;

/// <summary>
/// How this vendor's own chatter is labelled in the log.
/// </summary>
/// <remarks>
/// Declared here rather than in <see cref="LogCategory"/>, which is a table serving every
/// vendor. The string is the product's short name, not a company name, so a filter toggle
/// stays readable without putting a brand in the neutral logging namespace.
/// </remarks>
public static class TiaPortalLog
{
    public const string Category = "TiaPortal";
}
