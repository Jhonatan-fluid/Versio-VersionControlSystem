using Versio.Core.Plc;

namespace Versio.Plc.Codesys;

/// <summary>
/// How CODESYS's own chatter is labelled in the log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared by the vendor rather than by the log.</b> This was a <c>Codesys</c> constant in
/// <c>LogCategory</c>, which is a table serving every vendor — so the neutral logging namespace
/// named one brand, and the next vendor would have added a second constant beside it until the
/// table was a vendor list. A category belongs to whoever emits it.
/// </para>
/// <para>
/// <b>The string is unchanged, deliberately.</b> Lines still read <c>Codesys</c>, existing log
/// files still parse, and the filter still offers the same toggle. Nothing about this is visible
/// to somebody using the application — the whole change is which assembly writes the word down,
/// which is exactly what makes it safe to do in one commit.
/// </para>
/// <para>
/// Kept apart from <see cref="LogCategory.Plc"/> for the same reason <c>Watch</c> is kept apart
/// from <c>Fs</c>: the toolchain's line-by-line chatter is filterable on its own, without losing
/// the orchestration decisions that sit above it.
/// </para>
/// </remarks>
public static class CodesysLog
{
    /// <summary>CODESYS-specific lines, emitted by its runner and scripts.</summary>
    public const string Category = "Codesys";
}
