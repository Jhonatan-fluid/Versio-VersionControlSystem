# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS.
#
# Object type GUID table + the per-type export-form decision.
#
# Base table adopted from greenforge-labs/codescribe (Apache-2.0), src/object_type.py.
# Entries marked [VERSIO] were discovered by probing a real SP22 project and are not
# in codescribe's table.
#
# This module is deliberately PURE - no scriptengine import - so the same logic can be
# unit tested outside CODESYS on CPython. Keep it that way.

GUID_TYPE_MAPPING = {
    "6f9dac99-8de1-4efc-8465-68ac443b7d08": "POU",
    "2db5746d-d284-4425-9f7f-2663a34b0ebc": "DUT",
    "ffbfa93a-b94d-45fc-a329-229860183b1d": "GVL",
    "261bd6e6-249c-4232-bb6f-84c2fbeef430": "GVL_PERSISTENT",
    "327b6465-4e7f-4116-846a-8369c730fd66": "EVC",
    "6654496c-404d-479a-aad2-8551054e5f1e": "INTERFACE",
    "98a2708a-9b18-4f31-82ed-a1465b24fa2d": "TASK",
    "f8a58466-d7f6-439f-bbb8-d4600e41d099": "METHOD",
    "f89f7675-27f1-46b3-8abb-b7da8e774ffd": "METHOD_NORET",
    "5a3b8626-d3e9-4f37-98b5-66420063d91e": "PROPERTY",
    "792f2eb6-721e-4e64-ba20-bc98351056db": "PROPERTY_METHOD",
    "8ac092e5-3128-4e26-9e7e-11016c6684f2": "ACTION",
    "a10c6218-cb94-436f-91c6-e1652575253d": "TRANSITION",
    "bb0b9044-714e-4614-ad3e-33cbdf34d16b": "IMAGEPOOL",
    "2bef0454-1bd3-412a-ac2c-af0f31dbc40f": "TEXTLIST",
    "63784cbb-9ba0-45e6-9d69-babf3f040511": "GLOBAL_TEXTLIST",
    "adb5cb65-8e1d-4a00-b70a-375ea27582f3": "LIBRARY_MANAGER",
    "ae1de277-a207-4a28-9efb-456c06bd52f3": "TASK_CONFIGURATION",
    "085afe48-c5d8-4ea5-ab0d-b35701fa6009": "PROJECT_INFORMATION",
    "8753fe6f-4a22-4320-8103-e553c4fc8e04": "PROJECT_SETTINGS",
    "225bfe47-7336-4dbc-9419-4105a7c831fa": "DEVICE",
    "738bea1e-99bb-4f04-90bb-a7a567e74e3a": "FOLDER",
    "413e2a7d-adb1-4d2c-be29-6ae6e4fab820": "CALL_TO_POU",
    "f18bec89-9fef-401d-9953-2f11739a6808": "VISUALISATION",
    "4d3fdb8f-ab50-4c35-9d3a-d4bb9bb9a628": "VISUALISATION_MANAGER",
    "bc63f5fa-d286-4786-994e-7b27e4f97bd5": "TARGET_VISUALISATION",
    "0fdbf158-1ae0-47d9-9269-cd84be308e9d": "WEB_VISUALISATION",
    "8e687a04-7ca7-42d3-be06-fcbda676c5ef": "VISUALISATION_STYLE",
    # [VERSIO] discovered on CODESYS 3.5.22.20 (SP22 Patch 2), RefrigeratorControl.project.
    # codescribe locates these by name (find("Application")); the type GUIDs are more robust
    # because a project can rename the Application node.
    "40b404f9-e5dc-42c6-907f-c89f4a517386": "PLC_LOGIC",
    "639b491f-5557-464c-af91-1471bac9f549": "APPLICATION",
}

