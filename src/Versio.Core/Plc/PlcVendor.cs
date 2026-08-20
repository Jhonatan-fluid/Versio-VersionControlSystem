using System.Text.Json;
using System.Text.Json.Serialization;

namespace Versio.Core.Plc;

/// <summary>
/// Which PLC toolchain a project belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Versio started CODESYS-only, but nothing above this line is CODESYS-shaped: git, the diff,
/// the direction lock, change detection and the commit gate all work the same way whichever tool
/// produced the project. <b>This type is the seam where vendor knowledge begins.</b>
/// </para>
/// <para>
/// <b>An open identity, not a closed enum, and the difference is the architectural rule.</b> The
/// README, <c>CLAUDE.md</c> and both registries say that adding a vendor means implementing
/// <c>IPlcRunner</c>, <c>IPlcProjectFormat</c> and <c>IPlcToolchain</c> and registering them —
/// "nothing in <c>Sync/</c>, <c>Git/</c>, <c>Locking/</c>, <c>Watching/</c>, <c>Projects/</c> or
/// <c>Diff/</c> may change". While this was an <c>enum</c> that was not quite true: a vendor not
/// already listed could not be named at all without editing <c>Versio.Core</c>, the one assembly
/// the rule exists to keep untouched. You cannot register what you cannot name.
/// </para>
/// <para>
    /// <b>The four well-known vendors are kept as statics, deliberately.</b> Studio 5000
    /// is not implemented and costs nothing; TIA and TwinCAT are packs. They are how the
    /// abstraction gets judged against real second vendors rather than a hypothetical one,
    /// and <c>VendorSeamTests</c> asserts they are still here so nobody tidies the list
    /// down to the one that first worked. <see cref="Known"/> is that list;
    /// it is not a limit on what may be constructed.
/// </para>
/// <para>
/// <b>Comparison is case-insensitive</b>, because the name survives a round trip through
/// <c>repo.json</c> — a committed, hand-editable file — and a binding that stopped matching its
/// toolchain over the spelling of one letter would be a very confusing way to lose an export.
/// It is the same choice <c>PlcFormats.ForPath</c> already makes about extensions.
/// </para>
/// <para>
/// <b><c>default(PlcVendor)</c> reads as empty rather than throwing.</b> A struct's default
/// bypasses every constructor, so the backing string can be null however carefully the
/// constructor guards. This used to be ordinal 0 — <c>Codesys</c> — which meant an
/// uninitialised field silently claimed a vendor. Empty is the honest answer and
/// <see cref="IsUnknown"/> is how to ask.
/// </para>
/// </remarks>
[JsonConverter(typeof(PlcVendorJsonConverter))]
public readonly struct PlcVendor : IEquatable<PlcVendor>
{
    private readonly string? _name;

    public PlcVendor(string name) => _name = name;

    /// <summary>The vendor's name as it is written down and persisted.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>True for <c>default(PlcVendor)</c> — a vendor that was never set.</summary>
    public bool IsUnknown => string.IsNullOrWhiteSpace(_name);

    /// <summary>CODESYS 3.5 and the many OEM rebrands built on it.</summary>
    public static PlcVendor Codesys { get; } = new("Codesys");

    /// <summary>Siemens TIA Portal.</summary>
    public static PlcVendor TiaPortal { get; } = new("TiaPortal");

    /// <summary>Rockwell Studio 5000. Not implemented.</summary>
    public static PlcVendor Studio5000 { get; } = new("Studio5000");

    /// <summary>Beckhoff TwinCAT 3. CODESYS-derived, but diverged enough to need its own pack.</summary>
    public static PlcVendor TwinCat { get; } = new("TwinCat");

    /// <summary>
    /// The vendors Versio ships knowing about. Not a limit on what can be constructed.
    /// </summary>
    public static IReadOnlyList<PlcVendor> Known { get; } =
        [Codesys, TiaPortal, Studio5000, TwinCat];

    public bool Equals(PlcVendor other) =>
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is PlcVendor other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    public static bool operator ==(PlcVendor left, PlcVendor right) => left.Equals(right);

    public static bool operator !=(PlcVendor left, PlcVendor right) => !left.Equals(right);

    /// <summary>The name, so a vendor interpolated into a log line reads properly.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Reads and writes a <see cref="PlcVendor"/> as a plain JSON string.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the type rather than added to one serializer's options</b>, so every reader and writer
/// gets it — including the next one somebody adds without thinking about vendors at all.
/// </para>
/// <para>
/// The bytes are unchanged from when this was an enum with <c>JsonStringEnumConverter</c>:
/// <c>"vendor": "Codesys"</c>, exactly as every committed <c>repo.json</c> already holds. That is
/// the whole reason this converter exists rather than letting the struct serialise as an object
/// with a <c>Name</c> field — a shipped, committed file must keep meaning what it meant.
/// </para>
/// <para>
/// <b>And the hazard the old comment in <c>RepoConfigStore</c> warned about is now gone.</b> It
/// argued against ordinal serialisation because "inserting a vendor into <c>PlcVendor</c> —
/// alphabetically, say — would silently repoint every existing binding at a different toolchain".
/// There is no ordinal left to shift: the name is the identity.
/// </para>
/// </remarks>
public sealed class PlcVendorJsonConverter : JsonConverter<PlcVendor>
{
    public override PlcVendor Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(
        Utf8JsonWriter writer, PlcVendor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Name);

    /// <summary>Vendors are also written as dictionary keys, which JSON requires be strings.</summary>
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer, PlcVendor value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Name);

    public override PlcVendor ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);
}
