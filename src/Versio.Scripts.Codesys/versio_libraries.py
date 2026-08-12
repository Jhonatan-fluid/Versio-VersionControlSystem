# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS.
#
# Reads a CODESYS library manager and renders its references as YAML.
#
# WHERE THIS SITS IN THE VENDOR SEAM: the FORMAT is vendor-neutral - every PLC
# toolchain has some notion of a dependency on a versioned library, and the C# side
# (Versio.Core.Projects.LibraryCatalog) reads this file without knowing CODESYS
# exists. What is vendor-specific is exactly this file: how those references are
# obtained from one toolchain's scripting API. A second vendor writes its own reader
# and emits the same document.
#
# This module imports no scriptengine. It is handed objects and a `safe` caller, so
# the shaping rules can be exercised outside CODESYS.
#
# ---------------------------------------------------------------------------------
# MEASURED on CODESYS 3.5.22.20 (SP22 Patch 2), RefrigeratorControl.project, because
# the API this needs is not the one the obvious name suggests:
#
#   libman.get_libraries()   -> a list of STRINGS ("#Standard", "#Util"). Names only:
#                               no version, no company, no flags. Not enough.
#   libman.references        -> ScriptLibraryReference objects. This is the one.
#
#   The reference collection is INDEXED BY ID, NOT BY POSITION. refs[0] raises on a
#   project whose first reference has a non-zero id, which is every project. It is
#   iterated instead - the collection subclasses list, so iteration is positional.
#
#   ref.id IS RE-MINTED ON EVERY OPEN. Two consecutive runs over an untouched project
#   differed in 68 lines and every single one of them was an id; nothing else moved.
#   It is therefore NOT recorded, for the same reason the project-level
#   __VisualizationStyle's guid is not: a value that changes when you look at it is
#   not an identity, and recording one puts a spurious diff in every commit.
#
#   ref.hide_when_referenced_as_dependency raises on all 34 references here. Probed,
#   not assumed; it is simply not readable on this version, so it is not recorded.
# ---------------------------------------------------------------------------------

# Bump when the document's SHAPE changes, not when a value does. The C# reader keys
# its "can I trust this" decision off it, exactly as the manifest's schema does: an
# empty reference list means two different things - a project with no libraries, or a
# file written before anybody recorded them.
LIBRARY_SCHEMA = 1

# Key order for the document header and for each reference. This is the CONTRACT with
# the reader and with every diff a human looks at, which is why it is a list rather
# than dict order: dict order differs between IronPython 2.7 and CPython 3, and the
# export has to be byte-identical across runs.
TOP_KEYS = ["schema", "vendor", "manager", "path", "referenceCount"]

REF_KEYS = [
    "name",              # as CODESYS shows it: "#Standard", or "Name, Version (Company)"
    "namespace",         # what the IEC code actually types
    "placeholder",       # placeholder name without the leading '#', when it is one
    "defaultResolution",
    "redirection",       # placeholder pinned to a fixed version IN THE PROJECT
    "version",           # managed references only
    "company",
    "title",
    "systemLibrary",     # added implicitly by a plug-in rather than by a person
    "optional",
    "qualifiedOnly",
    "publishSymbols",
    "parameters",
]

VENDOR = "codesys"

# What this file deliberately does NOT carry, and why. Written into the file itself:
# an engineer reading a library list has to be able to tell "not recorded" from
# "not set", and the gap that started this whole piece of work was a comment claiming
# a file that nothing wrote.
_HEADER = u"""Library references, exported by Versio. Do not edit - it is rewritten on
every export, and importing this file back into the project is NOT supported: change
libraries in the IDE (diff-only, plan section 3.10).

NOT recorded here, on purpose:
  * the reference id - CODESYS re-mints it on every open, so recording it would put a
    spurious diff in every commit
  * how each reference RESOLVED on the machine that exported (effective resolution,
    resolution info, resolver) - that describes which libraries are installed here,
    not what the project asks for, so it would differ between two engineers who share
    this repository and neither would be wrong"""


