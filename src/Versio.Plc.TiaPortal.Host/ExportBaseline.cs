using System.Text.Json;

namespace Versio.Plc.TiaPortal.Host;

/// <summary>
/// The previous export's manifest, read back from the tree the export is about to rewrite,
/// so an object Portal reports unchanged can keep the files it already has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Every save re-exports every object, and on the owner's nine-PLC project that is
/// 1,366 <c>Export</c>-plus-<c>GenerateSource</c> round trips into Portal at about 88 ms each
/// — two minutes on top of the 37 s Portal start — for a save that touched one block. Portal
/// keeps a per-object <c>ModifiedDate</c> (measured 2026-10-29, DRIVING §7.1), so the export
/// records it beside each file and, next time, keeps the files of any object whose stamp has
/// not moved.
/// </para>
/// <para>
/// <b>Keeping is the risky direction, so every condition errs towards exporting.</b> A kept
/// object that had in fact changed is a stale mirror that claims to be current, which is the
/// one thing this tool must not produce. An exported object that had not changed costs 88 ms.
/// So an object is kept only when all of the following hold, and anything else — a baseline
/// this build cannot read, an object with no stamp, a recorded file that is missing or whose
/// bytes no longer hash to what was recorded — is exported in full:
/// </para>
/// <list type="bullet">
///   <item>the previous manifest is readable, at or above <see cref="StampSchema"/>, written by
///   this vendor, and by the same Portal (<see cref="ToolchainVersion"/>) — a different release
///   generates different text for the same object;</item>
///   <item>the live object reports a stamp, and it equals the stamp on every file recorded for
///   its identity;</item>
///   <item>every recorded file is one of the files this walk would write for the object at its
///   current place in the tree — a block moved to another group keeps its identity and may keep
///   its stamp, and its files must move with it;</item>
///   <item>every recorded file is on disk and still hashes to what was recorded — a mirror file
///   somebody edited is not the last export, whatever the stamp says.</item>
/// </list>
/// <para>
/// <b>Deletions, renames and new objects need nothing here.</b> The walk records only what it
/// visits, so an object gone from Portal has no record in the new manifest exactly as before;
/// a rename is a new identity (the name is in it) and exports in full; a new object has no
/// baseline. Nothing is removed from disk by this class: the swap, where there is one, and the
/// planner's view of the manifest are unchanged.
/// </para>
/// <para>
/// Pure: a string in, decisions out, with hashing handed in — so it is tested in the pure suite
/// without a Portal, like the host's import. It cannot use <c>Versio.Core</c>'s manifest
/// reader (net48), and it reads less than that reader does on purpose: a field it does not
/// recognise is a reason to export, never a reason to guess.
/// </para>
/// </remarks>
internal sealed class ExportBaseline
{
    /// <summary>
    /// The manifest schema from which each TIA object record carries a <c>stamp</c>. One
    /// sequence across vendors (<c>ProjectManifest.Schema</c>): 10 added the toolchain
    /// version, 11 the stamp. A TIA manifest below this has no stamps and reads as "export
    /// everything", which is what it always meant.
    /// </summary>
    public const int StampSchema = 11;

    /// <summary>One file of one object, as the previous export recorded it.</summary>
    public sealed record Record(
        string Guid, string Name, string Type, string Form, string Path,
        string Hash, string NHash, string Stamp)
    {
        /// <summary>Forms hashed as text (UTF-8, no BOM), the way the export hashes them.</summary>
        public bool IsTextual => Form is "SCL" or "SCL_REVIEW";
    }

    private readonly Dictionary<string, List<Record>> _byGuid;

    private ExportBaseline(string toolchainVersion, Dictionary<string, List<Record>> byGuid)
    {
        ToolchainVersion = toolchainVersion;
        _byGuid = byGuid;
    }

    public string ToolchainVersion { get; }

    public int ObjectCount => _byGuid.Count;

