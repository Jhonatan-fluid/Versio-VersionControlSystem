# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS. No f-strings.
#
# Read-only probe of the device / I/O surface. Libraries were measured this way
# before versio_libraries.py was written: the obvious names were wrong
# (libman.get_libraries() returns strings). Device YAML is the same job, and
# ScriptDeviceObject has never been called on this install.
#
# Lives in tools/ so it is not embedded into the shipping assembly.
#
# job.json: { "project": "<abs path>", "out": "<abs path to write probe.json>" }

import io
import json
import os
import sys
import traceback

import scriptengine  # type: ignore


DEVICE = "225bfe47-7336-4dbc-9419-4105a7c831fa"
PLC_LOGIC = "40b404f9-e5dc-42c6-907f-c89f4a517386"

# Names the founding plan and the ScriptEngine docs suggest. Each is tried;
# the interesting result is which exist, which raise, and what they return.
DEVICE_MEMBERS = [
    "parameters",
    "device_parameters",
    "get_all_parameters",
    "get_parameters",
    "connectors",
    "plugin",
    "device_identification",
    "identification",
    "driver_info",
    "io_mapping",
    "io_config",
    "is_simulated",
    "enable_simulation",
    "allow_simulation",
]

CONNECTOR_MEMBERS = [
    "id", "connector_id", "interface", "role", "module_type",
    "host_parameter_set", "host_parameters", "driver_info",
    "visible_name", "name", "modules", "is_explicit",
]

DRIVER_MEMBERS = [
    "required_libs", "bus_cycle_task", "needs_bus_cycle",
    "io_app", "always_mapping", "create_bit_channels",
]

PARAM_MEMBERS = [
    "Identifier", "VisibleName", "Value", "HasSubElements",
    "name", "value", "id", "type", "unit", "description",
    "index", "subindex", "channel", "is_explicit", "default",
]

ELEMENT_MEMBERS = [
    "Identifier", "VisibleName", "Value", "name", "value",
    "bit_offset", "bit_size", "mapping", "variable", "channel",
]


def emit(obj):
    print("@@VERSIO@@" + json.dumps(obj))


def safe(fn, default=None):
    try:
        return fn()
    except Exception:
        return default


def describe(fn):
    try:
        return {"ok": True, "value": plain(fn())}
    except Exception as e:
        return {"ok": False, "error": type(e).__name__, "message": str(e)}


def plain(v, depth=0):
    if v is None or isinstance(v, (bool, int, float)):
        return v
    if depth > 3:
        return repr(v)
    try:
        if isinstance(v, (str, unicode)):  # noqa: F821
            return v
    except NameError:
        if isinstance(v, str):
            return v
    try:
        n = len(v)
        if n > 40:
            return {"len": n, "sample": [plain(x, depth + 1) for x in list(v)[:8]]}
        return [plain(x, depth + 1) for x in v]
    except Exception:
        pass
    try:
        return unicode(v)  # noqa: F821
    except Exception:
        return repr(v)


def type_guid(obj):
    g = safe(lambda: str(obj.type), "")
    return g.lower() if g else ""


def walk(obj, path, into_plc_logic, devices, unknowns):
    name = safe(lambda: obj.get_name(), "?")
    guid = safe(lambda: str(obj.guid), "")
    tg = type_guid(obj)
    here = path + "/" + name if path else name
    children = safe(lambda: list(obj.get_children()), []) or []

    node = {
        "path": here,
        "name": name,
        "guid": guid,
        "type": tg,
        "childCount": len(children),
        "childNames": [safe(lambda c=child: c.get_name(), "?") for child in children],
        "index": safe(lambda: obj.index, None),
        "dir": [n for n in dir(obj) if not n.startswith("_")][:80],
    }

    if tg == DEVICE or (path.startswith("Device") and tg != PLC_LOGIC and not into_plc_logic):
        node["members"] = probe_members(obj)
        devices.append(node)
    elif tg and tg not in (
        DEVICE, PLC_LOGIC,
        "6f9dac99-8de1-4efc-8465-68ac443b7d08",  # POU
        "738bea1e-99bb-4f04-90bb-a7a567e74e3a",  # FOLDER
        "639b491f-5557-464c-af91-1471bac9f549",  # APPLICATION
        "adb5cb65-8e1d-4a00-b70a-375ea27582f3",  # LIBRARY_MANAGER
        "ae1de277-a207-4a28-9efb-456c06bd52f3",  # TASK_CONFIGURATION
        "f18bec89-9fef-401d-9953-2f11739a6808",  # VISUALISATION
    ) and "Device/" in (path + "/") and "Plc Logic" not in here:
        node["members"] = probe_members(obj)
        unknowns.append(node)

    next_plc = into_plc_logic or tg == PLC_LOGIC
    for child in children:
        walk(child, here, next_plc, devices, unknowns)


def probe_members(obj):
    out = {}
    for name in DEVICE_MEMBERS:
        attr = getattr(obj, name, None)
        if attr is None and not hasattr(obj, name):
            out[name] = {"ok": False, "error": "missing"}
            continue
        if callable(attr):
            out[name] = describe(lambda a=attr: a())
        else:
            out[name] = describe(lambda a=attr: a)
    # Parameter walk when a collection appeared.
    params = None
    for key in ("parameters", "device_parameters"):
        hit = out.get(key)
        if hit and hit.get("ok"):
            params = getattr(obj, key, None)
            break
    if params is not None:
        out["parameterSample"] = sample_parameters(params)
    connectors = getattr(obj, "connectors", None)
    if connectors is not None:
        out["connectorSample"] = sample_connectors(connectors)
    info = getattr(obj, "driver_info", None)
    if info is not None:
        out["driverInfoSample"] = probe_named(info, DRIVER_MEMBERS)
    getter = out.get("get_all_parameters")
    if getter and getter.get("ok"):
        try:
            out["allParameterSample"] = sample_parameters(obj.get_all_parameters())
        except Exception as e:
            out["allParameterSample"] = {"ok": False, "error": str(e)}
    return out