# How each type is written to disk.
#   ST_FULL   - declaration + delimiter + implementation, one .st
#   ST_DECL   - declaration only, one .st
#   ST_IMPL   - implementation only (actions/transitions have no declaration)
#   GVL_BOTH  - native .gvl.xml for structure + .gvl.st for a readable diff
#   NATIVE    - native export, non-recursive
#   NATIVE_R  - native export, recursive (object owns children we do not walk separately)
#   CONTAINER - a structural node: becomes a directory, children walked
#   LIBRARIES - the object's library references, written as neutral YAML
#   SKIP      - deliberately not exported
FORM_ST_FULL = "ST_FULL"
FORM_ST_DECL = "ST_DECL"
FORM_ST_IMPL = "ST_IMPL"
FORM_GVL_BOTH = "GVL_BOTH"
FORM_NATIVE = "NATIVE"
FORM_NATIVE_R = "NATIVE_R"
FORM_CONTAINER = "CONTAINER"
FORM_LIBRARIES = "LIBRARIES"
FORM_SKIP = "SKIP"

TYPE_FORM = {
    "FOLDER": FORM_CONTAINER,
    "DEVICE": FORM_CONTAINER,
    "PLC_LOGIC": FORM_CONTAINER,
    "APPLICATION": FORM_CONTAINER,

    "POU": FORM_ST_FULL,          # falls back to NATIVE when not textual - see decide_form
    "DUT": FORM_ST_DECL,
    "INTERFACE": FORM_ST_FULL,    # [VERSIO] codescribe does not export interfaces at all
    "GVL": FORM_GVL_BOTH,
    "GVL_PERSISTENT": FORM_GVL_BOTH,
    "EVC": FORM_NATIVE,
    "METHOD": FORM_ST_FULL,
    "METHOD_NORET": FORM_ST_FULL,
    "PROPERTY": FORM_NATIVE_R,    # carries getter/setter children
    "ACTION": FORM_ST_IMPL,
    "TRANSITION": FORM_ST_IMPL,

    "VISUALISATION": FORM_NATIVE,
    "TASK_CONFIGURATION": FORM_NATIVE_R,
    "TEXTLIST": FORM_NATIVE,           # [VERSIO] gap closed vs codescribe
    "GLOBAL_TEXTLIST": FORM_NATIVE,    # [VERSIO]
    "IMAGEPOOL": FORM_NATIVE,          # [VERSIO]
    "PROJECT_INFORMATION": FORM_NATIVE,  # [VERSIO]

    # [VERSIO] Was FORM_SKIP with the reason NOT_IMPLEMENTED - the gap that
    # _coverage.md was built to report. Native export is not an option here: the
    # library manager's own XML is a resolution snapshot rather than a list of what
    # the project asks for. Its references go to neutral YAML instead, which is the
    # form a second vendor can also produce.
    "LIBRARY_MANAGER": FORM_LIBRARIES,

    # Service objects: exporting the visu manager makes every later import raise an
    # interactive overwrite dialog (codescribe's finding). The template carries them.
    "VISUALISATION_MANAGER": FORM_SKIP,
    "TARGET_VISUALISATION": FORM_SKIP,
    "WEB_VISUALISATION": FORM_SKIP,
    "VISUALISATION_STYLE": FORM_SKIP,
    "PROJECT_SETTINGS": FORM_SKIP,     # template-only (Tier 3, plan section 3.10)
    "TASK": FORM_SKIP,                 # covered by TASK_CONFIGURATION recursive export
    "CALL_TO_POU": FORM_SKIP,          # ditto
}

# Files Versio writes into an export tree that are ABOUT the export rather than part
# of it.
#
# The importer walks the tree and treats every file it finds as an object to import,
# so anything here that it does not skip is handed to CODESYS as though it were a
# POU. That was a real defect the moment _coverage.md was added: the importer knew
# only about _manifest.json, by name, in two places.
#
# Defined once, and beside the exporter's own table, so the two halves cannot drift -
# the next file added here is added for both of them at the same time.
VERSIO_FILES = frozenset(["_manifest.json", "_coverage.md"])


