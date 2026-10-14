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
/// lines of XML. PLC data types (<c>SW.Types.PlcStruct</c>) carry the same tree and
/// are tabled too since 2026-10-04 - the owner asked for UDTs as tables rather than
/// SCL text - marked <see cref="MemberTable.IsDataType"/> so the pane says "default
/// value" as the IDE does. Function blocks and functions (<c>SW.Blocks.FB</c>,
/// <c>SW.Blocks.FC</c>) are tabled as an interface, also since 2026-10-04: their
/// sections are parameters, statics and temporaries, so each non-empty section opens
/// with a heading row, the way the IDE's interface editor groups them. Organization
/// blocks stay out - their interface is the system's start information, not something
/// anybody reviews.
/// </para>
/// <para>
/// A member whose type is written in quotes (<c>"MotL"</c>, <c>Array[1..4] of
/// "AnaVal"</c>) refers to a user-defined type or block by name; that name goes in
/// <see cref="MemberRow.TypeReference"/> for the host to resolve and link. The quotes
/// are this vendor's spelling, which is why the format does it rather than Core.
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
    private const string PlcStruct = "SW.Types.PlcStruct";
    private const string FunctionBlock = "SW.Blocks.FB";
    private const string Function = "SW.Blocks.FC";

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
            // The export writes a UTF-8 byte-order mark. File.ReadAllText strips it, but the
            // committed copy read through `git show` keeps it as a character in front of
            // <?xml, which XDocument rejects - so every changed table compared against
            // nothing and stayed unmarked (owner's demo, 2026-10-05). Same bytes, two readers.
            doc = Versio.Core.Text.SafeXml.Parse(content.TrimStart('\uFEFF'));
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

        var isInterface = block.Name.LocalName is FunctionBlock or Function;
        var members = new List<MemberRow>();
        var iface = Child(attrs, "Interface");
        var sections = iface is null ? null : Child(iface, "Sections");
        if (sections is not null)
        {
            WalkSections(sections, parentPath: "", depth: 0, inheritedSection: "", members,
                headings: isInterface);
        }

        if (isInterface)
        {
            members = ForInterface(members, isFunction: block.Name.LocalName == Function);
        }

        return new MemberTable
        {
            Name = name,
            Number = Text(attrs, "Number"),
            InstanceOf = Text(attrs, "InstanceOfName"),
            MemoryLayout = Text(attrs, "MemoryLayout"),
            Members = members,
            IsDataType = block.Name.LocalName == PlcStruct,
            IsInterface = isInterface,
        };
    }

    /// <summary>
    /// The data-block kinds, the PLC data type, and the two code blocks with a reviewable
    /// interface. Not every <c>SW.Blocks.*</c>: an organization block has an Interface too,
    /// and tabling it is the mutation the OB fixture exists to catch.
    /// </summary>
    private static bool IsDataBlock(XElement element) =>
        element.Name.LocalName is InstanceDb or GlobalDb or PlcStruct or FunctionBlock or Function;

    private static void WalkSections(
        XElement sections,
        string parentPath,
        int depth,
        string inheritedSection,
        List<MemberRow> members,
        bool headings = false)
    {
        foreach (var section in Named(sections, "Section"))
        {
            var sectionName = (string?)section.Attribute("Name") ?? inheritedSection;
            var sectionMembers = Named(section, "Member").ToList();

            // An empty section is noise in a review: "Static" with nothing under it says
            // nothing the absence of the heading does not.
            if (headings && sectionMembers.Count > 0)
            {
                members.Add(new MemberRow
                {
                    Path = sectionName,
                    Name = sectionName,
                    Section = sectionName,
                    IsSectionHeading = true,
                });
            }

            foreach (var member in sectionMembers)
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
        var datatype = (string?)member.Attribute("Datatype") ?? string.Empty;

        members.Add(new MemberRow
        {
            Path = path,
            Name = name,
            Datatype = datatype,
            TypeReference = TypeReferenceIn(datatype),
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
    /// What an interface's rows can truthfully say, which is less than a data block's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>External access exists only where there is storage to reach.</b> A function block's
    /// inputs, outputs, in-outs and statics live in its instance DB, and those carry the HMI
    /// flags. A function's parameters, any temporary, a constant and a return value have no
    /// storage outside the call, so "Read/Write" there was invented: the access walk defaults
    /// a missing flag to the IDE default, which is right for DB members and wrong for these.
    /// Seen on the owner's FC RealToAnaVal_1 on 2026-10-05, every parameter "Read/Write".
    /// </para>
    /// <para>
    /// <b>A Void return is not a value.</b> The IDE lists <c>Ret_Val : Void</c> for a function
    /// that returns nothing; as a row it is a section heading over nothing worth reading.
    /// Dropped, with its heading if that leaves the section empty.
    /// </para>
    /// </remarks>
    private static List<MemberRow> ForInterface(List<MemberRow> rows, bool isFunction)
    {
        var kept = rows
            .Where(r => r.IsSectionHeading
                        || !(r.Section == "Return"
                             && string.Equals(r.Datatype, "Void", StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.IsSectionHeading || (!isFunction && r.Section is "Input" or "Output" or "InOut" or "Static")
                ? r
                : r with { ExternalAccess = string.Empty })
            .ToList();

        // A heading followed by another heading (or by nothing) has lost its rows.
        return kept
            .Where((r, i) => !r.IsSectionHeading
                             || (i + 1 < kept.Count && !kept[i + 1].IsSectionHeading))
            .ToList();
    }

    /// <summary>
    /// The user type a datatype names: the first quoted name in it, so <c>"MotL"</c> and
    /// <c>Array[1..4] of "AnaVal"</c> both resolve. Elementary types are unquoted, and so
    /// are system types such as <c>IEC_TIMER</c> - those have nothing in the export to link.
    /// </summary>
    private static string TypeReferenceIn(string datatype)
    {
        var open = datatype.IndexOf('"', StringComparison.Ordinal);
        if (open < 0)
        {
            return string.Empty;
        }

        var close = datatype.IndexOf('"', open + 1);
        return close > open + 1 ? datatype[(open + 1)..close] : string.Empty;
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