def probe_named(target, names):
    out = {}
    for name in names:
        attr = getattr(target, name, None)
        if attr is None and not hasattr(target, name):
            out[name] = {"ok": False, "error": "missing"}
            continue
        if callable(attr):
            out[name] = describe(lambda a=attr: a())
        else:
            out[name] = describe(lambda a=attr: a)
    return out


def sample_connectors(connectors):
    rows = []
    try:
        seq = list(connectors)[:6]
    except Exception as e:
        return {"ok": False, "error": str(e)}
    for c in seq:
        row = probe_named(c, CONNECTOR_MEMBERS)
        hosts = getattr(c, "host_parameters", None)
        if hosts is not None:
            row["hostParameterSample"] = sample_host_parameters(hosts)
        rows.append(row)
    return {"ok": True, "countTried": len(seq), "rows": rows}


def sample_host_parameters(hosts):
    """Every host parameter, compact: name/id/value plus DataElement groups.

    Truncating here is how a CycleTime sitting past the first dozen would never
    reach the YAML schema. Duplicate names and reminted ids are the two facts the
    two-run compare needs the full list for.
    """
    rows = []
    try:
        seq = list(hosts)
    except Exception as e:
        return {"ok": False, "error": str(e)}
    groups = 0
    names = []
    for item in seq:
        kind_name = type(item).__name__
        if kind_name == "list" or "DataElement" in kind_name:
            groups += 1
            try:
                inner = list(item)
            except Exception:
                inner = [item]
            elements = []
            for el in inner[:24]:
                elements.append({
                    "name": safe(lambda e=el: e.name, None),
                    "value": safe(lambda e=el: _text(e.value), None),
                    "channel": safe(lambda e=el: e.channel, None),
                    "variable": safe(lambda e=el: _text(e.variable), None),
                    "mapping": safe(lambda e=el: _text(e.mapping), None),
                })
            rows.append({
                "kind": "group",
                "typeName": kind_name,
                "count": len(inner),
                "elements": elements,
            })
            continue
        name = safe(lambda p=item: p.name, None)
        names.append(name)
        rows.append({
            "kind": "param",
            "typeName": kind_name,
            "name": name,
            "id": safe(lambda p=item: _text(p.id), None),
            "value": safe(lambda p=item: _text(p.value), None),
            "unit": safe(lambda p=item: _text(p.unit), None),
            "description": safe(lambda p=item: _text(p.description), None),
        })
    dupes = sorted(set(n for n in names if n and names.count(n) > 1))
    return {
        "ok": True,
        "total": len(seq),
        "groups": groups,
        "duplicateNames": dupes,
        "rows": rows,
    }


def _text(value):
    if value is None:
        return None
    try:
        return unicode(value)  # noqa: F821
    except Exception:
        try:
            return str(value)
        except Exception:
            return None


def sample_parameters(params):
    rows = []
    try:
        seq = list(params)[:12]
    except Exception as e:
        return {"ok": False, "error": str(e)}
    for p in seq:
        rows.append({
            "dir": [n for n in dir(p) if not n.startswith("_")][:40],
            "Identifier": describe(lambda: p.Identifier),
            "VisibleName": describe(lambda: p.VisibleName),
            "Value": describe(lambda: p.Value),
            "HasSubElements": describe(lambda: p.HasSubElements),
            "name": describe(lambda: p.name),
            "value": describe(lambda: p.value),
        })
    return {"ok": True, "countTried": len(seq), "rows": rows}


def main():
    args = sys.argv[1:]
    if not args:
        emit({"t": "fatal", "msg": "no job file"})
        return 2
    with io.open(args[0], "r", encoding="utf-8") as f:
        job = json.load(f)
    project = job.get("project")
    out = job.get("out")
    if not project or not os.path.isfile(project):
        emit({"t": "fatal", "msg": "project missing: " + unicode(project)})  # noqa: F821
        return 3

    emit({"t": "info", "msg": "opening", "project": project})
    proj = scriptengine.projects.open(project)
    devices = []
    unknowns = []
    try:
        walk(proj, "", False, devices, unknowns)
        report = {
            "project": project,
            "deviceCount": len(devices),
            "unknownIoCount": len(unknowns),
            "devices": devices,
            "unknownIo": unknowns,
        }
        if out:
            folder = os.path.dirname(out)
            if folder and not os.path.isdir(folder):
                os.makedirs(folder)
            with io.open(out, "w", encoding="utf-8", newline="\n") as f:
                f.write(unicode(json.dumps(report, indent=2, sort_keys=True)))  # noqa: F821
            emit({"t": "info", "msg": "wrote", "out": out})
        emit({"t": "probe_result", "id": "devices", "title": "device/IO surface",
              "result": {
                  "deviceCount": len(devices),
                  "unknownIoCount": len(unknowns),
                  "devicePaths": [d["path"] for d in devices],
                  "unknownPaths": [u["path"] for u in unknowns],
              }})
        emit({"t": "done", "ok": "true"})
        return 0
    except Exception:
        emit({"t": "fatal", "msg": traceback.format_exc()})
        return 1
    finally:
        try:
            proj.close()
        except Exception:
            pass


if __name__ == "__main__":
    sys.exit(main())
