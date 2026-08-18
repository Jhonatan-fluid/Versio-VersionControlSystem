# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS.
#
# Versio importer. Executes a PLAN against a project - it no longer walks the export tree
# to decide anything. The plan arrives in the job file (schema 2), computed C#-side from
# the manifest by CodesysImportPlanner, and says per file: apply as text, apply through
# the typed API, hand to import_native, create, or skip and why. Files the plan does not
# mention are warned about and never touched.
#
# Strategy for the apply steps, in order of preference:
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
# job.json: { "schema": 2, "project": "...", "src": "<export root>", "save": true,
#             "plan": { "files": [ {"path", "action", "skip", "name", "resolve", "sibling"} ] } }

import io
import json
import os
import sys
import tempfile
import traceback

import scriptengine  # type: ignore

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from versio_decl import (DUT_ALIAS, DUT_ENUM, DUT_STRUCT, DUT_UNION,  # noqa: E402
                         KIND_DUT, KIND_FUNCTION, KIND_FUNCTION_BLOCK,
                         KIND_INTERFACE, KIND_PROGRAM, parse_declaration)
from versio_text import (WS, content_hash, join_st, normalize_decl_only,  # noqa: E402
                         scrub_native_xml, split_st)
from versio_types import (FORM_NATIVE_R, NATIVE_FORMS, can_verify,  # noqa: E402
                          is_versio_file)
from versio_native_equiv import normalize  # noqa: E402
import versio_tree  # noqa: E402
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


# Suffixes that decide HOW a textual file is applied - declaration only, implementation
# only, or both halves. Longest first so ".gvl.st" is matched before ".st".
#
# WHAT to do with a file, and WHICH object it is, no longer come from its name at all:
# the plan carries both, computed C#-side from the manifest (CodesysImportPlanner). The
# helpers that used to derive them here - object_name_from_file, and the disk probe for a
# typed sibling - are gone with the walk that called them. This list survives because the
# apply step still differs by suffix, and that really is a property of the file.
SUFFIXES = [".gvl.st", ".gvl.xml", ".vis.xml", ".action.st", ".transition.st", ".st", ".xml"]


def suffix_of(file_name):
    for suffix in SUFFIXES:
        if file_name.endswith(suffix):
            return suffix
    return ""


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


def find_by_path(proj, parts):
    """Navigate the object tree by name. Returns None when any segment is missing.

    Folder-blind, and that is the load-bearing part: a chain from the manifest names
    objects, and a folder is not one of the things it names. versio_tree carries the
    argument.
    """
    return versio_tree.find_by_path(proj, parts, safe)


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
        # Objects CODESYS reported an exception for, and objects it declined to import at
        # all. COUNTED, not just warned about - see the note on handle_file's native branch
        # for why the difference mattered enough to be worth two fields.
        self.errors = 0
        self.refused = 0

    def conflict(self, name, existingObject, newguid):
        self.replaced += 1
        return scriptengine.NativeImportResolve.replace

    def progress(self, name, pastedObject, exception):
        if exception:
            self.errors += 1
            self.owner.warn("NATIVE_IMPORT_ERROR", str(name), str(exception))
        else:
            self.pasted += 1

    def skipped(self, name):
        self.refused += 1
        self.owner.warn("NATIVE_IMPORT_SKIPPED_BY_CODESYS", str(name))

    @property
    def lost(self):
        """Objects in this file that did not reach the project, for any reason."""
        return self.errors + self.refused


