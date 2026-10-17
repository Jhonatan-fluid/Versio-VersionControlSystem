# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS. No f-strings.
#
# Read-only probe: what does ScriptEngine's library reference `.parameters` report,
# and can it tell a value the project stores from the resolved library's own default?
#
# Asked on 2026-10-16. A Patch 4 export of RefrigeratorControl wrote MAX_CLIENTS: "20"
# on #System_VisuElemXYChart. The Patch 2 export did not, and neither does the File-Based
# Storage copy of the same project, which Patch 4 saved and which stores only
# GC_POINTS_PER_POLYGON under libraryParameters. So either ScriptEngine reports defaults,
# or the project really holds the value and FBS drops it.
#
# Lives in tools/ so it is not embedded into the shipping assembly. Never saves.
#
# job.json: { "project": "<abs path>", "out": "<abs path to write probe.json>" }

import io
import json
import sys
import traceback

import scriptengine  # type: ignore

LIBRARY_MANAGER = "adb5cb65-8e1d-4a00-b70a-375ea27582f3"

# The references worth looking at closely: the one that gained a parameter, the one
# that has always had one, and Util, whose IBLOCKSIZE the exporter's docstring cites.
FOCUS = ("VisuElemXYChart", "VisuElemBase", "Util")


def emit(obj):
    print("@@VERSIO@@" + json.dumps(obj))


def safe(fn, default=None):
    try:
        return fn()
    except Exception as e:  # noqa
        return default


def err(fn):
    try:
        fn()
        return None
    except Exception as e:  # noqa
        return "%s: %s" % (type(e).__name__, e)


def text(v):
    """A string json can always encode: non-ASCII escaped as \uXXXX.

    IronPython strings are all `str`, and its json encoder decodes any `str` as UTF-8
    before encoding it, so one German 0xDF in a CODESYS string (seen on the first run)
    raised mid-dump. Escaping here keeps every value ASCII before json sees it.
    """
    if v is None:
        return None
    try:
        s = unicode(v)  # noqa
    except Exception:
        s = repr(v)
    return u"".join(c if ord(c) < 128 else u"\\u%04x" % ord(c) for c in s)


def public(obj):
    return sorted(n for n in (safe(lambda: dir(obj), []) or []) if not n.startswith("_"))


def type_guid(obj):
    g = safe(lambda: str(obj.type), "")
    return g.lower() if g else ""


def find_libmans(obj, path, out):
    name = safe(lambda: obj.get_name(), "?")
    here = path + "/" + name if path else name
    if type_guid(obj) == LIBRARY_MANAGER:
        out.append((here, obj))
    for child in safe(lambda: list(obj.get_children()), []) or []:
        find_libmans(child, here, out)


