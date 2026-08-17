# -*- coding: utf-8 -*-
# REMEMBER: this runs in CODESYS ScriptEngine = IronPython 2.7. No f-strings, no dict
# comprehensions with unpacking, and `except X as e` is the only spelling that works.
#
# Phase 0 probes P1-P6, P8 and P9 of docs/vendors/codesys/IMPORT-PLAN.md. Every one of them exists
# because the plan asserts something about the ScriptEngine API that nobody has run.
#
# ---------------------------------------------------------------------------------------
# WHY THIS LIVES IN tools/ AND NOT IN src/Versio.Plc.Codesys.Infrastructure/Scripts/
#
#   Versio.Plc.Codesys.Infrastructure.csproj embeds Scripts\*.py into the shipping
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
# job.json: { "project": "<abs path>", "probes": ["P1", "P2"],
#             "scripts": "<abs path to src\\Versio.Plc.Codesys.Infrastructure\\Scripts>" }
#
# `scripts` is optional and only P9 uses it. It is the ONE place this file reaches into
# the product, and the direction is what makes it safe: the isolation rule above is about
# a probe that renames, moves and removes objects never SHIPPING inside the product, not
# about the probe being forbidden to read it. P9 has to answer "what does the exporter
# record for a managed reference", and the only honest way to answer that is to call the
# exporter's own function rather than a copy of it.

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
    of versio_libraries.py has never executed. Can a fixed-version one be added quickly --
    and DOES THE BRANCH READ IT CORRECTLY once it exists?

    The second half is the one that matters and the first run of this probe skipped it. It
    reported that add_library works and that the result says is_managed: true, from which
    the plan concluded G8 was cheap. It never printed the managed type's members, and the
    exporter does not read the reference directly: it reads `ref.managed_library` and then
    .version / .company / .title off that. That spelling had never been called until this
    probe was extended on 2026-08-17. See
    out["managed_library"] and the three field maps beside it."""
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

            # THE HALF THE FIRST RUN OF THIS PROBE DID NOT MEASURE, and the whole reason
            # G8 was still open a day later. `members()` above was called on refs[0], a
            # PLACEHOLDER, so the managed type's member list has never been printed --
            # and versio_libraries.py reads `ref.managed_library` and then .version,
            # .company and .title off whatever that returns. Four spellings in this API
            # have already been wrong (move, find, set_priority, add_library), and a
            # `safe()` probe on a name that does not exist is indistinguishable from a
            # reference that has no version: the exporter would write a managed reference
            # as though it were a placeholder and warn about nothing.
            out["added_members"] = members(added)

            # Every field extract_reference() touches, on a managed reference, one at a
            # time. The last four are written UNCONDITIONALLY with safe(..., False) --
            # so if any of them raises here, the mirror has been recording a fabricated
            # `false` rather than a measured one, which is worse than omitting the key.
            out["added_record_fields"] = {}
            for field in ("qualified_only", "publish_symbols_in_container", "optional",
                          "system_library", "parameters", "is_redirected",
                          "placeholder_name",
                          # CODESYS's own typo. The CLR member list from the first run
                          # of this probe spells it ...depencency, while versio_libraries.py
                          # probed the correct spelling -- which is why its comment used to
                          # say the field "is not readable on this version". Both spellings
                          # are asked for here so the answer names which one works.
                          "hide_when_referenced_as_dependency",
                          "hide_when_referenced_as_depencency"):
                out["added_record_fields"][field] = describe(
                    lambda f=field, a=added: getattr(a, f))

            # The chain the exporter walks. Reported as three separate answers because
            # "no version came out" has three different causes and only one of them is a
            # bug in this repository: managed_library absent, managed_library present but
            # None, or present and missing the sub-fields.
            out["managed_library"] = describe(lambda: added.managed_library)
            managed = safe(lambda: added.managed_library, None)
            out["managed_library_members"] = members(managed) if managed is not None else None
            out["managed_library_fields"] = {}
            if managed is not None:
                for field in ("version", "company", "title", "default_namespace",
                              "namespace", "name"):
                    out["managed_library_fields"][field] = describe(
                        lambda f=field, m=managed: getattr(m, f))

            # And the same three fields off the REFERENCE, in case the shape is flat.
            # Cheap, and it is the alternative spelling the fix would use.
            out["added_flat_fields"] = {}
            for field in ("version", "company", "title"):
                out["added_flat_fields"][field] = describe(
                    lambda f=field, a=added: getattr(a, f))

            # A PLACEHOLDER's answer to the same call, which is the OTHER half of the
            # branch: extract_reference() enters its managed block whenever
            # `ref.managed_library` probes to something non-None, and a placeholder that
            # answers with an object rather than raising would take every reference in
            # the fixture down the managed path.
            if refs:
                out["placeholder_managed_library"] = describe(
                    lambda s=refs[0]: s.managed_library)

            # AND THE SAME MEASUREMENT WITH A LIBRARY THIS MACHINE ACTUALLY HAS.
            #
            # The run above added "Standard, 3.5.17.0 (System)" - the version the plan
            # quotes - and every field off its managed_library threw
            # NullReferenceException. That is one measurement with two possible readings:
            # the exporter reads the wrong chain, or a reference to a library that is not
            # INSTALLED carries no library information to read. Only the second run tells
            # them apart, and the difference decides whether G8 is a defect or a gap.
            #
            # The version is taken from the placeholder's own effective_resolution rather
            # than typed in, so this probe asks about whatever is installed on the machine
            # running it instead of hard-coding one engineer's CODESYS.
            installed = safe(lambda: _plain(refs[0].effective_resolution), None) if refs else None
            out["installed_spelling"] = installed
            if installed:
                out["add_installed"] = describe(lambda: libman.add_library(installed))
                refs_now = safe(lambda: list(libman.references), None)
                if refs_now:
                    inst = refs_now[-1]
                    out["installed_kind"] = _plain(safe(lambda: inst.GetType().Name, None))
                    out["installed_ref"] = {}
                    for field in ("name", "namespace", "is_managed", "is_placeholder",
                                  "system_library", "optional", "qualified_only",
                                  "publish_symbols_in_container"):
                        out["installed_ref"][field] = describe(
                            lambda f=field, r=inst: getattr(r, f))

                    # AND WHAT THE SHIPPING EXPORTER MAKES OF IT. Everything above is
                    # this file's own reading of the API; this is versio_libraries.py's,
                    # run against the same live object in the same process, and it is the
                    # only line here that can fail because of a bug in the product rather
                    # than a surprise in CODESYS.
                    try:
                        import versio_libraries
                        errs = []
                        out["product_record"] = versio_libraries.extract_reference(
                            inst, safe,
                            lambda code, detail: errs.append([code, detail]))
                        out["product_record_errors"] = errs
                        out["product_record_placeholder"] = versio_libraries.extract_reference(
                            refs[0], safe, lambda code, detail: errs.append([code, detail]))
                    except Exception as e:
                        out["product_record"] = {"error": type(e).__name__,
                                                 "message": str(e)}

                    out["installed_managed_library"] = describe(lambda: inst.managed_library)
                    mlib = safe(lambda: inst.managed_library, None)
                    out["installed_managed_fields"] = {}
                    if mlib is not None:
                        # The three the exporter writes, plus the two beside them: if
                        # `displayname` and `default_namespace` read while `title` does
                        # not, the record is recording the wrong pair of fields.
                        for field in ("version", "company", "title", "displayname",
                                      "default_namespace", "categories"):
                            out["installed_managed_fields"][field] = describe(
                                lambda f=field, m=mlib: getattr(m, f))

    return out


# ============================================================================ P11
def probe_move_matrix(proj, project_path):
    """WHICH CONTAINERS ACCEPT A MOVE, one destination at a time.

    The plan's last open task, and it exists because two measurements disagree without
    contradicting each other. P2 moved a POU into a folder under Application: the GUID
    survived a save, close and reopen. The feature checklist's D6 moved one OUT of
    Application into Plc Logic: CODESYS accepted the call, raised nothing, and left the
    object exactly where it was, which versio_import.py reports as RENAME_NOT_APPLIED
    because it re-resolves afterwards rather than trusting the call.

    So "Versio moves objects" is true of some destinations and false of others, and
    nobody had drawn the line.

    EVERY STEP RE-RESOLVES AT THE INTENDED PATH AFTERWARDS. That is the whole method:
    this toolchain has now reported success for work it did not do four times - move(),
    create_folder returning None, pous.remove(name), and a rename whose names were equal -
    and every one was caught by reading the value back rather than by the call raising.
    A step is `applied` only if the object is FOUND at its destination.

    STEPS ARE AN ORDERED LIST, NOT A CROSS PRODUCT, and that is the correction the first
    version needed: it paired movers with destinations by index and the third pair was
    Glob_Var into Application, which was already its parent. It reported applied: true
    having moved nothing, because find_at found the object where it had always been. A
    destination an object is already in cannot answer anything, so each step below names
    its object deliberately and `already_there` is checked before the call rather than
    inferred from the result.
    """
    app = find_at(proj, APP_PATH)
    if app is None:
        return {"verdict": "NO_APPLICATION"}

    # create_folder RETURNS None and creates the folder anyway (DRIVING.md), so every
    # destination here is found by name rather than taken from a return value.
    describe(lambda: app.create_folder("VersioMoveProbe"))
    folder = find_at(proj, APP_PATH + ["VersioMoveProbe"])
    if folder is not None:
        describe(lambda: folder.create_folder("VersioMoveProbeInner"))

    # Three more folders, and they exist to remove confounds rather than to test features.
    # The folder step below moves one that HAS CHILDREN, so on its own it cannot tell "this
    # destination refuses a folder" from "a folder with something in it cannot be moved".
    # VersioMoveEmpty is empty; VersioMoveHost is somewhere legal to move something to.
    #
    # VersioMoveEmpty2 exists because the first run of this probe used ONE empty folder for
    # both isolating steps: step 8 moved it into Plc Logic, succeeded, and step 9 then looked
    # for it at its old path under Application - OBJECT_NOT_AT_SOURCE, no call ever made. A
    # step whose source depends on an earlier step's OUTCOME measures nothing when that
    # outcome goes the other way, which is the by-index pairing defect in its third costume.
    describe(lambda: app.create_folder("VersioMoveEmpty"))
    describe(lambda: app.create_folder("VersioMoveEmpty2"))
    describe(lambda: app.create_folder("VersioMoveHost"))

    PROBE_FOLDER = APP_PATH + ["VersioMoveProbe"]
    INNER = PROBE_FOLDER + ["VersioMoveProbeInner"]
    EMPTY = APP_PATH + ["VersioMoveEmpty"]
    EMPTY2 = APP_PATH + ["VersioMoveEmpty2"]
    HOST = APP_PATH + ["VersioMoveHost"]

    # (label, object's current path, destination path). [] is the project root.
    #
    # The order matters twice: step 2 moves the object step 1 just moved, which is the
    # only way to ask "does a move OUT of a folder work" - and step 6 moves the folder
    # itself, which a mirror expresses by dragging a directory, so it is not exotic.
    steps = [
        ("into_folder_under_application", APP_PATH + ["Simulation"], PROBE_FOLDER),
        ("out_of_folder_back_to_application", PROBE_FOLDER + ["Simulation"], APP_PATH),
        ("into_nested_folder", APP_PATH + ["Diagnosis"], INNER),
        ("out_of_application_into_plc_logic", APP_PATH + ["Visualization"],
         ["Device", "Plc Logic"]),
        ("out_of_application_into_device", APP_PATH + ["PLC_PRG"], ["Device"]),
        ("out_of_application_into_project_root", APP_PATH + ["Glob_Var"], []),
        ("a_folder_with_children_into_plc_logic", PROBE_FOLDER, ["Device", "Plc Logic"]),

        # The two that isolate the variable. If the empty one is declined too, the
        # destination is what refuses; if it is accepted, the earlier row was about the
        # children and the finding would have been wrong. Each uses ITS OWN folder - see
        # the creation comment above - so neither step's source depends on the other's
        # outcome.
        ("an_empty_folder_into_plc_logic", EMPTY, ["Device", "Plc Logic"]),
        ("an_empty_folder_into_another_folder", EMPTY2, HOST),
    ]

    rows = []
    for label, from_path, to_path in steps:
        obj = find_at(proj, from_path)
        dest = proj if not to_path else find_at(proj, to_path)
        name = from_path[-1]

        if obj is None:
            rows.append({"step": label, "object": name, "verdict": "OBJECT_NOT_AT_SOURCE"})
            continue
        if dest is None:
            rows.append({"step": label, "object": name, "verdict": "NO_DESTINATION"})
            continue

        # Asked BEFORE the call. A step whose object is already in the destination proves
        # nothing, and the first version of this probe recorded exactly that as a pass.
        already = find_at(proj, to_path + [name]) is not None
        before = ident(obj)
        call = describe(lambda o=obj, d=dest: o.move(d, -1))
        found = find_at(proj, to_path + [name])

        rows.append({
            "step": label,
            "object": name,
            "from": "/".join(from_path[:-1]) or "<root>",
            "to": "/".join(to_path) or "<root>",
            "already_there_before": already,
            "call_ok": call.get("ok"),
            "call_error": call.get("error"),
            "call_message": call.get("message"),
            "applied": (found is not None) and not already,
            "parent_after": _plain(safe(lambda o=obj: o.parent.get_name(), None)),
            "guid_before": before["guid"],
            "guid_stable": before["guid"] == _plain(safe(lambda o=obj: o.guid, None)),
        })

    # One save and one reopen for the whole matrix: an in-session move that does not
    # survive being read off disk again would be the worst of the three outcomes, and
    # nothing has ever looked for it.
    proj.save()
    close_project()
    reopened = open_project(project_path)

    after = []
    for i, (row, (label, _from, to_path)) in enumerate(zip(rows, steps)):
        if not row.get("applied"):
            continue

        # A LATER STEP MAY HAVE MOVED THE SAME OBJECT AGAIN, and then this step's
        # destination is simply the wrong place to look. The first version of this loop
        # did look there, found nothing, and reported found_after_reopen: false for
        # `into_folder_under_application` - which reads as "a move into a folder does not
        # survive a reopen", contradicts P2, and is an artefact of step 2 moving the same
        # POU back out. An absence has to be attributed before it is reported.
        #
        # AND ONLY A STEP THAT ACTUALLY MOVED THE OBJECT CAN SUPERSEDE. The first run
        # matched later steps by NAME alone, and step 8's reopen check was skipped in
        # deference to a step 9 that had reported OBJECT_NOT_AT_SOURCE and called nothing -
        # so "an empty folder into Plc Logic survives a reopen" went unmeasured while three
        # documents claimed it. A declined or never-run step left the object exactly where
        # this step put it, which is the position this check exists to read.
        superseded = [rows[j]["step"] for j in range(i + 1, len(rows))
                      if rows[j].get("object") == row["object"] and rows[j].get("applied")]
        if superseded:
            after.append({"step": label, "object": row["object"],
                          "verdict": "SUPERSEDED",
                          "by": superseded,
                          "detail": "a later step moved this object again, so its "
                                    "position on disk belongs to that step"})
            continue

        found = find_at(reopened, to_path + [row["object"]])
        after.append({
            "step": label,
            "object": row["object"],
            "found_after_reopen": found is not None,
            "guid_stable_after_reopen": (found is not None and row["guid_before"]
                                         == _plain(safe(lambda f=found: f.guid, None))),
        })

    return {
        "rows": rows,
        "after_reopen": after,
        "summary": {
            "applied": [r["step"] for r in rows if r.get("applied")],
            "accepted_silently_and_not_applied": [
                r["step"] for r in rows
                if r.get("applied") is False and r.get("call_ok") and not r.get("already_there_before")],
            "raised": [r["step"] for r in rows if r.get("call_ok") is False],
            "vacuous": [r["step"] for r in rows if r.get("already_there_before")],
        },
    }


# ============================================================================ P12
def probe_encodable_name(proj, project_path):
    """Can an object whose name needs percent-encoding EXIST?

    E2 and E4 of the feature checklist need one, and the plan calls their fixture
    unbuildable on the strength of P5 - which measured that create_pou REFUSES the four
    awkward names. That is a fact about one door. Two others have never been tried, and
    the cost of assuming they are shut is two rows proven by unit tests and nothing else:

      1. rename(). Nobody has ever called it with a name create_pou refuses. A validator
         that runs on create and not on rename is a shape every toolchain has shipped at
         least once.
      2. An external-file object. README.md's own object list carries a real one, named
         {GUID}|C:\\Users\\...\\file.txt - so the IDE has the door. Is it scriptable?

    Same method as every probe here: call the thing, re-resolve afterwards, report what
    happened rather than what the call claimed.
    """
    app = find_at(proj, APP_PATH)
    obj = find_at(proj, APP_PATH + ["Simulation"])
    if app is None or obj is None:
        return {"verdict": "NO_TARGET"}

    # Door 1: the four names P5 measured create_pou refusing, attempted as renames of a
    # real POU. Each attempt re-resolves at the wanted name; a success updates the handle
    # so the next attempt renames from wherever the object now is.
    out = {"rename": []}
    current = u"Simulation"
    for wanted in [u"My.Thing", u"weird<name>", u"Pump ", u"Pump."]:
        call = describe(lambda o=obj, w=wanted: o.rename(w))
        landed = find_at(proj, APP_PATH + [wanted])
        out["rename"].append({
            "wanted": wanted,
            "call_ok": call.get("ok"),
            "call_error": call.get("error"),
            "call_message": call.get("message"),
            "applied": landed is not None,
        })
        if landed is not None:
            current = wanted
            obj = landed

    # Put the name back so door 2 measures against a sane tree. Reported, not assumed -
    # a probe that silently repairs its own state is a probe whose rows cannot be read
    # in order.
    if current != u"Simulation":
        out["rename_back"] = describe(lambda o=obj: o.rename(u"Simulation"))

    # Door 2: an external-file object, from a real file written beside the scratch
    # project. AttributeError in a row below means the member does not exist under that
    # spelling; any other error means it exists and refused - the distinction P9's
    # NullReferenceException taught this file to keep.
    ext = project_path[:project_path.rfind("\\") + 1] + "versio_external.txt"
    f = open(ext, "w")
    f.write("external file fixture for P12\n")
    f.close()

    out["module_external_names"] = sorted(
        [n for n in dir(scriptengine) if "xternal" in n])
    for target_name, target in (("project", proj), ("application", app)):
        row = {}
        for meth in ("create_external_file_object", "add_external_file",
                     "create_external_file", "import_external_file"):
            row[meth] = describe(
                lambda t=target, m=meth, p=ext: getattr(t, m)(p))
        out["door2_" + target_name] = row

    # What the tree actually holds afterwards, because a created external object's NAME
    # is the whole point: if one exists, does it carry the path - the |, : and \ that
    # need encoding - or something tamer?
    def child_names(container):
        return [_plain(safe(lambda c=c: c.get_name(), None))
                for c in (safe(lambda: container.get_children(), []) or [])]

    out["app_children_after"] = child_names(app)
    out["root_children_after"] = child_names(proj)
    return out


# ============================================================================ P13
def probe_external_file_shapes(proj, project_path):
    """What NAME does an external-file object actually carry - and therefore which of
    E2 and E4 its mirror stem exercises?

    P12 proved create_external_file_object works and left a puzzle in its own output:
    the object created on Application listed as plain `versio_external.txt` while the
    project root listing showed a `{GUID}|C:\\...\\versio_external.txt` entry beside a
    plain one. If get_name() is the bare filename, then E2/E4's fixture is
    machine-INDEPENDENT after all - the absolute-path caveat written into the plan
    would be about a listing artefact, not the name - and a filename can be chosen to
    need percent-encoding (E2: `%` and spaces) or shortening (E4: past MaxStem = 80).

    Four creations, each answered separately, then a save/close/reopen to see which
    names persist. The exporter's side of the question - stem, manifest record, form,
    UNKNOWN_TYPE_GUID - is deliberately NOT probed here: the real versio_export.py is
    run against the saved project afterwards, because the exporter's own reading is
    the only one that counts (the P9 lesson).
    """
    app = find_at(proj, APP_PATH)
    if app is None:
        return {"verdict": "NO_APPLICATION"}

    base = project_path[:project_path.rfind("\\") + 1]

    # (label, filename on disk, why it is in the list)
    cases = [
        ("plain", "versio_ext_plain.txt",
         "the control: no encoding, no shortening"),
        ("needs_encoding", "versio 50% ext.txt",
         "space and percent - legal in a Windows filename, encoded by EncodeStem"),
        ("needs_shortening",
         "versio_ext_long_" + ("x" * 80) + ".txt",
         "past MaxStem=80, so the stem takes the digest tail - E4's shape"),
    ]

    out = {"created": []}
    for label, fname, why in cases:
        path = base + fname
        f = open(path, "w")
        f.write("external file fixture for P13: " + label + "\n")
        f.close()

        call = describe(lambda a=app, p=path: a.create_external_file_object(p))
        # The name is read off the CHILD LIST rather than off the return value,
        # because create_folder taught this file that a return value can be None
        # while the object exists.
        found = None
        for child in safe(lambda: app.get_children(), []) or []:
            cname = safe(lambda c=child: c.get_name(), None)
            if cname is not None and fname in _plain(cname):
                found = child
                break

        row = {"case": label, "file": fname, "why": why,
               "call_ok": call.get("ok"), "call_error": call.get("error"),
               "call_message": call.get("message")}
        if found is not None:
            row["name"] = _plain(safe(lambda o=found: o.get_name(), None))
            row["guid"] = _plain(safe(lambda o=found: o.guid, None))
            # The type GUID decides which branch of versio_export.py this object
            # takes: a mapped type gets its form, an unmapped one gets
            # UNKNOWN_TYPE_GUID and a native export attempt.
            row["type_guid"] = _plain(safe(lambda o=found: o.type, None))
            row["embeds_full_path"] = base.lower() in (row["name"] or u"").lower()
        else:
            row["name"] = None
        out["created"].append(row)

    # A relative spelling, because if it works the fixture never touches an
    # absolute path at all. Reported either way; the cwd inside CODESYS is nobody's
    # to rely on, so this is a measurement rather than a plan.
    rel = describe(lambda: app.create_external_file_object("versio_ext_relative.txt"))
    out["relative_attempt"] = rel

    proj.save()
    close_project()
    reopened = open_project(project_path)
    app2 = find_at(reopened, APP_PATH)
    out["app_children_after_reopen"] = [
        _plain(safe(lambda c=c: c.get_name(), None))
        for c in (safe(lambda: app2.get_children(), []) or [])] if app2 is not None else None

    return out


# ============================================================================ P10
def probe_task_shape(proj, project_path):
    """What a task configuration and its tasks actually expose, field by field.

    P6 proved the seven fields READ. Two of them came back as objects -- a ScriptWatchdog
    and a ScriptPouObjectList -- and "it returned an object" is not enough to write a
    reader against. versio_tasks.py has to name sub-fields, and naming them from the API
    docs is how four wrong spellings got into the plan in the first place.
    """
    cfg = find_at(proj, APP_PATH + ["Task Configuration"])
    if cfg is None:
        return {"verdict": "NO_TARGET", "detail": "Task Configuration not found"}

    out = {"config": members(cfg), "tasks": []}

    for child in safe(lambda: cfg.get_children(), []) or []:
        name = safe(lambda: child.get_name(), None)
        row = {"name": name, "clr_type": _plain(safe(lambda: child.GetType().Name, None))}

        for field in TASK_FIELDS:
            row[field] = describe(lambda f=field, c=child: getattr(c, f))

        wd = safe(lambda: child.watchdog, None)
        if wd is not None:
            row["watchdog_type"] = _plain(safe(lambda: wd.GetType().Name, None))
            row["watchdog_members"] = members(wd)
            row["watchdog_fields"] = {}
            for f in ("enabled", "time", "sensitivity", "is_enabled"):
                row["watchdog_fields"][f] = describe(lambda ff=f, w=wd: getattr(w, ff))

        pous = safe(lambda: child.pous, None)
        if pous is not None:
            row["pous_type"] = _plain(safe(lambda: pous.GetType().Name, None))
            row["pous_members"] = members(pous)
            row["pous_list"] = describe(
                lambda p=pous: [_plain(safe(lambda x=x: x.get_name(), x)) for x in p])

        out["tasks"].append(row)

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
    "P10": ("task configuration shape, for versio_tasks.py", probe_task_shape),
    "P11": ("which containers accept a move", probe_move_matrix),
    "P12": ("can an encodable name exist", probe_encodable_name),
    "P13": ("what name an external-file object carries", probe_external_file_shapes),
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

    # versio_libraries.py imports no scriptengine and takes its objects and its `safe` probe
    # as arguments, so it loads here exactly as it loads under CPython in the parity tests.
    # That is the whole reason it was written that way.
    scripts_dir = job.get("scripts")
    if scripts_dir:
        sys.path.insert(0, scripts_dir)

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
