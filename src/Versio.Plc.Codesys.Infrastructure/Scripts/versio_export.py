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

import errno
import io
import json
import os
import shutil
import sys
import time
import traceback

import scriptengine  # type: ignore

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from versio_text import (content_hash, join_st, normalize_decl_only,  # noqa: E402
                         line_sketch, object_path, safe_file_stem,
                         scrub_native_xml, split_st, too_long_for_any_machine,
                         too_long_for_this_machine)
from versio_types import (EXPORT_ONLY_FORMS, FORM_CONTAINER,  # noqa: E402
                          FORM_EXTERNAL_NATIVE, FORM_GVL_BOTH, FORM_GVL_NATIVE,
                          FORM_PROPERTY_BOTH, FORM_PROPERTY_TEXT,
                          FORM_LIBRARIES, FORM_NATIVE, FORM_NATIVE_R,
                          FORM_NATIVE_WITH_CHILDREN, FORM_SKIP, FORM_ST_DECL,
                          FORM_ST_FULL, FORM_ST_IMPL, FORM_TASK_BOTH,
                          FORM_TASK_NATIVE, NATIVE_FORMS, SKIP_EXPLANATION,
                          SKIP_GAP, decide_form, descends_into_children,
                          file_name_for, skip_reason, type_of)
from versio_native_equiv import are_equivalent, normalize  # noqa: E402
import versio_libraries  # noqa: E402
import versio_tasks  # noqa: E402
import versio_yaml  # noqa: E402


# Which toolchain wrote this mirror. Recorded in the manifest from schema 8 so a reader
# does not have to infer it from the shape of the files - see the schema note below.
#
# Spelled the way repo.json and PlcVendor.Codesys spell it, NOT the way versio_libraries
# and versio_tasks spell it in their sidecars ("codesys", lower case, and duplicated in
# both modules). Those bytes are already committed in every mirror and rewriting them
# would churn a file in every repository to fix a capital letter; the C# side compares
# vendor names case-insensitively, which is what makes leaving them alone safe.
VENDOR = "Codesys"


def emit(obj):
    """Write one JSON-lines protocol event to stdout.

    The @@VERSIO@@ marker exists because CODESYS shares this stdout and cannot be
    silenced. The C# host splits structured events from CODESYS chatter on that
    prefix. Scraping prose was never an option: CODESYS's messages are localised
    and change between service packs.
    """
    print("@@VERSIO@@" + json.dumps(obj))


def safe(fn, default=None):
    """Call fn, returning default if it raises.

    The ScriptEngine throws for properties that do not apply to a given object
    type - has_textual_implementation on a folder, for instance - so probing is
    normal control flow here rather than error handling.
    """
    try:
        return fn()
    except Exception:
        return default


# The open project, so a failure anywhere can still close it.
#
# CODESYS writes <project>.~u while it holds a project and removes it on close. A script that
# died between open and close left that marker behind, and the next run then had to treat it as
# a crashed IDE and reason about whether the pid was alive - work caused entirely by us, on a
# project nobody actually had open.
#
# A module-level handle rather than a try/finally around the body of main(), because the body
# is long and re-indenting it to add a guard makes the diff about whitespace.
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
        # Worth a line: it means the marker is still there and the next run will find it.
        emit({"t": "warning", "code": "PROJECT_CLOSE_FAILED", "path": None, "detail": str(e)})


def write_text(path, text):
    """Write UTF-8 without a BOM, LF endings, creating parent folders.

    io.open rather than the builtin: Python 2's open() writes a byte stream and
    implicitly encodes unicode as ascii, so it dies on Cyrillic, accented
    characters and smart punctuation - all of which appear in real PLC comments.

    newline="" stops Python translating \\n into \\r\\n. The exporter hashes what
    it writes, so a CRLF here would change every file's hash on Windows and make
    the entire tree look modified.
    """
    d = os.path.dirname(path)
    if d and not os.path.isdir(d):
        os.makedirs(d)
    with io.open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def read_text(path):
    """Read UTF-8 with newlines preserved exactly as written."""
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        return f.read()


