# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS.
#
# Versio importer. Walks an export tree and applies it back to a project.
#
# Strategy, in order of preference:
#   1. ST objects  -> located in the tree and updated IN PLACE via
#                     textual_declaration.replace() / textual_implementation.replace().
#                     No removal, no re-creation, no conflict resolution: the object keeps
#                     its GUID and everything CODESYS hangs off it.
#   2. Native XML  -> import_native with a CUSTOM HANDLER returning
#                     NativeImportResolve.replace.
#
# On (2): codescribe reports that import_native pastes rather than replaces, and works
# around it by removing the existing object first - the most dangerous step in any import.
# That is only true with the DEFAULT handler. Measured on CODESYS 3.5.22.20 with a custom
# handler: verdict REPLACED_IN_PLACE, child count 11 before and after, nothing added or
# removed, and the object's GUID preserved exactly. So no removal is needed, and an object
# that does not exist yet is created by the same call.
#
# Argument shape matters: import_native([path], filter, handler) - a LIST. Passing a bare
# string can bind to the IEnumerable<string> overload and be iterated into characters,
# which is the reported "Could not cast: System.String in System.Char" failure.
#
# Usage: --runscript=versio_import.py --scriptargs:"<job.json>"
# job.json: { "project": "...", "src": "<export root>", "save": true }

import io
import json
import os
import sys
import traceback

import scriptengine  # type: ignore

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from versio_decl import (DUT_ALIAS, DUT_ENUM, DUT_STRUCT, DUT_UNION,  # noqa: E402
                         KIND_DUT, KIND_FUNCTION, KIND_FUNCTION_BLOCK,
                         KIND_INTERFACE, KIND_PROGRAM, parse_declaration)
from versio_text import normalize_decl_only, split_st  # noqa: E402
from versio_types import is_versio_file, type_of  # noqa: E402


def emit(obj):
    print("@@VERSIO@@" + json.dumps(obj))


def safe(fn, default=None):
    try:
        return fn()
    except Exception:
        return default


def read_text(path):
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        return f.read()


# Suffixes stripped to recover an object name from a file name. Longest first so that
# ".gvl.st" is matched before ".st".
SUFFIXES = [".gvl.st", ".gvl.xml", ".vis.xml", ".action.st", ".transition.st", ".st", ".xml"]


def object_name_from_file(file_name):
    """'Conveyor.Execute.action.st' -> ('Conveyor', 'Execute', '.action.st')"""
    for suffix in SUFFIXES:
        if file_name.endswith(suffix):
            stem = file_name[:-len(suffix)]
            if "." in stem:
                parent, child = stem.rsplit(".", 1)
                return parent, child, suffix
            return None, stem, suffix
    return None, file_name, ""


def find_child(parent, name):
    for child in safe(lambda: parent.get_children(), []) or []:
        if safe(lambda: child.get_name(), None) == name:
            return child
    return None


def find_by_path(proj, parts):
    """Navigate the object tree by name. Returns None when any segment is missing."""
    node = proj
    for part in parts:
        node = find_child(node, part)
        if node is None:
            return None
    return node


class ReplaceHandler(scriptengine.NativeImportHandler):  # type: ignore
    """Always replace on conflict.

    Rename would silently produce 'Signals_1' - never what an automated pipeline wants,
    and a disaster to unpick later. Skip would silently drop the incoming change. This is
    hard-coded on purpose and must not become a user preference.
    """

    def __init__(self, owner):
        self.owner = owner
        self.replaced = 0
        self.pasted = 0

    def conflict(self, name, existingObject, newguid):
        self.replaced += 1
        return scriptengine.NativeImportResolve.replace

    def progress(self, name, pastedObject, exception):
        if exception:
            self.owner.warn("NATIVE_IMPORT_ERROR", str(name), str(exception))
        else:
            self.pasted += 1

    def skipped(self, name):
        self.owner.warn("NATIVE_IMPORT_SKIPPED_BY_CODESYS", str(name))


