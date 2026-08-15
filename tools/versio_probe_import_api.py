# -*- coding: utf-8 -*-
# REMEMBER: this runs in CODESYS ScriptEngine = IronPython 2.7. No f-strings, no dict
# comprehensions with unpacking, and `except X as e` is the only spelling that works.
#
# Phase 0 probes P1-P6, P8 and P9 of docs/CODESYS-IMPORT-PLAN.md. Every one of them exists
# because the plan asserts something about the ScriptEngine API that nobody has run.
#
# ---------------------------------------------------------------------------------------
# WHY THIS LIVES IN tools/ AND NOT IN src/Versio.Scripts.Codesys/
#
#   Versio.Infrastructure.csproj embeds src\Versio.Scripts.Codesys\*.py into the shipping
#   assembly. This script RENAMES, MOVES, REMOVES and CREATES objects. CODESYS.md B4
#   already records the discomfort of versio_delete_objects.py sitting inside the product
#   while never being part of it, and one script that can delete somebody's POUs shipping
#   by accident of a glob is enough. Run-Codesys.ps1 takes any path, so nothing is lost.
#
#   The cost of this choice, stated so it is not rediscovered: this file cannot import
#   versio_types / versio_text as siblings. It is therefore self-contained and depends on
#   nothing but scriptengine and the standard library.
# ---------------------------------------------------------------------------------------
#
# EVERY PROBE MUTATES THE PROJECT. Run each group against its own scratch copy;
# Probe-ImportApi.ps1 does that and is the intended entry point.
#
# job.json: { "project": "<abs path>", "probes": ["P1", "P2"] }

import json
import sys
import traceback

import scriptengine  # type: ignore


def emit(obj):
    """One JSON object per line. The @@VERSIO@@ marker is how the host separates our
    output from CODESYS's own chatter on the shared stdout."""
    print("@@VERSIO@@" + json.dumps(obj))


def safe(fn, default=None):
    """Call and swallow. Used for every property read on a ScriptObject, because several
    of them raise rather than return None for objects that do not have them."""
    try:
        return fn()
    except Exception:
        return default


def describe(fn):
    """Call and report either the value or the exception TYPE and message.

    The distinction matters more here than anywhere else in this repository: a probe that
    reports None for both "the property is absent" and "the property raised" answers the
    question it was written to ask with the same word for two different answers.
    """
    try:
        return {"ok": True, "value": _plain(fn())}
    except Exception as e:
        return {"ok": False, "error": type(e).__name__, "message": str(e)}


def _plain(v):
    """Make a CLR value JSON-safe without pretending it was a string all along."""
    if v is None or isinstance(v, (bool, int, float)):
        return v
    try:
        return unicode(v)  # noqa: F821  - IronPython 2.7
    except Exception:
        return repr(v)


_OPEN = {"project": None}


def close_project():
    """Same reasoning as versio_export/versio_import: CODESYS writes <project>.~u while it
    holds a project and removes it on close, so a script that dies in between leaves a
    marker the NEXT run has to treat as a crashed IDE."""
    handle = _OPEN["project"]
    _OPEN["project"] = None
    if handle is None:
        return
    try:
        handle.close()
    except Exception as e:
        emit({"t": "warning", "code": "PROJECT_CLOSE_FAILED", "detail": str(e)})


def open_project(path):
    proj = scriptengine.projects.open(path)
    _OPEN["project"] = proj
    return proj


def members(obj):
    """What this object is and what it exposes.

    MEASURED 2026-08-15: `dir()` on a ScriptObject returns an EMPTY list. These are CLR
    proxies and IronPython does not enumerate their members, so introspection from inside
    the script cannot answer "does rename exist" -- which is precisely why the plan says to
    reflect over ScriptEngine3.dll from outside and calls reflection authoritative. The
    interface names below come from the CLR type instead, and they are the useful half:
    `MainTask` reporting a task interface is what says the typed Phase 4 path is real.

    So a probe here proves a member exists by CALLING it, never by looking for it.
    """
    out = {"dir": sorted([n for n in dir(obj) if not n.startswith("_")])}
    clr_type = safe(lambda: obj.GetType(), None)
    if clr_type is not None:
        out["clr_type"] = _plain(safe(lambda: clr_type.FullName, None))
        out["interfaces"] = sorted([_plain(i.Name) for i in
                                    (safe(lambda: clr_type.GetInterfaces(), []) or [])])
        out["clr_members"] = sorted(set([_plain(m.Name) for m in
                                         (safe(lambda: clr_type.GetMembers(), []) or [])
                                         if not _plain(m.Name).startswith("get_")
                                         and not _plain(m.Name).startswith("set_")]))
    return out


