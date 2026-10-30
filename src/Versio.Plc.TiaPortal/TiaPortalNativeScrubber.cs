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
            var doc = Versio.Core.Text.SafeXml.Parse(xml, LoadOptions.PreserveWhitespace);
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

            // Portal's export stamp is an element, not an attribute: DocumentInfo/Created, the
            // time of the export, in every file, every time. The host drops the element from
            // its own hash and keeps the previous file when that is all that moved (since
            // 2026-10-29); a changed object's file carries a new one, and must compare the same.
            foreach (var created in doc.Descendants()
                         .Where(e => e.Name.LocalName == "Created" && e.Parent?.Name.LocalName == "DocumentInfo")
                         .ToList())
            {
                created.Remove();
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
    /// The composition identity when SimaticML carries a <em>project</em> identity, otherwise null.
    /// </summary>
    /// <remarks>
    /// Prefer this over name+number if it ever appears. A rename must not change the
    /// manifest identity, and a key built from the name would turn every rename into a
    /// delete plus a create.
    /// SimaticML's <c>UId</c> is not that: it is a document-local integer. CleaningStation
    /// first exported every guid as <c>"0"</c> (wrapper), then as <c>"1"</c> once zero was
    /// skipped — 26,661 distinct UId values in 159 files, the most common being <c>"35"</c>.
    /// Only a GUID-shaped value is accepted.
    /// </remarks>
    public static string? TryReadUid(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var doc = Versio.Core.Text.SafeXml.Parse(xml);
            return doc.Descendants()
                .Select(e => (string?)e.Attribute("UId") ?? (string?)e.Attribute("ID")
                    ?? (string?)e.Attribute("Id"))
                .Select(v => v?.Trim())
                .FirstOrDefault(LooksLikeProjectIdentity);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Integers, including 0 and 1, are SimaticML's per-document numbering. A UUID or a
    /// 32-character hex blob would be a project identity if this vendor ever wrote one.
    /// </summary>
    internal static bool LooksLikeProjectIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.All(char.IsDigit))
        {
            return false;
        }

        return value.Length >= 8 && (value.Contains('-', StringComparison.Ordinal) || value.Length >= 32);
    }
}
