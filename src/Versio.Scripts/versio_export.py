# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS.
#
# Versio exporter. Walks the project object tree and writes one file per object,
# mirroring the CODESYS structure as folders, then writes a manifest of
# (guid, type, path, canonical hash).
#
# Usage:
#   CODESYS.exe --profile="..." --noUI --runscript=versio_export.py --scriptargs:"<job.json>"
# job.json: { "project": "...", "out": "<export root>", "bindingId": "b-xxxx" }

import io
import json
import os
import shutil
import sys
import traceback

import scriptengine  # type: ignore

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from versio_text import (content_hash, join_st, normalize_decl_only,  # noqa: E402
                         safe_file_stem, scrub_native_xml)
from versio_types import (FORM_CONTAINER, FORM_GVL_BOTH, FORM_NATIVE,  # noqa: E402
                          FORM_NATIVE_R, FORM_SKIP, FORM_ST_DECL,
                          FORM_ST_FULL, FORM_ST_IMPL, decide_form,
                          file_name_for, type_of)


def emit(obj):
    print("@@VERSIO@@" + json.dumps(obj))


def safe(fn, default=None):
    try:
        return fn()
    except Exception:
        return default


def write_text(path, text):
    """UTF-8, no BOM, LF endings. Python 2's builtin open() would encode as ascii and
    die on Cyrillic / accented characters / smart quotes."""
    d = os.path.dirname(path)
    if d and not os.path.isdir(d):
        os.makedirs(d)
    with io.open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def read_text(path):
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        return f.read()


class Exporter(object):
    def __init__(self, out_root):
        self.out_root = out_root
        self.records = []
        self.warnings = []
        self.total = 0
        self.skipped = 0

    def warn(self, code, path, detail=None):
        w = {"code": code, "path": path}
        if detail:
            w["detail"] = detail
        self.warnings.append(w)
        emit({"t": "warning", "code": code, "path": path, "detail": detail})

    def record(self, obj, obj_type, form, rel_path, text):
        h = content_hash(text)
        self.records.append({
            "guid": safe(lambda: str(obj.guid), ""),
            "name": safe(lambda: obj.get_name(), ""),
            "type": obj_type,
            "form": form,
            "path": rel_path.replace("\\", "/"),
            "hash": h,
            "bytes": len(text.encode("utf-8")),
        })
        emit({"t": "object", "path": rel_path.replace("\\", "/"),
              "type": obj_type, "form": form, "hash": h})

    # --- native export goes through a temp file, then gets scrubbed --------------
    def export_native(self, obj, abs_path, recursive):
        tmp = abs_path + ".raw"
        d = os.path.dirname(abs_path)
        if d and not os.path.isdir(d):
            os.makedirs(d)
        obj.export_native(tmp, recursive)
        raw = read_text(tmp)
        os.remove(tmp)
        scrubbed, n = scrub_native_xml(raw)
        write_text(abs_path, scrubbed)
        return scrubbed, n

    def handle(self, obj, rel_dir, parent_name):
        name = safe(lambda: obj.get_name(), None)
        if name is None:
            self.warn("UNNAMED_OBJECT", rel_dir)
            return
        type_guid = safe(lambda: str(obj.type), "")
        obj_type = type_of(type_guid)
        has_impl = bool(safe(lambda: obj.has_textual_implementation, False))
        has_decl = bool(safe(lambda: obj.has_textual_declaration, False))

        if obj_type == "UNKNOWN":
            self.warn("UNKNOWN_TYPE_GUID", rel_dir + "/" + name, type_guid)

        form = decide_form(obj_type, has_impl, has_decl)
        self.total += 1

        if form == FORM_SKIP:
            self.skipped += 1
            emit({"t": "skip", "path": rel_dir + "/" + name, "type": obj_type})
            return

        if form == FORM_CONTAINER:
            child_dir = os.path.join(rel_dir, safe_file_stem(name))
            for child in safe(lambda: obj.get_children(), []) or []:
                self.handle(child, child_dir, None)
            return

        stem_name = safe_file_stem(name)
        parent_stem = safe_file_stem(parent_name) if parent_name else None
        fname = file_name_for(stem_name, obj_type, form, parent_stem)
        rel_path = os.path.join(rel_dir, fname)
        abs_path = os.path.join(self.out_root, rel_path)

        try:
            if form == FORM_ST_FULL:
                text = join_st(obj.textual_declaration.text, obj.textual_implementation.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text)

            elif form == FORM_ST_DECL:
                text = normalize_decl_only(obj.textual_declaration.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text)

            elif form == FORM_ST_IMPL:
                text = normalize_decl_only(obj.textual_implementation.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text)

            elif form == FORM_GVL_BOTH:
                xml_path = os.path.join(rel_dir, stem_name + ".gvl.xml")
                xml_text, _ = self.export_native(obj, os.path.join(self.out_root, xml_path), False)
                self.record(obj, obj_type, FORM_NATIVE, xml_path, xml_text)
                st_text = normalize_decl_only(obj.textual_declaration.text)
                write_text(abs_path, st_text)
                self.record(obj, obj_type, FORM_ST_DECL, rel_path, st_text)

            elif form in (FORM_NATIVE, FORM_NATIVE_R):
                text, _ = self.export_native(obj, abs_path, form == FORM_NATIVE_R)
                self.record(obj, obj_type, form, rel_path, text)
        except Exception as e:
            self.warn("EXPORT_FAILED", rel_dir + "/" + name, str(e))
            return

        # Children of a leaf object (methods, actions, properties on a POU) live
        # alongside it in the same folder, prefixed with the parent name.
        if form != FORM_NATIVE_R:
            for child in safe(lambda: obj.get_children(), []) or []:
                self.handle(child, rel_dir, name)


def main():
    args = sys.argv[1:]
    if not args:
        emit({"t": "fatal", "msg": "no job file argument"})
        return 2
    with open(args[0], "r") as f:
        job = json.load(f)

    out_root = job["out"]
    # Staging + swap: a locked target folder or a mid-export crash must not destroy
    # the existing on-disk copy.
    staging = out_root + ".versio_staging"
    if os.path.isdir(staging):
        shutil.rmtree(staging)
    os.makedirs(staging)

    emit({"t": "start", "python": sys.version, "project": job["project"], "out": out_root})

    proj = scriptengine.projects.open(job["project"])
    emit({"t": "opened", "path": str(proj.path)})

    ex = Exporter(staging)
    for child in proj.get_children():
        ex.handle(child, "", None)

    manifest = {
        "schema": 1,
        "bindingId": job.get("bindingId", ""),
        "project": os.path.basename(job["project"]),
        "objectCount": len(ex.records),
        "objects": sorted(ex.records, key=lambda r: r["path"]),
        "warnings": ex.warnings,
    }
    mpath = os.path.join(staging, "_manifest.json")
    write_text(mpath, json.dumps(manifest, indent=2, sort_keys=True).decode("utf-8")
               if isinstance(json.dumps(manifest), bytes) else
               unicode(json.dumps(manifest, indent=2, sort_keys=True)))  # noqa: F821

    # Swap staging into place only once the export completed.
    if os.path.isdir(out_root):
        shutil.rmtree(out_root)
    os.rename(staging, out_root)

    proj.close()
    emit({"t": "summary", "walked": ex.total, "written": len(ex.records),
          "skipped": ex.skipped, "warnings": len(ex.warnings)})
    emit({"t": "done"})
    return 0


try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})
    rc = 1

emit({"t": "exit", "rc": rc})