def walk(node, depth=0):
    """Every object under a node, breadth first, with its depth."""
    for child in safe(lambda: node.get_children(), []) or []:
        yield child, depth
        for pair in walk(child, depth + 1):
            yield pair


def find_named(proj, name):
    """First object anywhere with this name.

    ONLY SAFE FOR NAMES THAT OCCUR ONCE. The first version of P1 and P2 used this for
    'Simulation' and measured the wrong object entirely: a task's assignment to a POU is
    itself an object in the tree, of type CALL_TO_POU, and it is NAMED AFTER THE POU IT
    CALLS. So the first 'Simulation' the walk meets is MainTask's call reference, sitting
    under Task Configuration, and renaming that says nothing about renaming the POU.

    It reported guid_stable_after_reopen: false and old_name_still_present: true, which
    reads as a devastating finding about rename and is an artefact of the lookup. Use
    find_at for anything an identity claim depends on.
    """
    for obj, _d in walk(proj):
        if safe(lambda: obj.get_name(), None) == name:
            return obj
    return None


def find_at(proj, path):
    """Walk a named path level by level, so the object found is the one meant."""
    node = proj
    for segment in path:
        found = None
        for child in safe(lambda: node.get_children(), []) or []:
            if safe(lambda: child.get_name(), None) == segment:
                found = child
                break
        if found is None:
            return None
        node = found
    return node


# Where the fixture's POUs actually live. Named once so a probe cannot drift from it.
APP_PATH = ["Device", "Plc Logic", "Application"]


def find_pou(proj, name):
    return find_at(proj, APP_PATH + [name])


def ident(obj):
    """The three fields every identity probe compares."""
    return {
        "name": safe(lambda: obj.get_name(), None),
        "guid": _plain(safe(lambda: obj.guid, None)),
        "parent": _plain(safe(lambda: obj.parent.get_name(), None)),
        "index": safe(lambda: obj.index, None),
    }


# ============================================================================ P0
def probe_api(proj):
    """What the API actually offers. Everything else here depends on this being right."""
    out = {"project": members(proj)}

    pou = find_named(proj, "PLC_PRG")
    if pou is not None:
        out["object"] = members(pou)

    for name in ("Task Configuration", "MainTask", "Library Manager"):
        obj = find_named(proj, name)
        if obj is not None:
            out[name] = members(obj)

    # members() returns a dict for CLR objects; the module is a plain python object, so
    # its useful list is under "dir". Iterating members() directly yielded the dict KEYS
    # and reported an empty API surface.
    out["scriptengine"] = [n for n in members(scriptengine)["dir"]
                           if "Task" in n or "Native" in n or "Pou" in n or "Dut" in n]
    return out


# ============================================================================ P1
def probe_rename(proj, project_path):
    """G2. Does an object keep its GUID across a rename, and across a save/reopen?

    In-session preservation is necessary and not sufficient. What Phase 3 actually needs is
    that references, task assignments and visualisation bindings survive -- and those are
    resolved from the saved file. So this saves, closes, reopens and looks again.
    """
    target = find_pou(proj, "Simulation")
    if target is None:
        return {"verdict": "NO_TARGET", "detail": "Application/Simulation not found"}

    before = ident(target)
    new_name = "Simulation_VersioProbe"

    renamed = describe(lambda: target.rename(new_name))
    if not renamed["ok"]:
        return {"verdict": "RENAME_RAISED", "before": before, "error": renamed}

    after = ident(target)
    proj.save()
    close_project()

    reopened = open_project(project_path)
    found = find_pou(reopened, new_name)
    # The POU path only. A task call reference keeps the OLD name and would otherwise
    # read as "the rename did not take".
    old_still_there = find_pou(reopened, before["name"]) is not None
    persisted = ident(found) if found is not None else None

    return {
        "verdict": "RENAMED",
        "before": before,
        "after_in_session": after,
        "after_reopen": persisted,
        "guid_stable_in_session": before["guid"] == after["guid"],
        "guid_stable_after_reopen": persisted is not None and before["guid"] == persisted["guid"],
        "old_name_still_present": old_still_there,
    }


