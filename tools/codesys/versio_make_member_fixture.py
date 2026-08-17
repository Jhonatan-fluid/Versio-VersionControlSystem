# -*- coding: utf-8 -*-
# REMEMBER: this runs in CODESYS ScriptEngine = IronPython 2.7. No f-strings, no dict
# comprehensions with unpacking, and `except X as e` is the only spelling that works.
#
# Builds the SECOND fixture: a project with MEMBER OBJECTS in it.
#
# ---------------------------------------------------------------------------------------
# WHY THIS EXISTS
#
#   RefrigeratorControl has thirteen records and not one member object - no method, no
#   action, no property, no folder below Application. Its manifest therefore records no
#   `parent` for anything, and every resolve chain the import has ever built came from
#   directory names. The import feature checklist (docs/vendors/codesys/IMPORT-PLAN.md) marks five
#   rows NOT REACHED for that one reason, and two of them are G4 and G5 - the gaps with the
#   least evidence behind them.
#
#   Two of the missing forms only exist on members: ACTION and TRANSITION export as
#   ST_IMPL, which nothing in this repository has ever imported, and PROPERTY exports as
#   NATIVE_R, whose recursive shape is the one thing TASK_NATIVE was mistaken for.
#
# WHY IT LIVES IN tools/ AND NOT IN src/Versio.Plc.Codesys.Infrastructure/Scripts/
#
#   Same reason as versio_probe_import_api.py: Versio.Plc.Codesys.Infrastructure.csproj
#   embeds Scripts\*.py into the shipping assembly, and a script that creates
#   and renames objects has no business travelling with the product. The cost is that this
#   file cannot import versio_types as a sibling, so it depends on nothing but scriptengine
#   and the standard library.
#
# WHY EVERY CALL IS REPORTED RATHER THAN ASSUMED
#
#   `create_method`, `create_action` and `create_property` have never been called by
#   anything in this repository. Reflection has been wrong about this API four times -
#   `move`, `find`, `set_priority` and `add_library` all had the wrong spelling or the
#   wrong argument types - and `dir()` on a ScriptObject returns an empty list, so nothing
#   short of calling them answers anything. Each attempt below reports the value or the
#   exception TYPE and message, and the run continues either way: a fixture that is missing
#   its property is still worth having, and a probe that dies on its first surprise tells
#   you about one surprise per run.
#
#   `create_folder` is the standing warning: it RETURNS None and creates the folder anyway
#   (vendors/codesys/DRIVING.md), so the destination is found by name afterwards rather than taken from the
#   return value.
# ---------------------------------------------------------------------------------------
#
# job.json: { "project": "<abs path to a COPY>" }

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


def attempt(what, fn):
    """Call, report, and never raise. Returns the value or None."""
    try:
        value = fn()
    except Exception as e:
        emit({"t": "attempt", "what": what, "ok": False,
              "error": type(e).__name__, "message": str(e)})
        return None

    emit({"t": "attempt", "what": what, "ok": True,
          "value": None if value is None else str(value)})
    return value


def find_child(parent, name):
    """One level, by name, tolerating the two shapes get_children returns."""
    for child in safe(lambda: parent.get_children(), []) or []:
        if safe(lambda c=child: c.get_name(), None) == name:
            return child
    return None


def find_path(proj, parts):
    node = proj
    for part in parts:
        node = find_child(node, part)
        if node is None:
            return None
    return node


def declare(obj, declaration, implementation=None):
    """Write an object's text, so the fixture has something a diff can show.

    Separate from creation on purpose: create_pou takes a language and a type and gives
    back an empty object, and everything a reviewer would actually read is written here.

    `.replace(text)`, NOT `.text = text`. The property is READ-ONLY and assigning to it
    raises `AttributeError: can't assign to read-only property text of type
    'ScriptTextDocument'` - measured here on the first run of this script, and the fifth
    wrong spelling this API has been caught on after move, find, set_priority and
    add_library. versio_import.py has always called replace(); this file was written
    without reading it, which is the whole argument for reading the caller that already
    works before inventing a call.
    """
    name = str(safe(lambda: obj.get_name(), "?"))
    if declaration is not None:
        attempt("declaration of " + name,
                lambda: obj.textual_declaration.replace(declaration))
    if implementation is not None:
        attempt("implementation of " + name,
                lambda: obj.textual_implementation.replace(implementation))


