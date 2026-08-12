# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS.
#
# Phase 0.17 probe. Answers three questions that decide how dangerous the import path is:
#
#   Q1  Does import_native accept a bare string, or must it be a list?
#       (A SP21 report shows "Could not cast: System.String in System.Char", which is the
#        classic IronPython symptom of a string being iterated into characters because
#        overload resolution picked the IEnumerable<string> overload.)
#
#   Q2  Does a custom NativeImportHandler returning NativeImportResolve.replace actually
#       replace IN PLACE, or does it paste a duplicate alongside?
#       codescribe assumes paste, and works around it by removing the object first - the
#       single most dangerous step in the whole import path.
#
#   Q3  Does the object keep its GUID across the import? GUID stability is what lets a
#       moved object be recognised as a move rather than a delete plus an add.
#
# Usage: --runscript=versio_probe_native_import.py --scriptargs:"<job.json>"
# job.json: { "project": "...", "xml": "<a native .xml export>", "parentPath": "Device/Plc Logic/Application" }

import json
import os
import sys
import traceback

import scriptengine  # type: ignore


def emit(obj):
    print("@@VERSIO@@" + json.dumps(obj))


def safe(fn, default=None):
    try:
        return fn()
    except Exception:
        return default


def find_child(parent, name):
    for child in safe(lambda: parent.get_children(), []) or []:
        if safe(lambda: child.get_name(), None) == name:
            return child
    return None


def find_by_path(proj, parts):
    node = proj
    for part in parts:
        node = find_child(node, part)
        if node is None:
            return None
    return node


def snapshot(parent):
    """name -> guid for every direct child."""
    out = {}
    for child in safe(lambda: parent.get_children(), []) or []:
        name = safe(lambda: child.get_name(), "?")
        out[name] = safe(lambda: str(child.guid), "?")
    return out


class ReplaceHandler(scriptengine.NativeImportHandler):  # type: ignore
    """Always replace. Rename would silently create Signals_1 and is never what an
    automated pipeline wants."""

    def __init__(self):
        self.conflicts = []
        self.progress_calls = []
        self.skipped_calls = []

    def conflict(self, name, existingObject, newguid):
        self.conflicts.append({
            "name": str(name),
            "existing": safe(lambda: str(existingObject.get_name()), None),
            "existingGuid": safe(lambda: str(existingObject.guid), None),
            "newGuid": str(newguid),
        })
        emit({"t": "conflict_callback", "name": str(name), "newGuid": str(newguid)})
        return scriptengine.NativeImportResolve.replace

    def progress(self, name, pastedObject, exception):
        self.progress_calls.append({
            "name": str(name),
            "pasted": safe(lambda: str(pastedObject.get_name()), None),
            "exception": str(exception) if exception else None,
        })
        emit({"t": "progress_callback", "name": str(name),
              "exception": str(exception) if exception else None})

    def skipped(self, name):
        self.skipped_calls.append(str(name))
        emit({"t": "skipped_callback", "name": str(name)})


def try_call(label, fn):
    """Run one API shape and report exactly how it failed, if it did."""
    try:
        fn()
        emit({"t": "call_ok", "label": label})
        return True, None
    except Exception as e:
        emit({"t": "call_failed", "label": label, "error": str(e),
              "type": type(e).__name__})
        return False, str(e)


def main():
    args = sys.argv[1:]
    with open(args[0], "r") as f:
        job = json.load(f)

    proj = scriptengine.projects.open(job["project"])
    emit({"t": "opened", "path": str(proj.path)})

    parts = [p for p in job["parentPath"].split("/") if p]
    parent = find_by_path(proj, parts)
    if parent is None:
        emit({"t": "fatal", "msg": "parent path not found: " + job["parentPath"]})
        proj.close()
        return 2

    xml = job["xml"]
    target_name = os.path.basename(xml)
    for suffix in (".vis.xml", ".xml"):
        if target_name.endswith(suffix):
            target_name = target_name[:-len(suffix)]
            break

    before = snapshot(parent)
    emit({"t": "before", "childCount": len(before),
          "target": target_name, "targetGuid": before.get(target_name)})

    handler = ReplaceHandler()

    # --- Q1: which argument shape does import_native accept? -------------------
    ok_list, err_list = try_call("list+handler",
                                 lambda: parent.import_native([xml], None, handler))
    if not ok_list:
        ok_str, err_str = try_call("bare-string+handler",
                                   lambda: parent.import_native(xml, None, handler))
        if not ok_str:
            ok_bare, err_bare = try_call("bare-string-only",
                                         lambda: parent.import_native(xml))
            if not ok_bare:
                emit({"t": "summary", "verdict": "ALL_CALL_SHAPES_FAILED",
                      "listError": err_list, "stringError": err_str, "bareError": err_bare})
                proj.close()
                return 1

    after = snapshot(parent)

    # --- Q2/Q3: replaced in place, or pasted a duplicate? ----------------------
    added = sorted(set(after) - set(before))
    removed = sorted(set(before) - set(after))
    guid_before = before.get(target_name)
    guid_after = after.get(target_name)

    if len(after) == len(before) and not added:
        verdict = "REPLACED_IN_PLACE"
    elif added:
        verdict = "PASTED_DUPLICATE"
    else:
        verdict = "UNCLEAR"

    emit({"t": "summary",
          "verdict": verdict,
          "childCountBefore": len(before),
          "childCountAfter": len(after),
          "added": added,
          "removed": removed,
          "targetGuidBefore": guid_before,
          "targetGuidAfter": guid_after,
          "guidPreserved": guid_before == guid_after,
          "conflictCallbacks": len(handler.conflicts),
          "conflicts": handler.conflicts,
          "progressCallbacks": len(handler.progress_calls),
          "skippedCallbacks": len(handler.skipped_calls)})

    # Never save: this probe must not mutate anything, even a copy.
    proj.close()
    return 0


try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})
    rc = 1

emit({"t": "exit", "rc": rc})