def net_props(obj, depth=0, seen=None):
    """Every .NET property and field of an object, public or not, one level of values.

    ScriptLibraryParameters exposes only GetEnumerator to python. The Library Manager
    dialog shows a Value and a Default value per parameter, so the object it wraps
    should hold both; reflection is the only way to see them from a script.
    """
    import System  # type: ignore
    from System.Reflection import BindingFlags  # type: ignore
    flags = (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    seen = seen if seen is not None else set()
    out = {}
    t = safe(lambda: obj.GetType())
    if t is None:
        return {"<no GetType>": text(obj)}
    out["<type>"] = text(t.FullName)
    for p in safe(lambda: list(t.GetProperties(flags)), []) or []:
        if safe(lambda: p.GetIndexParameters().Length, 1) != 0:
            continue
        v = safe(lambda: p.GetValue(obj, None), "<raised>")
        out["P:" + p.Name] = text(v)
    for f in safe(lambda: list(t.GetFields(flags)), []) or []:
        v = safe(lambda: f.GetValue(obj), "<raised>")
        out["F:" + f.Name] = text(v)
        if depth < 2 and v is not None and id(v) not in seen and not isinstance(v, (str, unicode, int, bool)):  # noqa
            seen.add(id(v))
            out["F:" + f.Name + "{}"] = net_props(v, depth + 1, seen)
    return out


def reflect_get(obj, name, strict=False):
    """A property by name, public or not, walking up the base types."""
    from System.Reflection import BindingFlags  # type: ignore
    flags = (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    t = obj.GetType()
    while t is not None:
        p = t.GetProperty(name, flags)
        if p is not None:
            return p.GetValue(obj, None)
        t = t.BaseType
    if strict:
        raise AttributeError("no property %s on %s" % (name, obj.GetType().FullName))
    return None


def param_surface(item):
    """Everything about parameters on the CODESYS LibMan item behind a reference.

    Members whose name mentions Param, from the concrete type and every interface it
    implements, with values for properties. Then each element of any enumerable that
    turns up, reflected one level, which is where Value / DefaultValue would show.
    """
    from System.Reflection import BindingFlags  # type: ignore
    flags = (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    t = item.GetType()
    types = [t] + list(t.GetInterfaces())
    out = {"<type>": text(t.FullName), "members": {}, "fields": {}}

    # Private fields are per declaring type, so walk the bases: a stored override list
    # would live on whichever class declares it.
    from System.Reflection import BindingFlags as BF  # type: ignore
    decl = BF.Instance | BF.Public | BF.NonPublic | BF.DeclaredOnly
    bt = t
    while bt is not None:
        for f in safe(lambda: list(bt.GetFields(decl)), []) or []:
            v = safe(lambda: f.GetValue(item), "<raised>")
            rec = {"type": text(f.FieldType.FullName), "value": text(v)}
            elems = []
            for el in safe(lambda: list(v), []) if not isinstance(v, (str, unicode)) else []:  # noqa
                elems.append({"repr": text(el), "net": safe(lambda: net_props(el, 1), "<raised>")})
                if len(elems) >= 10:
                    break
            if elems:
                rec["elements"] = elems
            out["fields"][bt.Name + "." + f.Name] = rec
        bt = bt.BaseType

    # The two places a value could come from instead: the Library Manager that owns the
    # item (File-Based Storage keeps libraryParameters there, keyed by placeholder), and
    # the resolved library itself, whose declared parameters carry the defaults.
    def fields_of(obj, want=None):
        res = {}
        bt2 = obj.GetType()
        while bt2 is not None:
            for f in safe(lambda: list(bt2.GetFields(decl)), []) or []:
                if want and not any(w in f.Name.lower() for w in want):
                    continue
                v = safe(lambda: f.GetValue(obj), "<raised>")
                rec = {"type": text(f.FieldType.FullName), "value": text(v)}
                elems = []
                for el in safe(lambda: list(v), []) if v is not None else []:
                    elems.append({"repr": text(el), "net": safe(lambda: net_props(el, 1), "<raised>")})
                    if len(elems) >= 40:
                        break
                if elems:
                    rec["elements"] = elems
                res[bt2.Name + "." + f.Name] = rec
            bt2 = bt2.BaseType
        return res

    root = safe(lambda: reflect_get(item, "RootLibManObject")) or safe(
        lambda: [f for f in [item.GetType().BaseType.GetField("_rootLibManObject", decl)] if f][0].GetValue(item))
    if root is not None:
        out["root"] = {"<type>": text(root.GetType().FullName),
                       "fields": fields_of(root, ("param", "value", "override"))}
        # The stored table itself: placeholder -> {parameter -> expression}.
        bt3 = root.GetType()
        table = None
        while bt3 is not None and table is None:
            f = bt3.GetField("_parameterValues", decl)
            if f is not None:
                table = f.GetValue(root)
            bt3 = bt3.BaseType
        if table is not None:
            stored = {}
            for kv in safe(lambda: list(table), []) or []:
                inner = kv.Value
                stored[text(kv.Key)] = dict(
                    (text(k), text(safe(lambda: inner[k])))
                    for k in safe(lambda: list(inner.Keys), []) or [])
            out["stored_parameter_values"] = stored
    lib = safe(lambda: reflect_get(item, "EffectiveResolution"))
    if lib is not None:
        out["library"] = {"<type>": text(lib.GetType().FullName),
                          "repr": text(lib),
                          "fields": fields_of(lib, ("param", "default", "value")),
                          "param_members": [text(m) for m in safe(lambda: list(lib.GetType().GetMembers(flags)), []) or []
                                            if "param" in m.Name.lower()]}

    # And what the no-argument lookup answers, value by value.
    pv = safe(lambda: item.GetParameterValues())
    if pv is not None:
        out["GetParameterValues()"] = dict(
            (text(k), text(safe(lambda: pv[k]))) for k in safe(lambda: list(pv.Keys), []) or [])
    for ty in types:
        for m in safe(lambda: list(ty.GetMembers(flags)), []) or []:
            if "param" not in m.Name.lower():
                continue
            key = "%s.%s (%s)" % (ty.Name, m.Name, m.MemberType)
            rec = {}
            if text(m.MemberType) == "Property" and safe(lambda: m.GetIndexParameters().Length, 1) == 0:
                v = safe(lambda: m.GetValue(item, None), "<raised>")
                rec["value"] = text(v)
                elems = []
                for el in safe(lambda: list(v), []) or []:
                    elems.append({"repr": text(el), "net": safe(lambda: net_props(el, 1), "<raised>")})
                    if len(elems) >= 10:
                        break
                if elems:
                    rec["elements"] = elems
            elif text(m.MemberType) == "Method":
                rec["signature"] = text(m)
            out["members"][key] = rec
    return out


def describe_parameters(params):
    rec = {
        "type": text(safe(lambda: type(params).__name__)),
        "members": public(params),
        "count": safe(lambda: len(params)),
        "net": safe(lambda: net_props(params), "<raised>"),
        "items": [],
    }
    # What the enumerator yields, as objects rather than names.
    elems = []
    e = safe(lambda: params.GetEnumerator())
    while e is not None and safe(lambda: e.MoveNext(), False):
        cur = safe(lambda: e.Current)
        elems.append({"repr": text(cur), "net": safe(lambda: net_props(cur), "<raised>")})
    rec["enumerated"] = elems
    for name in safe(lambda: list(params), []) or []:
        item = {"name": text(name), "value": text(safe(lambda: params[name]))}
        # Anything that might say "default" or "explicitly set".
        for probe in ("get_default", "default", "is_default", "is_overwritten",
                      "is_set", "contains", "has_value"):
            member = safe(lambda: getattr(params, probe))
            if member is None:
                continue
            if callable(member):
                item[probe] = text(safe(lambda: member(name), "<raised>"))
            else:
                item[probe] = text(member)
        rec["items"].append(item)
    return rec


def describe_reference(ref):
    rec = {
        "name": text(safe(lambda: ref.name)),
        "namespace": text(safe(lambda: ref.namespace)),
        "is_placeholder": safe(lambda: bool(ref.is_placeholder)),
        "is_managed": safe(lambda: bool(ref.is_managed)),
        "is_resolved": text(safe(lambda: ref.is_resolved)),
        "effective_resolution": text(safe(lambda: ref.effective_resolution)),
        "default_resolution": text(safe(lambda: ref.default_resolution)),
        "members": public(ref),
    }
    params = safe(lambda: ref.parameters)
    rec["parameters_error"] = err(lambda: ref.parameters)
    item = safe(lambda: reflect_get(ref, "MyItem"))
    rec["item_lookup"] = text(item) if item is not None else err(lambda: reflect_get(ref, "MyItem", strict=True))
    if item is not None and params is not None and safe(lambda: len(params), 0):
        rec["item"] = safe(lambda: param_surface(item), "<raised>")
    if params is not None:
        rec["parameters"] = describe_parameters(params)

    # The resolved library, if ScriptEngine exposes one: its own parameter list is what a
    # default would come from.
    for attr in ("managed_library", "library", "resolved_library"):
        lib = safe(lambda: getattr(ref, attr))
        if lib is None:
            continue
        rec[attr] = {
            "repr": text(lib),
            "members": public(lib),
        }
        lp = safe(lambda: lib.parameters)
        if lp is not None:
            rec[attr]["parameters"] = describe_parameters(lp)
    return rec


def main():
    job = json.load(io.open(sys.argv[-1], encoding="utf-8"))
    proj = scriptengine.projects.open(job["project"])
    result = {"project": job["project"], "managers": []}
    try:
        libmans = []
        find_libmans(proj, "", libmans)
        for path, libman in libmans:
            mgr = {"path": path, "members": public(libman), "references": []}
            for ref in safe(lambda: list(libman.references), []) or []:
                r = describe_reference(ref)
                mgr["references"].append(r)
                ns = r.get("namespace") or ""
                params = (r.get("parameters") or {}).get("items") or []
                if params or any(f in ns for f in FOCUS):
                    emit({"t": "ref", "manager": path, "namespace": ns, "count": len(params)})
            result["managers"].append(mgr)
    finally:
        safe(lambda: proj.close())

    with io.open(job["out"], "w", encoding="utf-8") as f:
        f.write(unicode(json.dumps(result, indent=2)))  # noqa
    emit({"t": "done", "managers": len(result["managers"])})


try:
    main()
except Exception:
    emit({"t": "fatal", "msg": traceback.format_exc()})
    sys.exit(1)
