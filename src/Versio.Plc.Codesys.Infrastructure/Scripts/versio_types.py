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
    # [VERSIO] measured by P13 (2026-08-17) on CODESYS 3.5.22.20 with real
    # create_external_file_object calls, then confirmed by a real export: the first is
    # the external-file object under an application; the second is the companion node
    # CODESYS puts at the PROJECT ROOT for a file created from an ABSOLUTE path, whose
    # name embeds that path ({GUID}|C:\...\file.txt) - a relative creation gets no
    # companion at all. Both exported natively and losslessly while still UNKNOWN; the
    # mapping exists so a fixture carrying them does not warn on every export, because
    # a warning that is always present is how a real one gets read past.
    "a56744ff-693f-4597-95f9-0e1c529fffc2": "EXTERNAL_FILE",
    "9001d745-b9c5-4d77-90b7-b29c3f77a23b": "EXTERNAL_FILE_SOURCE",
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
# Native .xml for fidelity plus .tasks.yaml for review AND for import. Follows
# GVL_BOTH exactly: one object, two files, one GUID in the manifest. The difference
# is which half is authoritative - a GVL imports from its .st, and a task
# configuration imports from its .tasks.yaml, because handing the .xml to
# import_native is what rewrites Core2 (G7) and throws on the task objects (G11).
#
# "A GVL imports from its .st" WAS NOT TRUE WHEN THIS WAS WRITTEN, and stayed untrue for
# months: the .gvl.xml was recorded plain NATIVE, so both halves were applied and the
# native phase overwrote the textual one. This paragraph was the specification and
# FORM_GVL_NATIVE is what finally enforces it - see G12. A sentence describing a sibling
# form is not a test of it, and this one is why the invariant now has one.
FORM_TASK_BOTH = "TASK_BOTH"
FORM_NATIVE = "NATIVE"
FORM_NATIVE_R = "NATIVE_R"
FORM_CONTAINER = "CONTAINER"
FORM_LIBRARIES = "LIBRARIES"
FORM_SKIP = "SKIP"

# TASK_NATIVE - the .xml half of a TASK_BOTH pair. RECORDED, NEVER DECIDED: decide_form
# does not return it, TYPE_FORM does not contain it, and it never reaches file_name_for,
# because the TASK_BOTH branch of the exporter builds that path itself. It exists for one
# reason - so the manifest SAYS the file is export-only instead of leaving a reader to
# infer it.
#
# It was recorded as NATIVE_R until 2026-08-15, which made it indistinguishable from a
# PROPERTY's recursive export - an importable file. That is how Phase 4 re-opened the gap
# _coverage.md was built to close: the moment the .tasks.yaml became the importable half,
# the .xml beside it became a file the export writes and the import silently ignores, and
# nothing anywhere said so. Editing it looked exactly like editing a POU.
FORM_TASK_NATIVE = "TASK_NATIVE"

# EXTERNAL_NATIVE - an external-file object's one and only file, and export-only from the
# day the type was first imported rather than corrected later, because the correction was
# measured first: the initial E4 round-trip test handed one of these back to import_native
# with NativeImportResolve.replace, and CODESYS DUPLICATED the object - same name, same
# parent, a re-minted GUID, two manifest records colliding on one path. Replace-matching
# evidently does not recognise this type, so every import would have added a copy: G7's
# shape, an unasked-for mutation per cycle, on a new type. The payload is metadata (a
# FileId and a download flag), so there is nothing an engineer could meaningfully edit in
# the mirror anyway - change external files in the IDE.
#
# DECIDED, unlike TASK_NATIVE and GVL_NATIVE above: these objects have no importable
# sibling, so the form comes straight from TYPE_FORM rather than from a two-file branch.
FORM_EXTERNAL_NATIVE = "EXTERNAL_NATIVE"

# GVL_NATIVE - the .xml half of a GVL_BOTH pair, and the same idea as FORM_TASK_NATIVE
# above. RECORDED, NEVER DECIDED, for the same reasons and by the same mechanism.
#
# It was recorded as plain NATIVE until 2026-08-16, and that is G12. Both halves of a GVL
# were therefore importable, and versio_import.py runs textual before native - so an edit
# to the .gvl.st was applied to the object and then overwritten by the .gvl.xml's copy of
# the declaration as it stood at export time, seconds later, with the log reporting
# `applied ... (declaration)` on the way past. The comment on FORM_TASK_BOTH said "a GVL
# imports from its .st" throughout; nothing enforced it, and _coverage.md's silence about
# the file promised the opposite.
#
# WHAT THIS CHANGES BESIDES THE OVERWRITE, because it is not only a label. An export-only
# record neither votes nor vetoes in the delete planner, so a GVL now has exactly ONE
# importable file and stops being a two-file object: deleting `Glob_Var.gvl.st` alone
# removes the GVL, where before the two-file rule kept it. That is deliberate. A GVL now
# behaves like every other single-file object - deleting Simulation.st has always removed
# the POU - and the deletion still has to pass the content check, because ST_DECL is
# verifiable. The .gvl.xml is left in the mirror until the next export tidies it.
#
# AN OLDER MIRROR STILL CARRIES `NATIVE` HERE, and therefore still has the defect until it
# is re-exported. There is no way to tell a pre-fix record from a genuine native object by
# reading it, which is the same limitation every manifest addition has: absent never means
# unchanged.
FORM_GVL_NATIVE = "GVL_NATIVE"