    /// <summary>
    /// Read a previous manifest, or null with the sentence that says why the export is full.
    /// </summary>
    public static ExportBaseline? Load(string? manifestJson, string toolchainVersion, out string refusal)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            refusal = "no previous manifest in the export tree (first export)";
            return null;
        }

        JsonDocument doc;
        try
        {
            // net48 has no [NotNullWhen] on IsNullOrWhiteSpace, hence the bang.
            doc = JsonDocument.Parse(manifestJson!);
        }
        catch (JsonException ex)
        {
            refusal = "the previous manifest is not readable JSON: " + ex.Message;
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                refusal = "the previous manifest is not a JSON object";
                return null;
            }

            var schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32()
                : 0;
            if (schema < StampSchema)
            {
                refusal = $"the previous manifest is schema {schema}, below {StampSchema}, so it records no stamps";
                return null;
            }

            var vendor = root.TryGetProperty("vendor", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
            if (!string.Equals(vendor, "TiaPortal", StringComparison.Ordinal))
            {
                refusal = $"the previous manifest was written by '{vendor}', not by this vendor";
                return null;
            }

            var recorded = root.TryGetProperty("toolchainVersion", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? ""
                : "";
            if (recorded.Length == 0 || !string.Equals(recorded, toolchainVersion, StringComparison.Ordinal))
            {
                refusal = recorded.Length == 0
                    ? "the previous manifest names no toolchain"
                    : $"the previous export was by '{recorded}' and this one is by '{toolchainVersion}'";
                return null;
            }

            if (!root.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array)
            {
                refusal = "the previous manifest has no objects array";
                return null;
            }

            var byGuid = new Dictionary<string, List<Record>>(StringComparer.Ordinal);
            foreach (var o in objects.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var guid = Field(o, "guid");
                if (guid.Length == 0)
                {
                    continue;
                }

                if (!byGuid.TryGetValue(guid, out var list))
                {
                    list = [];
                    byGuid[guid] = list;
                }

                list.Add(new Record(
                    guid, Field(o, "name"), Field(o, "type"), Field(o, "form"), Field(o, "path"),
                    Field(o, "hash"), Field(o, "nhash"), Field(o, "stamp")));
            }

            refusal = "";
            return new ExportBaseline(toolchainVersion, byGuid);
        }
    }

    /// <summary>Why an object was not kept — the buckets the per-PLC summary counts.</summary>
    public enum ExportReason
    {
        /// <summary>Kept: nothing to export.</summary>
        Kept,

        /// <summary>No record under this identity: new, or renamed, or the last export failed on it.</summary>
        New,

        /// <summary>The stamp moved.</summary>
        Changed,

        /// <summary>The live object reports no stamp.</summary>
        NoStamp,

        /// <summary>Recorded at another place in the tree.</summary>
        Moved,

        /// <summary>A recorded file is missing from the mirror or no longer hashes to its record.</summary>
        Unverifiable,
    }

    /// <summary>
    /// The records to carry forward unchanged for an object, or null with the reason it exports.
    /// </summary>
    /// <param name="guid">The identity the walk would record.</param>
    /// <param name="stamp">The live object's stamp, empty when it has none.</param>
    /// <param name="expectedPaths">Every mirror-relative path the walk could write for this
    /// object where it is now (its XML, and its SCL or UDT source).</param>
    /// <param name="hashOnDisk">The hash of a recorded path's current bytes, in the form the
    /// export records (text or raw per the record), or null when the file is missing.</param>
    public IReadOnlyList<Record>? Keep(
        string guid, string stamp, IReadOnlyCollection<string> expectedPaths,
        Func<Record, string?> hashOnDisk, out ExportReason reason)
    {
        if (string.IsNullOrEmpty(stamp))
        {
            reason = ExportReason.NoStamp;
            return null;
        }

        if (!_byGuid.TryGetValue(guid, out var records) || records.Count == 0)
        {
            reason = ExportReason.New;
            return null;
        }

        foreach (var r in records)
        {
            if (!string.Equals(r.Stamp, stamp, StringComparison.Ordinal))
            {
                reason = ExportReason.Changed;
                return null;
            }
        }

        foreach (var r in records)
        {
            if (!expectedPaths.Contains(r.Path))
            {
                reason = ExportReason.Moved;
                return null;
            }
        }

        foreach (var r in records)
        {
            var current = hashOnDisk(r);
            if (current is null || r.Hash.Length == 0 || !string.Equals(current, r.Hash, StringComparison.Ordinal))
            {
                reason = ExportReason.Unverifiable;
                return null;
            }
        }

        reason = ExportReason.Kept;
        return records;
    }

    private static string Field(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
