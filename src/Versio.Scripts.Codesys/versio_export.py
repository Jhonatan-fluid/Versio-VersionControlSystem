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

import io
import json
import os
import shutil
import sys
import traceback

import scriptengine  # type: ignore

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from versio_text import (content_hash, join_st, normalize_decl_only,  # noqa: E402
                         safe_file_stem, scrub_native_xml)
from versio_types import (FORM_CONTAINER, FORM_GVL_BOTH, FORM_NATIVE,  # noqa: E402
                          FORM_NATIVE_R, FORM_SKIP, FORM_ST_DECL,
                          FORM_ST_FULL, FORM_ST_IMPL, SKIP_EXPLANATION,
                          SKIP_GAP, decide_form, file_name_for, skip_reason,
                          type_of)


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

    def __init__(self, out_root):
        self.out_root = out_root
        self.records = []
        self.warnings = []
        self.skips = []
        self.total = 0
        self.skipped = 0

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

    def record(self, obj, obj_type, form, rel_path, text):
        """Note one written file in the manifest, keyed by the object's GUID.

        The GUID is the identity, not the path: it is what lets a moved object be
        recognised as a move rather than a delete plus an add, and it survives a
        rename that the filename cannot.
        """
        h = content_hash(text)
        self.records.append({
            "guid": safe(lambda: str(obj.guid), ""),
            "name": safe(lambda: obj.get_name(), ""),
            "type": obj_type,
            "form": form,
            "path": rel_path.replace("\\", "/"),
            "hash": h,
            "bytes": len(text.encode("utf-8")),
        })
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
        write_text(abs_path, scrubbed)
        return scrubbed, n

    def handle(self, obj, rel_dir, parent_name):
        """Export one object and recurse into its children.

        `rel_dir` is the folder mirroring this object's place in the CODESYS tree.
        `parent_name` is set only for sub-POU members - methods, actions,
        transitions, properties - whose filenames are prefixed with their owner so
        a flat folder can still reconstruct ownership on import.

        Unmapped type GUIDs are exported natively and warned about, never dropped:
        a newer CODESYS adding a type must not silently lose objects.
        """
        name = safe(lambda: obj.get_name(), None)
        if name is None:
            self.warn("UNNAMED_OBJECT", rel_dir)
            return
        type_guid = safe(lambda: str(obj.type), "")
        obj_type = type_of(type_guid)
        has_impl = bool(safe(lambda: obj.has_textual_implementation, False))
        has_decl = bool(safe(lambda: obj.has_textual_declaration, False))

        if obj_type == "UNKNOWN":
            self.warn("UNKNOWN_TYPE_GUID", rel_dir + "/" + name, type_guid)

        form = decide_form(obj_type, has_impl, has_decl)
        self.total += 1

        if form == FORM_SKIP:
            # Recorded, not merely counted. A count says "8 objects were not
            # exported" and leaves the reader to guess whether that was a decision
            # or a hole; the reason is the whole information.
            reason = skip_reason(obj_type)
            self.skipped += 1
            self.skips.append({
                "guid": safe(lambda: str(obj.guid), ""),
                "name": name,
                "type": obj_type,
                "path": (rel_dir + "/" + name).replace("\\", "/").lstrip("/"),
                "reason": reason,
            })
            emit({"t": "skip", "path": rel_dir + "/" + name,
                  "type": obj_type, "reason": reason})
            return

        if form == FORM_CONTAINER:
            child_dir = os.path.join(rel_dir, safe_file_stem(name))
            for child in safe(lambda: obj.get_children(), []) or []:
                self.handle(child, child_dir, None)
            return

        stem_name = safe_file_stem(name)
        parent_stem = safe_file_stem(parent_name) if parent_name else None
        fname = file_name_for(stem_name, obj_type, form, parent_stem)
        rel_path = os.path.join(rel_dir, fname)
        abs_path = os.path.join(self.out_root, rel_path)

        try:
            if form == FORM_ST_FULL:
                text = join_st(obj.textual_declaration.text, obj.textual_implementation.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text)

            elif form == FORM_ST_DECL:
                text = normalize_decl_only(obj.textual_declaration.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text)

            elif form == FORM_ST_IMPL:
                text = normalize_decl_only(obj.textual_implementation.text)
                write_text(abs_path, text)
                self.record(obj, obj_type, form, rel_path, text)

            elif form == FORM_GVL_BOTH:
                xml_path = os.path.join(rel_dir, stem_name + ".gvl.xml")
                xml_text, _ = self.export_native(obj, os.path.join(self.out_root, xml_path), False)
                self.record(obj, obj_type, FORM_NATIVE, xml_path, xml_text)
                st_text = normalize_decl_only(obj.textual_declaration.text)
                write_text(abs_path, st_text)
                self.record(obj, obj_type, FORM_ST_DECL, rel_path, st_text)

            elif form in (FORM_NATIVE, FORM_NATIVE_R):
                text, _ = self.export_native(obj, abs_path, form == FORM_NATIVE_R)
                self.record(obj, obj_type, form, rel_path, text)
        except Exception as e:
            self.warn("EXPORT_FAILED", rel_dir + "/" + name, str(e))
            return

        # Children of a leaf object (methods, actions, properties on a POU) live
        # alongside it in the same folder, prefixed with the parent name.
        if form != FORM_NATIVE_R:
            for child in safe(lambda: obj.get_children(), []) or []:
                self.handle(child, rel_dir, name)


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
    if os.path.isdir(staging):
        shutil.rmtree(staging)
    os.makedirs(staging)

    emit({"t": "start", "python": sys.version, "project": job["project"], "out": out_root})

    proj = scriptengine.projects.open(job["project"])
    emit({"t": "opened", "path": str(proj.path)})

    ex = Exporter(staging)
    for child in proj.get_children():
        ex.handle(child, "", None)

    manifest = {
        # 2: adds "skipped". A schema-1 manifest is not a manifest with nothing
        # skipped - it is one written before anybody counted, and the reader has to
        # be able to tell those apart or an old export reads as perfect coverage.
        "schema": 2,
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
    mpath = os.path.join(staging, "_manifest.json")
    write_text(mpath, json.dumps(manifest, indent=2, sort_keys=True).decode("utf-8")
               if isinstance(json.dumps(manifest), bytes) else
               unicode(json.dumps(manifest, indent=2, sort_keys=True)))  # noqa: F821

    write_text(os.path.join(staging, "_coverage.md"),
               coverage_markdown(manifest["project"], ex.records, ex.skips, ex.warnings))

    # Swap staging into place only once the export completed.
    if os.path.isdir(out_root):
        shutil.rmtree(out_root)
    os.rename(staging, out_root)

    proj.close()
    gaps = len([s for s in ex.skips if s["reason"] == SKIP_GAP])
    emit({"t": "summary", "walked": ex.total, "written": len(ex.records),
          "skipped": ex.skipped, "gaps": gaps, "warnings": len(ex.warnings)})
    emit({"t": "done"})
    return 0


try:
    rc = main()
except Exception as e:
    emit({"t": "fatal", "msg": str(e), "trace": traceback.format_exc()})
    rc = 1

emit({"t": "exit", "rc": rc})