# ============================================================================ P2
def probe_move(proj, project_path):
    """G2. Same question for move, plus what `new_index` actually means -- the plan uses
    the parameter without knowing whether it is a position among siblings or something
    else, and Phase 3 has to pass it a number."""
    target = find_pou(proj, "Simulation")
    app = find_at(proj, APP_PATH)
    if target is None or app is None:
        return {"verdict": "NO_TARGET",
                "detail": "need Simulation and Application"}

    folder = describe(lambda: app.create_folder("VersioProbeFolder"))
    if not folder["ok"]:
        return {"verdict": "CREATE_FOLDER_RAISED", "error": folder}

    dest = find_at(proj, APP_PATH + ["VersioProbeFolder"])
    before = ident(target)

    # MEASURED: move(dest, 0) raises ValueError "index must be -1 if the new parent node
    # does not have sorted children", and move(dest) raises a NullReferenceException. So
    # -1 is not a "put it last" convenience, it is the only accepted value for an unsorted
    # parent -- which is what Phase 3 has to pass and what the plan does not say.
    moved = describe(lambda: target.move(dest, -1))
    attempts = {"minus_one": moved}
    if not moved["ok"]:
        attempts["zero"] = describe(lambda: target.move(dest, 0))
        attempts["one_arg"] = describe(lambda: target.move(dest))
        if not attempts["zero"]["ok"] and not attempts["one_arg"]["ok"]:
            return {"verdict": "MOVE_RAISED", "before": before, "attempts": attempts}

    after = ident(target)
    proj.save()
    close_project()

    reopened = open_project(project_path)
    found = find_at(reopened, APP_PATH + ["VersioProbeFolder", before["name"]])
    persisted = ident(found) if found is not None else None

    return {
        "verdict": "MOVED",
        "before": before,
        "after_in_session": after,
        "after_reopen": persisted,
        "guid_stable_in_session": before["guid"] == after["guid"],
        "guid_stable_after_reopen": persisted is not None and before["guid"] == persisted["guid"],
        "parent_changed": persisted is not None and persisted["parent"] != before["parent"],
    }


# ============================================================================ P3
def probe_remove_referenced(proj, project_path):
    """G1. remove() an object that something else references. Throws, cascades, or dangles?

    This is the probe the plan names as an open risk. Glob_Var is a GVL whose variables are
    used by the POUs, so removing it is exactly the dangerous case: not an isolated leaf.
    """
    target = find_pou(proj, "Glob_Var")
    if target is None:
        return {"verdict": "NO_TARGET", "detail": "Glob_Var not found"}

    before = ident(target)
    siblings_before = len([1 for _o, _d in walk(proj)])

    removed = describe(lambda: target.remove())
    if not removed["ok"]:
        return {"verdict": "REMOVE_RAISED", "before": before, "error": removed,
                "reading": "refuses to remove a referenced object"}

    still_there = find_named(proj, before["name"]) is not None
    siblings_after = len([1 for _o, _d in walk(proj)])

    saved = describe(lambda: proj.save())
    close_project()

    reopened = open_project(project_path)
    after_reopen = find_named(reopened, before["name"]) is not None
    count_reopen = len([1 for _o, _d in walk(reopened)])

    return {
        "verdict": "REMOVED",
        "before": before,
        "objects_before": siblings_before,
        "objects_after": siblings_after,
        "gone_in_session": not still_there,
        "gone_after_reopen": not after_reopen,
        "objects_after_reopen": count_reopen,
        "save": saved,
        "reading": "removed without complaint - the references are now dangling and "
                   "nothing warned, which is the case Phase 2's content check has to catch",
    }