def is_versio_file(file_name):
    """True for a bookkeeping file that describes the export rather than the project."""
    return file_name in VERSIO_FILES


# Files that are part of the PROJECT but travel one way only: exported for review,
# never applied back.
#
# The export tree turns out to hold THREE kinds of file, not two:
#
#   1. objects           - .st and .xml, exported and imported
#   2. bookkeeping       - VERSIO_FILES, about the export rather than in it
#   3. export-only       - real project state, written for the diff, but with no
#                          import path that can be trusted
#
# Library references are the first of the third kind. Handing this file to
# import_native would be the _coverage.md defect again - CODESYS would be asked to
# parse YAML as native XML and every import would carry a failure warning that is
# always present and therefore always read past. Adding a library through the
# scripting API is a different operation from importing an object, and doing it
# wrongly rewires what the machine compiles against, so it is not attempted here:
# the file is diff-only, and both the coverage report and the manifest say so.
EXPORT_ONLY_SUFFIXES = (".libraries.yaml",)


def is_export_only(file_name):
    """True for a file the export writes that the import must not feed to the toolchain."""
    lowered = file_name.lower()
    for suffix in EXPORT_ONLY_SUFFIXES:
        if lowered.endswith(suffix):
            return True
    return False


# Why a type is skipped, and whether that is a decision or a gap.
#
# The distinction is the entire point. "Not exported" covers two very different
# situations, and collapsing them is how a hole stays invisible:
#
#   SERVICE_OBJECT / TEMPLATE_ONLY / COVERED_ELSEWHERE
#       Deliberate. The object's content is either carried by the template, or
#       already inside another object's recursive export. Nothing is lost.
#
#   NOT_IMPLEMENTED
#       A real gap. The object holds state that no exported file records, so a
#       change to it does not appear in the mirror, does not move the hash gate
#       and does not reach a diff. This is the one somebody has to be told about.
#
# The codes are STABLE machine strings, like the warning codes: the manifest,
# _coverage.md, the C# side and the tests all key off them, and prose alone could
# be reworded and silently break every one of them.
SKIP_DELIBERATE = "SERVICE_OBJECT"
SKIP_TEMPLATE = "TEMPLATE_ONLY"
SKIP_ELSEWHERE = "COVERED_ELSEWHERE"
SKIP_GAP = "NOT_IMPLEMENTED"

SKIP_REASON = {
    # Exporting the visu manager makes every later import raise an interactive
    # overwrite dialog (codescribe's finding). The template carries them.
    "VISUALISATION_MANAGER": SKIP_DELIBERATE,
    "TARGET_VISUALISATION": SKIP_DELIBERATE,
    "WEB_VISUALISATION": SKIP_DELIBERATE,
    "VISUALISATION_STYLE": SKIP_DELIBERATE,

    "PROJECT_SETTINGS": SKIP_TEMPLATE,

    "TASK": SKIP_ELSEWHERE,          # inside TASK_CONFIGURATION's recursive export
    "CALL_TO_POU": SKIP_ELSEWHERE,   # ditto
}

# One sentence per code, for _coverage.md and for anyone reading the manifest.
SKIP_EXPLANATION = {
    SKIP_DELIBERATE:
        "Service object. Exporting it makes every later import raise an interactive "
        "overwrite dialog; the project template carries it instead.",
    SKIP_TEMPLATE:
        "Template-only (Tier 3). Diffable in principle, but changed in CODESYS rather "
        "than in the mirror.",
    SKIP_ELSEWHERE:
        "Already covered by another object's recursive export, so exporting it again "
        "would record the same content twice.",
    SKIP_GAP:
        "NOT EXPORTED YET. Changes to this object do not appear in the mirror, do not "
        "move the change gate and will not show up in a diff or a commit.",
}


