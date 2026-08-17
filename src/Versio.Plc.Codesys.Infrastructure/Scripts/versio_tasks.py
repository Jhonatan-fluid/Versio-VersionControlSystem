# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS.
#
# Reads a CODESYS task configuration and renders its tasks as YAML.
#
# WHERE THIS SITS IN THE VENDOR SEAM: the FORMAT is vendor-neutral - every PLC toolchain
# schedules code somehow, and the C# side (Versio.Core.Projects.TaskCatalog) reads this
# file without knowing CODESYS exists. What is vendor-specific is exactly this file: how
# those fields are obtained from one toolchain's scripting API. A second vendor writes its
# own reader and emits the same document.
#
# This module imports no scriptengine. It is handed objects and a `safe` caller, so the
# shaping rules can be exercised outside CODESYS. Same contract as versio_libraries.py,
# which is the precedent this deliberately copies.
#
# ---------------------------------------------------------------------------------------
# WHY THIS EXISTS AT ALL, which is not "for readability":
#
# Task configuration used to round-trip as native XML, and that cost two defects:
#
#   G7  import_native rewrites Core2 without being asked. That is a task's CPU core
#       affinity - a change to what the machine does, made by a tool nobody asked to
#       change it.
#   G11 Every import after the first THROWS on the task objects and reports failed: 0.
#       Trigger is a stale <project>_project.precompilecache; the fix is to stop handing
#       this file to import_native at all.
#
# So the YAML is the importable form and the native XML becomes export-only. Readability
# is the side benefit.
# ---------------------------------------------------------------------------------------
# MEASURED on CODESYS 3.5.22.20 (SP22 Patch 2), RefrigeratorControl.project, probe P10.
# Every field below was read off a real task before it was written down here, because the
# plan's own API table had four wrong spellings that reflection could not catch:
#
#   priority        "1" / "31"        a STRING, not an int
#   interval        "t#20ms" / "100"  a STRING, and NOT a consistent format - MainTask
#                                     carries the IEC duration literal, VISU_TASK a bare
#                                     number. Recorded VERBATIM for that reason: a
#                                     normaliser here would rewrite one of them and put a
#                                     diff in front of somebody that they did not make.
#   interval_unit   "ms"              separate from interval, and the two together are the
#                                     truth
#   core_binding    -2 / -1           an INT, and it is the XML's Core2 - confirmed on both
#                                     tasks, MainTask Core2=-2 reading -2 and VISU_TASK
#                                     Core2=-1 reading -1. That correspondence is what makes
#                                     the typed path a fix for G7 rather than a detour
#                                     around it, so it was checked rather than assumed.
#   watchdog        ScriptWatchdog    with enabled / time / sensitivity. `is_enabled` does
#                                     NOT exist - probed, and it raises AttributeError.
#   pous            ScriptPouObjectList, iterable -> ['PLC_PRG', 'Simulation']
# ---------------------------------------------------------------------------------------

# Bump when the document's SHAPE changes, not when a value does. The C# reader keys its
# "can I trust this" decision off it, exactly as the manifest's schema does: an empty task
# list means two different things - a project that schedules nothing, or a file written
# before anybody recorded them.
TASK_SCHEMA = 1

# Key order for the document header and for each task. This is the CONTRACT with the reader
# and with every diff a human looks at, which is why it is a list rather than dict order:
# dict order differs between IronPython 2.7 and CPython 3, and the export has to be
# byte-identical across runs.
TOP_KEYS = ["schema", "vendor", "configuration", "path", "taskCount"]

TASK_KEYS = [
    "name",
    "priority",         # verbatim string, "0" is a real and different value from absent
    "interval",         # verbatim: "t#20ms" or "100" - the format is the project's, not ours
    "intervalUnit",
    "coreBinding",      # the XML's Core2. -1 means unbound
    "watchdogEnabled",
    "watchdogTime",
    "watchdogSensitivity",
    "pous",             # the POUs this task calls, in the task's own order
]

VENDOR = "codesys"