# ============================================================================ P5 + P4
AWKWARD = [
    ("dotted", "My.Thing"),           # G5 - breaks the owner/member filename split
    ("encoded", "weird<name>"),       # G4 - needs percent-encoding on disk
    ("trailing_space", "Pump "),      # the collision safe_file_stem was fixed for
    ("trailing_dot", "Pump."),        # its twin
]


def probe_create_awkward(proj):
    """P5 then P4, in one run because P4 needs objects P5 creates.

    REACHABILITY FIRST. If CODESYS refuses to create a POU called `My.Thing`, then G5 is
    'cannot occur' and costs no defensive code at all -- which is a better outcome than
    handling it. The plan says so explicitly and nobody has asked CODESYS.
    """
    app = find_at(proj, APP_PATH)
    if app is None:
        return {"verdict": "NO_TARGET", "detail": "Application not found"}

    results = []
    for label, name in AWKWARD:
        created = describe(lambda: app.create_pou(name,
                                                  scriptengine.PouType.Program,
                                                  scriptengine.ImplementationLanguages.st))
        row = {"label": label, "requested": name, "create": created}

        if created["ok"]:
            obj = find_named(proj, name)
            # CODESYS may accept the call and store a DIFFERENT name. That is the answer
            # to G3 as much as an outright refusal is.
            row["actual_name"] = safe(lambda: obj.get_name(), None) if obj else None
            row["name_kept_verbatim"] = row["actual_name"] == name

            # P4: can CODESYS resolve what our filename encoding cannot?
            #
            # Three spellings, because the overload binding is exactly the thing being
            # probed. The plan cites find(String[] namePath) and find(name, recursive) from
            # reflection; how IronPython binds a python list to a CLR String[] versus a
            # params array is not something to assume, and guessing wrong here would report
            # "CODESYS cannot resolve this name" when the truth is "we called it wrong".
            row["find_list"] = describe(lambda n=name: _plain(proj.find(["Application", n])))
            row["find_varargs"] = describe(lambda n=name: _plain(proj.find("Application", n)))
            row["find_recursive"] = describe(lambda n=name: _plain(proj.find(n, True)))

        results.append(row)

    # P4, which has no group of its own and would otherwise go unanswered: every awkward
    # name was REFUSED above, so there is nothing encoded to look up. The overload binding
    # is still worth pinning down, because Phase 1 plans to resolve objects through
    # find(namePath) instead of parsing filenames, and "which spelling works" is the whole
    # of that plan's mechanism. 'Plc Logic' carries a space, which is the one awkward
    # character the fixture really has.
    finds = {}
    for label, args in (
        ("varargs_path", ("Device", "Plc Logic", "Application", "Simulation")),
        ("list_path", (["Device", "Plc Logic", "Application", "Simulation"],)),
        ("name_recursive", ("Simulation", True)),
        ("name_only", ("Simulation",)),
        ("spaced_name", ("Plc Logic", True)),
    ):
        finds[label] = describe(lambda a=args: _plain(proj.find(*a)))

    return {"verdict": "PROBED", "names": results, "find_overloads": finds,
            "reading": "a refusal closes the matching gap as unreachable; an acceptance "
                       "means the importer must handle it"}


# ============================================================================ P6
TASK_FIELDS = ["priority", "interval", "interval_unit", "core_binding",
               "watchdog", "pous", "event_pou_guid"]


