using System.Xml.Linq;
using Versio.Core.Projects;

namespace Versio.Plc.TiaPortal;

/// <summary>
/// A member table from this vendor's native data-block XML.
/// </summary>
/// <remarks>
/// <para>
/// The pane is vendor-neutral; this file is not. Instance and global data blocks
/// carry an Interface tree the IDE already draws as a start-value table, and the
/// rendered view asks the format for that table rather than highlighting a thousand
/// lines of XML. Function blocks, functions and PLC data types use the same Interface
/// schema and are refused here — that is a later pane, and widening the local-name
/// gate would silently table the wrong objects.
/// </para>
/// <para>
/// Nested members flatten to a dotted path. Array start values live on
/// <c>Subelement</c> rows indexed from the Path attribute. External access
/// collapses this vendor's two flags (accessible / writable) to one opaque
/// string; omitted flags are the IDE default, not locked. Nothing here writes
/// back.
/// </para>
/// </remarks>
public static class TiaPortalMemberTableParser
{
    private const string InstanceDb = "SW.Blocks.InstanceDB";
    private const string GlobalDb = "SW.Blocks.GlobalDB";

    public static MemberTable? TryParse(string relativePath, string content)
    {
        if (string.IsNullOrWhiteSpace(content)
            || !relativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var block = doc.Descendants().FirstOrDefault(IsDataBlock);
        if (block is null)
        {
            return null;
        }

        var attrs = Child(block, "AttributeList") ?? block;
        var name = Text(attrs, "Name");
        if (name.Length == 0)
        {
            return null;
        }

        var members = new List<MemberRow>();
        var iface = Child(attrs, "Interface");
        var sections = iface is null ? null : Child(iface, "Sections");
        if (sections is not null)
        {
            WalkSections(sections, parentPath: "", depth: 0, inheritedSection: "", members);
        }

        return new MemberTable
        {
            Name = name,
            Number = Text(attrs, "Number"),
            InstanceOf = Text(attrs, "InstanceOfName"),
            MemoryLayout = Text(attrs, "MemoryLayout"),
            Members = members,
        };
    }

    /// <summary>
    /// Only the two data-block kinds. <c>SW.Blocks.FB</c> has an Interface too;
    /// treating any <c>SW.Blocks.*</c> as a table is the mutation the FB fixture
    /// exists to catch.
    /// </summary>
    private static bool IsDataBlock(XElement element) =>
        element.Name.LocalName is InstanceDb or GlobalDb;

    private static void WalkSections(
        XElement sections,
        string parentPath,
        int depth,
        string inheritedSection,
        List<MemberRow> members)
    {
        foreach (var section in Named(sections, "Section"))
        {
            var sectionName = (string?)section.Attribute("Name") ?? inheritedSection;
            foreach (var member in Named(section, "Member"))
            {
                WalkMember(member, parentPath, depth, sectionName, members);
            }
        }
    }

    private static void WalkMember(
        XElement member,
        string parentPath,
        int depth,
        string section,
        List<MemberRow> members)
    {
        var name = (string?)member.Attribute("Name") ?? string.Empty;
        var path = parentPath.Length == 0 ? name : parentPath + "." + name;
        var access = AccessOf(member);

        members.Add(new MemberRow
        {
            Path = path,
            Name = name,
            Datatype = (string?)member.Attribute("Datatype") ?? string.Empty,
            Section = section,
            StartValue = Text(member, "StartValue"),
            ExternalAccess = access,
            Comment = FirstComment(member),
            Depth = depth,
        });

        foreach (var sub in Named(member, "Subelement"))
        {
            var index = (string?)sub.Attribute("Path") ?? string.Empty;
            members.Add(new MemberRow
            {
                Path = path + "[" + index + "]",
                Name = name + "[" + index + "]",
                Datatype = (string?)member.Attribute("Datatype") ?? string.Empty,
                Section = section,
                StartValue = Text(sub, "StartValue"),
                ExternalAccess = access,
                Comment = FirstComment(sub),
                Depth = depth + 1,
            });
        }

        foreach (var nested in Named(member, "Sections"))
        {
            WalkSections(nested, path, depth + 1, section, members);
        }
    }

    /// <summary>
    /// This vendor writes <c>ExternalAccessible</c> / <c>ExternalWritable</c> only
    /// when they are not the IDE default (true). A member with no AttributeList is
    /// therefore Read/Write, not None — treating omitted as false would paint every
    /// nested UDT member as locked when the XML simply did not restate the default.
    /// <c>ExternalVisible</c> is HMI-engineering visibility, not access, and is
    /// ignored here.
    /// </summary>
    private static string AccessOf(XElement member)
    {
        var accessible = NamedBoolean(member, "ExternalAccessible", defaultValue: true);
        var writable = NamedBoolean(member, "ExternalWritable", defaultValue: true);
        if (!accessible)
        {
            return "None";
        }

        return writable ? "Read/Write" : "Read";
    }

    private static bool NamedBoolean(XElement member, string name, bool defaultValue)
    {
        var list = Child(member, "AttributeList");
        if (list is null)
        {
            return defaultValue;
        }

        var attr = list.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "BooleanAttribute"
            && string.Equals((string?)e.Attribute("Name"), name, StringComparison.Ordinal));
        if (attr is null)
        {
            return defaultValue;
        }

        return bool.TryParse(attr.Value.Trim(), out var value) ? value : defaultValue;
    }

    private static string FirstComment(XElement parent)
    {
        var comment = Child(parent, "Comment");
        if (comment is null)
        {
            return string.Empty;
        }

        var text = Named(comment, "MultiLanguageText")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);

        return text ?? string.Empty;
    }

    private static string Text(XElement parent, string local) =>
        Child(parent, local)?.Value.Trim() ?? string.Empty;

    private static XElement? Child(XElement parent, string local) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == local);

    private static IEnumerable<XElement> Named(XElement parent, string local) =>
        parent.Elements().Where(e => e.Name.LocalName == local);
}