def build(proj):
    app = find_path(proj, ["Device", "Plc Logic", "Application"])
    if app is None:
        emit({"t": "fatal", "msg": "no Device/Plc Logic/Application in this project"})
        return

    # A FOLDER, so a resolve chain has a directory level below Application that is a real
    # object rather than a container CODESYS invents.
    attempt("create_folder(Pumps)", lambda: app.create_folder("Pumps"))
    folder = find_child(app, "Pumps")
    emit({"t": "found", "what": "folder Pumps", "found": folder is not None})

    owner = folder if folder is not None else app

    # THE FUNCTION BLOCK that carries the members. Inside the folder, so its manifest path
    # has a directory segment AND its members have a recorded parent - the two halves of a
    # resolve chain that the first fixture cannot produce between them.
    fb = attempt("create_pou(FB_Pump, FunctionBlock, st)",
                 lambda: owner.create_pou("FB_Pump",
                                          scriptengine.PouType.FunctionBlock,
                                          scriptengine.ImplementationLanguages.st))
    if fb is None:
        fb = find_child(owner, "FB_Pump")

    if fb is not None:
        declare(fb,
                u"FUNCTION_BLOCK FB_Pump\nVAR_INPUT\n\tenable : BOOL;\n"
                u"END_VAR\nVAR_OUTPUT\n\trunning : BOOL;\nEND_VAR\n"
                u"VAR\n\tspeed : INT;\nEND_VAR",
                u"running := enable;\n")

        # A METHOD - ST_FULL, and a member.
        #
        # THE SECOND ARGUMENT IS A RETURN TYPE, NOT A LANGUAGE, which is not what the
        # neighbouring create_pou and create_action take. Passing the language raises
        # `TypeError: expected str, got Guid` - measured, and exactly the shape of the four
        # earlier corrections: reflection says the member exists and says nothing about
        # what it accepts.
        method = attempt(
            "create_method(Start, BOOL)",
            lambda: fb.create_method("Start", "BOOL"))
        if method is None:
            method = attempt("create_method(Start)", lambda: fb.create_method("Start"))
        if method is None:
            method = find_child(fb, "Start")

        if method is not None:
            declare(method,
                    u"METHOD Start : BOOL\nVAR_INPUT\n\ttarget : INT;\nEND_VAR",
                    u"speed := target;\nStart := TRUE;\n")

        # AN ACTION - ST_IMPL, the form with no declaration at all, and the one the import
        # has never applied to anything.
        action = attempt(
            "create_action(Stop, st)",
            lambda: fb.create_action("Stop", scriptengine.ImplementationLanguages.st))
        if action is None:
            action = attempt("create_action(Stop)", lambda: fb.create_action("Stop"))
        if action is None:
            action = find_child(fb, "Stop")

        if action is not None:
            # Implementation only. An action has no declaration to write, which is the
            # whole reason ST_IMPL is a separate form.
            declare(action, None, u"speed := 0;\nrunning := FALSE;\n")

        # A PROPERTY - NATIVE_R, and the only type that owns children the exporter does not
        # walk separately. It is what NATIVE_R means, and TASK_NATIVE was mistaken for it
        # for a whole phase.
        prop = attempt(
            "create_property(Speed, INT, st)",
            lambda: fb.create_property("Speed", "INT",
                                       scriptengine.ImplementationLanguages.st))
        if prop is None:
            prop = attempt("create_property(Speed, INT)",
                           lambda: fb.create_property("Speed", "INT"))
        if prop is None:
            prop = find_child(fb, "Speed")

        emit({"t": "found", "what": "property Speed", "found": prop is not None})

    # A DUT - ST_DECL on something that is not a GVL, so the declaration-only path has a
    # second subject and G12's fix can be told apart from the form it changed.
    dut = attempt("create_dut(E_Mode, Enumeration)",
                  lambda: app.create_dut("E_Mode", scriptengine.DutType.Enumeration))
    if dut is None:
        dut = find_child(app, "E_Mode")
    if dut is not None:
        declare(dut, u"TYPE E_Mode :\n(\n\tIdle := 0,\n\tRunning := 1\n);\nEND_TYPE", None)

    # TWO EXTERNAL-FILE OBJECTS, added 2026-08-17 for E2/E4, and BOTH BY RELATIVE PATH -
    # that is the load-bearing choice, measured by P13 before it was made. An external
    # file created from an ABSOLUTE path gains a companion node at the project root
    # whose name embeds that path ({GUID}|C:\...\file.txt), which would make this
    # fixture differ per machine - the G9 machine-dependence trap. A relative creation
    # gets no companion, a bare-filename name, and a payload whose FileId is relative
    # too, so the fixture stays committable. The file need not exist on disk; P13
    # created one from a name with no file behind it.
    #
    # The second name is past MaxStem = 80, so its mirror file takes the shortened
    # digest-tail stem - the one shape (E4) no committed mirror had ever carried, and
    # the reason resolution-by-manifest rather than by stem has never been proven
    # against real exporter bytes.
    attempt("create_external_file_object(Wiring Notes.txt)",
            lambda: app.create_external_file_object("Wiring Notes.txt"))
    emit({"t": "found", "what": "external file Wiring Notes.txt",
          "found": find_child(app, "Wiring Notes.txt") is not None})

    long_name = "Commissioning_Checklist_" + ("Section_" * 9) + "Final.txt"
    attempt("create_external_file_object(<%d chars>)" % len(long_name),
            lambda: app.create_external_file_object(long_name))
    emit({"t": "found", "what": "external file with a %d-character name" % len(long_name),
          "found": find_child(app, long_name) is not None})

    # THE TREE, read back rather than assumed. Every write in this API has to be re-read -
    # move() accepted an instruction and did nothing, create_folder returns None while
    # succeeding, pous.remove reported success and changed nothing. This is that rule.
    def walk(node, depth, out):
        for child in safe(lambda: node.get_children(), []) or []:
            name = safe(lambda c=child: c.get_name(), "?")
            guid = str(safe(lambda c=child: c.guid, ""))
            out.append({"depth": depth, "name": name, "guid": guid})
            walk(child, depth + 1, out)

    tree = []
    walk(app, 0, tree)
    emit({"t": "tree", "under": "Device/Plc Logic/Application", "nodes": tree})


def main():
    if len(sys.argv) < 2:
        emit({"t": "fatal", "msg": "no job file argument"})
        return 2

    with open(sys.argv[1], "r") as handle:
        job = json.load(handle)

    proj = scriptengine.projects.open(job["project"])
    emit({"t": "opened", "path": str(proj.path)})

    build(proj)

    proj.save()
    emit({"t": "saved"})
    proj.close()
    emit({"t": "done"})
    return 0


rc = 1
try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})

emit({"t": "exit", "rc": rc})
sys.exit(rc)
