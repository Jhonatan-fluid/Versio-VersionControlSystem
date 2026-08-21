using System.Text.Json;
using System.Xml.Linq;

namespace Versio.Plc.TwinCat.Host;

/// <summary>
/// Tools-only TIID dump. Not product Export — the measurement that led to
/// export-only <c>*.devices.yaml</c>. Two untouched runs of the same copy must
/// be SHA-identical or the dump itself is churn and cannot become a catalog.
/// </summary>
internal static partial class HostApp
{
    private static void IoProbe(object sysMan, string outDir, bool writeTest, bool createTest)
    {
        Emit("progress", "phase", "ioprobe", "detail", "TIID");
        Directory.CreateDirectory(outDir);

        var report = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["op"] = "ioprobe",
        };

        var tiid = Lookup(sysMan, "TIID");
        if (tiid is null)
        {
            report["tiid"] = "missing";
            report["summary"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["present"] = false,
                ["itemCount"] = 0,
            };
        }
        else
        {
            var items = new List<SortedDictionary<string, object?>>();
            WalkIo(tiid, items);
            report["items"] = items;
            report["summary"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["present"] = true,
                ["itemCount"] = items.Count,
                ["rootName"] = TryProp(tiid, "Name") ?? "",
                ["rootPath"] = TryProp(tiid, "PathName") ?? "",
            };
        }

        if (writeTest && tiid is not null)
        {
            // Separate from the SHA dump: a setter that fails to restore would
            // make two "identical" runs differ. The harness compares read-only
            // dumps first, then optionally runs this on the same copy without Save.
            report["writeTest"] = TryWriteAndRestore(tiid);
        }