class Exporter(object):
    """Walks the CODESYS object tree and writes one file per object.

    Folders in the export mirror folders in the project, so the on-disk shape is
    the shape the engineer sees in the IDE. Each object's form - readable ST or
    native XML - is decided by versio_types.decide_form.
    """

    def __init__(self, out_root, expected=0, previous=None):
        self.out_root = out_root

        # WHERE THE LAST EXPORT'S BYTES ARE, or None on the first export ever.
        #
        # out_root above is the STAGING directory - empty by construction, because
        # main() creates it fresh and the three-rename swap happens afterwards. So the
        # mirror somebody has committed is somewhere else entirely, and preserving a
        # byte from it means being told where. That is what this is.
        #
        # Read BEFORE the swap, which is the only window in which both exist.
        self.previous = previous
        self.preserved = 0
        self.records = []
        self.warnings = []
        self.skips = []
        self.total = 0
        self.skipped = 0

        # How many objects the pre-count expects to reach. Used only for the progress
        # denominator, so a wrong answer costs a bar that does not land on 100% - never
        # a wrong export. Zero means "nobody counted", and progress is emitted without
        # a total rather than with a made-up one.
        self.expected = expected

    def warn(self, code, path, detail=None):
        """Record a warning and emit it immediately.

        `code` is a STABLE machine string, not prose. It is what lets the
        manifest, _coverage.md, the UI and the tests all agree about what
        happened - a message alone could be reworded and silently break them.
        """
        w = {"code": code, "path": path}
        if detail:
            w["detail"] = detail
        self.warnings.append(w)
        emit({"t": "warning", "code": code, "path": path, "detail": detail})

    def check_path_length(self, rel_path, abs_path):
        """Both G9 guards, for ONE file the export is about to write.

        PER FILE, NOT PER OBJECT, and that distinction is the bug this was extracted
        for. The first version measured only the object's primary path, so a
        GVL_BOTH's .gvl.xml went unchecked - and that is the LONGER of its pair by a
        character, so the one file a GVL could overflow on was the one nothing looked
        at. Same shape as G7 and G12: a rule written for objects, applied to a thing
        that writes two files. TASK_BOTH was safe only by luck, its .tasks.yaml being
        longer than its .xml.

        The warning names the FILE rather than the object, unlike EXPORT_FAILED and
        UNNAMED_OBJECT. A path length is a fact about a path; naming the object would
        make a GVL's two answers indistinguishable, which is exactly what left the
        second one unnoticed.
        """
        spelled = rel_path.replace("\\", "/")

        detail = too_long_for_any_machine(spelled)
        if detail:
            self.warn("PATH_TOO_LONG", spelled, detail)

        detail = too_long_for_this_machine(abs_path)
        if detail:
            # emit, NOT warn, and this is the whole reason the two guards are separate
            # functions. warn() appends to self.warnings, which is written into
            # _manifest.json - so a sentence whose answer depends on the repository
            # root would make the manifest differ between two developers exporting the
            # same project, and the mirror would churn on every export by whoever is
            # checked out deeper. That is the churn Phase 5 closed, arriving through a
            # new door. PROJECT_CLOSE_FAILED goes this way for the same reason: a
            # protocol event, not a coverage fact.
            emit({"t": "warning", "code": "PATH_TOO_LONG_HERE",
                  "path": spelled, "detail": detail})

    def progress(self, name):
        """Say which object is being exported, and how far through the tree we are.

        Emitted BEFORE the object is written rather than after, because the field the
        host shows is the name of the thing currently being worked on - and the objects
        worth naming are exactly the ones slow enough to still be on screen. The
        fixture's Live_Visu is 2.5 MB on its own.

        `total` is omitted when nobody counted, which the host reads as an
        indeterminate bar rather than as a total of zero. Before this the host had a
        handler for these events and no script emitted one, so an export was a
        featureless 17-31 seconds no matter how much of it was left.
        """
        evt = {"t": "progress", "done": self.total, "obj": name}
        if self.expected > 0:
            evt["total"] = self.expected
        emit(evt)

    def record(self, obj, obj_type, form, rel_path, text, parent_guid=None):
        """Note one written file in the manifest, keyed by the object's GUID.

        The GUID is the identity, not the path: it is what lets a moved object be
        recognised as a move rather than a delete plus an add, and it survives a
        rename that the filename cannot.

        `parent_guid` is the owner for a sub-POU member - a method, action,
        transition or property - and absent for everything else. It is what lets
        the panel draw clsLight's methods UNDER clsLight, the way CODESYS does.

        Written as the parent's GUID rather than as its name or its path, for the
        same reason identity is a GUID everywhere else here: the export puts a
        member beside its owner as `clsLight.Light.xml`, and reading ownership back
        out of that filename is a guess that a name containing a dot defeats. A
        property's own accessors nest again, so this is a chain and not one level.
        """
        h = content_hash(text)
        rec = {
            "guid": safe(lambda: str(obj.guid), ""),
            "name": safe(lambda: obj.get_name(), ""),
            "type": obj_type,
            "form": form,
            "path": rel_path.replace("\\", "/"),
            "hash": h,
            "bytes": len(text.encode("utf-8")),
        }

        # A second hash for native payloads, over the NORMALISED form - schema 5.
        #
        # `hash` above is over the bytes on disk, and for a native file those churn: three
        # export/import round trips of the fixture with nothing edited came back
        # byte-different on 4 of them every time. That makes `hash` useless for the one
        # question a delete has to answer - "is the live object still what was exported?" -
        # because a re-export never reproduces it. Comparing through versio_native_equiv
        # would settle it, but it needs the OLD bytes, and for a deletion those were in the
        # file that was just deleted. Recording the comparison form HERE is what breaks that
        # circle: the old side is in the manifest, so the fresh export has something to be
        # compared against.
        #
        # Additive, and deliberately so. A pre-schema-5 mirror simply has no nhash, and
        # can_verify answers False for its native objects - the same "unavailable until the
        # next export" shape the planner already uses. No existing mirror is invalidated and
        # no committed file changes shape.
        #
        # mask_guids is left at its default of True, and that is the load-bearing choice: see
        # the note above NATIVE_FORMS in versio_types.py. Across a round trip CODESYS re-mints
        # GUIDs, measured as 3 differing lines of 117 in GlobalTextList.xml and all three
        # System.Guid values, so an unmasked hash would refuse those deletions forever.
        if form in NATIVE_FORMS:
            rec["nhash"] = content_hash(normalize(text)[0])

        # A third hash for a full ST body, over the IMPLEMENTATION alone - schema 6.
        #
        # It exists so a renamed POU can be recognised as one. `hash` is over the whole file,
        # and a POU's file opens `PROGRAM Simulation` - the object's name is IN the content -
        # so renaming the object changes the file and the whole-file hash matches nothing.
        # The body does not change, and that is the signal.
        #
        # ST_FULL only, and that is not an oversight. An action or a transition is
        # implementation with no declaration, and a GVL's .st is a declaration with no name
        # in it, so for both of those the whole-file hash already survives a rename and a
        # second hash would say nothing new.
        #
        # DELIBERATELY NOT a name-masked declaration hash beside it. That was the first
        # design; masking means finding the name inside the declaration, and doing it
        # identically in IronPython 2.7 and .NET regex - across Unicode word boundaries and a
        # comment that happens to contain the object's name - is a parity problem worth more
        # than it buys. The implementation alone is exact, needs no parsing, and is the half
        # a rename does not touch.
        if form == FORM_ST_FULL:
            _decl, impl = split_st(text)
            rec["ihash"] = content_hash(impl)

            # And a bounded fingerprint of the whole file - schema 7.
            #
            # `ihash` pairs a rename whose BODY is untouched. A rename that edits the body too
            # matches no hash at all, and the only way to recognise it is to compare content -
            # which needs the departed file, and the mirror has just lost it. Git has it when
            # the mirror was committed; THIS is what covers the case where it was not, or
            # where the file was never tracked.
            #
            # Recorded here because this is the last moment the content exists anywhere.
            rec["shash"] = line_sketch(text)

        # Omitted rather than written empty, so a top-level object's record is
        # byte-identical to what schema 3 wrote. An export that changed every line
        # of the manifest for objects that did not move would put a diff in front
        # of somebody with nothing in it to read.
        if parent_guid:
            rec["parent"] = parent_guid

        self.records.append(rec)
        emit({"t": "object", "path": rel_path.replace("\\", "/"),
              "type": obj_type, "form": form, "hash": h})

    def export_native(self, obj, abs_path, recursive):
        """Export one object as native CODESYS XML, scrubbed for determinism.

        CODESYS writes to a file, so this goes via a temp file and rewrites it in
        place. The scrub removes the Timestamp field, which CODESYS re-mints on
        every run and which would otherwise make every export produce a diff.

        GUIDs are deliberately NOT scrubbed. codescribe tried that and it broke
        visualisations; they are identity, not noise.
        """
        tmp = abs_path + ".raw"
        d = os.path.dirname(abs_path)
        if d and not os.path.isdir(d):
            os.makedirs(d)
        obj.export_native(tmp, recursive)
        raw = read_text(tmp)
        os.remove(tmp)
        scrubbed, n = scrub_native_xml(raw)

        # G10, AND THE ORDER HERE IS THE WHOLE FIX.
        #
        # The substitution happens BEFORE the caller hashes anything, because the
        # caller hashes what this returns: record() does `h = content_hash(text)` on
        # the value it is handed, and write_text's docstring states the invariant it
        # depends on - "the exporter hashes what it writes". Swapping the bytes at
        # write time instead would leave _manifest.json recording a hash of a payload
        # that is not the one on disk beside it, and Versio's hash gate reads the
        # disk. The manifest is the one file that still differed between two steady
        # state snapshots, and it settles only if the recorded hash follows the bytes
        # actually written.
        payload = self.preserve_equivalent(abs_path, scrubbed)
        write_text(abs_path, payload)
        return payload, n

    def preserve_equivalent(self, abs_path, scrubbed):
        """The bytes already in the mirror when they mean the same thing, else the new
        ones.

        WHY THIS EXISTS. P7 ran three import/export cycles with nothing edited between
        them and watched UniqueIdGenerator climb 44 -> 47 -> 50 -> 53, +3 every time,
        never settling. Four of eight native files differed forever. Every one of those
        differences is semantically nothing - measured, not assumed: all four are
        NativeXmlNormalizer-equivalent with symmetric mask counts, which is what says
        they differ in the masked positions and nowhere else.

        KEYED ON EQUIVALENCE, NEVER ON THE COUNTER, and that is not a stylistic
        preference. GlobalTextList.xml churns every cycle and contains no counter at
        all; a fix aimed at UniqueIdGenerator would have left that file diffing forever
        and looked like it worked on the three that do carry one.

        THE DETERMINISM GATE STILL HOLDS. Two exports into two empty directories stay
        byte-identical, because neither has a previous mirror to preserve from - the
        branch below is simply not taken. Only export-into-an-existing-mirror behaves
        differently, and that is the case this is for.

        MASKED COMPARISON, deliberately. are_equivalent masks GUIDs by default and the
        churn is exactly there: Phase 2 measured that GUIDs are stable across two
        exports and NOT across a round trip. Keeping the old bytes therefore keeps the
        old ids, which is what the mirror already held and what CODESYS already accepts
        back - import_native replaces an existing object in place either way.

        WHAT MASKING COSTS, written down rather than discovered. An object DELETED and
        RECREATED with identical content has a new identity but an equivalent payload,
        so this keeps the old bytes and the mirror's XML carries ids the project no
        longer has - while the manifest, which reads obj.guid directly, records the new
        one. The two then disagree.

        Not fixed, and this is the reasoning rather than an oversight. The disagreement
        is not destructive: importing that file replaces the object in place and the old
        ids simply come back, which is the state the mirror already described. And it
        cannot be told apart from the ordinary case by looking at the payload, because
        the ordinary case IS a GUID that changed for no reason - that is the churn this
        exists to remove. The cheap fix if it ever matters is to refuse preservation
        when the last manifest's GUID for this path differs from the live object's,
        which costs loading the old manifest here; nobody has seen it happen, so it is
        recorded rather than built.
        """
        if not self.previous:
            return scrubbed

        rel_path = os.path.relpath(abs_path, self.out_root)
        old_path = os.path.join(self.previous, rel_path)
        if not os.path.isfile(old_path):
            return scrubbed

        old = safe(lambda: read_text(old_path), None)
        if old is None or old == scrubbed:
            # Already identical: there is nothing to preserve and nothing to say. Most
            # files in most exports land here, which is why this is not reported.
            return scrubbed

        if not safe(lambda: are_equivalent(old, scrubbed), False):
            # A real change. This is the ordinary reason a native file differs and the
            # whole point of exporting it.
            return scrubbed

        self.preserved += 1
        emit({"t": "preserved", "path": rel_path.replace("\\", "/")})
        return old

    def export_libraries(self, obj, obj_path, rel_path):
        """Render one library manager's references as neutral YAML.

        This is the object whose absence started the coverage report: a library
        reference - adding one, bumping a version, repointing a placeholder - changed
        nothing in the mirror, moved nothing through the hash gate and reached no
        diff, while a comment in this file claimed a config/libraries.yaml that
        nothing had ever written.

        The file is EXPORT-ONLY. It is written for review and is never applied back:
        see EXPORT_ONLY_FORMS in versio_types for why an import path is not merely
        missing but deliberately absent.
        """
        refs = versio_libraries.read_references(
            obj, safe,
            on_error=lambda code, detail: self.warn(code, rel_path, detail))

        name = safe(lambda: obj.get_name(), u"Library Manager")
        text = versio_libraries.catalog_document(versio_yaml, name, obj_path, refs)

        # Said out loud, at Info: this is the one export form whose whole point is
        # content that used to be invisible, and "0 references" is a real answer that
        # somebody may need to see rather than infer from a file they never opened.
        emit({"t": "libraries", "path": rel_path.replace("\\", "/"),
              "count": len(refs),
              "system": len([r for r in refs if r.get("systemLibrary")])})
        return text

    def export_tasks(self, obj, obj_path, rel_path):
        """Render one task configuration's tasks as neutral YAML.

        Unlike the library file beside it, this one is IMPORTABLE and is the authoritative
        half of the pair. The .xml is written too, for fidelity and for a reviewer who
        wants the whole picture, but it is never applied back - handing it to import_native
        is what rewrote Core2 on every import (G7) and what threw on the task objects while
        reporting failed: 0 (G11).
        """
        tasks = versio_tasks.read_tasks(
            obj, safe,
            on_error=lambda code, detail: self.warn(code, rel_path, detail))

        name = safe(lambda: obj.get_name(), u"Task Configuration")
        text = versio_tasks.catalog_document(versio_yaml, name, obj_path, tasks)

        # Said out loud, at Info, for the same reason the library count is: this is state
        # that used to be an opaque XML blob in the diff, and "how many tasks" is a
        # question somebody may need answered without opening the file.
        emit({"t": "tasks", "path": rel_path.replace("\\", "/"), "count": len(tasks)})
        return text

    def handle(self, obj, rel_dir, parent_guid=None):
        """Export one object and recurse into its children.

        `rel_dir` is the directory mirroring this object's place in the CODESYS tree,
        and from schema 9 that is the WHOLE of where an object goes: a container
        makes a directory, an object with children makes one named after itself, and
        a member goes inside its owner's. The mirror path is the path the IDE shows.

        `parent_guid` is the owner of a sub-POU member - a method, action, transition
        or property - and absent for everything else. It is not how the file is
        placed any more; it answers the question the path cannot, which is whether
        `FB_Alarm/M_Reset.st` is the POU's method or an object that happens to sit
        in a folder of the same name. It survives a rename, as a name never could.

        There used to be a `parent_name` beside it, because a member's FILE was
        prefixed with its owner (`FB_Alarm.M_Reset.st`) to reconstruct ownership out
        of a flat directory. The directory does that now, and a prefix as well would
        say the same thing twice - `FB_Alarm/FB_Alarm.M_Reset.st`.

        Unmapped type GUIDs are exported natively and warned about, never dropped:
        a newer CODESYS adding a type must not silently lose objects.
        """
        name = safe(lambda: obj.get_name(), None)
        if name is None:
            # object_path with no name, not a raw rel_dir. This is the earliest way an
            # object can vanish - before `total`, before a form, before any record or
            # skip - so the directory is the entire description of it that will ever
            # exist, and ExportCoverage reads this code and prints that directory. A
            # backslash in it makes the one clue unsearchable.
            self.warn("UNNAMED_OBJECT", object_path(rel_dir, None))
            return
        type_guid = safe(lambda: str(obj.type), "")
        obj_type = type_of(type_guid)
        has_impl = bool(safe(lambda: obj.has_textual_implementation, False))
        has_decl = bool(safe(lambda: obj.has_textual_declaration, False))

        if obj_type == "UNKNOWN":
            self.warn("UNKNOWN_TYPE_GUID", object_path(rel_dir, name), type_guid)

        # Read once and reused: an unmapped type's form depends on whether it HAS children,
        # and asking CODESYS twice for the same list is both wasteful and a chance for the
        # decision and the walk to see different answers.
        children = safe(lambda: obj.get_children(), []) or []

        form = decide_form(obj_type, has_impl, has_decl, bool(children))
        self.total += 1
        self.progress(name)

        if form == FORM_SKIP:
            # Recorded, not merely counted. A count says "8 objects were not
            # exported" and leaves the reader to guess whether that was a decision
            # or a hole; the reason is the whole information.
            reason = skip_reason(obj_type)
            self.skipped += 1

            # One spelling of the path, shared by the manifest and the log line. Two would
            # mean a reader comparing the log against _manifest.json sees different strings
            # for the same object and has to work out whether that means anything.
            #
            # This was the only site that honoured that, and it was stated here rather
            # than anywhere the other four callers would see it - so the two warnings did
            # not. versio_text.object_path is the rule now; the rest of the argument is
            # in its docstring.
            skip_path = object_path(rel_dir, name)

            self.skips.append({
                # No guid. CODESYS re-mints the project-level __VisualizationStyle's guid on
                # EVERY open, so recording it here made _manifest.json differ on every export
                # - a spurious diff in every commit, which is the exact noise the Timestamp
                # scrub exists to remove. A guid that changes when you look at it is not an
                # identity, and a skipped object has nothing pointing at it anyway: path and
                # type are what the report is keyed on.
                "name": name,
                "type": obj_type,
                "path": skip_path,
                "reason": reason,
            })
            emit({"t": "skip", "path": skip_path, "type": obj_type, "reason": reason})
            return

        if form == FORM_CONTAINER:
            # A folder is a directory, wherever it sits - including inside a POU, where
            # `parent_guid` carries the OWNER through it so the members below still record
            # whose they are. The two facts are independent: the directory says where the
            # object is shown, the owner GUID says what it belongs to, and a method grouped
            # into `Private` needs both said.
            child_dir = os.path.join(rel_dir, safe_file_stem(name))
            for child in children:
                self.handle(child, child_dir, parent_guid)
            return

        stem_name = safe_file_stem(name)
        fname = file_name_for(stem_name, obj_type, form)
        rel_path = os.path.join(rel_dir, fname)
        abs_path = os.path.join(self.out_root, rel_path)

        # G9. Said BEFORE the write rather than left to fail: Windows raises an
        # IOError that names the path and not the reason, and one of the two answers
        # is not a failure on this machine at all. If a write does then throw,
        # EXPORT_FAILED fires beside these carrying the interpreter's own message -
        # the object is reported either way, which is what the coverage change landed
        # first to guarantee.
        #
        # Neither guard shortens anything. Shortening keyed on a total path would make
        # a file name depend on where the repository is checked out, so two developers
        # would produce different names for one object and git would see renames that
        # are not renames. See too_long_for_any_machine.
        #
        # This is the object's PRIMARY file. The two-file forms check their second one
        # themselves, below - see check_path_length for why that is not a tidy-up.
        self.check_path_length(rel_path, abs_path)

        try:
            if form == FORM_ST_FULL:
                text = join_st(obj.textual_declaration.text, obj.textual_implementation.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text, parent_guid)

            elif form == FORM_ST_DECL:
                text = normalize_decl_only(obj.textual_declaration.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text, parent_guid)

            elif form == FORM_ST_IMPL:
                text = normalize_decl_only(obj.textual_implementation.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text, parent_guid)

            elif form == FORM_GVL_BOTH:
                xml_path = os.path.join(rel_dir, stem_name + ".gvl.xml")
                xml_abs = os.path.join(self.out_root, xml_path)

                # The LONGER half of this pair, by the one character between .gvl.st
                # and .gvl.xml - so this is the file a GVL overflows on first, and it
                # was the one the guard did not look at.
                self.check_path_length(xml_path, xml_abs)
                xml_text, _ = self.export_native(obj, xml_abs, False)

                # Recorded GVL_NATIVE, not NATIVE. The bytes are the same native export
                # either way; the form is the only thing that tells the importer, the
                # coverage report and the panel that this file is not applied back. It
                # said NATIVE until 2026-08-16, which made both halves importable and
                # let the later phase overwrite the earlier one - G12.
                self.record(obj, obj_type, FORM_GVL_NATIVE, xml_path, xml_text,
                            parent_guid)
                st_text = normalize_decl_only(obj.textual_declaration.text)
                write_text(abs_path, st_text)
                self.record(obj, obj_type, FORM_ST_DECL, rel_path, st_text, parent_guid)

            elif form == FORM_TASK_BOTH:
                # Two files, one object, one GUID - exactly like GVL_BOTH. The .xml keeps
                # the name it has always had, so this is additive: no committed file is
                # renamed and no mirror churns structurally on the way in.
                xml_path = os.path.join(rel_dir, stem_name + ".xml")
                xml_abs = os.path.join(self.out_root, xml_path)

                # Shorter than its .tasks.yaml sibling, so the primary check already
                # covers it. Checked anyway: "safe because the other name is longer"
                # is true of today's suffixes and of nothing else.
                self.check_path_length(xml_path, xml_abs)
                xml_text, _ = self.export_native(obj, xml_abs, True)

                # Recorded TASK_NATIVE, not NATIVE_R. The bytes are a recursive native
                # export either way, but the manifest's form is the only thing that tells
                # a reader this file is never applied back - and the import stopped
                # applying it the moment the .tasks.yaml beside it became the importable
                # half. NATIVE_R said the opposite, because a PROPERTY's NATIVE_R does
                # import.
                self.record(obj, obj_type, FORM_TASK_NATIVE, xml_path, xml_text,
                            parent_guid)

                obj_path = object_path(rel_dir, name)
                text = self.export_tasks(obj, obj_path, rel_path)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text, parent_guid)

            elif form == FORM_PROPERTY_BOTH:
                # Two files, one object, one GUID - the GVL_BOTH shape with the halves
                # swapped. The recursive .xml is the importable one and is byte for byte what
                # this object exported before the readable half existed; the .st beside it is
                # the property's header, written for a reader and never applied back.
                #
                # Recorded NATIVE_R, not PROPERTY_BOTH: the form is what tells the planner
                # this file is the importable half, and it is the same payload as always, so
                # nothing about the import changes. The accessors are walked separately below
                # and write their bodies as PROPERTY_TEXT.
                xml_path = os.path.join(rel_dir, stem_name + ".xml")
                xml_abs = os.path.join(self.out_root, xml_path)
                self.check_path_length(xml_path, xml_abs)
                xml_text, _ = self.export_native(obj, xml_abs, True)
                self.record(obj, obj_type, FORM_NATIVE_R, xml_path, xml_text, parent_guid)

                decl = normalize_decl_only(obj.textual_declaration.text)
                write_text(abs_path, decl)
                self.record(obj, obj_type, FORM_PROPERTY_TEXT, rel_path, decl, parent_guid)

            elif form == FORM_PROPERTY_TEXT:
                # An accessor's body, for reading. Its declaration is empty on every property
                # measured - CODESYS reports has_textual_declaration true with zero characters
                # - so there is nothing to join, and this is the implementation alone exactly
                # as an ACTION's is.
                text = normalize_decl_only(obj.textual_implementation.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text, parent_guid)

            elif form == FORM_LIBRARIES:
                obj_path = object_path(rel_dir, name)
                text = self.export_libraries(obj, obj_path, rel_path)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text, parent_guid)

            elif form in (FORM_NATIVE, FORM_NATIVE_R, FORM_NATIVE_WITH_CHILDREN,
                          FORM_EXTERNAL_NATIVE):
                # Only NATIVE_R exports its subtree into its own file. NATIVE_WITH_CHILDREN
                # writes just the node, because its children are walked separately below.
                # EXTERNAL_NATIVE is the same bytes as NATIVE; the form is what tells the
                # importer never to hand them back - import_native duplicates this type
                # instead of replacing it (see versio_types.FORM_EXTERNAL_NATIVE).
                text, _ = self.export_native(obj, abs_path, form == FORM_NATIVE_R)
                self.record(obj, obj_type, form, rel_path, text, parent_guid)
        except Exception as e:
            # object_path, not rel_dir + "/" + name. This warning is the ONLY record
            # that the object exists: the exception unwinds past every record() call in
            # the try above, so a failed object reaches neither `objects` nor `skipped`.
            # ExportCoverage reads this code now and prints the path in the panel, so it
            # has to be the same string the skips use or it matches nothing.
            self.warn("EXPORT_FAILED", object_path(rel_dir, name), str(e))
            return

        if form == FORM_NATIVE_WITH_CHILDREN:
            # An unmapped type that turned out to be structural. Its children are objects
            # in their own right rather than members, so they go into a subdirectory named
            # after it and record NO owner GUID - which is the only thing separating this
            # from the member recursion below, now that both write into a directory named
            # after this object. The other difference is that this node also had a payload
            # worth writing, which is why it is here and not in the FORM_CONTAINER branch.
            child_dir = os.path.join(rel_dir, stem_name)
            for child in children:
                self.handle(child, child_dir)
            return

        # Children of a leaf object - methods, actions, transitions, properties - live in a
        # DIRECTORY NAMED AFTER IT, so the mirror path is the path the IDE shows.
        #
        # THE RULE THIS REPLACED, and why one rule is worth a rename of every mirror.
        # Ownership used to be spelled three different ways in one tree: a directory for a
        # container, a `FB_Alarm.` prefix on a member's file name, and - for a folder inside
        # a POU - nothing at all, the folder erased and its members flattened up. Three
        # encodings meant three things to get right, and each had already been got wrong: the
        # flattening lost the folder (fixed 2026-08-17), and before that the prefix was the
        # only record of ownership, which a name containing a dot defeats.
        #
        # Now there is one: `Device/Plc Logic/Application/FB_Alarm/Private/_M_Reset.st` is
        # what CODESYS shows, segment for segment. The importer resolves it by splitting the
        # path, the panel draws it by reading the path, and neither needs to know what a
        # member is. `parent` stays in the manifest because it answers a question the path
        # cannot: whether `FB_Alarm/M_Reset.st` is the POU's method or an object that happens
        # to sit in a folder of the same name.
        #
        # THE COST, stated because somebody will meet it: every file in every mirror written
        # before schema 9 is at a different path, so the first export after this renames the
        # lot. Content is untouched, so git pairs them as renames and history survives; the
        # planner reads both layouts and branches on the schema number, so an un-re-exported
        # mirror keeps importing correctly until it is.
        #
        # TASK_BOTH does not recurse, for the same reason NATIVE_R does not: its recursive
        # .xml already contains every task, and its .tasks.yaml lists them again. Walking
        # them a third time wrote nothing extra - a TASK is FORM_SKIP - but it moved the
        # counters, and THAT is what caught it: walked 18 -> 20 and skipped 4 -> 6 on a
        # project that had gained no objects. descends_into_children was updated for the
        # pre-count and this guard was not, so the count and the walk disagreed, which is
        # precisely the divergence that function's docstring says cannot happen.
        if form not in (FORM_NATIVE_R, FORM_TASK_BOTH):
            own_guid = safe(lambda: str(obj.guid), "")
            child_dir = os.path.join(rel_dir, stem_name)
            for child in children:
                self.handle(child, child_dir, own_guid)


