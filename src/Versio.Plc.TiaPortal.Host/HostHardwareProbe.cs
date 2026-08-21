using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Versio.Plc.TiaPortal.Host;

/// <summary>
/// Tools-only hardware surface dump. Not product Export — the owner picks a slice
/// after this file exists. Two untouched runs of the same copy must be SHA-identical
/// or the dump itself is churn and cannot become a catalog.
/// </summary>
internal static partial class HostApp
{
    private static readonly string[] ServiceTypeNames =
    [
        "Siemens.Engineering.HW.Features.SoftwareContainer",
        "Siemens.Engineering.HW.Features.NetworkInterface",
        "Siemens.Engineering.HW.Features.NetworkPort",
        "Siemens.Engineering.HW.IoController",
        "Siemens.Engineering.HW.IoConnector",
        "Siemens.Engineering.HW.IoSystem",
        "Siemens.Engineering.HW.Features.IoController",
        "Siemens.Engineering.HW.Features.IoConnector",
        "Siemens.Engineering.HW.Features.IoSystem",
        "Siemens.Engineering.HW.Features.AddressComposition",
        "Siemens.Engineering.Compiler.ICompilable",
    ];

    private static readonly string[] InterestingAttributes =
    [
        "TypeIdentifier", "OrderNumber", "ShortDesignation", "FirmwareVersion",
        "Comment", "Author", "PositionNumber", "Slot", "Subslot",
        "StartAddress", "Address", "Length", "ProcessImage", "Classification",
        "Name", "IpAddress", "SubnetMask", "RouterAddress", "PnDeviceName",
    ];

    private static void HardwareProbe(object project, string outDir, bool writeTest)
    {
        Emit("progress", "phase", "hwprobe", "detail", "api types");
        Directory.CreateDirectory(outDir);

        var report = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["op"] = "hwprobe",
            ["projectName"] = Str(project, "Name") ?? "",
            ["apiTypes"] = DiscoverHwTypes(),
            ["projectCompositions"] = CompositionNames(project),
            ["serviceTypes"] = ProbeServiceTypePresence(),
        };