# A PROPERTY WRITES ITS PAYLOAD AND ITS READABLE TEXT, and only the payload comes back.
#
# The owner asked for properties as text - seven on one function block, every one an opaque
# XML blob whose getter is ordinary Structured Text. Exporting them AS ST_DECL + ST_IMPL was
# tried on 2026-08-18 and produced beautiful files; the round trip then came back with all
# seven accessor bodies EMPTY, which is silent destruction of every property in the project.
# See the Finding in STATUS.md.
#
# So this is the GVL_BOTH shape with the halves swapped. A GVL's readable .st is the half
# that imports and its .xml is export-only; a property's recursive .xml is the half that
# imports - unchanged, still the only thing ever applied back - and its text is export-only.
# Reviewable in a diff, readable in the panel, and never reassembled, which is precisely the
# step that emptied them.
#
# The cost is stated rather than hidden: a property EDITED IN THE MIRROR does not flow back.
# That is the same bargain the .gvl.xml and the task configuration's .xml already make, the
# import announces it per file, and the panel says "change this in the IDE".
FORM_PROPERTY_BOTH = "PROPERTY_BOTH"
FORM_PROPERTY_TEXT = "PROPERTY_TEXT"

# Forms whose file is written for review and never applied back.
#
# ONE list, because the rule has to be in one place: it is read by the coverage report
# here and mirrored by ExportCoverage.IsExportOnly on the C# side, which is what the
# panel's lock glyph reads. The two are compared in TypeTableParityTests - a form added
# here and not there means the file is announced in _coverage.md and still looks editable
# in the app, which is half a warning and worse than none.
EXPORT_ONLY_FORMS = (FORM_LIBRARIES, FORM_TASK_NATIVE, FORM_GVL_NATIVE,
                     FORM_EXTERNAL_NATIVE, FORM_PROPERTY_TEXT)

# NATIVE_WITH_CHILDREN - export the node itself natively, non-recursively, AND descend into
# a subdirectory for its children.
#
# Reserved for UNMAPPED types that turn out to be structural, and it is not reachable from
# TYPE_FORM: decide_form produces it, from a type it does not recognise that has children.
#
# The type table is incomplete by nature. Probing a 25-object toy project found two missing
# GUIDs - "Plc Logic" and "Application" - and a real machine project will find more. Treating
# an unknown container as a plain leaf flattens its children into the PARENT's folder and
# quietly destroys the tree shape; treating it as a pure container drops whatever payload the
# node itself carries. This does neither.
#
# ExportFormRules.cs has specified this since the seam was written and nothing called it, so
# for a long time the exporter that actually runs did the flattening the C# was documented to
# prevent. Adding the two probed GUIDs to the table hid it rather than fixed it: the next
# unmapped container would have flattened just the same.
FORM_NATIVE_WITH_CHILDREN = "NATIVE_WITH_CHILDREN"


# Forms whose exported file can be rebuilt from a live object, and therefore the only forms
# whose DELETION can be checked before it happens.
#
# Here rather than in versio_import.py for two reasons, and the second is the load-bearing
# one. This module is pure - no scriptengine - so the rule can be run under CPython and
# compared against its C# mirror, CodesysImportPlanner.WhyUnverifiable, by
# PythonFileNameParityTests. The importer cannot be imported outside CODESYS at all, so a
# rule living there is a rule nothing can compare.
#
# The two sides have to agree because they answer the same question ~20 s apart: the planner
# answers it for the confirmation dialog, before CODESYS is started, and the script answers it
# for the object itself. A planner that promises a removal the script then refuses puts a lie
# in front of somebody about to destroy work - which is exactly what shipped once, pointed the
# other way, when the script grew remove() and the dialog went on saying objects would stay.
#
# A textual form is checked by rebuilding the file byte for byte - P7 measured zero .st
# differences across three round trips, so the comparison means what it says.
CAN_VERIFY_FORMS = (FORM_ST_FULL, FORM_ST_DECL, FORM_ST_IMPL)

