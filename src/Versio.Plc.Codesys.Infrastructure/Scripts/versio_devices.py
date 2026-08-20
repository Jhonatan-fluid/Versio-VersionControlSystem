# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS.
#
# Reads a CODESYS device object and renders its connectors, parameters and empty
# slots as YAML.
#
# WHERE THIS SITS IN THE VENDOR SEAM: the FORMAT is vendor-neutral - every PLC
# toolchain has a device tree with parameters a reviewer diffs - and the C# side
# (Versio.Core.Projects.DeviceCatalog) reads this file without knowing CODESYS
# exists. What is vendor-specific is exactly this file: how those facts are
# obtained from one toolchain's scripting API.
#
# This module imports no scriptengine. It is handed objects and a `safe` caller, so
# the shaping rules can be exercised outside CODESYS.
#
# ---------------------------------------------------------------------------------
# MEASURED on CODESYS 3.5.22.20 (SP22 Patch 2), RefrigeratorControl.project, twice
# on an untouched copy. The two fingerprints were byte-identical. The founding
# plan named the wrong API, the same class of miss as libman.get_libraries():
#
#   obj.parameters / get_all_parameters() / Identifier / VisibleName / Value
#       missing on this install. dir() on IDispatch is empty; names must be tried.
#   obj.device_parameters
#       exists, always []. The payload is connector.host_parameters.
#   obj.io_mapping
#       missing. Coupler host_parameters are 193 DeviceParameter / Compound
#       values, not a separate mapping collection (groups: 0). Compound values
#       travel as their `value` string.
#   connector.host_parameters[].name / .id / .value
#       this is the one. EtherCAT master's Common.Ethercat connector has 55,
#       including MasterCycleTime=4000. Ids were STABLE across the two opens,
#       unlike library ref.id which remints. Duplicate names are real (coupler
#       "---", "Cycle Time", eight IO-Link port states), so a name that collides
#       is keyed name#id. Unique names stay as the name.
#   empty IO-Link slots, type 085766fd-043e-4545-8e8d-d651d56d5d3b
#       eight siblings all named <Empty>, all connector_id 1. obj.index is 0..7
#       and matches get_children() order. Native export collided them onto one
#       %3CEmpty%3E.xml; they are listed here instead of written as files.
#   device_identification / identification / driver_info members we tried
#       missing. Which .device description a node is is not in this file.
# ---------------------------------------------------------------------------------

DEVICE_SCHEMA = 1

TOP_KEYS = ["schema", "vendor", "name", "path", "slotCount", "slots", "connectorCount"]

CONN_KEYS = [
    "connectorId",
    "interface",
    "moduleType",
    "parameters",
]

VENDOR = "codesys"

_HEADER = u"""Device tree, exported by Versio. Do not edit - it is rewritten on
every export, and importing this file back into the project is NOT supported: change
devices in the IDE.

NOT recorded here, on purpose:
  * which device description the node is (identification was missing on this
    ScriptEngine)
  * driver_info fields we could not read
  * IEC %I/%Q mapping as a separate table - it is not on io_mapping here; compound
    parameter values are recorded as their value strings
  * parameter id, except as a suffix on a duplicate name - the id is stable on this
    install but it is not what a reviewer reads"""


def read_device(obj, safe, type_of, on_error=None):
    """Shape one ScriptDeviceObject into (slots map, connector records)."""
    slots = read_slots(obj, safe, type_of, on_error)
    connectors = read_connectors(obj, safe, on_error)
    return slots, connectors


