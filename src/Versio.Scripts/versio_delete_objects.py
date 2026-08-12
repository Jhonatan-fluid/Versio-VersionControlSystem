# -*- coding: utf-8 -*-
# Test fixture helper: deletes named objects from a project so the importer has to
# recreate them. Used by Test-ObjectCreation.ps1 - never part of the product.

import json
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


def main():
    with open(sys.argv[1], "r") as f:
        job = json.load(f)

    proj = scriptengine.projects.open(job["project"])
    removed = []
    for path in job["objects"]:
        parts = [p for p in path.split("/") if p]
        obj = find_by_path(proj, parts)
        if obj is None:
            emit({"t": "warning", "code": "NOT_FOUND", "path": path})
            continue
        obj.remove()
        removed.append(path)
        emit({"t": "removed", "path": path})

    if removed:
        proj.save()
    emit({"t": "summary", "removed": len(removed)})
    proj.close()
    return 0


try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})
    rc = 1

emit({"t": "exit", "rc": rc})