# What this file deliberately does NOT carry, and why. Written into the file itself,
# because an engineer reading a task list has to be able to tell "not recorded" from
# "not set" - and the gap that started this whole piece of work was a comment claiming a
# file that nothing wrote.
_HEADER = u"""Task configuration, exported by Versio. THIS FILE IS THE IMPORTABLE ONE:
edit a value here and the next import applies it to the project. The .xml beside it is
exported for fidelity and is never imported - which is what stops CODESYS rewriting a
task's core binding on every import.

Values are recorded VERBATIM, including interval: CODESYS stores "t#20ms" on one task and
"100" on another in the same project, and normalising either would put a diff in front of
somebody that they did not make.

NOT recorded here, on purpose:
  * the event POU guid - it was observed changing during a native import, and its meaning
    for an interval-driven task is unestablished. A value that moves on its own does not
    belong in a diff"""


def extract_task(task, safe):
    """Shape one task object into the neutral record.

    `safe` is the caller's exception-swallowing probe. The ScriptEngine raises for
    properties that do not apply to a given object, so probing is normal control flow here
    rather than error handling - the same reason versio_libraries.py takes one.

    Absent fields are left OUT of the dict rather than set to None, so the emitter omits
    them entirely: a task with an empty `interval:` line invites the reader to think the
    interval was blanked rather than never applicable.
    """
    rec = {}

    def put(key, value):
        if value is not None and value != "":
            rec[key] = value

    put("name", _text(safe(lambda: task.get_name())))
    put("priority", _text(safe(lambda: task.priority)))
    put("interval", _text(safe(lambda: task.interval)))
    put("intervalUnit", _text(safe(lambda: task.interval_unit)))

    # An int, and the only field here that is. -1 is "not bound to a core" and is a real
    # value, so it must not be dropped by the emptiness check above.
    core = safe(lambda: task.core_binding)
    if core is not None:
        rec["coreBinding"] = int(core)

    watchdog = safe(lambda: task.watchdog)
    if watchdog is not None:
        # enabled is a bool and False is meaningful, so it is set directly rather than
        # through put(), which would drop it.
        enabled = safe(lambda: watchdog.enabled)
        if enabled is not None:
            rec["watchdogEnabled"] = bool(enabled)
        put("watchdogTime", _text(safe(lambda: watchdog.time)))
        put("watchdogSensitivity", _text(safe(lambda: watchdog.sensitivity)))

    pous = read_pous(task, safe)
    if pous:
        rec["pous"] = pous

    return rec


def read_pous(task, safe):
    """The POUs a task calls, in the task's own order.

    NOT SORTED, deliberately, for the same reason the library references are not: the order
    is the project's, and re-ordering the file would hide a reorder somebody made in the
    IDE - a change the mirror exists to show. For a task the order is more than cosmetic,
    since it is the order the POUs run in.
    """
    pous = safe(lambda: task.pous)
    if pous is None:
        return []

    out = []
    for entry in safe(lambda: list(pous), []) or []:
        # Measured as plain strings ('PLC_PRG', 'VisuElems.Visu_Prg'), but ask for a name
        # first: the collection is typed ScriptPouObjectList and a future version returning
        # objects should not silently render as repr().
        name = safe(lambda e=entry: e.get_name())
        text = _text(name if name is not None else entry)
        if text:
            out.append(text)
    return out


def read_tasks(config, safe, on_error=None):
    """Every task under one task configuration, in the configuration's own order."""
    children = safe(lambda: config.get_children())
    if children is None:
        if on_error is not None:
            on_error("TASK_CHILDREN_UNAVAILABLE", None)
        return []

    out = []
    try:
        for child in children:
            out.append(extract_task(child, safe))
    except Exception as e:
        # Report a partial read rather than pretending the shorter list is the truth. A
        # task list that silently loses its tail is worse than one that says it is
        # incomplete: the diff would read as "somebody deleted a task".
        if on_error is not None:
            on_error("TASK_LIST_PARTIAL", str(e))
    return out


def catalog_document(yaml, config_name, config_path, tasks):
    """Render the whole `.tasks.yaml` document.

    `yaml` is passed in rather than imported so this module stays importable on its own;
    the exporter wires the two together.
    """
    top = {
        "schema": TASK_SCHEMA,
        "vendor": VENDOR,
        "configuration": config_name,
        "path": config_path.replace("\\", "/"),
        "taskCount": len(tasks),
    }
    return yaml.document(_HEADER, top, TOP_KEYS, "tasks", tasks, TASK_KEYS)


def _text(value):
    """Coerce a .NET value to text, or None when there is nothing there."""
    if value is None:
        return None
    try:
        text = u"%s" % (value,)
    except Exception:
        return None
    return text