        var devices = new List<SortedDictionary<string, object?>>();
        var summary = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["deviceCount"] = 0,
            ["itemCount"] = 0,
            ["ports"] = 0,
            ["portsWithPartner"] = 0,
            ["emptySlotGaps"] = 0,
            ["softwareContainers"] = 0,
        };

        foreach (var device in Enumerate(GetProp(project, "Devices"))
                     .OrderBy(d => Str(d, "Name") ?? "", StringComparer.OrdinalIgnoreCase))
        {
            Emit("progress", "phase", "hwprobe", "detail", Str(device, "Name") ?? "device");
            devices.Add(ProbeDevice(device, summary));
        }

        summary["deviceCount"] = devices.Count;
        report["devices"] = devices;
        report["subnets"] = ProbeSubnets(project);
        report["deviceGroups"] = ProbeNamedComposition(project, "DeviceGroups");
        report["writeApis"] = ProbeWriteApis(devices);
        report["summary"] = summary;

        if (writeTest)
        {
            // Separate from the SHA dump: a setter that fails to restore would
            // make two "identical" runs differ. The harness compares read-only
            // dumps first, then optionally runs this on the same copy without Save.
            report["writeTest"] = TryWriteAndRestore(project);
        }

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,
        });
        File.WriteAllText(Path.Combine(outDir, "hardware.json"), json, Utf8NoBom);
        Emit("info", "op", "hwprobe", "path", Path.Combine(outDir, "hardware.json"));
    }

    private static SortedDictionary<string, object?> ProbeDevice(
        object device, SortedDictionary<string, object?> summary)
    {
        var rec = ObjectBasics(device);
        rec["methods"] = MethodNames(device);
        rec["writeMethods"] = WriteMethodNames(device);
        rec["items"] = ProbeItems(device, summary);
        return rec;
    }

    private static List<SortedDictionary<string, object?>> ProbeItems(
        object root, SortedDictionary<string, object?> summary)
    {
        var rows = new List<SortedDictionary<string, object?>>();
        var positions = new List<int>();

        foreach (var item in WalkDeviceItems(root))
        {
            var row = ObjectBasics(item);
            row["attributes"] = ProbeAttributes(item);
            row["services"] = ProbeServices(item, summary);
            row["methods"] = MethodNames(item);
            row["writeMethods"] = WriteMethodNames(item);
            row["ports"] = ProbePorts(item, summary);
            rows.Add(row);

            summary["itemCount"] = (int)summary["itemCount"]! + 1;
            if (TryInt(row, "positionNumber") is { } pos && pos >= 0 && pos < 256)
            {
                positions.Add(pos);
            }
        }

        if (positions.Count > 0)
        {
            positions.Sort();
            var gaps = 0;
            for (var i = 1; i < positions.Count; i++)
            {
                gaps += Math.Max(0, positions[i] - positions[i - 1] - 1);
            }

            summary["emptySlotGaps"] = (int)summary["emptySlotGaps"]! + gaps;
            rows[0]["positionNumbers"] = positions;
            rows[0]["positionGaps"] = gaps;
        }

        return rows;
    }

    private static SortedDictionary<string, object?> ObjectBasics(object obj)
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = TryStr(obj, "Name") ?? "",
            ["type"] = obj.GetType().Name,
            ["typeFull"] = obj.GetType().FullName ?? obj.GetType().Name,
            ["classification"] = TryStr(obj, "Classification") ?? "",
            ["typeIdentifier"] = TryStr(obj, "TypeIdentifier")
                                 ?? TryAttribute(obj, "TypeIdentifier") ?? "",
            ["positionNumber"] = TryStr(obj, "PositionNumber")
                                 ?? TryAttribute(obj, "PositionNumber") ?? "",
            ["author"] = TryStr(obj, "Author") ?? TryAttribute(obj, "Author") ?? "",
            ["comment"] = TryStr(obj, "Comment") ?? TryAttribute(obj, "Comment") ?? "",
        };
        return rec;
    }

    private static SortedDictionary<string, object?> ProbeAttributes(object obj)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var canWrite = new SortedSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var name in InterestingAttributes)
        {
            names.Add(name);
        }

        try
        {
            var getInfos = obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0)
                ?? obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetAttributes" && m.GetParameters().Length == 0);
            var listed = getInfos?.Invoke(obj, null);
            foreach (var info in Enumerate(listed))
            {
                var name = TryStr(info, "Name") ?? info.ToString() ?? "";
                if (name.Length == 0)
                {
                    continue;
                }

                names.Add(name);
                if (TryBool(info, "CanWrite") || TryBool(info, "IsWritable")
                    || string.Equals(TryStr(info, "AccessMode"), "ReadWrite", StringComparison.OrdinalIgnoreCase))
                {
                    canWrite.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("GetAttributes: " + Innermost(ex));
        }

        foreach (var name in names)
        {
            if (IsChurnAttribute(name))
            {
                continue;
            }

            var value = TryAttribute(obj, name);
            if (value is not null)
            {
                values[name] = value;
            }
        }

        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["names"] = names.ToList(),
            ["values"] = values,
            ["canWrite"] = canWrite.ToList(),
            ["valueCount"] = values.Count,
        };
        if (errors.Count > 0)
        {
            rec["errors"] = errors;
        }

        return rec;
    }

    private static SortedDictionary<string, object?> ProbeServices(
        object item, SortedDictionary<string, object?> summary)
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var typeName in ServiceTypeNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            var svc = GetService(item, typeName);
            if (svc is null)
            {
                rec[typeName] = "missing";
                continue;
            }

            rec[typeName] = DescribeService(svc);
            if (typeName.EndsWith("SoftwareContainer", StringComparison.Ordinal))
            {
                summary["softwareContainers"] = (int)summary["softwareContainers"]! + 1;
            }
        }

        return rec;
    }

    private static SortedDictionary<string, object?> DescribeService(object svc)
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = svc.GetType().Name,
            ["typeFull"] = svc.GetType().FullName ?? svc.GetType().Name,
        };
        var props = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in svc.GetType().GetProperties()
                     .Where(p => p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (p.Name is "Parent" or "Project" or "Device" or "DeviceItem")
            {
                continue;
            }

            props[p.Name] = Scalar(TryGet(p, svc));
        }

        rec["properties"] = props;
        rec["methods"] = MethodNames(svc);

        var software = TryProp(svc, "Software");
        if (software is not null)
        {
            rec["softwareType"] = software.GetType().Name;
            rec["softwareName"] = TryStr(software, "Name") ?? "";
        }

        return rec;
    }

    private static List<SortedDictionary<string, object?>> ProbePorts(
        object item, SortedDictionary<string, object?> summary)
    {
        var ports = new List<SortedDictionary<string, object?>>();
        foreach (var typeName in new[]
                 {
                     "Siemens.Engineering.HW.Features.NetworkPort",
                     "Siemens.Engineering.HW.Features.NetworkInterface",
                 })
        {
            var svc = GetService(item, typeName);
            if (svc is null)
            {
                continue;
            }

            ports.AddRange(PortsFrom(svc, typeName, summary));
        }

        foreach (var composition in new[] { "Ports", "NetworkPorts", "IoConnectors", "Nodes" })
        {
            foreach (var port in Enumerate(TryProp(item, composition)))
            {
                ports.Add(DescribePort(port, composition, summary));
            }
        }

        return ports
            .GroupBy(p => (string)(p["name"] ?? "") + "|" + (string)(p["source"] ?? ""))
            .Select(g => g.First())
            .OrderBy(p => (string)(p["name"] ?? ""), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<SortedDictionary<string, object?>> PortsFrom(
        object svc, string source, SortedDictionary<string, object?> summary)
    {
        var ports = new List<SortedDictionary<string, object?>>();
        ports.Add(DescribePort(svc, source, summary));
        foreach (var composition in new[]
                 {
                     "Ports", "NetworkPorts", "ConnectedPorts", "PartnerPorts",
                     "IoConnectors", "Nodes", "InterfacePorts",
                 })
        {
            foreach (var port in Enumerate(TryProp(svc, composition)))
            {
                ports.Add(DescribePort(port, source + "." + composition, summary));
            }
        }

        return ports;
    }

    private static SortedDictionary<string, object?> DescribePort(
        object port, string source, SortedDictionary<string, object?> summary)
    {
        summary["ports"] = (int)summary["ports"]! + 1;
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = source,
            ["name"] = TryStr(port, "Name") ?? TryStr(port, "InterfaceName") ?? port.GetType().Name,
            ["type"] = port.GetType().Name,
            ["partner"] = PartnerOf(port),
        };
        var props = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in port.GetType().GetProperties()
                     .Where(p => p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (p.Name is "Parent" or "Project")
            {
                continue;
            }

            var value = Scalar(TryGet(p, port));
            if (value is not null)
            {
                props[p.Name] = value;
            }
        }

        rec["properties"] = props;
        if (rec["partner"] is string partner && partner.Length > 0)
        {
            summary["portsWithPartner"] = (int)summary["portsWithPartner"]! + 1;
        }

        return rec;
    }

    private static string PartnerOf(object port)
    {
        foreach (var name in new[]
                 {
                     "PartnerPort", "ConnectedPort", "Partner", "ConnectedTo",
                     "RemotePort", "RemoteInterface",
                 })
        {
            var value = TryProp(port, name);
            if (value is null)
            {
                continue;
            }

            var labelled = Label(value);
            if (labelled.Length > 0 &&
                !labelled.Equals(port.GetType().Name, StringComparison.Ordinal))
            {
                return labelled;
            }
        }

        foreach (var composition in new[] { "ConnectedPorts", "PartnerPorts" })
        {
            var partners = Enumerate(TryProp(port, composition))
                .Select(Label)
                .Where(s => s.Length > 0)
                .ToList();
            if (partners.Count > 0)
            {
                return string.Join("; ", partners);
            }
        }

        var attr = TryAttribute(port, "ConnectedTo")
                   ?? TryAttribute(port, "Partner")
                   ?? TryAttribute(port, "PartnerPort");
        return attr ?? "";
    }

    private static string Label(object obj)
    {
        var parent = TryProp(obj, "Parent") ?? TryProp(obj, "Device") ?? TryProp(obj, "DeviceItem");
        var device = TryStr(parent, "Name");
        var port = TryStr(obj, "Name") ?? TryStr(obj, "InterfaceName");
        if (!string.IsNullOrEmpty(device) && !string.IsNullOrEmpty(port))
        {
            return device + " / " + port;
        }

        return port ?? device ?? obj.GetType().Name;
    }

    private static List<SortedDictionary<string, object?>> ProbeSubnets(object project)
    {
        var rows = new List<SortedDictionary<string, object?>>();
        foreach (var subnet in Enumerate(TryProp(project, "Subnets"))
                     .OrderBy(s => TryStr(s, "Name") ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var rec = ObjectBasics(subnet);
            rec["methods"] = MethodNames(subnet);
            rec["writeMethods"] = WriteMethodNames(subnet);
            var nodes = new List<SortedDictionary<string, object?>>();
            foreach (var node in Enumerate(TryProp(subnet, "Nodes") ?? TryProp(subnet, "NetworkNodes")))
            {
                nodes.Add(new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = TryStr(node, "Name") ?? "",
                    ["type"] = node.GetType().Name,
                    ["nodeId"] = TryStr(node, "NodeId") ?? "",
                    ["nodeType"] = TryStr(node, "NodeType") ?? "",
                    ["parent"] = Label(TryProp(node, "Parent") ?? TryProp(node, "OwnedBy") ?? node),
                    ["connectedSubnet"] = Scalar(TryProp(node, "ConnectedSubnet")),
                });
            }

            rec["nodes"] = nodes
                .OrderBy(n => (string)(n["name"] ?? ""), StringComparer.OrdinalIgnoreCase)
                .ToList();
            rec["ioSystems"] = NamedList(TryProp(subnet, "IoSystems") ?? TryProp(subnet, "IOSystems"));
            rows.Add(rec);
        }

        return rows;
    }

    private static List<SortedDictionary<string, object?>> ProbeNamedComposition(object project, string name)
    {
        var rows = new List<SortedDictionary<string, object?>>();
        foreach (var item in Enumerate(TryProp(project, name))
                     .OrderBy(s => TryStr(s, "Name") ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var rec = ObjectBasics(item);
            rec["methods"] = MethodNames(item);
            rec["children"] = NamedList(
                TryProp(item, "Devices") ?? TryProp(item, "Groups") ?? TryProp(item, "Items"));
            rows.Add(rec);
        }

        return rows;
    }

    private static List<string> NamedList(object? composition)
    {
        var names = new List<string>();
        foreach (var item in Enumerate(composition))
        {
            names.Add(Label(item));
            if (names.Count == 12)
            {
                names.Add("...");
                break;
            }
        }

        return names;
    }

    private static SortedDictionary<string, object?> ProbeWriteApis(
        List<SortedDictionary<string, object?>> devices)
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (device["writeMethods"] is List<string> methods)
            {
                foreach (var m in methods)
                {
                    seen.Add("Device." + m);
                }
            }

            if (device["items"] is not List<SortedDictionary<string, object?>> items)
            {
                continue;
            }

            foreach (var item in items)
            {
                if (item["writeMethods"] is List<string> itemMethods)
                {
                    foreach (var m in itemMethods)
                    {
                        seen.Add("DeviceItem." + m);
                    }
                }
            }
        }

        rec["methodsSeen"] = seen.OrderBy(s => s, StringComparer.Ordinal).ToList();
        rec["createWithItem"] = seen.Contains("Device.CreateWithItem") ? "present" : "missing";
        rec["plugNew"] = seen.Any(s => s.IndexOf(".PlugNew", StringComparison.Ordinal) >= 0)
            ? "present" : "missing";
        rec["unplug"] = seen.Any(s => s.IndexOf(".Unplug", StringComparison.Ordinal) >= 0)
            ? "present" : "missing";
        rec["setAttribute"] = seen.Any(s => s.IndexOf(".SetAttribute", StringComparison.Ordinal) >= 0)
            ? "present" : "missing";
        rec["import"] = seen.Any(s => s.EndsWith(".Import", StringComparison.Ordinal))
            ? "present" : "missing";
        rec["export"] = seen.Any(s => s.EndsWith(".Export", StringComparison.Ordinal))
            ? "present" : "missing";
        return rec;
    }

    private static SortedDictionary<string, object?> TryWriteAndRestore(object project)
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attempted"] = false,
            ["applied"] = false,
            ["restored"] = false,
            ["saved"] = false,
            ["detail"] = "no writable Comment on a DeviceItem",
        };

        object? target = null;
        string? original = null;
        foreach (var device in Enumerate(GetProp(project, "Devices")))
        {
            foreach (var item in WalkDeviceItems(device))
            {
                original = TryAttribute(item, "Comment") ?? TryStr(item, "Comment") ?? "";
                if (!CanSetAttribute(item, "Comment"))
                {
                    continue;
                }

                target = item;
                break;
            }

            if (target is not null)
            {
                break;
            }
        }

        if (target is null)
        {
            return rec;
        }

        rec["attempted"] = true;
        rec["target"] = TryStr(target, "Name") ?? target.GetType().Name;
        const string marker = "versio-hwprobe-temp";
        try
        {
            SetAttribute(target, "Comment", marker);
            var read = TryAttribute(target, "Comment") ?? TryStr(target, "Comment");
            rec["applied"] = string.Equals(read, marker, StringComparison.Ordinal);
            rec["readBack"] = read ?? "";
        }
        catch (Exception ex)
        {
            rec["applied"] = false;
            rec["detail"] = Innermost(ex);
            return rec;
        }

        try
        {
            SetAttribute(target, "Comment", original ?? "");
            var restored = TryAttribute(target, "Comment") ?? TryStr(target, "Comment");
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

        return rec;
    }

    private static bool CanSetAttribute(object obj, string name)
    {
        try
        {
            var getInfos = obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0)
                ?? obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetAttributes" && m.GetParameters().Length == 0);
            foreach (var info in Enumerate(getInfos?.Invoke(obj, null)))
            {
                if (!string.Equals(TryStr(info, "Name"), name, StringComparison.Ordinal))
                {
                    continue;
                }

                return TryBool(info, "CanWrite") || TryBool(info, "IsWritable")
                    || string.Equals(TryStr(info, "AccessMode"), "ReadWrite", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Fall through: try SetAttribute anyway only when the method exists.
        }

        return WriteMethodNames(obj).Contains("SetAttribute");
    }

    private static void SetAttribute(object obj, string name, object value)
    {
        var method = obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "SetAttribute"
                                 && m.GetParameters().Length == 2);
        if (method is null)
        {
            throw new InvalidOperationException("SetAttribute missing");
        }

        method.Invoke(obj, [name, value]);
    }

    private static SortedDictionary<string, object?> ProbeServiceTypePresence()
    {
        var rec = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in ServiceTypeNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            rec[name] = FindType(name) is null ? "type-missing" : "type-present";
        }

        return rec;
    }

    private static List<string> DiscoverHwTypes()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var t in types)
            {
                if (t.FullName is null)
                {
                    continue;
                }

                if (t.FullName.StartsWith("Siemens.Engineering.HW", StringComparison.Ordinal)
                    || t.FullName.IndexOf(".HW.Features.", StringComparison.Ordinal) >= 0)
                {
                    names.Add(t.FullName);
                }
            }
        }

        return [.. names];
    }

    private static List<string> CompositionNames(object obj) =>
        obj.GetType().GetProperties()
            .Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static List<string> MethodNames(object obj) =>
        obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static List<string> WriteMethodNames(object obj) =>
        MethodNames(obj)
            .Where(n => n is "CreateWithItem" or "CreateFrom" or "PlugNew" or "PlugCopy"
                or "PlugMove" or "CanPlugNew" or "PlugNewFromGsd" or "Unplug"
                or "SetAttribute" or "SetAttributes" or "Import" or "Export" or "Delete"
                or "ConnectToPort" or "DisconnectFromPort")
            .ToList();

    private static string? TryAttribute(object obj, string name)
    {
        try
        {
            var method = obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetAttribute"
                                     && m.GetParameters().Length == 1);
            var value = method?.Invoke(obj, [name]);
            return Scalar(value)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryStr(object? target, string name)
    {
        try
        {
            return target is null ? null : GetProp(target, name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static object? TryProp(object? target, string name)
    {
        try
        {
            return target is null ? null : GetProp(target, name);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryGet(PropertyInfo p, object target)
    {
        try
        {
            return p.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryBool(object target, string name)
    {
        try
        {
            return GetProp(target, name) is true;
        }
        catch
        {
            return false;
        }
    }

    private static int? TryInt(SortedDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null)
        {
            return null;
        }

        return int.TryParse(v.ToString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    private static object? Scalar(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s.Length > 240 ? s.Substring(0, 240) + "..." : s;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal:
                return value;
            case Enum:
                return value.ToString();
            case IEnumerable enumerable when value is not string:
            {
                var labels = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    labels.Add(Label(item));
                    if (labels.Count == 8)
                    {
                        labels.Add("...");
                        break;
                    }
                }

                return labels.Count == 0 ? "collection:0" : string.Join("; ", labels);
            }
            default:
            {
                var name = TryStr(value, "Name");
                var type = value.GetType().Name;
                return string.IsNullOrEmpty(name) ? type : type + ":" + name;
            }
        }
    }

    private static bool IsChurnAttribute(string name) =>
        name.IndexOf("Date", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("Time", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("Stamp", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("Modified", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("Created", StringComparison.OrdinalIgnoreCase) >= 0;
}