class Importer(object):
    def __init__(self, proj, src_root):
        self.proj = proj
        self.src_root = src_root
        self.updated = 0
        self.unchanged = 0
        self.missing = 0
        self.failed = 0
        self.native = 0
        self.created = 0
        self.warnings = []

    def warn(self, code, path, detail=None):
        self.warnings.append({"code": code, "path": path, "detail": detail})
        emit({"t": "warning", "code": code, "path": path, "detail": detail})

    def apply_st(self, obj, rel_path, abs_path, suffix):
        content = read_text(abs_path)

        if suffix in (".gvl.st",):
            # GVL: declaration only.
            new_decl = normalize_decl_only(content)
            old = safe(lambda: obj.textual_declaration.text, None)
            if old is not None and normalize_decl_only(old) == new_decl:
                self.unchanged += 1
                return
            obj.textual_declaration.replace(new_decl)
            self.updated += 1
            emit({"t": "updated", "path": rel_path, "part": "declaration"})
            return

        if suffix in (".action.st", ".transition.st"):
            # No textual declaration on these objects.
            new_impl = normalize_decl_only(content)
            old = safe(lambda: obj.textual_implementation.text, None)
            if old is not None and normalize_decl_only(old) == new_impl:
                self.unchanged += 1
                return
            obj.textual_implementation.replace(new_impl)
            self.updated += 1
            emit({"t": "updated", "path": rel_path, "part": "implementation"})
            return

        # Full ST: declaration + implementation.
        decl, impl = split_st(content)
        changed = False

        old_decl = safe(lambda: obj.textual_declaration.text, None)
        if old_decl is not None and old_decl.replace(u"\r\n", u"\n").strip() != decl:
            obj.textual_declaration.replace(decl + u"\n")
            changed = True

        if safe(lambda: obj.has_textual_implementation, False):
            old_impl = safe(lambda: obj.textual_implementation.text, None)
            if old_impl is not None and old_impl.replace(u"\r\n", u"\n").strip() != impl:
                obj.textual_implementation.replace(impl + u"\n")
                changed = True

        if changed:
            self.updated += 1
            emit({"t": "updated", "path": rel_path, "part": "both"})
        else:
            self.unchanged += 1

    def create_textual(self, parts, abs_path, rel_path, suffix):
        """Create a textual object that does not exist yet.

        Everything create_pou / create_dut need is stated in the declaration itself, so it
        is parsed rather than guessed. An unrecognised declaration is REPORTED, never
        approximated: creating the wrong kind of object is worse than creating none.
        """
        owner_parts = parts[:-1]
        owner = find_by_path(self.proj, owner_parts) if owner_parts else self.proj
        if owner is None:
            self.missing += 1
            self.warn("PARENT_NOT_FOUND", rel_path, "/".join(owner_parts))
            return None

        name = parts[-1]
        content = read_text(abs_path)
        declaration = normalize_decl_only(content) if suffix == ".gvl.st" else split_st(content)[0]
        info = parse_declaration(declaration)

        if suffix == ".gvl.st":
            # A GVL body has no POU keyword, so the parser cannot identify it - but the
            # file name already told us what it is.
            info = {"kind": "GVL", "name": name, "canCreate": True}

        if not info.get("canCreate"):
            self.missing += 1
            self.warn("CANNOT_IDENTIFY_DECLARATION", rel_path,
                      "no recognisable PROGRAM / FUNCTION_BLOCK / FUNCTION / INTERFACE / TYPE header")
            return None

        kind = info["kind"]
        try:
            if kind == "GVL":
                obj = owner.create_gvl(name)
            elif kind == KIND_PROGRAM:
                obj = owner.create_pou(name, scriptengine.PouType.Program,
                                       scriptengine.ImplementationLanguages.st)
            elif kind == KIND_FUNCTION_BLOCK:
                obj = owner.create_pou(name, scriptengine.PouType.FunctionBlock,
                                       scriptengine.ImplementationLanguages.st,
                                       None, info.get("extends"), info.get("implements") or None)
            elif kind == KIND_FUNCTION:
                obj = owner.create_pou(name, scriptengine.PouType.Function,
                                       scriptengine.ImplementationLanguages.st,
                                       info.get("returnOrBase"))
            elif kind == KIND_INTERFACE:
                obj = owner.create_interface(name)
            elif kind == KIND_DUT:
                dut_map = {
                    DUT_STRUCT: scriptengine.DutType.Structure,
                    DUT_ENUM: scriptengine.DutType.Enumeration,
                    DUT_ALIAS: scriptengine.DutType.Alias,
                    DUT_UNION: scriptengine.DutType.Union,
                }
                dut_type = dut_map.get(info.get("dut"), scriptengine.DutType.Structure)
                obj = owner.create_dut(name, dut_type, info.get("returnOrBase"))
            else:
                self.missing += 1
                self.warn("UNSUPPORTED_CREATE_KIND", rel_path, str(kind))
                return None
        except Exception as e:
            self.failed += 1
            self.warn("CREATE_FAILED", rel_path, str(e))
            return None

        self.created += 1
        emit({"t": "created", "path": rel_path, "kind": str(kind), "name": name,
              "into": "/".join(owner_parts) or "<project>"})
        return obj

    def handle_file(self, abs_path, rel_path):
        file_name = os.path.basename(rel_path)
        if is_versio_file(file_name):
            return

        rel_dir = os.path.dirname(rel_path).replace("\\", "/")
        parent_name, obj_name, suffix = object_name_from_file(file_name)

        parts = [p for p in rel_dir.split("/") if p]
        if parent_name:
            parts = parts + [parent_name, obj_name]
        else:
            parts = parts + [obj_name]

        if not suffix.endswith(".st"):
            # Native XML: import into the OWNING node. This both replaces an existing
            # object in place and creates one that is not there yet, so it needs no
            # existence check of its own.
            owner_parts = parts[:-1]
            owner = find_by_path(self.proj, owner_parts) if owner_parts else self.proj
            if owner is None:
                self.missing += 1
                self.warn("PARENT_NOT_FOUND", rel_path, "/".join(owner_parts))
                return

            existed = find_by_path(self.proj, parts) is not None
            try:
                handler = ReplaceHandler(self)
                owner.import_native([abs_path], None, handler)
                self.native += 1
                if not existed:
                    self.created += 1
                emit({"t": "native_imported", "path": rel_path,
                      "into": "/".join(owner_parts) or "<project>",
                      "replaced": existed})
            except Exception as e:
                self.failed += 1
                self.warn("NATIVE_IMPORT_FAILED", rel_path, str(e))
            return

        obj = find_by_path(self.proj, parts)
        if obj is None:
            obj = self.create_textual(parts, abs_path, rel_path, suffix)
            if obj is None:
                return

        try:
            self.apply_st(obj, rel_path, abs_path, suffix)
        except Exception as e:
            self.failed += 1
            self.warn("APPLY_FAILED", rel_path, str(e))

    def run(self):
        """Two phases, in this order, with a save between them.

        Creating an object mutates CODESYS's internal name dictionary. Running a native
        import in the same session immediately afterwards makes its conflict resolution
        enumerate that dictionary while it is still being modified, and CODESYS throws:

            System.InvalidOperationException: Collection was modified;
            enumeration operation may not execute.
              at _3S.CoDeSys.ObjectManager.Project.GetUniqueName(...)

        Worse, the exception is swallowed by import_native's progress callback, so the
        import silently does nothing and the result LOOKS correct. Separating the phases
        and saving in between lets the object manager settle first.
        """
        textual = []
        native = []
        for dir_path, _dir_names, file_names in os.walk(self.src_root):
            for file_name in sorted(file_names):
                # Versio's own bookkeeping - the manifest and the coverage report.
                # They describe the export; they are not objects in it, and handing
                # one to CODESYS as native XML is how a junk object gets created.
                if is_versio_file(file_name):
                    continue
                abs_path = os.path.join(dir_path, file_name)
                rel_path = os.path.relpath(abs_path, self.src_root).replace("\\", "/")
                (textual if file_name.endswith(".st") else native).append((abs_path, rel_path))

        emit({"t": "phase", "name": "textual", "files": len(textual)})
        for abs_path, rel_path in textual:
            self.handle_file(abs_path, rel_path)

        if self.created > 0 or self.updated > 0:
            self.proj.save()
            emit({"t": "phase_saved", "after": "textual"})

        emit({"t": "phase", "name": "native", "files": len(native)})
        for abs_path, rel_path in native:
            self.handle_file(abs_path, rel_path)


def main():
    args = sys.argv[1:]
    if not args:
        emit({"t": "fatal", "msg": "no job file argument"})
        return 2
    with open(args[0], "r") as f:
        job = json.load(f)

    emit({"t": "start", "project": job["project"], "src": job["src"]})

    proj = scriptengine.projects.open(job["project"])
    emit({"t": "opened", "path": str(proj.path)})

    imp = Importer(proj, job["src"])
    imp.run()

    dirty = imp.updated > 0 or imp.native > 0
    if job.get("save", True) and dirty:
        proj.save()
        emit({"t": "saved"})
    elif not dirty:
        emit({"t": "nothing_to_save"})

    emit({"t": "summary", "updated": imp.updated, "unchanged": imp.unchanged,
          "native": imp.native, "created": imp.created,
          "missing": imp.missing, "failed": imp.failed, "warnings": len(imp.warnings)})

    proj.close()
    emit({"t": "done"})
    return 0


try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})
    rc = 1

emit({"t": "exit", "rc": rc})