def probe_task_object(proj, project_path):
    """G7. Is the typed ScriptTaskObject path sufficient to replace round-tripping Task
    Configuration as native XML? If it is, Phase 4 is real and the Core2 mutation and G11
    both go away with it."""
    task = find_named(proj, "MainTask")
    if task is None:
        return {"verdict": "NO_TARGET", "detail": "MainTask not found"}

    out = {"members": members(task), "read": {}}
    for field in TASK_FIELDS:
        out["read"][field] = describe(lambda f=field: getattr(task, f))

    # Write one back and confirm it takes. Priority is the safest: an integer with no
    # cross-object meaning, unlike core_binding which is what import_native keeps changing.
    original = safe(lambda: task.priority, None)
    out["original_priority"] = _plain(original)

    if original is not None:
        target_value = int(original) + 1 if int(original) < 30 else int(original) - 1

        # MEASURED: set_priority() does NOT exist -- AttributeError on ScriptObject. The
        # plan's API table lists set_core_binding / set_priority / set_interval from
        # reflection, and those names are wrong for what a script actually holds. Reading
        # all seven fields works, so the extender is there; only the setter spelling was
        # guessed. Direct property assignment is the .NET-natural alternative, tried first.
        # MEASURED, in order: set_priority() does not exist (AttributeError), and assigning
        # an int raises "expected str, got int". The property READS as the string "1", and
        # it wants a string back -- these fields are IEC literals, not numbers, which is
        # also why `interval` reads as "t#20ms" rather than as a duration.
        out["set_by_assignment_str"] = describe(
            lambda: setattr(task, "priority", str(target_value)))
        out["set_by_assignment_int"] = describe(
            lambda: setattr(task, "priority", target_value))
        out["set_by_method"] = describe(lambda: task.set_priority(target_value))

        out["read_back"] = describe(lambda: task.priority)
        out["took"] = safe(lambda: int(task.priority), None) == target_value

        # core_binding is the field import_native keeps changing (Core2, G7). If it takes a
        # typed write too, Phase 4 can set every field the native round trip was mangling.
        out["core_binding_before"] = describe(lambda: task.core_binding)
        out["set_core_binding"] = describe(
            lambda: setattr(task, "core_binding", -1))
        out["core_binding_after"] = describe(lambda: task.core_binding)

        proj.save()
        close_project()
        reopened = open_project(project_path)
        again = find_named(reopened, "MainTask")
        out["after_reopen"] = describe(lambda: again.priority) if again else None
        out["persisted"] = (again is not None
                            and safe(lambda: int(again.priority), None) == target_value)

    return out


# ============================================================================ P8
def probe_path_length(proj):
    """G9. Stems are capped at 80; the TOTAL path is not, and `.versio_staging` makes the
    staging path 16 characters longer than the mirror it becomes. Where does it break?

    This probes the CODESYS side only -- how deep a chain it will let us build. What the
    exporter then does with that is Versio's problem and is measured elsewhere.
    """
    app = find_at(proj, APP_PATH)
    if app is None:
        return {"verdict": "NO_TARGET", "detail": "Application not found"}

    segment = "VersioProbeDeepFolderSegment" + ("X" * 12)  # 40 chars
    node = app
    chain = []
    for depth in range(1, 11):
        made = describe(lambda n=node, d=depth: n.create_folder(segment + str(d)))
        # Each level contributes the segment, its depth digit and a separator. Written out
        # rather than derived from the accumulating list, which was clever and unreadable.
        chain.append({"depth": depth, "create": made,
                      "nominal_path_len": depth * (len(segment) + 2)})
        if not made["ok"]:
            return {"verdict": "CREATE_FAILED_AT_DEPTH", "depth": depth, "chain": chain}
        node = find_named(proj, segment + str(depth))
        if node is None:
            return {"verdict": "CREATED_BUT_NOT_FOUND", "depth": depth, "chain": chain}

    return {"verdict": "TEN_LEVELS_OK", "chain": chain,
            "segment_len": len(segment),
            "reading": "CODESYS imposes no obvious limit; the 260-character ceiling is "
                       "Windows' and therefore the exporter's to guard"}