def count_objects(obj, depth=0):
    """How many objects the export walk will reach, from this node down.

    A denominator for the progress bar, and nothing else. It exists because the host
    cannot show "9 of 25" without knowing 25, and the walk itself only learns the total
    by finishing.

    DESCENT MATCHES Exporter.handle EXACTLY, and cheaply: versio_types.descends_into_children
    answers from the type GUID alone, so this second pass costs one get_children() and one
    .type per node - and crucially NOT has_textual_declaration / has_textual_implementation,
    which are the expensive properties and the ones that raise. See that function for why
    deciding descent without them is sound.

    Wrong here is cheap by construction: the count feeds a progress fraction and nothing
    reads it afterwards. An exception anywhere returns what was counted so far, because a
    progress bar is never worth failing an export over.
    """
    if depth > 40:                     # same cycle guard the probe uses
        return 0

    total = 1
    try:
        obj_type = type_of(safe(lambda: str(obj.type), ""))
        if not descends_into_children(obj_type):
            return total
        for child in safe(lambda: obj.get_children(), []) or []:
            total += count_objects(child, depth + 1)
    except Exception:
        pass
    return total


def count_files(path):
    """How many files are under a directory. Cheap, and only ever used for a log line."""
    total = 0
    for _dir_path, _dir_names, file_names in os.walk(path):
        total += len(file_names)
    return total


