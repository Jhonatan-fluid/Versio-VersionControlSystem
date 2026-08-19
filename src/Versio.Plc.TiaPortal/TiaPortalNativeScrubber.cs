using System.Xml.Linq;
using Versio.Core.Hashing;

namespace Versio.Plc.TiaPortal;

/// <summary>
/// A comparison form for SimaticML that ignores the attributes that churn between exports.
/// </summary>
/// <remarks>
/// Native XML from this toolchain is not byte-stable. A deletion has to ask whether the
/// live object still matches what was exported, and hashing the raw file would always
/// say no. Stripping dates, composition ids that the importer remints, and insignificant
/// whitespace is the same job the first vendor's native scrubber does — different
/// attributes, same reason.
/// </remarks>
public static class TiaPortalNativeScrubber
{
    private static readonly HashSet<string> ChurnAttributes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DateModified", "DateCreated", "ModificationDate", "CreationDate",
            "Changed", "TimeStamp", "Timestamp", "Version",
        };

    public static string Normalize(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return string.Empty;
        }

        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            if (doc.Root is null)
            {
                return xml.Trim();
            }

            foreach (var element in doc.Descendants())
            {
                foreach (var attr in element.Attributes().ToList())
                {
                    if (ChurnAttributes.Contains(attr.Name.LocalName))
                    {
                        attr.Remove();
                    }
                }
            }

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch (System.Xml.XmlException)
        {
            return xml.Trim();
        }
    }

    public static string Hash(string? xml) => ContentHash.Of(Normalize(xml));

    /// <summary>
    /// The composition identity when SimaticML carries one, otherwise null.
    /// </summary>
    /// <remarks>
    /// Prefer this over name+number. A rename must not change the manifest identity, and
    /// a key built from the name would turn every rename into a delete plus a create.
    /// </remarks>
    public static string? TryReadUid(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var uid = doc.Descendants()
                .Select(e => (string?)e.Attribute("UId") ?? (string?)e.Attribute("ID")
                    ?? (string?)e.Attribute("Id"))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            return uid;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