# Native forms are checked through the NORMALISED hash the export recorded beside the raw one,
# and only when the manifest carries it - schema 5 and later.
#
# The raw hash cannot be used and that is measured, not assumed. It is taken over the exported
# native bytes, and those churn: across three export/import round trips of the fixture with
# nothing edited, 4 native files came back byte-different every time. Hashing a fresh export
# against the recorded raw hash would refuse every native deletion forever, for a reason with
# nothing to do with the object.
#
# So the export records content_hash(normalize(text)) as well, and a delete check re-exports
# the live object, normalises it the same way, and compares. What normalisation removes is
# exactly the churn: counter values, and the ordering of unordered containers.
#
# MASKED GUIDS, and this is the subtle one. versio_native_equiv.normalize says masking must be
# OFF when comparing two exports of the same project, because GUIDs are stable across exports -
# and M1 confirms that, two exports into two empty directories are byte-identical. But this is
# not that comparison. The recorded hash and the fresh export are separated by however many
# round trips have happened since, and ACROSS A ROUND TRIP CODESYS re-mints GUIDs: measured on
# GlobalTextList.xml, 3 lines of 117 differ and all three are System.Guid values (GuidInit,
# GuidReInit, GuidExitX). That is the round-trip case the docstring says masking is required
# for.
#
# What it costs: an edit that changes GUID VALUES and nothing else would read as unchanged.
# Masking is positional - each GUID becomes <masked-guid>, so an added or removed one still
# shows - and no realistic IDE edit changes only the value of an existing GUID. It is stated
# here rather than glossed because it is the one way this check can be wrong, and being wrong
# here destroys work nothing else holds a copy of.
NATIVE_FORMS = (FORM_NATIVE, FORM_NATIVE_R, FORM_NATIVE_WITH_CHILDREN)


def can_verify(form, normalized_hash=None):
    """Can a deletion of an object exported in this form be checked before it happens?

    `normalized_hash` is the manifest's `nhash` for the file, absent on a pre-schema-5
    mirror. A native object exported before Versio recorded one stays unverifiable until
    the project is exported again - the same "unavailable until the next export" shape the
    planner already uses for a manifest below the reporting schema.
    """
    if form in CAN_VERIFY_FORMS:
        return True
    return form in NATIVE_FORMS and bool(normalized_hash)

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
    # A PROPERTY STAYS RECURSIVE, AND IT IS NOT FOR WANT OF TRYING.
    #
    # The owner asked for properties as readable text, and the shape is genuinely there.
    # Measured on their project 2026-08-18: a PROPERTY has a textual declaration of 29-64
    # characters (the pragma and `PROPERTY P_Active : BOOL`) and NO implementation; each
    # accessor has an EMPTY declaration and a real implementation of 17-40. So ST_DECL +
    # ST_IMPL was tried, and the EXPORT half worked perfectly - P_Active.st holding the
    # header, P_Active/Get.st holding `P_Active := State = E_AlarmState.Active;`.
    #
    # IT WAS REVERTED BECAUSE THE ROUND TRIP EMPTIES THE GETTER: export, import, re-export
    # with nothing edited, and all seven accessor bodies come back as empty files. Which half
    # does it is not yet measured - replacing the property's declaration may reset its
    # accessors, or applying an accessor's EMPTY declaration may wipe the implementation
    # beside it. Until that is known, the recursive payload is the only form that survives
    # the trip: the accessors travel inside it and nothing is reassembled.
    "PROPERTY": FORM_PROPERTY_BOTH,   # importable .xml + readable .st beside it
    "PROPERTY_METHOD": FORM_PROPERTY_TEXT,
    "ACTION": FORM_ST_IMPL,
    "TRANSITION": FORM_ST_IMPL,

    "VISUALISATION": FORM_NATIVE,
    # Was FORM_NATIVE_R, which round-tripped through import_native and cost two
    # defects: G7 (Core2 rewritten unasked) and G11 (every import after the first
    # throws on MainTask and VISU_TASK while reporting failed: 0).
    "TASK_CONFIGURATION": FORM_TASK_BOTH,
    "TEXTLIST": FORM_NATIVE,           # [VERSIO] gap closed vs codescribe
    "GLOBAL_TEXTLIST": FORM_NATIVE,    # [VERSIO]
    "IMAGEPOOL": FORM_NATIVE,          # [VERSIO]
    "PROJECT_INFORMATION": FORM_NATIVE,  # [VERSIO]

    # [VERSIO] Export-only, and the reason is a measurement, not a policy: see the
    # comment on FORM_EXTERNAL_NATIVE. import_native duplicates these instead of
    # replacing them.
    "EXTERNAL_FILE": FORM_EXTERNAL_NATIVE,
    "EXTERNAL_FILE_SOURCE": FORM_EXTERNAL_NATIVE,

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