def rename_retrying(src, dst):
    """os.rename with a short retry on Windows' access-denied.

    Errno 13 on a rename is very often transient here: git hashing the tree for a
    status, a virus scanner, a directory enumeration - each holds a handle for
    milliseconds, and one millisecond is all a rename needs to lose. Five attempts
    over a second outlives all of those; what it does NOT outlive is a process
    PARKED in the tree, which is the caller's fallback to handle.
    """
    for attempt in range(5):
        try:
            os.rename(src, dst)
            return
        except OSError as err:
            if err.errno != errno.EACCES or attempt == 4:
                raise
            time.sleep(0.2)


def replace_contents(out_root, staging):
    """Replace the mirror's files without renaming any directory it already has.

    The degraded swap, for the day a process is parked inside the mirror: renaming
    a directory needs every handle in the tree gone, but renaming and deleting the
    FILES needs nothing of the sort - measured on the live repository that hit
    this, where every file renamed freely while two directories refused.

    Old files are removed first and new ones moved in after, per directory, so the
    mixed-content window is as small as file operations allow. Directories the old
    tree had and the new one does not are removed only if they empty out - one
    that refuses is exactly the held one, and it is left standing, empty, for the
    next full swap to take.
    """
    for base, _dirs, files in os.walk(out_root, topdown=False):
        for name in files:
            os.remove(os.path.join(base, name))

    for base, _dirs, files in os.walk(staging):
        rel = os.path.relpath(base, staging)
        target = out_root if rel == "." else os.path.join(out_root, rel)
        if not os.path.isdir(target):
            os.makedirs(target)
        for name in files:
            os.rename(os.path.join(base, name), os.path.join(target, name))

    for base, _dirs, files in os.walk(out_root, topdown=False):
        if base != out_root and not os.listdir(base):
            try:
                os.rmdir(base)
            except OSError:
                pass

    shutil.rmtree(staging)


