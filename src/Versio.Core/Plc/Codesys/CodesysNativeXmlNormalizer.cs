using System.Text.RegularExpressions;

namespace Versio.Core.Plc.Codesys;

/// <summary>
/// Normalises native CODESYS XML for <b>comparison only</b> — never for what is written
/// to disk.
/// </summary>
/// <remarks>
/// <para>
/// Measured on CODESYS 3.5.22.20: a native <c>import_native</c> round trip is
/// <b>semantically lossless but not byte-stable</b>. Re-exporting an imported object
/// produces a file of identical length whose bytes differ in three ways, none of which
/// change behaviour:
/// </para>
/// <list type="number">
///   <item><b>Counters.</b> <c>UniqueIdGenerator</c> advanced 26 → 29 on a visualisation.</item>
///   <item><b>Unordered collections reordered.</b> Element properties came back with
///     <c>UseInitValue</c> and <c>DescriptionTextId</c> swapped — same content, different
///     serialisation order.</item>
///   <item><b>Internal reference GUIDs regenerated.</b> <c>EventPOUGuid</c>,
///     <c>GuidInit</c>/<c>GuidReInit</c>/<c>GuidExitX</c> and unnamed link GUIDs are
///     re-minted by CODESYS on import. 49 of 107 differing lines in one visualisation
///     became identical once GUIDs were masked.</item>
/// </list>
/// <para>
/// <b>This is why the fidelity check cannot be a byte comparison for native objects.</b>
/// The export direction is unaffected — repeated exports of an unchanged project are
/// byte-identical, so git diffs stay clean. It is only the import → re-export path that
/// perturbs serialisation.
/// </para>
/// <para>
/// Note the distinction from codescribe's warning: they broke visualisations by
/// <i>rewriting</i> GUIDs in the file on disk. This masks them <i>in memory, for a
/// comparison</i>. The bytes on disk keep their real GUIDs.
/// </para>
/// </remarks>
public static partial class NativeXmlNormalizer
{
    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex AnyGuid();

    [GeneratedRegex("""<Single Name="(?<name>[^"]*(?:UniqueIdGenerator|Timestamp)[^"]*)"(?<attrs>[^>]*)>(?<val>[^<]*)</Single>""")]
    private static partial Regex VolatileCounter();

    /// <summary>Element names whose child order carries no meaning, so a reordering must
    /// not read as a change.</summary>
    private static readonly string[] UnorderedContainers = ["List", "Properties", "Dictionary"];

    public sealed record Normalized(string Text, int GuidsMasked, int CountersMasked);

    /// <summary>
    /// Produce a comparison form of a native XML payload.
    /// </summary>
    /// <param name="maskGuids">
    /// Mask internal reference GUIDs. Required for import round-trip comparison; leave off
    /// when comparing two exports of the same project, where GUIDs are stable and a change
    /// in one is genuinely meaningful.
    /// </param>
    public static Normalized Normalize(string? xml, bool maskGuids = true)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return new Normalized(string.Empty, 0, 0);
        }

        var text = xml.Replace("\r\n", "\n");

        var counters = 0;
        text = VolatileCounter().Replace(text, m =>
        {
            counters++;
            return $"<Single Name=\"{m.Groups["name"].Value}\"{m.Groups["attrs"].Value}>" +
                   $"<masked/></Single>";
        });

        var guids = 0;
        if (maskGuids)
        {
            text = AnyGuid().Replace(text, _ =>
            {
                guids++;
                return "<masked-guid>";
            });
        }

        // Sort the lines of each unordered container so a reordering is not a difference.
        text = SortUnorderedBlocks(text);

        return new Normalized(text, guids, counters);
    }

    /// <summary>Two payloads are equivalent when their normalised forms match.</summary>
    public static bool AreEquivalent(string? a, string? b, bool maskGuids = true) =>
        string.Equals(Normalize(a, maskGuids).Text, Normalize(b, maskGuids).Text,
                      StringComparison.Ordinal);

    /// <summary>
    /// Sort sibling lines inside containers whose order is not meaningful.
    /// </summary>
    /// <remarks>
    /// Deliberately line-based rather than a full XML parse: these payloads reach 2.5 MB
    /// and are compared on every import, and the reordering CODESYS performs is at the
    /// level of whole sibling elements on their own lines.
    /// </remarks>
    private static string SortUnorderedBlocks(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            var container = UnorderedContainers.FirstOrDefault(
                c => line.Contains($"<{c} ", StringComparison.Ordinal) ||
                     line.Contains($"<{c}>", StringComparison.Ordinal));

            if (container is null || line.Contains("/>", StringComparison.Ordinal))
            {
                result.Add(line);
                i++;
                continue;
            }

            var closing = $"</{container}>";
            var end = i + 1;
            while (end < lines.Length && !lines[end].Contains(closing, StringComparison.Ordinal))
            {
                end++;
            }

            if (end >= lines.Length)
            {
                result.Add(line);
                i++;
                continue;
            }

            result.Add(line);
            var body = lines[(i + 1)..end].ToArray();
            Array.Sort(body, StringComparer.Ordinal);
            result.AddRange(body);
            result.Add(lines[end]);
            i = end + 1;
        }

        return string.Join('\n', result);
    }
}