# The export tree holds THREE kinds of file, not two:
#
#   1. objects           - .st and .xml, exported and imported
#   2. bookkeeping       - VERSIO_FILES, about the export rather than in it
#   3. export-only       - real project state, written for the diff, but with no
#                          import path that can be trusted (EXPORT_ONLY_FORMS above:
#                          library references, and the native half of a task pair)
#
# The import side used to re-derive the third kind from file suffixes here -
# is_export_only, is_typed_form, and typed_sibling_candidates, whose docstring promised
# "once the importer is plan-driven, the plan says which files are importable and this
# rule goes". The importer is plan-driven now (job schema 2), the plan is computed
# C#-side from the manifest's per-file `form`, and those three functions went with the
# walk that called them. What remains authoritative on this side is EXPORT_ONLY_FORMS -
# the coverage report reads it, and TypeTableParityTests holds it equal to
# ExportCoverage.IsExportOnly so the two languages cannot drift.


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

    # A GRAPHICAL accessor only. One with a textual body is exported as readable text
    # (FORM_PROPERTY_TEXT); this reason is reached through decide_form when there is no body
    # to write, and it is COVERED_ELSEWHERE rather than a gap because the accessor is already
    # inside its property's recursive payload either way.
    "PROPERTY_METHOD": SKIP_ELSEWHERE,
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
    FORM_NATIVE_WITH_CHILDREN: ".xml",
    # Same suffix as plain NATIVE on purpose: the two external files already committed
    # in the member fixture keep their names, so retyping the form renamed nothing.
    FORM_EXTERNAL_NATIVE: ".xml",
    FORM_LIBRARIES: ".libraries.yaml",
    FORM_TASK_BOTH: ".tasks.yaml",
    FORM_PROPERTY_BOTH: ".st",
    FORM_PROPERTY_TEXT: ".st",
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


def decide_form(obj_type, has_textual_impl, has_textual_decl, has_children=False):
    """Pure decision: what on-disk form does this object take?

    A POU declared as ST but drawn in CFC/LD/FBD has no textual implementation, and
    must go to native XML or its graphical layout is lost.

    `has_children` matters only for an UNMAPPED type: one with children is most likely
    structural and must keep its subtree, so it becomes NATIVE_WITH_CHILDREN rather than a
    plain leaf whose children flatten into the parent's folder. Mirrors the same argument
    in ExportFormRules.Decide.
    """
    form = TYPE_FORM.get(obj_type)
    if form is None:
        # Unmapped type: never silently drop it, and never silently flatten it either.
        return FORM_NATIVE_WITH_CHILDREN if has_children else FORM_NATIVE
    if form == FORM_ST_FULL and not has_textual_impl:
        return FORM_NATIVE
    if form == FORM_ST_DECL and not has_textual_decl:
        return FORM_NATIVE
    if form == FORM_ST_IMPL and not has_textual_impl:
        return FORM_NATIVE
    if form == FORM_GVL_BOTH and not has_textual_decl:
        return FORM_NATIVE

    # A graphical property has no header to write, so it is the recursive payload alone -
    # exactly what it was before the readable half existed.
    if form == FORM_PROPERTY_BOTH and not has_textual_decl:
        return FORM_NATIVE_R

    # An accessor with no textual body is already inside its property's recursive payload,
    # so there is nothing to add and nothing is lost by saying so. SKIP rather than NATIVE:
    # a second native file for the same object is the double representation that G12 and G7
    # were both about.
    if form == FORM_PROPERTY_TEXT and not has_textual_impl:
        return FORM_SKIP
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
    # TASK_BOTH joins NATIVE_R: its .xml is a recursive export that already contains
    # the tasks, and its .tasks.yaml lists them too. Walking them again would write a
    # third copy of each task as a file of its own.
    return form not in (FORM_SKIP, FORM_NATIVE_R, FORM_TASK_BOTH)


def file_name_for(name, obj_type, form, parent_name=None):
    """Build the on-disk file name. Sub-POU members are prefixed with their parent so
    a flat folder can still reconstruct ownership on import."""
    stem = name
    if parent_name and obj_type in ("METHOD", "METHOD_NORET", "PROPERTY", "ACTION", "TRANSITION"):
        stem = parent_name + "." + name
    return stem + INFIX.get(obj_type, "") + SUFFIX.get(form, ".xml")