def coverage_markdown(project, records, skips, warnings):
    """Render the coverage report.

    Written beside the manifest and from the same lists, so the two cannot
    disagree: the manifest is what the application reads, this is what a person
    reads in a pull request. Deriving the report from a second walk of the project
    would eventually produce a file that contradicted the data it sits next to.

    DETERMINISTIC ON PURPOSE - sorted throughout, and no timestamp anywhere. The
    export is byte-identical across runs and this file is part of it; a clock in
    here would put a spurious diff in every single commit, which is exactly the
    noise the Timestamp scrub exists to remove.
    """
    by_reason = {}
    for s in skips:
        by_reason.setdefault(s["reason"], []).append(s)

    gaps = by_reason.get(SKIP_GAP, [])

    lines = []
    lines.append(u"# Export coverage")
    lines.append(u"")
    lines.append(u"Generated by Versio. Do not edit - it is rewritten on every export.")
    lines.append(u"")
    lines.append(u"| | |")
    lines.append(u"|---|---|")
    lines.append(u"| Project | `%s` |" % project)
    lines.append(u"| Files written | %d |" % len(records))
    lines.append(u"| Objects not exported | %d |" % len(skips))
    lines.append(u"| **Of those, real gaps** | **%d** |" % len(gaps))
    lines.append(u"| Warnings | %d |" % len(warnings))
    lines.append(u"")

    if gaps:
        lines.append(u"## Not exported, and that is a gap")
        lines.append(u"")
        lines.append(u"**Changes to these objects do not appear in the mirror.** They do not move")
        lines.append(u"the change gate, they do not reach a diff, and they will not be in a commit.")
        lines.append(u"")
        for s in sorted(gaps, key=lambda x: (x["path"], x["type"])):
            lines.append(u"- `%s` — %s" % (s["path"], s["type"]))
        lines.append(u"")

    # Exported, but one way. A file that is written for review and silently ignored
    # on the way back is its own kind of gap: editing it looks exactly like editing a
    # POU, and nothing would tell the engineer their change went nowhere. Stated here
    # for the same reason the skips are.
    #
    # The remedy differs per file, and saying the wrong one is its own small defect. A
    # library file has no importable form anywhere, so the answer really is "in the IDE".
    # A task configuration has its importable half sitting right beside it, and sending
    # somebody to the IDE would walk them past the file they were meant to edit.
    #
    # The sibling is found by GUID rather than by rewriting the suffix, because the GUID is
    # the identity everywhere else in this manifest and a suffix rule would have to be kept
    # in step with SUFFIX by hand. Any future one object, two files form gets this for free.
    one_way = [r for r in records if r.get("form") in EXPORT_ONLY_FORMS]
    if one_way:
        # Empty GUIDs are excluded rather than indexed. record() falls back to "" when
        # obj.guid raises, so indexing on it would file every unreadable object under one
        # key and hand a one-way file the path of an unrelated object as its remedy -
        # a confidently wrong instruction, which is worse than the generic one.
        importable = {}
        for r in records:
            if r.get("form") not in EXPORT_ONLY_FORMS and r.get("guid"):
                importable.setdefault(r["guid"], r.get("path"))

        lines.append(u"## Exported for review, not applied back")
        lines.append(u"")
        lines.append(u"**These files are written by the export and ignored by the import.**")
        lines.append(u"They show what the project asks for, so a change reaches the diff and")
        lines.append(u"the commit - but editing one here does not change the project.")
        lines.append(u"")
        for r in sorted(one_way, key=lambda x: x["path"]):
            sibling = importable.get(r["guid"]) if r.get("guid") else None
            if sibling:
                lines.append(u"- `%s` — %s · edit `%s` instead"
                             % (r["path"], r["type"], sibling))
            else:
                lines.append(u"- `%s` — %s · change it in the IDE"
                             % (r["path"], r["type"]))
        lines.append(u"")

    other = sorted(k for k in by_reason if k != SKIP_GAP)
    if other:
        lines.append(u"## Not exported, deliberately")
        lines.append(u"")
        for reason in other:
            lines.append(u"### %s" % reason)
            lines.append(u"")
            lines.append(SKIP_EXPLANATION.get(reason, u"No explanation recorded."))
            lines.append(u"")
            for s in sorted(by_reason[reason], key=lambda x: (x["path"], x["type"])):
                lines.append(u"- `%s` — %s" % (s["path"], s["type"]))
            lines.append(u"")

    if warnings:
        lines.append(u"## Warnings")
        lines.append(u"")
        for w in sorted(warnings, key=lambda x: (x.get("code", ""), x.get("path", ""))):
            lines.append(u"- `%s` %s%s" % (
                w.get("code", ""), w.get("path", ""),
                (u" — " + w["detail"]) if w.get("detail") else u""))
        lines.append(u"")

    lines.append(u"## What was written")
    lines.append(u"")
    forms = {}
    for r in records:
        forms[r["form"]] = forms.get(r["form"], 0) + 1
    for form in sorted(forms):
        lines.append(u"- %s — %d file(s)" % (form, forms[form]))
    lines.append(u"")

    return u"\n".join(lines)