def read_slots(obj, safe, type_of, on_error=None):
    """Empty IO-Link slots under this device, keyed by CODESYS's own 0-based index.

    MEASURED: eight <Empty> children, index 0..7, matching get_children() order.
    connector_id is 1 on every one and cannot tell them apart.

    If index is unreadable, do NOT fall back to the display name: that is the
    collision this file exists to prevent. Key them unindexed-0..n instead so
    eight siblings named <Empty> stay eight entries.
    """
    slots = {}
    children = safe(lambda: list(obj.get_children()), []) or []
    unindexed = 0
    for child in children:
        kind = type_of(safe(lambda c=child: str(c.type), u""))
        if kind != u"DEVICE_SLOT":
            continue
        idx = safe(lambda c=child: c.index)
        name = _text(safe(lambda c=child: c.get_name(), u"<Empty>")) or u"<Empty>"
        if idx is not None:
            key = u"%s" % (idx,)
        else:
            if on_error is not None:
                on_error("DEVICE_SLOT_INDEX_UNREADABLE", name)
            while True:
                key = u"unindexed-%d" % unindexed
                unindexed += 1
                if key not in slots:
                    break
        slots[key] = name
    return slots


def read_connectors(obj, safe, on_error=None):
    connectors = safe(lambda: obj.connectors)
    if connectors is None:
        if on_error is not None:
            on_error("DEVICE_CONNECTORS_UNAVAILABLE", None)
        return []

    out = []
    try:
        for conn in connectors:
            rec = extract_connector(conn, safe)
            if rec:
                out.append(rec)
    except Exception as e:
        if on_error is not None:
            on_error("DEVICE_CONNECTORS_PARTIAL", str(e))
    return out


def extract_connector(conn, safe):
    rec = {}

    def put(key, value):
        if value is not None and value != u"":
            rec[key] = value

    cid = safe(lambda: conn.connector_id)
    if cid is not None:
        rec["connectorId"] = cid

    put("interface", _text(safe(lambda: conn.interface)))
    mt = safe(lambda: conn.module_type)
    if mt is not None:
        rec["moduleType"] = mt

    params = read_parameters(conn, safe)
    if params:
        rec["parameters"] = params

    return rec


def read_parameters(conn, safe):
    """host_parameters as a name -> value map.

    Duplicate names (measured on the coupler) are keyed name#id. The id is stable
    across two opens of the same untouched project on this install, which is why
    it is allowed into the key at all: library ref.id reminted and was refused.
    Unique names stay as the name, which is what a CycleTime diff should read as.
    """
    hosts = safe(lambda: conn.host_parameters)
    if hosts is None:
        return {}

    rows = []
    try:
        seq = list(hosts)
    except Exception:
        return {}

    for item in seq:
        kind = type(item).__name__
        if kind == "list" or u"DataElement" in kind:
            # Nested PDO-style groups. Not observed as host_parameters entries on
            # RefrigeratorControl (groups: 0); compound parameters carry their
            # payload in `value` instead. Skip rather than write python repr.
            continue
        name = _text(safe(lambda p=item: p.name))
        ident = _text(safe(lambda p=item: p.id))
        value = _text(safe(lambda p=item: p.value))
        if value is None:
            value = u""
        rows.append((name or u"", ident or u"", value))

    counts = {}
    for name, _, _ in rows:
        if name:
            counts[name] = counts.get(name, 0) + 1

    out = {}
    used = {}
    for name, ident, value in rows:
        if not name and not ident:
            continue
        if name and counts.get(name, 0) > 1 and ident:
            key = name + u"#" + ident
        elif name:
            key = name
        else:
            key = u"#" + ident
        # Re-check after the suffix. Three parameters named N with no id used to
        # become N, N#dup, N#dup — the third overwrote the second, three in two out,
        # no warning, and a byte-identical re-export never noticed.
        if key in used:
            n = 2
            base = key
            while key in used:
                key = u"%s#%d" % (base, n)
                n += 1
        used[key] = True
        out[key] = value
    return out


def catalog_document(yaml, name, path, slots, connectors):
    top = {
        "schema": DEVICE_SCHEMA,
        "vendor": VENDOR,
        "name": name,
        "path": path.replace("\\", "/"),
        "connectorCount": len(connectors),
    }
    if slots:
        top["slotCount"] = len(slots)
        top["slots"] = slots
    return yaml.document(_HEADER, top, TOP_KEYS, "connectors", connectors, CONN_KEYS)


def _text(value):
    if value is None:
        return None
    try:
        text = u"%s" % (value,)
    except Exception:
        return None
    return text
