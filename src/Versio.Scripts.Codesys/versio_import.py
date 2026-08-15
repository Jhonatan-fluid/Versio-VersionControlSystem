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
from versio_types import (is_export_only, is_typed_form,  # noqa: E402
                          is_versio_file, type_of, typed_sibling_candidates)
import versio_yaml  # noqa: E402


def emit(obj):
    print("@@VERSIO@@" + json.dumps(obj))


def safe(fn, default=None):
    try:
        return fn()
    except Exception:
        return default


# The open project, so a failure anywhere can still close it. See the same block in
# versio_export.py: CODESYS writes <project>.~u while it holds a project, and a script that
# died between open and close left that marker behind for the next run to reason about.
#
# It matters more on this side. An import runs with the project WRITEABLE and has already
# saved by the time the native phase runs, so a failure here can leave a modified project
# held open by a process that no longer exists.
_OPEN = {"project": None}


def remember_project(proj):
    _OPEN["project"] = proj
    return proj


def close_project(proj=None):
    """Close the open project, at most once, never raising."""
    handle = proj if proj is not None else _OPEN["project"]
    _OPEN["project"] = None
    if handle is None:
        return
    try:
        handle.close()
    except Exception as e:
        emit({"t": "warning", "code": "PROJECT_CLOSE_FAILED", "path": None, "detail": str(e)})


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


def typed_sibling_for(abs_path):
    """The typed file sitting beside this native one, by name, or None.

    The RULE lives in versio_types (which suffixes pair with a .xml); the LOOKING lives
    here, because this is the half that is allowed to touch a filesystem. Returns the name
    rather than True so the event can say which file won: "skipped because X is
    authoritative" is a sentence somebody can act on.
    """
    for candidate in typed_sibling_candidates(abs_path):
        if os.path.isfile(candidate):
            return os.path.basename(candidate)
    return None