# ============================================================================ P9
def probe_library_reference(proj):
    """G8. Every reference in the fixture is a placeholder, so the managed_library branch
    of versio_libraries.py has never executed. Can a fixed-version one be added quickly?"""
    libman = find_named(proj, "Library Manager")
    if libman is None:
        return {"verdict": "NO_TARGET", "detail": "Library Manager not found"}

    out = {"members": members(libman)}
    refs = safe(lambda: list(libman.references), None)
    out["reference_count"] = len(refs) if refs is not None else None

    if refs:
        sample = refs[0]
        out["reference_members"] = members(sample)
        out["sample"] = {}
        # MEASURED: the fields guessed first -- placeholder, version, resolution -- do not
        # exist. These are the ones the CLR type actually carries, and the distinction
        # matters for G8: `system_library` and `default_resolution` are how a fixed-version
        # reference would be told apart from a placeholder.
        for field in ("name", "namespace", "effective_resolution", "default_resolution",
                      "placeholder_name", "system_library", "optional", "resolver_guid",
                      "resolution_info"):
            out["sample"][field] = describe(lambda f=field, s=sample: getattr(s, f))

        # Is ANY of them already fixed-version rather than a placeholder? If one is, G8 is
        # exercisable against the fixture as it stands and needs no new project.
        kinds = {}
        for r in refs:
            kind = _plain(safe(lambda rr=r: rr.GetType().Name, None))
            kinds[kind] = kinds.get(kind, 0) + 1
        out["reference_kinds"] = kinds

    # MEASURED: add_library() takes exactly ONE argument, not (name, version).
    out["add_library_two_args"] = describe(lambda: libman.add_library("Standard", "3.5.17.0"))
    out["add_library_one_arg"] = describe(lambda: libman.add_library("Standard, 3.5.17.0 (System)"))
    if out["add_library_one_arg"]["ok"]:
        refs_after = safe(lambda: list(libman.references), None)
        out["reference_count_after"] = len(refs_after) if refs_after is not None else None
        if refs_after:
            added = refs_after[-1]
            out["added_kind"] = _plain(safe(lambda: added.GetType().Name, None))
            # MEASURED: a ScriptManagedLibraryReference has NO effective_resolution -- that
            # belongs to the placeholder type. is_managed / is_placeholder are how the two
            # are told apart, which is exactly what versio_libraries.py has to branch on.
            out["added"] = {}
            for field in ("name", "namespace", "is_managed", "is_placeholder",
                          "system_library", "default_resolution", "resolution_info",
                          "version", "optional"):
                out["added"][field] = describe(lambda f=field, a=added: getattr(a, f))

    return out


PROBES = {
    "P0": ("API surface", lambda proj, path: probe_api(proj)),
    "P1": ("rename preserves identity", probe_rename),
    "P2": ("move preserves identity", probe_move),
    "P3": ("remove a referenced object", probe_remove_referenced),
    "P5": ("create awkward names, then find them", lambda proj, path: probe_create_awkward(proj)),
    "P6": ("typed task configuration", probe_task_object),
    "P8": ("deep folder chain", lambda proj, path: probe_path_length(proj)),
    "P9": ("fixed-version library reference", lambda proj, path: probe_library_reference(proj)),
}


def main():
    args = sys.argv[1:]
    if not args:
        emit({"t": "fatal", "msg": "no job file argument"})
        return 2

    f = open(args[0], "r")
    try:
        job = json.load(f)
    finally:
        f.close()

    wanted = job.get("probes") or ["P0"]
    project_path = job["project"]

    emit({"t": "start", "project": project_path, "probes": wanted})
    proj = open_project(project_path)
    emit({"t": "opened", "path": _plain(safe(lambda: proj.path, project_path))})

    for pid in wanted:
        if pid not in PROBES:
            emit({"t": "probe_skipped", "id": pid, "reason": "unknown probe id"})
            continue
        title, fn = PROBES[pid]
        emit({"t": "probe_start", "id": pid, "title": title})
        try:
            # Several probes reopen the project, so always hand them the CURRENT handle.
            result = fn(_OPEN["project"], project_path)
            emit({"t": "probe_result", "id": pid, "title": title, "result": result})
        except Exception as e:
            emit({"t": "probe_failed", "id": pid, "error": str(e),
                  "trace": traceback.format_exc()})

    close_project()
    emit({"t": "done"})
    return 0


try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})
    rc = 1
finally:
    close_project()

emit({"t": "exit", "rc": rc})