def skip_reason(obj_type):
    """Why this type is not exported. Unknown skips are reported as a gap.

    Defaulting to the gap rather than to a deliberate decision is the safe way
    round: a type added to TYPE_FORM as SKIP without a reason beside it is far
    more likely to be an oversight than a considered omission, and the cost of
    saying so wrongly is a line in a report.
    """
    return SKIP_REASON.get(obj_type, SKIP_GAP)

# Suffixes per form. Kept here so the C# PathMapper and the python exporter cannot drift.
#
# FORM_GVL_BOTH resolves to ".st": a GVL writes TWO files, and the exporter builds the
# ".gvl.xml" path explicitly. Omitting this entry made file_name_for() fall through to the
# ".xml" default, so the ST write landed on the same path as the native export and
# destroyed it - leaving a file called .gvl.xml that contained structured text. Caught by
# the round-trip harness; the C# PathMapper had it right all along.
SUFFIX = {
    FORM_ST_FULL: ".st",
    FORM_ST_DECL: ".st",
    FORM_ST_IMPL: ".st",
    FORM_GVL_BOTH: ".st",
    FORM_NATIVE: ".xml",
    FORM_NATIVE_R: ".xml",
    FORM_LIBRARIES: ".libraries.yaml",
}

# Types whose file name gets an infix to avoid colliding with a same-named sibling
# of another type. A "Main" visualisation and a "Main" POU are common.
INFIX = {
    "VISUALISATION": ".vis",
    "GVL": ".gvl",
    "GVL_PERSISTENT": ".gvl",
    "ACTION": ".action",
    "TRANSITION": ".transition",
}


def type_of(type_guid):
    return GUID_TYPE_MAPPING.get(str(type_guid).lower(), "UNKNOWN")


def decide_form(obj_type, has_textual_impl, has_textual_decl):
    """Pure decision: what on-disk form does this object take?

    A POU declared as ST but drawn in CFC/LD/FBD has no textual implementation, and
    must go to native XML or its graphical layout is lost.
    """
    form = TYPE_FORM.get(obj_type)
    if form is None:
        return FORM_NATIVE          # unmapped type: never silently drop it
    if form == FORM_ST_FULL and not has_textual_impl:
        return FORM_NATIVE
    if form == FORM_ST_DECL and not has_textual_decl:
        return FORM_NATIVE
    if form == FORM_ST_IMPL and not has_textual_impl:
        return FORM_NATIVE
    if form == FORM_GVL_BOTH and not has_textual_decl:
        return FORM_NATIVE
    return form


def descends_into_children(obj_type):
    """True when the exporter walks this type's children as objects of their own.

    Decidable from the TYPE ALONE, which is the whole point: it lets the exporter count
    the tree before walking it - for a progress total - without probing
    has_textual_declaration / has_textual_implementation on every object, which are the
    two expensive properties and the two that raise.

    That is sound because decide_form only ever downgrades ST_FULL / ST_DECL / ST_IMPL /
    GVL_BOTH to FORM_NATIVE, and FORM_NATIVE descends. It never turns a descending type
    into a non-descending one, so the count and the walk cannot disagree:

      FORM_SKIP      the exporter returns before recursing
      FORM_NATIVE_R  the children are already inside its own recursive export
      anything else  walked, including an unmapped type, which is why the default is True
    """
    form = TYPE_FORM.get(obj_type)
    return form not in (FORM_SKIP, FORM_NATIVE_R)


def file_name_for(name, obj_type, form, parent_name=None):
    """Build the on-disk file name. Sub-POU members are prefixed with their parent so
    a flat folder can still reconstruct ownership on import."""
    stem = name
    if parent_name and obj_type in ("METHOD", "METHOD_NORET", "PROPERTY", "ACTION", "TRANSITION"):
        stem = parent_name + "." + name
    return stem + INFIX.get(obj_type, "") + SUFFIX.get(form, ".xml")