def _same(current, wanted):
    """Compare a live property against a wanted value without pretending about types.

    CODESYS returns priority as the string "1" and core binding as the int -2, so a
    comparison that coerced everything one way would report a spurious change on one of
    them and then write a value identical to what was already there.
    """
    if isinstance(wanted, int) and not isinstance(wanted, bool):
        try:
            return int(current) == wanted
        except (TypeError, ValueError):
            return False
    return (u"%s" % (current,)) == (u"%s" % (wanted,))


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
        self.one_way = 0
        self.warnings = []

    def warn(self, code, path, detail=None):
        self.warnings.append({"code": code, "path": path, "detail": detail})
        emit({"t": "warning", "code": code, "path": path, "detail": detail})

    def skip_one_way(self, rel_path):
        """Note an export-only file that the import deliberately leaves alone.

        NOT a warning. A warning that fires on every single import of every project
        that has a library manager is noise, and noise is how a real native-import
        failure gets read past - the exact failure mode _coverage.md caused before it
        was excluded. It is emitted and counted instead, so the log answers "what
        happened to my libraries.yaml edit" without anybody having to guess.
        """
        self.one_way += 1
        emit({"t": "not_imported", "path": rel_path, "reason": "EXPORT_ONLY"})

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

    # Fields that have been WRITTEN to a real task and seen to persist across a reopen
    # (probe P6), with the type each one demands. The types are not uniform and that is not
    # a style choice in this table: priority takes a string and raises TypeError on an int,
    # core_binding takes an int. Anything not listed here is attempted anyway and reported
    # if it refuses - see apply_tasks - because a file that looks editable while one of its
    # fields is silently ignored is the exact gap _coverage.md exists to close.
    # (document key, ScriptEngine property, wanted type).
    #
    # A LIST, not a dict, and that is not a style choice: dict iteration order differs
    # between IronPython 2.7 and CPython 3, so a dict here would apply the fields in an
    # arbitrary order and emit its task_set lines in an arbitrary order with them. The
    # order below is deliberate - the unit before the interval it qualifies, so a task
    # moving from milliseconds to microseconds is never momentarily set to the new number
    # under the old unit.
    #
    # versio_yaml.py's own header records this hazard for the emitter; it applies just as
    # much to the half that writes back.
    TASK_FIELDS = [
        ("priority", "priority", "str"),
        ("coreBinding", "core_binding", "int"),
        ("intervalUnit", "interval_unit", "str"),
        ("interval", "interval", "str"),
    ]

    def apply_tasks(self, abs_path, rel_path):
        """Apply a `.tasks.yaml` through the typed API.

        THIS IS THE POINT OF PHASE 4. Task configuration used to be handed to
        import_native, and that rewrote Core2 - a task's CPU core affinity - on every
        import without being asked, and threw on the task objects on every import after the
        first while the run reported failed: 0. Setting the fields through properties
        instead means CODESYS is told exactly what changed and nothing else moves.
        """
        top, list_key, tasks = versio_yaml.parse_document(read_text(abs_path))
        if list_key != "tasks":
            self.warn("TASKS_DOCUMENT_UNRECOGNISED", rel_path, str(list_key))
            return

        # The configuration object sits where the file does: the mirror's directory path is
        # the project's object path, which is the whole reason the export writes it there.
        parts = [p for p in os.path.dirname(rel_path).replace("\\", "/").split("/") if p]
        config_name = top.get("configuration")
        if not config_name:
            self.warn("TASKS_CONFIG_NAME_MISSING", rel_path)
            return

        config = find_by_path(self.proj, parts + [config_name])
        if config is None:
            self.missing += 1
            self.warn("PARENT_NOT_FOUND", rel_path, "/".join(parts + [config_name]))
            return

        by_name = {}
        for child in safe(lambda: config.get_children(), []) or []:
            child_name = safe(lambda c=child: c.get_name(), None)
            if child_name is not None:
                by_name[child_name] = child

        changed = 0
        for task in tasks:
            name = task.get("name")
            if not name:
                continue
            obj = by_name.get(name)
            if obj is None:
                # NOT created. Adding a task changes what the machine runs, and this phase
                # is about stopping unasked-for changes rather than adding a new kind.
                self.warn("TASK_NOT_FOUND", rel_path, name)
                continue
            changed += self.apply_task_fields(obj, task, rel_path, name)

        # A task the PROJECT has and the document does not is left alone, silently. That is
        # deliberate and it is a limitation rather than an oversight: removing a task is a
        # deletion, deletions are Phase 2, and they need the manifest, the confirmation
        # dialog and the content check before anything is allowed to remove one. Deleting a
        # task from this file today therefore does nothing - stated here because a file that
        # looks editable while one of its edits is ignored is the exact shape of gap this
        # phase was written to close.
        if changed:
            self.updated += 1
            emit({"t": "updated", "path": rel_path, "part": "tasks", "fields": changed})
        else:
            self.unchanged += 1

    def apply_task_fields(self, obj, task, rel_path, name):
        """Set one task's fields, reporting any that refuse. Returns how many changed."""
        changed = 0
        for key, prop, kind in self.TASK_FIELDS:
            if key not in task:
                continue

            wanted = task[key]
            if kind == "int":
                try:
                    wanted = int(wanted)
                except (TypeError, ValueError):
                    self.warn("TASK_FIELD_NOT_A_NUMBER", rel_path,
                              "%s.%s=%s" % (name, key, task[key]))
                    continue

            current = safe(lambda p=prop: getattr(obj, p), None)
            if current is not None and _same(current, wanted):
                continue

            try:
                setattr(obj, prop, wanted)
            except Exception as e:
                # Reported, never swallowed. An editable-looking field that quietly does
                # nothing is worse than one that says it cannot be applied.
                self.warn("TASK_FIELD_REFUSED", rel_path,
                          "%s.%s: %s" % (name, key, str(e)))
                continue

            changed += 1
            emit({"t": "task_set", "path": rel_path, "task": name,
                  "field": key, "value": u"%s" % (wanted,)})
        return changed

    def handle_file(self, abs_path, rel_path):
        file_name = os.path.basename(rel_path)
        if is_versio_file(file_name):
            return
        if is_export_only(file_name):
            # Belt and braces: run() already filters these out. Guarding both entry
            # points is the lesson from _coverage.md, where the importer knew about
            # _manifest.json by name in two places and a third file was added to
            # neither - and CODESYS was handed markdown as native XML.
            self.skip_one_way(rel_path)
            return

        rel_dir = os.path.dirname(rel_path).replace("\\", "/")
        parent_name, obj_name, suffix = object_name_from_file(file_name)

        parts = [p for p in rel_dir.split("/") if p]
        if parent_name:
            parts = parts + [parent_name, obj_name]
        else:
            parts = parts + [obj_name]

        if not suffix.endswith(".st"):
            # A native file that has a TYPED SIBLING is export-only: the sibling is the
            # importable form and this one is written for fidelity and for the diff.
            #
            # Sibling adjacency rather than a suffix of its own, because renaming
            # `Task Configuration.xml` would rewrite a committed file for a reason that has
            # nothing to do with the project. Once Phase 1 lands, the plan says which files
            # are importable and this rule can go.
            sibling = typed_sibling_for(abs_path)
            if sibling is not None:
                emit({"t": "not_imported", "path": rel_path,
                      "reason": "TYPED_SIBLING", "sibling": sibling})
                self.one_way += 1
                return

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
        typed = []
        native = []
        for dir_path, _dir_names, file_names in os.walk(self.src_root):
            for file_name in sorted(file_names):
                # Versio's own bookkeeping - the manifest and the coverage report.
                # They describe the export; they are not objects in it, and handing
                # one to CODESYS as native XML is how a junk object gets created.
                if is_versio_file(file_name):
                    continue

                # Real project state, written for review, with no import path that
                # can be trusted - library references today. Skipped here rather than
                # sorted into the native list, because "native" means "hand it to
                # import_native" and this is YAML.
                if is_export_only(file_name):
                    self.skip_one_way(
                        os.path.relpath(os.path.join(dir_path, file_name),
                                        self.src_root).replace("\\", "/"))
                    continue

                abs_path = os.path.join(dir_path, file_name)
                rel_path = os.path.relpath(abs_path, self.src_root).replace("\\", "/")

                # Typed files go with the textual phase, not the native one: they are
                # applied through properties and never reach import_native, so they must
                # not be in the list whose name means "hand this to CODESYS as XML".
                if is_typed_form(file_name):
                    typed.append((abs_path, rel_path))
                else:
                    (textual if file_name.endswith(".st") else native).append(
                        (abs_path, rel_path))

        emit({"t": "phase", "name": "textual", "files": len(textual) + len(typed)})
        for abs_path, rel_path in textual:
            self.handle_file(abs_path, rel_path)
        for abs_path, rel_path in typed:
            try:
                self.apply_tasks(abs_path, rel_path)
            except Exception as e:
                self.failed += 1
                self.warn("TASKS_APPLY_FAILED", rel_path, str(e))

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

    proj = remember_project(scriptengine.projects.open(job["project"]))
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
          "missing": imp.missing, "failed": imp.failed,
          "notImported": imp.one_way, "warnings": len(imp.warnings)})

    close_project(proj)
    emit({"t": "done"})
    return 0


try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})
    rc = 1
finally:
    # A no-op after a clean run. On a failure it is what stops a <project>.~u marker - and
    # a project this script may already have written to - outliving the process holding it.
    close_project()

emit({"t": "exit", "rc": rc})
