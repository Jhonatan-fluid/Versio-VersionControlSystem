# -*- coding: utf-8 -*-
# REMEMBER: this runs in CODESYS ScriptEngine = IronPython 2.7
#
# Phase 0 probe. Opens a project, walks the whole object tree and emits one JSON
# line per object describing what it is and which export form it would take.
# Answers Phase 0 tasks 0.1, 0.6 and 0.10 in a single run.
#
# Usage:
#   CODESYS.exe --noUI --runscript="versio_probe.py" --scriptargs:"<job.json>"
#
# job.json: { "project": "<abs path>", "out": "<abs path to write report.json>" }

import json
import os
import sys
import traceback

import scriptengine  # type: ignore

# --- the object type GUID table -------------------------------------------------
# Adopted from greenforge-labs/codescribe (Apache-2.0), src/object_type.py.
# There is no published list; deriving it independently costs days.
GUID_TYPE_MAPPING = {
    "6f9dac99-8de1-4efc-8465-68ac443b7d08": "POU",
    "2db5746d-d284-4425-9f7f-2663a34b0ebc": "DUT",
    "ffbfa93a-b94d-45fc-a329-229860183b1d": "GVL",
    "261bd6e6-249c-4232-bb6f-84c2fbeef430": "GVL_PERSISTENT",
    "327b6465-4e7f-4116-846a-8369c730fd66": "EVC",
    "6654496c-404d-479a-aad2-8551054e5f1e": "INTERFACE",
    "98a2708a-9b18-4f31-82ed-a1465b24fa2d": "TASK",
    "f8a58466-d7f6-439f-bbb8-d4600e41d099": "METHOD",
    "f89f7675-27f1-46b3-8abb-b7da8e774ffd": "METHOD_NORET",
    "5a3b8626-d3e9-4f37-98b5-66420063d91e": "PROPERTY",
    "792f2eb6-721e-4e64-ba20-bc98351056db": "PROPERTY_METHOD",
    "8ac092e5-3128-4e26-9e7e-11016c6684f2": "ACTION",
    "a10c6218-cb94-436f-91c6-e1652575253d": "TRANSITION",
    "bb0b9044-714e-4614-ad3e-33cbdf34d16b": "IMAGEPOOL",
    "2bef0454-1bd3-412a-ac2c-af0f31dbc40f": "TEXTLIST",
    "63784cbb-9ba0-45e6-9d69-babf3f040511": "GLOBAL_TEXTLIST",
    "adb5cb65-8e1d-4a00-b70a-375ea27582f3": "LIBRARY_MANAGER",
    "ae1de277-a207-4a28-9efb-456c06bd52f3": "TASK_CONFIGURATION",
    "085afe48-c5d8-4ea5-ab0d-b35701fa6009": "PROJECT_INFORMATION",
    "8753fe6f-4a22-4320-8103-e553c4fc8e04": "PROJECT_SETTINGS",
    "225bfe47-7336-4dbc-9419-4105a7c831fa": "DEVICE",
    "738bea1e-99bb-4f04-90bb-a7a567e74e3a": "FOLDER",
    "413e2a7d-adb1-4d2c-be29-6ae6e4fab820": "CALL_TO_POU",
    "f18bec89-9fef-401d-9953-2f11739a6808": "VISUALISATION",
    "4d3fdb8f-ab50-4c35-9d3a-d4bb9bb9a628": "VISUALISATION_MANAGER",
    "bc63f5fa-d286-4786-994e-7b27e4f97bd5": "TARGET_VISUALISATION",
    "0fdbf158-1ae0-47d9-9269-cd84be308e9d": "WEB_VISUALISATION",
    "8e687a04-7ca7-42d3-be06-fcbda676c5ef": "VISUALISATION_STYLE",
}


def emit(obj):
    """One JSON object per line on stdout. Never prose."""
    print("@@VERSIO@@" + json.dumps(obj))


def safe(fn, default=None):
    try:
        return fn()
    except Exception:
        return default


def describe(obj, path):
    type_guid = safe(lambda: str(obj.type), "")
    name = safe(lambda: obj.get_name(), "<unnamed>")
    rec = {
        "name": name,
        "path": path,
        "typeGuid": type_guid,
        "type": GUID_TYPE_MAPPING.get(type_guid, "UNKNOWN"),
        "isFolder": safe(lambda: bool(obj.is_folder), False),
        "guid": safe(lambda: str(obj.guid), ""),
        "hasTextualDecl": safe(lambda: bool(obj.has_textual_declaration), False),
        "hasTextualImpl": safe(lambda: bool(obj.has_textual_implementation), False),
        "childCount": safe(lambda: len(obj.get_children()), 0),
    }
    # Record sizes rather than content: we want shape, not a dump of the project.
    if rec["hasTextualDecl"]:
        rec["declLen"] = safe(lambda: len(obj.textual_declaration.text), -1)
    if rec["hasTextualImpl"]:
        rec["implLen"] = safe(lambda: len(obj.textual_implementation.text), -1)
    return rec


def walk(obj, path, acc, depth=0):
    if depth > 40:                     # cycle guard; real trees are nowhere near this
        return
    rec = describe(obj, path)
    acc.append(rec)
    emit({"t": "object", "o": rec})
    for child in safe(lambda: obj.get_children(), []) or []:
        child_name = safe(lambda: child.get_name(), "<unnamed>")
        walk(child, path + "/" + child_name, acc, depth + 1)


def main():
    args = sys.argv[1:]
    if not args:
        emit({"t": "fatal", "msg": "no job file argument"})
        return 2

    with open(args[0], "r") as f:
        job = json.load(f)

    emit({"t": "start", "python": sys.version, "job": job})

    proj = scriptengine.projects.open(job["project"])
    emit({"t": "opened", "path": str(proj.path), "dirty": safe(lambda: bool(proj.dirty), None)})

    acc = []
    for child in proj.get_children():
        walk(child, "/" + safe(lambda: child.get_name(), "<unnamed>"), acc)

    # Roll up what we found so the summary is readable without post-processing.
    counts = {}
    for r in acc:
        counts[r["type"]] = counts.get(r["type"], 0) + 1

    unknown = [r for r in acc if r["type"] == "UNKNOWN"]
    report = {
        "project": job["project"],
        "objectCount": len(acc),
        "counts": counts,
        "unknownTypeGuids": sorted(set(r["typeGuid"] for r in unknown)),
        "objects": acc,
    }

    with open(job["out"], "w") as f:
        json.dump(report, f, indent=2)

    emit({"t": "summary", "objectCount": len(acc), "counts": counts,
          "unknown": len(unknown), "out": job["out"]})

    proj.close()
    emit({"t": "done"})
    return 0


try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})
    rc = 1

emit({"t": "exit", "rc": rc})