def extract_reference(ref, safe):
    """Shape one ScriptLibraryReference into the neutral record.

    `safe` is the caller's exception-swallowing probe. The ScriptEngine raises for
    properties that do not apply to a given kind of reference - a managed library has
    no placeholder name, a placeholder has no managed library - so probing is normal
    control flow here rather than error handling.

    Absent fields are left OUT of the dict rather than set to None, so the emitter
    omits them entirely: a placeholder reference with an empty `version:` line invites
    the reader to think the version was blanked rather than never applicable.
    """
    rec = {}

    def put(key, value):
        if value is not None and value != "":
            rec[key] = value

    put("name", safe(lambda: _text(ref.name)))
    put("namespace", safe(lambda: _text(ref.namespace)))

    is_placeholder = bool(safe(lambda: ref.is_placeholder, False))
    if is_placeholder:
        put("placeholder", safe(lambda: _text(ref.placeholder_name)))
        put("defaultResolution", safe(lambda: _text(ref.default_resolution)))

        # A redirection is a version the PROJECT pins the placeholder to, so it is
        # project state and belongs in the mirror - unlike the effective resolution,
        # which is whatever this machine happened to have installed.
        if bool(safe(lambda: ref.is_redirected, False)):
            put("redirection", safe(lambda: _text(ref.get_redirection())))

    # Managed (fixed-version) references. NOT EXERCISED BY THE FIXTURE - all 34 of its
    # references are placeholders - so this path is written from the API's own
    # description and is unproven against a real project. Recorded as a limitation
    # rather than presented as tested.
    managed = safe(lambda: ref.managed_library)
    if managed is not None:
        put("version", safe(lambda: _text(managed.version)))
        put("company", safe(lambda: _text(managed.company)))
        put("title", safe(lambda: _text(managed.title)))

    rec["systemLibrary"] = bool(safe(lambda: ref.system_library, False))
    rec["optional"] = bool(safe(lambda: ref.optional, False))
    rec["qualifiedOnly"] = bool(safe(lambda: ref.qualified_only, False))
    rec["publishSymbols"] = bool(safe(lambda: ref.publish_symbols_in_container, False))

    params = read_parameters(ref, safe)
    if params:
        rec["parameters"] = params

    return rec


def read_parameters(ref, safe):
    """Library parameters, as a name -> IEC expression map.

    Real project state and worth diffing: the fixture pins Util's IBLOCKSIZE to 22800,
    and a change to that number changes what the machine does.
    """
    params = safe(lambda: ref.parameters)
    if params is None:
        return {}

    out = {}
    for name in safe(lambda: list(params), []) or []:
        key = _text(name)
        if key is None:
            continue
        value = safe(lambda: params[name])
        out[key] = _text(value) if value is not None else u""
    return out


def read_references(libman, safe, on_error=None):
    """Every reference in one library manager, in the manager's own order.

    NOT SORTED, deliberately. The order is the project's, it was measured stable
    across runs, and re-ordering the file would hide a reorder somebody made in the
    IDE - a change the mirror is supposed to show.

    The collection is iterated rather than indexed: `references[i]` looks up by ID and
    raises for a position, which is how the first attempt at this read zero references
    out of a manager that has 21.
    """
    refs = safe(lambda: libman.references)
    if refs is None:
        if on_error is not None:
            on_error("LIBRARY_REFERENCES_UNAVAILABLE", None)
        return []

    out = []
    try:
        for ref in refs:
            out.append(extract_reference(ref, safe))
    except Exception as e:
        # Report a partial read rather than pretending the shorter list is the truth.
        # A library list that silently loses its tail is worse than one that says it
        # is incomplete: the diff would read as "somebody removed six libraries".
        if on_error is not None:
            on_error("LIBRARY_REFERENCES_PARTIAL", str(e))
    return out


def catalog_document(yaml, manager_name, manager_path, references):
    """Render the whole `.libraries.yaml` document.

    `yaml` is passed in rather than imported so this module stays importable on its
    own; the exporter wires the two together.
    """
    top = {
        "schema": LIBRARY_SCHEMA,
        "vendor": VENDOR,
        "manager": manager_name,
        "path": manager_path.replace("\\", "/"),
        "referenceCount": len(references),
    }
    return yaml.document(_HEADER, top, TOP_KEYS, "references", references, REF_KEYS)


def _text(value):
    """Coerce a .NET value to text, or None when there is nothing there."""
    if value is None:
        return None
    try:
        text = u"%s" % (value,)
    except Exception:
        return None
    return text