class Importer(object):
    """Executes a plan. Decides nothing.

    The walk that used to live here derived everything from file names, and that
    derivation was the shared root cause of four of the import plan's gaps: a created
    object named after its file instead of its declaration (G3), encoded stems compared
    against raw object names (G4), the owner split guessed from a dot (G5), and any
    unrecognised file handed to import_native (G6). The plan is computed C#-side from the
    manifest - CodesysImportPlanner, pure and tested in milliseconds - and arrives through
    the job file. What remains here is the half only this process can do: calling the
    ScriptEngine.
    """

    def __init__(self, proj, src_root, plan, save_between_phases=True):
        self.proj = proj
        self.src_root = src_root
        self.plan = plan

        # The job's `save` flag, honoured by the phase-split save below as well as by the
        # final save in main(). Nothing sends false today; the flag exists so that if
        # anything ever does, the phase save does not persist what the final save was told
        # not to.
        self.save_between_phases = save_between_phases
        self.updated = 0
        self.unchanged = 0
        self.missing = 0
        self.failed = 0
        self.native = 0
        self.created = 0
        self.one_way = 0

        # Files whose native import applied SOME of their objects and lost others. Counted in
        # `failed` as well - this is not a second failure count, it is the answer to "did the
        # project change anyway", which decides whether the run saves.
        #
        # Without it, making a partial import stop incrementing `native` would have made
        # `dirty` below read False on exactly the run that had already written to the project,
        # so the objects that DID paste would be dropped on close. Fixing a lying counter must
        # not cost somebody the half of the import that worked.
        self.partial = 0

        # Objects taken out of the project, and deletions this script refused to perform.
        # Separate counters because they answer different questions: the first is what
        # changed, the second is what somebody asked for and did not get - and a refusal
        # that only appears as a smaller number than expected is a refusal nobody reads.
        self.removed = 0
        self.kept = 0

        # Objects renamed or moved. In the `dirty` test below for the same reason `removed`
        # and `partial` are: a run whose only work was a rename would otherwise close
        # without saving, and the object would come back under its old name. That is the
        # third time this project would have paid for the same omission.
        self.renamed = 0

        # Task POU references repointed at a renamed object. Counted separately from
        # `renamed` because it answers a different question: how much of the project had to
        # be corrected BECAUSE of a rename, which is the number that would have been zero
        # while Versio was claiming references looked after themselves.
        self.retargeted = 0
        self.warnings = []

        # Why the last native re-export failed, so the ONE warning the caller emits can carry
        # the toolchain's own words. Initialised here rather than relied on being set by the
        # path that writes it: IronPython raises AttributeError on an unset attribute, and
        # that would turn a refusal-with-an-explanation into a fatal.
        self.reexport_error = None

    def warn(self, code, path, detail=None):
        self.warnings.append({"code": code, "path": path, "detail": detail})
        emit({"t": "warning", "code": code, "path": path, "detail": detail})

    # An export-only skip is emitted and counted, NOT warned. A warning that fires on
    # every single import of every project that has a library manager is noise, and noise
    # is how a real native-import failure gets read past - the exact failure mode
    # _coverage.md caused before it was excluded. announce_skip carries the rule now.

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
        # WS rather than a bare strip(), for the reason versio_text.WS gives: `decl` came
        # from split_st and was trimmed with WS, so comparing it against a differently
        # trimmed live text would report an unchanged object as changed and rewrite it on
        # every single import.
        if old_decl is not None and old_decl.replace(u"\r\n", u"\n").strip(WS) != decl:
            obj.textual_declaration.replace(decl + u"\n")
            changed = True

        if safe(lambda: obj.has_textual_implementation, False):
            old_impl = safe(lambda: obj.textual_implementation.text, None)
            if old_impl is not None and old_impl.replace(u"\r\n", u"\n").strip(WS) != impl:
                obj.textual_implementation.replace(impl + u"\n")
                changed = True

        if changed:
            self.updated += 1
            emit({"t": "updated", "path": rel_path, "part": "both"})
        else:
            self.unchanged += 1

    def create_textual(self, owner_parts, name, abs_path, rel_path, suffix):
        """Create a textual object that does not exist yet.

        Everything create_pou / create_dut need is stated in the declaration itself, so it
        is parsed rather than guessed. An unrecognised declaration is REPORTED, never
        approximated: creating the wrong kind of object is worse than creating none.

        The NAME arrives from the plan, which read it out of the declaration C#-side -
        this used to be parts[-1], the file's stem, which is how PROGRAM Foo in Bar.st
        created an object called Bar (G3).
        """
        owner = find_by_path(self.proj, owner_parts) if owner_parts else self.proj
        if owner is None:
            self.missing += 1
            self.warn("PARENT_NOT_FOUND", rel_path, "/".join(owner_parts))
            return None

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

    def apply_tasks(self, abs_path, rel_path, chain):
        """Apply a `.tasks.yaml` through the typed API.

        THIS IS THE POINT OF PHASE 4. Task configuration used to be handed to
        import_native, and that rewrote Core2 - a task's CPU core affinity - on every
        import without being asked, and threw on the task objects on every import after the
        first while the run reported failed: 0. Setting the fields through properties
        instead means CODESYS is told exactly what changed and nothing else moves.

        `chain` is the plan's resolve path to the configuration object - real names, from
        the manifest. The file's directory and its own `configuration:` key used to supply
        this; the manifest is the record the export actually wrote, so it wins.
        """
        top, list_key, tasks = versio_yaml.parse_document(read_text(abs_path))
        if list_key != "tasks":
            self.warn("TASKS_DOCUMENT_UNRECOGNISED", rel_path, str(list_key))
            return

        config = find_by_path(self.proj, chain)
        if config is None:
            self.missing += 1
            self.warn("PARENT_NOT_FOUND", rel_path, "/".join(chain))
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

    def announce_skip(self, entry):
        """Say what is deliberately not imported, in the words the log already uses.

        Bookkeeping is silent: it is present in every mirror, and a line that always
        appears is how a real one gets read past. exportOnly emits the not_imported event -
        reason TYPED_SIBLING when the manifest names an editable sibling, EXPORT_ONLY when
        the change belongs in the IDE - so the log keeps saying "edit X instead". stray and
        unknownForm WARN, and that warning IS the fix for G6: before the plan, a stray file
        was not skipped at all - the walk's last branch handed it to import_native.
        """
        rel_path = entry["path"]
        skip = entry.get("skip")
        if skip == "bookkeeping":
            return
        if skip == "exportOnly":
            self.one_way += 1
            sibling = entry.get("sibling")
            if sibling:
                emit({"t": "not_imported", "path": rel_path,
                      "reason": "TYPED_SIBLING", "sibling": os.path.basename(sibling)})
            else:
                emit({"t": "not_imported", "path": rel_path, "reason": "EXPORT_ONLY"})
            return
        if skip == "stray":
            self.warn("NOT_IN_EXPORT", rel_path,
                      "not a file the export writes - never handed to the toolchain")
            return
        if skip == "renameTarget":
            # SILENT, like bookkeeping. This file is where a renamed object's content already
            # is: the rename phase moved the object, so applying the file would write content
            # the object already has, and creating it would produce a duplicate. The rename
            # itself is announced by its own event, which is the line worth reading - saying
            # it twice, once as a skip, would make an ordinary rename look like two events.
            return
        self.warn("FORM_NOT_UNDERSTOOD", rel_path, str(entry.get("name")))

    def entry_paths(self, entry, needs_chain=True):
        """The absolute path, relative path and resolve chain of one plan entry, or None
        when the entry cannot be executed - its file vanished after planning, or the plan
        carries no resolve chain to act on. Both are reported, not guessed around."""
        rel_path = entry["path"]
        abs_path = os.path.join(self.src_root, rel_path)
        if not os.path.isfile(abs_path):
            self.failed += 1
            self.warn("PLANNED_FILE_MISSING", rel_path,
                      "was in the mirror when the import was planned")
            return None

        chain = entry.get("resolve") or []
        if needs_chain and not chain:
            self.failed += 1
            self.warn("PLAN_ENTRY_UNRESOLVABLE", rel_path, "no resolve chain")
            return None

        return abs_path, rel_path, chain

    def handle_textual(self, entry):
        located = self.entry_paths(entry)
        if located is None:
            return
        abs_path, rel_path, chain = located

        obj = find_by_path(self.proj, chain)
        if obj is None:
            # "text" and "create" converge here on purpose: a planned update whose object
            # is gone (the manifest is older than the project) is created, and a planned
            # create that already exists is updated. The plan says what SHOULD exist;
            # the project says what does; acting on the difference is this side's job.
            obj = self.create_textual(chain[:-1], entry.get("name") or chain[-1],
                                      abs_path, rel_path, suffix_of(os.path.basename(rel_path)))
            if obj is None:
                return

        try:
            self.apply_st(obj, rel_path, abs_path,
                          suffix_of(os.path.basename(rel_path)))
        except Exception as e:
            self.failed += 1
            self.warn("APPLY_FAILED", rel_path, str(e))

    def handle_native(self, entry):
        located = self.entry_paths(entry)
        if located is None:
            return
        abs_path, rel_path, chain = located

        # Native XML: import into the OWNING node. This both replaces an existing object
        # in place and creates one that is not there yet, so it needs no existence check
        # of its own. The chain is the plan's - real decoded names from the manifest,
        # where the walk used to compare percent-encoded stems against raw names (G4).
        owner_parts = chain[:-1]
        owner = find_by_path(self.proj, owner_parts) if owner_parts else self.proj
        if owner is None:
            self.missing += 1
            self.warn("PARENT_NOT_FOUND", rel_path, "/".join(owner_parts))
            return

        existed = find_by_path(self.proj, chain) is not None
        try:
            handler = ReplaceHandler(self)
            owner.import_native([abs_path], None, handler)

            # import_native DOES NOT RAISE when an individual object fails. CODESYS hands
            # the exception to the handler's progress callback and carries on, so the call
            # above returns normally on a run that lost objects - which is why this branch
            # used to count `native += 1` and leave `failed` at zero, and the summary read
            # `native: 8, failed: 0` on the G11 runs where MainTask and VISU_TASK both
            # threw. The warnings were emitted all along; the one-line answer underneath
            # them said the opposite, and the one-line answer is what gets read.
            #
            # A file, not an object: `failed` counts files everywhere else in this script
            # (CREATE_FAILED, APPLY_FAILED, NATIVE_IMPORT_FAILED are all per file), so a
            # file that lost two objects counts once here and the per-object detail stays
            # in the warnings where it already was. Mixing the two units in one counter
            # would make the summary wrong in a subtler way than the bug being fixed.
            if handler.lost:
                self.failed += 1
                self.partial += 1
                emit({"t": "native_partial", "path": rel_path,
                      "into": "/".join(owner_parts) or "<project>",
                      "pasted": handler.pasted, "errors": handler.errors,
                      "refused": handler.refused})
                return

            self.native += 1
            if not existed:
                self.created += 1
            emit({"t": "native_imported", "path": rel_path,
                  "into": "/".join(owner_parts) or "<project>",
                  "replaced": existed})
        except Exception as e:
            self.failed += 1
            self.warn("NATIVE_IMPORT_FAILED", rel_path, str(e))

    def warn_unplanned(self, planned_paths):
        """Files on disk the plan has never heard of: they arrived between planning and
        execution. Warned, never imported - G6's rule does not get a race-window
        exception, and a file nobody planned is a file nobody agreed to apply."""
        for dir_path, _dir_names, file_names in os.walk(self.src_root):
            for file_name in sorted(file_names):
                rel_path = os.path.relpath(
                    os.path.join(dir_path, file_name), self.src_root).replace("\\", "/")
                if rel_path in planned_paths or is_versio_file(file_name):
                    continue
                self.warn("NOT_IN_PLAN", rel_path,
                          "appeared after the import was planned - not imported")

    def exported_text_of(self, obj, form):
        """Rebuild what the exporter would have written for this object, or None.

        TEXTUAL forms only - CAN_VERIFY_FORMS is exactly the set this handles, and a native
        one goes to normalized_native_of instead, which compares through a re-export rather
        than a rebuild. So a None from here means the live object did not yield its text: a
        different failure from "this form can never be checked", and reported as a different
        code.

        The content check turns on this being EXACTLY what versio_export.py produces:
        join_st for a full ST body, normalize_decl_only for a declaration- or
        implementation-only one. If the two ever diverge the check stops meaning
        "unchanged" and starts meaning "exported by a different version", which would
        refuse every deletion for a reason that has nothing to do with the object.
        """
        if form == "ST_FULL":
            decl = safe(lambda: obj.textual_declaration.text, None)
            impl = safe(lambda: obj.textual_implementation.text, None)
            if decl is None or impl is None:
                return None
            return join_st(decl, impl)

        if form == "ST_DECL":
            decl = safe(lambda: obj.textual_declaration.text, None)
            return None if decl is None else normalize_decl_only(decl)

        if form == "ST_IMPL":
            impl = safe(lambda: obj.textual_implementation.text, None)
            return None if impl is None else normalize_decl_only(impl)

        return None

    def rename_planned(self):
        """Move and rename objects whose file changed name or folder in the mirror.

        WHY THIS EXISTS AT ALL. A rename in the mirror is indistinguishable from a deletion
        and an unrelated create, and importing it as one destroys the object and builds a
        new one. The new object gets a new GUID, and every reference, task assignment and
        visualisation binding that pointed at the old one is left pointing at nothing. P1
        and P2 measured that rename() and move() preserve the GUID - in session and across
        a save and reopen - which is the whole reason for doing this rather than the pair.

        NO CONTENT CHECK, and that is a deliberate difference from delete_planned. A
        deletion has one because being wrong destroys work that exists nowhere else; a
        rename destroys nothing, so an object edited in the IDE since the export keeps its
        edit and simply gains its new name. What a rename does need is certainty about
        WHICH object it is, and that comes from the planner: a rename is only ever built
        out of a deletion candidate, so it has already passed the recorded-owner-chain rule.
        """
        renames = self.plan.get("renames") or []
        if not renames:
            return

        emit({"t": "phase", "name": "rename", "files": len(renames)})

        for rename in renames:
            old_name = rename.get("oldName") or ""
            new_name = rename.get("newName") or ""
            chain = rename.get("resolve") or []
            parent_chain = rename.get("newParent") or []

            if not chain or not new_name:
                self.failed += 1
                self.warn("RENAME_INCOMPLETE", old_name,
                          "the plan carries no location or no new name")
                continue

            obj = find_by_path(self.proj, chain)
            if obj is None:
                # Somebody already renamed it in the IDE, or a previous import did. The
                # mirror and the project may well agree; either way there is nothing here to
                # act on and inventing one would be guessing.
                self.warn("RENAME_NOT_FOUND", old_name,
                          "no object at %s" % "/".join(chain))
                continue

            # The move first, then the rename. Moving by the OLD name is the only order that
            # works: after a rename the object no longer answers to the name the plan's
            # chain was built from, so a failure between the two steps would leave nothing
            # able to find it again.
            if rename.get("isMove"):
                parent = (find_by_path(self.proj, parent_chain) if parent_chain
                          else self.proj)
                if parent is None:
                    self.failed += 1
                    self.warn("RENAME_NO_PARENT", old_name,
                              "no object at %s to move it into" % "/".join(parent_chain))
                    continue
                try:
                    # -1 is not a style choice. move(dest, 0) raises "index must be -1 if the
                    # new parent node does not have sorted children", and the one-argument
                    # move(dest) raises a NullReferenceException. Measured, both.
                    obj.move(parent, -1)
                except Exception as e:
                    self.failed += 1
                    self.warn("MOVE_FAILED", old_name, str(e))
                    continue

            if old_name != new_name:
                try:
                    obj.rename(new_name)
                except Exception as e:
                    # A duplicate name lands here, which is the collision the phase ordering
                    # in run() cannot solve. Counted as a failure so the summary says so.
                    self.failed += 1
                    self.warn("RENAME_FAILED", old_name, str(e))
                    continue

            # CONFIRM IT ACTUALLY HAPPENED, because move() does not always say when it did
            # not. Measured on 3.5.22.20: moving a POU out of Application and into Plc Logic
            # raises nothing, returns nothing, and leaves the object exactly where it was.
            # The import then reported "renamed 1" and a re-export showed the old path - a
            # success message for work that did not occur, which is the same lie as a dialog
            # promising an object will stay and then deleting it.
            #
            # So the object is re-resolved at the location it was supposed to end up. This is
            # cheap - a walk of names already in memory - and it is the only thing standing
            # between "CODESYS declined" and "Versio said it worked".
            #
            # WHAT IS DECLINED IS NOW MEASURED - P11, nine ordered moves on one project,
            # each re-resolved afterwards (and run twice: the first run's ninth step never
            # executed, which three documents missed - the probe finding in FINDINGS.md):
            #
            #   performed   a POU into a folder, into a folder inside that folder, and back
            #               out into Application; a GVL to the PROJECT ROOT; an EMPTY FOLDER
            #               into Plc Logic, and into an ordinary folder
            #   declined    a POU into Device; a visualisation into Plc Logic; a folder
            #               HOLDING A POU into Plc Logic
            #   raised      nothing, ever - every call returned None
            #
            # THE DESTINATION IS NOT WHAT DECIDES, and the first version of this comment said
            # it was. An empty folder goes into Plc Logic quite happily while a POU does not,
            # and a folder stops being movable there once a POU is inside it - so what is
            # being moved matters as much as where. It reads like "the destination must be
            # able to hold the whole subtree", but that is an inference from nine rows and it
            # is not what this code relies on.
            #
            # What this code relies on is that every refusal is SILENT. The read-back below
            # is not belt-and-braces; it is the only signal that exists.
            landed = find_by_path(self.proj, list(parent_chain) + [new_name])
            if landed is None:
                self.failed += 1
                # The sentence names what was measured rather than advising in general,
                # because the rule is one version's and the person reading it has to decide
                # whether their destination is one of the two kinds.
                detail = ("the toolchain accepted the %s without error but the object is not "
                          "at %s - it has been left where it was"
                          % ("move" if rename.get("isMove") else "rename",
                             "/".join(list(parent_chain) + [new_name])))
                if rename.get("isMove"):
                    detail += (". Measured on 3.5.22.20: CODESYS declines a move in silence "
                               "when the destination cannot hold what is being moved - a POU "
                               "into Device, a visualisation into Plc Logic, a folder with a "
                               "POU in it into Plc Logic - while moves within the application "
                               "are performed. Move it in the IDE if that is what you meant")
                self.warn("RENAME_NOT_APPLIED", old_name, detail)
                continue

            self.renamed += 1
            emit({"t": "renamed", "name": old_name, "newName": new_name,
                  "path": "/".join(chain), "newParent": "/".join(parent_chain),
                  "moved": bool(rename.get("isMove"))})

            if old_name != new_name:
                self.retarget_task_pous(old_name, new_name)

    def retarget_task_pous(self, old_name, new_name):
        """Point every task that ran `old_name` at `new_name`.

        WHY THIS IS NOT OPTIONAL. A task's POU list is stored BY NAME, and rename() does not
        rewrite it - measured: renaming Simulation to Renamed left MainTask listing
        Simulation, a POU that no longer existed. The object survived with its GUID and the
        thing that ran it did not follow, so the rename produced a project that was quietly
        broken in a way nothing reported.

        Versio claimed the opposite in its own confirmation dialog for a while, on the
        strength of the GUID surviving. It is fixed here rather than only in the wording.

        THE API, all of it measured because none of it is guessable:
          - the list is a ScriptPouObjectList of plain str, not of objects;
          - `remove(name)` REPORTS SUCCESS AND DOES NOTHING - a silent no-op of exactly the
            kind move() turned out to be;
          - `remove(index)` raises "not found in the specified Collection", so the argument
            is an item and an int is not one;
          - `replace(name, name)` raises "expected int, got str";
          - `replace(index, name)` works, and survives a save and reopen.

        BY NAME IS ALL THERE IS, so this is as exact as the toolchain allows: two objects
        called Simulation in different folders are one entry in a task list, and nothing in
        the list says which was meant. Renaming one retargets the entry either way - the same
        ambiguity CODESYS itself has, not one added here.
        """
        for task in self.walk_tasks(self.proj):
            pous = safe(lambda: list(task.pous), None)
            if not pous:
                continue

            for index, name in enumerate(pous):
                if name != old_name:
                    continue
                try:
                    task.pous.replace(index, new_name)
                except Exception as e:
                    self.warn("TASK_POU_RETARGET_FAILED",
                              safe(lambda: task.get_name(), ""), str(e))
                    continue

                self.retargeted += 1
                emit({"t": "task_pou_retargeted",
                      "task": safe(lambda: task.get_name(), ""),
                      "from": old_name, "to": new_name})

    def walk_tasks(self, node, depth=0):
        """Every object in the project that carries a POU list.

        Found by probing for the attribute rather than by looking for a TASK type GUID. The
        type table is incomplete by nature - that is written down in versio_types - and a
        task under an unmapped container would be missed by a type walk while still holding
        a reference this has to fix.

        Depth-capped for the same reason the export's walk is: a cycle in the object tree
        would otherwise hang an import with no output.
        """
        if depth > 32:
            return

        for child in safe(lambda: node.get_children(), []) or []:
            if safe(lambda: child.pous, None) is not None:
                yield child

            for found in self.walk_tasks(child, depth + 1):
                yield found

    def delete_planned(self):
        """Remove objects the mirror no longer has any file for.

        The planner has already checked everything it can see: a trusted manifest, every
        file for the GUID gone, export-only records neither voting nor vetoing, nothing
        surviving inside the object at any depth, and an owner chain that was recorded
        rather than guessed. What only this process can check is the project itself - that
        the object is still there, and that it still matches what was exported.

        THAT LAST CHECK IS THE POINT. A file missing from the mirror says somebody deleted
        it. It does not say the object is unchanged since: an engineer may have edited it in
        the IDE in the meantime, and that work exists nowhere else. So a live object whose
        content no longer hashes to what the manifest recorded is KEPT, and said so.
        """
        deletions = self.plan.get("deletions") or []
        if not deletions:
            return

        emit({"t": "phase", "name": "delete", "files": len(deletions)})

        for deletion in deletions:
            name = deletion.get("name") or ""
            chain = deletion.get("resolve") or []
            files = deletion.get("files") or []

            if not chain:
                # Nothing to walk. Refusing beats guessing at a location for an operation
                # that cannot be undone.
                self.kept += 1
                self.warn("DELETE_NO_CHAIN", name, "the plan carries no resolve chain")
                continue

            obj = find_by_path(self.proj, chain)
            if obj is None:
                # Already gone - somebody deleted it in the IDE, or a previous import did.
                # The mirror and the project agree; there is simply nothing left to do, so
                # this is not a failure and not a refusal.
                emit({"t": "already_gone", "name": name, "path": "/".join(chain)})
                continue

            refusal = self.why_not_delete(obj, name, files)
            if refusal is not None:
                self.kept += 1
                self.warn(refusal[0], name, refusal[1])
                continue

            try:
                obj.remove()
            except Exception as e:
                self.failed += 1
                self.warn("DELETE_FAILED", name, str(e))
                continue

            self.removed += 1
            emit({"t": "removed", "name": name, "path": "/".join(chain),
                  "files": [f.get("path") for f in files]})

    def why_not_delete(self, obj, name, files):
        """(code, detail) when this deletion must not happen, or None to proceed."""
        if not files:
            return ("DELETE_NO_RECORD",
                    "the plan lists no exported file for it, so there is nothing to check "
                    "the live object against")

        for entry in files:
            form = entry.get("form") or ""
            recorded = entry.get("hash") or ""
            nrecorded = entry.get("nhash") or ""

            if not can_verify(form, nrecorded):
                # Either a form that can never be checked, or a native one exported before
                # Versio recorded a normalised hash. Both are predictable without opening the
                # project, which is why the planner says the same thing to the dialog.
                return ("DELETE_UNVERIFIABLE",
                        "its %s file cannot be compared against the live object, so whether "
                        "it changed since the export is unknowable" % (form or "recorded"))

            if form in NATIVE_FORMS:
                live = self.normalized_native_of(obj, form)
                if live is None:
                    return ("DELETE_UNREADABLE",
                            "its %s file could not be re-exported from the live object, so "
                            "the deletion cannot be checked: %s"
                            % (form, self.reexport_error or "no reason reported"))
                compare_to = nrecorded
            else:
                live = self.exported_text_of(obj, form)
                if live is None:
                    # A form that CAN be checked, on an object that would not give up its
                    # text. Reported apart from the line above because they are different
                    # problems: one is a limit of this version and the other is this object,
                    # right now, and merging them would have the log blame the form for
                    # something it did not do.
                    return ("DELETE_UNREADABLE",
                            "its %s file should be rebuildable but the live object returned "
                            "no text, so the deletion cannot be checked" % form)
                compare_to = recorded

            if content_hash(live) != compare_to:
                return ("DELETE_CONTENT_CHANGED",
                        "the object in the project no longer matches what was exported to "
                        "%s, so it has been edited since - the missing file is not "
                        "permission to destroy that work" % entry.get("path"))

        return None

    def normalized_native_of(self, obj, form):
        """Re-export this object natively and return its comparison form, or None.

        The native half of the content check, and the thing that closes Phase 2's open
        half. It cannot work the way the textual half does: `hash` in the manifest is over
        the exported bytes, and those churn - three round trips of the fixture with nothing
        edited came back byte-different on 4 native files every time - so a fresh export
        never reproduces the recorded value. The manifest carries `nhash` as well from
        schema 5, over the NORMALISED payload, and normalisation is exactly what removes
        that churn.

        Every step here has to match versio_export.py, because the recorded value was
        produced by it:

          - recursive only for NATIVE_R, which is the rule export_native is called with;
          - scrub_native_xml, which removes the Timestamp CODESYS re-mints per run;
          - normalize with masking left at its default.

        Any divergence turns the check from "unchanged" into "exported by a different
        version of Versio", which would refuse every native deletion for a reason that has
        nothing to do with the object.
        """
        # The system temp directory, NOT the mirror. The mirror is a git working tree with a
        # RepositoryWatcher on it, and writing into it mid-import would raise a change event
        # for a file that is not part of the export - a check that was supposed to observe the
        # project and nothing else, kicking the sync engine instead.
        handle, tmp = tempfile.mkstemp(suffix=".xml", prefix="versio_delete_check_")
        os.close(handle)
        try:
            try:
                obj.export_native(tmp, form == FORM_NATIVE_R)
                raw = read_text(tmp)
            finally:
                if os.path.isfile(tmp):
                    os.remove(tmp)
        except Exception as e:
            # Recorded, NOT warned about here. The caller turns this into one
            # DELETE_UNREADABLE carrying this text: warning twice for one event would put two
            # lines in front of somebody for a single refusal, and the more informative of the
            # two would be the one the summary did not count.
            self.reexport_error = str(e)
            return None

        self.reexport_error = None
        scrubbed, _ = scrub_native_xml(raw)
        return normalize(scrubbed)[0]

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

        The lists used to be built by walking the mirror; they are read off the plan now.
        The phase split and everything below it survives unchanged - WHAT to import moved
        C#-side, WHEN to save did not.
        """
        entries = self.plan.get("files") or []
        by_action = {}
        for entry in entries:
            by_action.setdefault(entry.get("action"), []).append(entry)

        for entry in by_action.get("skip", []):
            self.announce_skip(entry)

        # "create" joins the textual phase: the planner only plans creates for textual
        # sources, and handle_textual converges create-and-update anyway. Typed files go
        # with the textual phase, not the native one: they are applied through properties
        # and never reach import_native, so they must not be in the list whose name means
        # "hand this to CODESYS as XML".
        textual = by_action.get("text", []) + by_action.get("create", [])
        typed = by_action.get("typed", [])
        native = by_action.get("native", [])

        # RENAMES AND MOVES FIRST, before anything is applied or removed.
        #
        # The object has to be at its final name and under its final owner before the
        # textual and native phases resolve anything, because those phases walk the tree by
        # NAME. Renaming afterwards would have them resolve the object under its old name -
        # which still exists at that point, so nothing would fail; the content would simply
        # land in the object about to be renamed, and the rename would then move an object
        # carrying somebody else's edit.
        #
        # THE ORDER IT DOES NOT SOLVE, said plainly rather than implied. Renaming A to B
        # while a DIFFERENT object called B is still in the project cannot work from here:
        # deletions run last, for reasons that outrank this, so B is still there. CODESYS
        # refuses the duplicate and the rename is reported failed - which is the right
        # outcome and not a silent one, but it means "swap two objects' names in one commit"
        # needs two imports. Ordering renames after deletes to fix it would put a
        # destructive step before a safe one, and that trade is not worth making.
        self.rename_planned()

        emit({"t": "phase", "name": "textual", "files": len(textual) + len(typed)})
        for entry in textual:
            self.handle_textual(entry)
        for entry in typed:
            located = self.entry_paths(entry)
            if located is None:
                continue
            abs_path, rel_path, chain = located
            try:
                self.apply_tasks(abs_path, rel_path, chain)
            except Exception as e:
                self.failed += 1
                self.warn("TASKS_APPLY_FAILED", rel_path, str(e))

        # UNCONDITIONAL since 2026-08-17, and the history of this line is the argument for
        # it. It read `if self.created > 0 or self.updated > 0`, which is the same shape as
        # the dirty-test bug this file has paid for twice: a condition deciding whether to
        # SAVE that does not count every way the project has been written. Renames run
        # before this line and mutate the very name dictionary the docstring above
        # describes, and were not counted; a native-only import skipped the save entirely -
        # the exact shape of run the phase split exists to protect, carried in the import
        # plan as an open judgement call. One save per import is cheaper than the question,
        # and a condition that cannot be wrong is cheaper than one that has to be kept in
        # step with every new phase.
        if self.save_between_phases:
            self.proj.save()
            emit({"t": "phase_saved", "after": "textual"})

        emit({"t": "phase", "name": "native", "files": len(native)})
        for entry in native:
            self.handle_native(entry)

        # DELETIONS LAST, and that is not arbitrary.
        #
        # Everything above puts objects into the project; this takes them out. Running it
        # first would remove an object a later step might still be resolving through - and
        # P3 measured that remove() takes a referenced object out silently, with no
        # exception, no cascade and no warning. Last means the project is otherwise complete
        # at the moment anything is destroyed.
        self.delete_planned()

        # An action this script has never heard of would otherwise vanish without a trace:
        # bucketed above, consumed by nothing, and exempt from NOT_IN_PLAN because the file
        # IS in the plan. A newer C# planner speaking a newer verb must be heard refusing,
        # not silently ignored - the same rule as the schema gate, one level down.
        for action, dropped in by_action.items():
            if action not in ("skip", "text", "create", "typed", "native"):
                for entry in dropped:
                    self.failed += 1
                    self.warn("UNKNOWN_PLAN_ACTION", entry.get("path"), str(action))

        self.warn_unplanned(set(entry.get("path") for entry in entries))


def main():
    args = sys.argv[1:]
    if not args:
        emit({"t": "fatal", "msg": "no job file argument"})
        return 2
    with open(args[0], "r") as f:
        job = json.load(f)

    # BEFORE the project is opened, so a refused job holds nothing. The schema is the job
    # file's shape, and this script executes exactly the plan a schema-2 job carries -
    # refusing an unexpected schema outright beats half-executing it, which is how a stale
    # extracted script meeting a newer job file would corrupt quietly.
    schema = job.get("schema", 1)
    if schema != 2:
        emit({"t": "fatal",
              "msg": "job schema %s is not the 2 this script executes - refusing" % schema})
        return 2

    plan = job.get("plan")
    if plan is None:
        emit({"t": "fatal", "msg": "a schema 2 import job carries a plan; this one does not"})
        return 2

    emit({"t": "start", "project": job["project"], "src": job["src"]})

    proj = remember_project(scriptengine.projects.open(job["project"]))
    emit({"t": "opened", "path": str(proj.path)})

    imp = Importer(proj, job["src"], plan, job.get("save", True))
    imp.run()

    # `partial` is in here because a native import that lost some objects still applied the
    # rest. It no longer counts towards `native`, and if the save keyed only on that, the run
    # that wrote to the project would be the one that did not save it.
    # `removed` is in here for the same reason `partial` is, and the reason is a bug this
    # project has already paid for once: a counter that decides whether to SAVE must count
    # every way the project can have been written to. A run whose only work was deleting an
    # object would otherwise close without saving, putting the object back - an import that
    # reported success, removed something, and changed nothing.
    dirty = (imp.updated > 0 or imp.native > 0 or imp.partial > 0
             or imp.removed > 0 or imp.renamed > 0 or imp.retargeted > 0)
    if job.get("save", True) and dirty:
        proj.save()
        emit({"t": "saved"})
    elif not dirty:
        emit({"t": "nothing_to_save"})

    emit({"t": "summary", "updated": imp.updated, "unchanged": imp.unchanged,
          "native": imp.native, "created": imp.created,
          "missing": imp.missing, "failed": imp.failed,
          "partial": imp.partial, "removed": imp.removed, "renamed": imp.renamed,
          "retargeted": imp.retargeted,
          "kept": imp.kept,
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