def main():
    """Read the job file, export the project, swap the result into place.

    The single argument is a path to job.json rather than a set of flags: Windows
    --scriptargs quoting is genuinely awful, so exactly one path crosses the
    boundary and everything else travels as JSON inside the file.

    The job file must be UTF-8 WITHOUT a BOM - IronPython 2.7's json.load fails on
    one with "No JSON object could be decoded".
    """
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
    trash = out_root + ".versio_trash"
    if os.path.isdir(staging):
        # Left behind by an export that was killed. Said out loud: it is evidence that a
        # previous run did not finish, and finding out silently is finding out never.
        emit({"t": "staging-cleared", "path": staging, "files": count_files(staging)})
        shutil.rmtree(staging)
    if os.path.isdir(trash):
        # The other half of the same story, and a more interesting one: a trash directory
        # survives only if a run died in the three-rename swap below, which is the window
        # this design exists to make harmless. If the mirror is missing, THIS is where it is.
        emit({"t": "trash-cleared", "path": trash, "files": count_files(trash)})
        shutil.rmtree(trash)
    os.makedirs(staging)

    emit({"t": "start", "python": sys.version, "project": job["project"], "out": out_root})

    proj = remember_project(scriptengine.projects.open(job["project"]))
    emit({"t": "opened", "path": str(proj.path)})

    # Counted before the walk so progress has a denominator. One extra pass over the tree
    # reading only .type and get_children(), which is noise beside a 17-31 s cold start -
    # and the alternative is a bar that cannot move for the whole of an export.
    roots = list(proj.get_children())
    expected = sum(count_objects(child) for child in roots)
    emit({"t": "counted", "objects": expected})

    # PREVIOUS is the live mirror, not staging - and it is read here because this is the
    # only moment both exist. main() has just created staging empty and the three-rename
    # swap has not happened yet, so out_root still holds what the last export wrote.
    ex = Exporter(staging, expected,
                  previous=out_root if os.path.isdir(out_root) else None)
    for child in roots:
        ex.handle(child, "")

    manifest = {
        # 2: adds "skipped". A schema-1 manifest is not a manifest with nothing
        # skipped - it is one written before anybody counted, and the reader has to
        # be able to tell those apart or an old export reads as perfect coverage.
        #
        # 3: library references are exported (form LIBRARIES) instead of being
        # skipped as NOT_IMPLEMENTED. Same ambiguity, one level along: on a schema-2
        # manifest "no LIBRARIES records" means nobody was writing them yet, and on a
        # schema-3 one it means the project genuinely has no library manager. Reading
        # the first as the second would report full coverage over the exact hole this
        # work was done to close.
        #
        # 4: a member records its owner's GUID in "parent". Before this the manifest
        # said where a method's FILE sat and not what the method belonged to, so the
        # panel drew clsLight, Light, SetLifeTime and four InitRooms as siblings in
        # one folder, where CODESYS draws each method under the function block that
        # owns it. A schema-3 manifest carries no parents at all, which is why the
        # reader falls back to the flat shape rather than treating "no parents" as
        # "nothing is a member".
        #
        # 5: a native record carries "nhash", the hash of its NORMALISED payload,
        # beside the raw one. Same ambiguity as every bump above, and the reason
        # the field cannot simply be read optimistically: on a schema-4 manifest a
        # native object with no nhash means nobody was writing them yet, and its
        # deletion has to stay refused. Reading "absent" as "unchanged" would
        # delete work on every mirror exported before today.
        # 7: a full ST record carries "shash", a bounded fingerprint of its lines,
        # so a rename that edited the body can be recognised on a mirror that was
        # never committed - where git has no old copy to compare against.
        #
        # 6: a full ST record carries "ihash", the hash of its implementation alone.
        # A schema-5 manifest has none because nothing wrote one, and a renamed POU
        # is simply not recognised as renamed on such a mirror - it falls back to a
        # delete and a create, which is what happened before this existed.
        # 8: the manifest names the vendor that wrote it. Every manifest before this one
        # was written by CODESYS because CODESYS was all there was, so an absent "vendor"
        # reads as Codesys rather than as unknown - the same backward-compatible shape
        # ProjectBinding.Vendor already uses, and for the same reason: a mirror exported by
        # an older Versio must keep meaning what it meant.
        #
        # Worth recording rather than inferring. The type strings in this manifest are
        # CODESYS's own tree vocabulary - PLC_LOGIC, CALL_TO_POU, GVL_PERSISTENT - and a
        # reader that has to guess whose vocabulary it is holding cannot translate them
        # for display without guessing at the same time.
        # 9: THE PATH IS THE PROJECT PATH. A member lives in a directory named after its
        # owner - Application/FB_Alarm/M_Reset.st - where before it sat beside it as
        # Application/FB_Alarm.M_Reset.st, and a folder inside a POU is a directory rather
        # than being erased. This is the one bump that is not additive: the same object is
        # at a different path than it was, so a reader must know which layout it is holding
        # before it can resolve anything. On schema 8 and below the owner chain has to be
        # re-applied on top of the directories to reach the object; on 9 doing that would
        # name FB_Alarm twice. CodesysImportPlanner branches on exactly this number, which
        # is why an un-re-exported mirror keeps importing correctly.
        # 10: the toolchain version that produced this mirror. Additive - a schema-9
        # manifest simply has no toolchainVersion, and that reads as "nobody recorded it"
        # rather than as "unknown version", which is the same ambiguity `vendor` was bumped
        # for at 8. Without the bump a reader cannot tell an old mirror from one exported by
        # a CODESYS that somehow reported nothing.
        "schema": 10,
        "vendor": VENDOR,

        # WHAT sys.version RETURNS INSIDE CODESYS, verbatim and unparsed:
        #
        #   CODESYS.exe CODESYS V3.5 SP22 Patch 2, ScriptEngine.plugin 4.2.0.0
        #
        # Not the CPython version string it is in any other interpreter - IronPython hosted
        # by CODESYS answers with the host and the ScriptEngine plug-in. It has been in the
        # `start` event since the first export and reached the log and nowhere else, which is
        # why the project card could not say which CODESYS a mirror came from.
        #
        # VERBATIM, and that is the decision worth stating. Parsing "3.5.22.20" out of it
        # would produce a tidier field and throw away the plug-in version, the patch wording
        # and whatever a future release calls itself - and the one question this answers is
        # "which CODESYS wrote this", which the whole string answers and a parsed subset only
        # answers until the wording changes. The panel can shorten it; the manifest records
        # what was said.
        #
        # It is the toolchain that PERFORMED THE EXPORT, not necessarily the one that last
        # saved the project. Those coincide whenever Versio did the export, which is every
        # mirror it writes; a project last saved elsewhere and exported here would report
        # this machine's CODESYS, and the field's name says `exported` for that reason.
        "toolchainVersion": safe(lambda: sys.version, ""),
        "bindingId": job.get("bindingId", ""),
        "project": os.path.basename(job["project"]),
        "objectCount": len(ex.records),
        "objects": sorted(ex.records, key=lambda r: r["path"]),

        # What was NOT written, with the reason. Previously this was a count in a
        # log line nobody kept, so an object type that silently stopped being
        # exported would have looked exactly like a project that no longer had one.
        "skipped": sorted(ex.skips, key=lambda r: (r["path"], r["type"])),
        "warnings": ex.warnings,
    }
    # Serialised ONCE. This used to call json.dumps three times in one expression - twice to
    # decide whether the result was bytes, and again to produce it - on a document that holds
    # every object in the project.
    #
    # sort_keys and the fixed indent are load-bearing, not cosmetic: the export is
    # byte-identical across runs and this file is part of it, and dict order differs between
    # interpreters.
    serialized = json.dumps(manifest, indent=2, sort_keys=True)

    # IronPython 2.7 hands back a byte string here and CPython 3 hands back text. write_text
    # wants text on both, and the payload is pure ASCII because json escapes everything else.
    if isinstance(serialized, bytes):
        serialized = serialized.decode("utf-8")

    mpath = os.path.join(staging, "_manifest.json")
    write_text(mpath, serialized)

    write_text(os.path.join(staging, "_coverage.md"),
               coverage_markdown(manifest["project"], ex.records, ex.skips, ex.warnings))

    # Swap staging into place only once the export completed.
    #
    # This is the most destructive thing an export does - it deletes the whole export
    # directory and puts a new one in its place - and it used to happen with nothing
    # written down at all. If it half-failed, the log said the export succeeded and the
    # directory disagreed, with no line anywhere to connect the two.
    replaced = count_files(out_root) if os.path.isdir(out_root) else 0
    emit({"t": "swap", "out": out_root, "replacing": replaced,
          "with": count_files(staging)})

    # THREE RENAMES, AND NO MOMENT WITH NO MIRROR.
    #
    # This used to be rmtree(out_root) followed by rename(staging, out_root), and a kill,
    # a crash or a power cut between those two statements left the project with NO export
    # at all - not a stale one, not a partial one, none. rmtree is not instantaneous
    # either: the fixture's visualisation tree alone is 2.5 MB across 33,644 lines, so the
    # window is real rather than theoretical.
    #
    # Renaming is atomic on NTFS, so at every instant between these three statements the
    # mirror is either the old tree or the new one. What a crash can leave behind now is a
    # .versio_trash directory holding the previous export - recoverable by hand, cleared
    # and announced by the next run - instead of nothing.
    #
    # AND A FALLBACK, because on Windows a directory rename is refused (errno 13) while
    # ANY process holds a handle on any directory inside the tree - and a process's
    # CURRENT WORKING DIRECTORY is such a handle. Measured on a live repository: an IDE
    # whose cwd had landed inside the mirror made every export fail at this exact line,
    # twice, while every FILE in the tree renamed freely. Somebody browsing their own
    # export in a file dialog must not be able to break the export - so when the rename
    # is refused after retries, the mirror is replaced CONTENT-first instead: files
    # removed and moved one by one, directories left standing. The trade is stated: the
    # atomic either-old-or-new guarantee becomes a brief mixed-content window, which is
    # still strictly better than the alternative on offer, no export at all.
    renamed = True
    if os.path.isdir(out_root):
        try:
            rename_retrying(out_root, trash)
        except OSError as err:
            if err.errno != errno.EACCES:
                raise
            renamed = False
            emit({"t": "swap-fallback", "out": out_root, "error": str(err)})
            replace_contents(out_root, staging)

    if renamed:
        # Retried as well: with the old mirror already in the trash, a refusal HERE is
        # the no-mirror window the three renames exist to prevent, so this one is worth
        # being stubborn about.
        rename_retrying(staging, out_root)

    emit({"t": "swapped", "out": out_root, "files": count_files(out_root)})

    # Deleted only once the new tree is in place. Doing it before the second rename would
    # put the window straight back where it was.
    if os.path.isdir(trash):
        shutil.rmtree(trash)

    close_project(proj)
    gaps = len([s for s in ex.skips if s["reason"] == SKIP_GAP])
    emit({"t": "summary", "walked": ex.total, "written": len(ex.records),
          "skipped": ex.skipped, "gaps": gaps, "warnings": len(ex.warnings),
          "preserved": ex.preserved})
    emit({"t": "done"})
    return 0


try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})
    rc = 1
finally:
    # A no-op after a clean run, which already closed it. On a failure it is what stops a
    # <project>.~u marker outliving the process that wrote it.
    close_project()

emit({"t": "exit", "rc": rc})