        if (createTest && tiid is not null)
        {
            report["createTest"] = TryCreateChildUnsaved(tiid);
        }

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,
        });
        File.WriteAllText(Path.Combine(outDir, "io.json"), json, Utf8NoBom);
        Emit("info", "op", "ioprobe", "path", Path.Combine(outDir, "io.json"));
    }

    private static void WalkIo(object item, List<SortedDictionary<string, object?>> into)
    {
        into.Add(DescribeIoNode(item));
        var n = ChildCountOf(item);
        for (var i = 1; i <= n; i++)
        {
            object? child;
            try
            {
                child = ChildOf(item, i);
            }
            catch (Exception ex)
            {
                into.Add(new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["index"] = i,
                    ["error"] = Innermost(ex),
                });
                continue;
            }

            if (child is not null)
            {
                WalkIo(child, into);
            }
        }
    }

    private static SortedDictionary<string, object?> DescribeIoNode(object item)
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = TryProp(item, "Name") ?? "",
            ["pathName"] = TryProp(item, "PathName") ?? "",
            ["itemType"] = TryProp(item, "ItemType") ?? "",
            ["itemSubType"] = TryProp(item, "ItemSubType") ?? "",
            ["comment"] = TryProp(item, "Comment") ?? "",
            ["disabled"] = TryProp(item, "Disabled") ?? "",
            ["childCount"] = ChildCountOf(item),
        };

        var xml = TryProduceXmlExcerpt(item);
        if (xml is not null)
        {
            rec["produceXml"] = xml;
        }

        return rec;
    }

    /// <summary>
    /// Root element and a handful of attributes, not the whole blob. ProduceXml of
    /// an I/O device can be large and may churn; a catalog cannot be built from
    /// bytes that differ on an untouched open.
    /// </summary>
    private static SortedDictionary<string, object?>? TryProduceXmlExcerpt(object item)
    {
        string xml;
        try
        {
            xml = Com.Call(item, "ProduceXml")?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            return new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = Innermost(ex),
            };
        }

        if (string.IsNullOrEmpty(xml))
        {
            return new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["length"] = 0,
            };
        }

        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["length"] = xml.Length,
        };

        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null)
            {
                return rec;
            }

            rec["root"] = root.Name.LocalName;
            var attrs = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in root.Attributes().Take(8))
            {
                attrs[a.Name.LocalName] = a.Value.Length > 80 ? a.Value[..80] : a.Value;
            }

            rec["attributes"] = attrs;
        }
        catch (Exception ex)
        {
            rec["parse"] = Innermost(ex);
        }

        return rec;
    }

    private static SortedDictionary<string, object?> TryWriteAndRestore(object tiid)
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attempted"] = false,
            ["applied"] = false,
            ["restored"] = false,
            ["saved"] = false,
            ["detail"] = "no writable Comment on a TIID node",
        };

        object? target = null;
        string? original = null;
        foreach (var item in FlattenIo(tiid))
        {
            try
            {
                original = Com.Str(item, "Comment") ?? "";
            }
            catch
            {
                continue;
            }

            try
            {
                Com.Set(item, "Comment", original);
            }
            catch
            {
                continue;
            }

            target = item;
            break;
        }

        if (target is null)
        {
            return rec;
        }

        rec["attempted"] = true;
        rec["target"] = TryProp(target, "PathName") ?? TryProp(target, "Name") ?? "";
        const string marker = "versio-ioprobe-temp";
        try
        {
            Com.Set(target, "Comment", marker);
            var read = Com.Str(target, "Comment");
            rec["applied"] = string.Equals(read, marker, StringComparison.Ordinal);
            rec["readBack"] = read ?? "";
            rec["produceXmlHasMarker"] = ProduceXmlContains(target, marker);
        }
        catch (Exception ex)
        {
            rec["applied"] = false;
            rec["detail"] = Innermost(ex);
            return rec;
        }

        try
        {
            Com.Set(target, "Comment", original ?? "");
            var restored = Com.Str(target, "Comment") ?? "";
            rec["restored"] = string.Equals(restored, original ?? "", StringComparison.Ordinal);
            rec["detail"] = rec["restored"] is true
                ? "Comment set and restored; project was not saved"
                : "restore read back '" + restored + "' expected '" + original + "'";
        }
        catch (Exception ex)
        {
            rec["restored"] = false;
            rec["detail"] = "restore failed: " + Innermost(ex);
        }

        rec["disabledTest"] = TryDisabledRead(target);
        return rec;
    }

    private static bool ProduceXmlContains(object item, string needle)
    {
        try
        {
            var xml = Com.Call(item, "ProduceXml")?.ToString() ?? "";
            return xml.Contains(needle, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static SortedDictionary<string, object?> TryDisabledRead(object item)
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            rec["value"] = TryProp(item, "Disabled") ?? "";
            rec["readable"] = true;
        }
        catch (Exception ex)
        {
            rec["readable"] = false;
            rec["detail"] = Innermost(ex);
        }

        return rec;
    }

    /// <summary>
    /// Probe-only. EtherCAT master subtype 111 is Beckhoff's documented CreateChild
    /// on TIID — TIA's PlugNew. Product import must never call this. The session
    /// quits without Save, so the copy on disk is unchanged.
    /// </summary>
    private static SortedDictionary<string, object?> TryCreateChildUnsaved(object tiid)
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attempted"] = true,
            ["created"] = false,
            ["saved"] = false,
        };

        // Beckhoff sample: CreateChild(name, subtype, before, vInfo).
        // The PLC host measured a different argument order (insert-last = 1,
        // subtype last). Try the documented I/O order first, then the PLC order.
        object? created = null;
        string? used = null;
        try
        {
            created = Com.Call(tiid, "CreateChild", "EtherCAT Master", 111, "", "");
            used = "name,111,empty,empty";
        }
        catch (Exception ex)
        {
            rec["firstAttempt"] = Innermost(ex);
            try
            {
                created = Com.Call(tiid, "CreateChild", "EtherCAT Master", 1, "", 111);
                used = "name,1,empty,111";
            }
            catch (Exception ex2)
            {
                rec["created"] = false;
                rec["detail"] = Innermost(ex2);
                return rec;
            }
        }

        rec["createChildArgs"] = used;
        rec["created"] = created is not null;
        if (created is null)
        {
            rec["detail"] = "CreateChild returned nothing";
            return rec;
        }

        rec["createdName"] = TryProp(created, "Name") ?? "";
        rec["createdPath"] = TryProp(created, "PathName") ?? "";
        rec["createdSubType"] = TryProp(created, "ItemSubType") ?? "";

        const string marker = "versio-ioprobe-created";
        try
        {
            Com.Set(created, "Comment", marker);
            var read = Com.Str(created, "Comment");
            rec["commentApplied"] = string.Equals(read, marker, StringComparison.Ordinal);
            rec["commentReadBack"] = read ?? "";
            rec["produceXmlHasMarker"] = ProduceXmlContains(created, marker);
        }
        catch (Exception ex)
        {
            rec["commentApplied"] = false;
            rec["commentDetail"] = Innermost(ex);
        }

        rec["detail"] = "CreateChild ran; project was not saved";
        return rec;
    }

    private static IEnumerable<object> FlattenIo(object root)
    {
        yield return root;
        var n = ChildCountOf(root);
        for (var i = 1; i <= n; i++)
        {
            object? child;
            try
            {
                child = ChildOf(root, i);
            }
            catch
            {
                continue;
            }

            if (child is null)
            {
                continue;
            }

            foreach (var nested in FlattenIo(child))
            {
                yield return nested;
            }
        }
    }

    private static string? TryProp(object? item, string name)
    {
        if (item is null)
        {
            return null;
        }

        try
        {
            return Com.Str(item, name);
        }
        catch
        {
            return null;
        }
    }
}
