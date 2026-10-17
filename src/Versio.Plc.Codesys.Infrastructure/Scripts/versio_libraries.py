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
#   ref.hide_when_referenced_as_depencency is the READABLE spelling - one 'd' short,
#   and the typo is CODESYS's own. This file used to say the correctly spelled property
#   "raises on all 34 references here... it is simply not readable on this version",
#   which was the wrong conclusion from a true observation: it raises because under that
#   name it does not exist. Reflection over a reference lists the misspelling, and it
#   reads False. Still NOT recorded - it is a display hint rather than something a diff
#   needs - but recorded as a CHOICE now, because "we could not read it" invites the
#   next person to retry and "we chose not to" does not.
#
#   managed_library.version / .company / .title belong in this list too, and that is
#   the whole of G8: they describe the library as INSTALLED on the exporting machine,
#   and for a library it does not have they raise while is_managed still reads True.
#   See parse_managed_name.
# ---------------------------------------------------------------------------------

# Bump when the document's SHAPE changes, not when a value does. The C# reader keys
# its "can I trust this" decision off it, exactly as the manifest's schema does: an
# empty reference list means two different things - a project with no libraries, or a
# file written before anybody recorded them.
# The one import. versio_yaml is a pure sibling - no scriptengine, no IO - and is
# embedded and path-mounted beside this file everywhere it runs.
from versio_yaml import text_of as _text

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
    "version",           # managed references only, PARSED FROM `name` - see
    "company",           # parse_managed_name for why not from managed_library
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


def parse_managed_name(name):
    """Split a managed reference's own name into title, version and company.

    Returns a dict, or None when the name is not that shape.

    MEASURED TWICE ON REAL CODESYS 3.5.22.20, and the second run is the one that decided
    this function exists. A managed reference carries version, company and title only
    through `ref.managed_library`, and the fields on that object throw
    NullReferenceException for a library this machine does not have INSTALLED:

        add_library("Standard, 3.5.17.0 (System)")   not installed here
            is_managed              -> True
            managed_library.version -> SystemError: Object reference not set
            .company, .title, .default_namespace -> the same

        add_library("Standard, 3.5.22.0 (System)")   installed here
            managed_library.version -> "3.5.22.0"
            .company -> "System"    .title -> "Standard"

    So managed_library reports INSTALLATION state, which is the one thing this file's
    header says it refuses to record: two engineers exporting the same project would
    produce different files and neither would be wrong, and the mirror would churn on
    every export by whoever has the library. That is G9's machine-dependent warning
    arriving through a third door, and the first two arrived because nobody asked where
    a value came from.

    The reference's own `name` is project state - it is what the project asks for,
    installed or not - and it carries the same three values: measured byte-identical to
    managed_library.displayname, "Standard, 3.5.22.0 (System)".

    THE PARSE IS DELIBERATELY STRICT. It splits on the LAST " (" and the LAST ", ",
    requires the closing bracket, and requires the version to be digits and dots and to
    contain at least one digit. A title carrying a comma or a bracket therefore yields
    None - no version at all - rather than a plausible wrong one. That asymmetry is the
    point: an absent version is a gap somebody can see in the file, and a wrong version
    is a diff somebody acts on.
    """
    if not name or not name.endswith(u")"):
        return None

    head, bracket, company = name[:-1].rpartition(u" (")
    if not bracket or not company or not head:
        return None

    title, comma, version = head.rpartition(u", ")
    if not comma or not title or not version:
        return None

    # Explicit digit set rather than isdigit(): on a unicode string isdigit() is true for
    # Arabic-Indic and other numerals, and a version made of those would parse here and
    # then not match anything a person typed.
    if [c for c in version if c not in u"0123456789."]:
        return None
    if not [c for c in version if c in u"0123456789"]:
        return None

    return {"title": title, "version": version, "company": company}


def extract_reference(ref, safe, on_error=None, stored_parameters=None):
    """Shape one ScriptLibraryReference into the neutral record.

    `safe` is the caller's exception-swallowing probe. The ScriptEngine raises for
    properties that do not apply to a given kind of reference - a managed library has
    no placeholder name, a placeholder has no managed library - so probing is normal
    control flow here rather than error handling.

    `on_error` is the same channel read_references uses, and it is here because a
    reference this function cannot classify used to be recorded as though it were an
    ordinary one, minus the fields it could not read. Silence is the wrong answer to
    "I do not know what this is": a managed reference written without its version looks
    exactly like a placeholder that never had one.

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

    # is_placeholder and is_managed are the API's OWN discriminators and both are
    # readable on both types, which is why the branch turns on them rather than on
    # whether some other property happened to raise. It used to enter the managed half
    # whenever `ref.managed_library` probed to something non-None - a test of a spelling,
    # not of a kind, and one that answers "not managed" for a managed reference whose
    # library is not installed.
    is_placeholder = bool(safe(lambda: ref.is_placeholder, False))
    is_managed = bool(safe(lambda: ref.is_managed, False))

    if is_placeholder:
        put("placeholder", safe(lambda: _text(ref.placeholder_name)))
        put("defaultResolution", safe(lambda: _text(ref.default_resolution)))

        # A redirection is a version the PROJECT pins the placeholder to, so it is
        # project state and belongs in the mirror - unlike the effective resolution,
        # which is whatever this machine happened to have installed.
        if bool(safe(lambda: ref.is_redirected, False)):
            put("redirection", safe(lambda: _text(ref.get_redirection())))

    elif is_managed:
        # Fixed-version reference. Parsed from `name`, NOT read off managed_library:
        # see parse_managed_name, where the measurement is. The name is the only
        # description of this reference that is the same on every machine.
        parsed = parse_managed_name(rec.get("name"))
        if parsed is None:
            # Not a guess and not a silence. The reference is still recorded - its name
            # and flags are real project state and worth diffing - but the three fields
            # that describe WHICH library it is are left out and said out loud, because
            # a managed reference silently missing its version is indistinguishable in
            # the file from a placeholder that has none.
            if on_error is not None:
                on_error("LIBRARY_MANAGED_NAME_UNPARSED", rec.get("name"))
        else:
            put("version", parsed["version"])
            put("company", parsed["company"])
            put("title", parsed["title"])

    elif on_error is not None:
        # Neither kind. Nothing in this CODESYS version produces one, and that is
        # exactly why it is reported: the two discriminators above are what the API
        # offers, so a reference answering False to both is a shape nobody has seen and
        # the record for it carries no version, no placeholder and no explanation.
        on_error("LIBRARY_REFERENCE_UNCLASSIFIED", rec.get("name"))

    rec["systemLibrary"] = bool(safe(lambda: ref.system_library, False))
    rec["optional"] = bool(safe(lambda: ref.optional, False))
    rec["qualifiedOnly"] = bool(safe(lambda: ref.qualified_only, False))
    rec["publishSymbols"] = bool(safe(lambda: ref.publish_symbols_in_container, False))

    params = read_parameters(ref, safe, stored_parameters, on_error)
    if params:
        rec["parameters"] = params

    return rec


def read_parameters(ref, safe, stored_parameters=None, on_error=None):
    """Library parameters THE PROJECT SETS, as a name -> IEC expression map.

    Real project state and worth diffing: the fixture pins Util's IBLOCKSIZE to 22800,
    and a change to that number changes what the machine does.

    NOT `ref.parameters` when it can be helped. MEASURED 2026-10-16 on SP22 Patch 4
    (tools/codesys/Probe-LibraryParams.ps1): `ref.parameters` is GetParameterValues(),
    which merges the overrides the project stores with the defaults the RESOLVED
    library declares. VisuElemXYChart 4.10 declares MAX_CLIENTS = 20, so every Patch 4
    export wrote MAX_CLIENTS: "20" for a project whose stored table holds nothing for
    XYChart. The project had not changed, and File-Based Storage, saved by the same
    Patch 4, does not have the parameter. A value that follows the library installed on
    the exporting machine is a false diff on every machine whose library differs.

    `stored_parameters` is the owning manager's own table, placeholder name -> {name:
    value}, read by the caller, because reading it takes .NET reflection and this
    module stays pure. When the caller has it, that table is the answer: a reference
    absent from it sets nothing.

    When it is None the table could not be read, which is a newer CODESYS renaming a
    private field. The merged view is then the only one there is, and it is used, but
    LIBRARY_PARAMETERS_EFFECTIVE is reported for every reference it gives parameters
    for: the mirror may now show a library default as if the project set it, and that
    must be said rather than discovered in a diff.
    """
    if stored_parameters is not None:
        stored = stored_parameters.get(_text(safe(lambda: ref.name)) or u"", {})
        return dict((_text(k), _text(v) if v is not None else u"")
                    for k, v in stored.items() if _text(k) is not None)

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

    if out and on_error is not None:
        on_error("LIBRARY_PARAMETERS_EFFECTIVE", safe(lambda: ref.name))
    return out


def read_references(libman, safe, on_error=None, stored_parameters=None):
    """Every reference in one library manager, in the manager's own order.

    NOT SORTED, deliberately. The order is the project's, it was measured stable
    across runs, and re-ordering the file would hide a reorder somebody made in the
    IDE - a change the mirror is supposed to show.

    The collection is iterated rather than indexed: `references[i]` looks up by ID and
    raises for a position, which is how the first attempt at this read zero references
    out of a manager that has 21.

    `stored_parameters` is the manager's stored parameter table, or None when it could
    not be read; see read_parameters for why it matters.
    """
    refs = safe(lambda: libman.references)
    if refs is None:
        if on_error is not None:
            on_error("LIBRARY_REFERENCES_UNAVAILABLE", None)
        return []

    out = []
    try:
        for ref in refs:
            out.append(extract_reference(ref, safe, on_error, stored_parameters))
    except Exception as e:
        # Report a partial read rather than pretending the shorter list is the truth.
        # A library list that silently loses its tail is worse than one that says it
        # is incomplete: the diff would read as "somebody removed six libraries".
        if on_error is not None:
            on_error("LIBRARY_REFERENCES_PARTIAL", str(e))
    return out


def catalog_document(yaml, manager_name, manager_path, references):
    """Render the whole `.libraries.yaml` document.

    `yaml` is passed in so the exporter's wiring of reader to emitter stays explicit
    at its call site; the parity tests hand it in the same way. (The module does
    import `text_of` from versio_yaml - a leaf helper, not the render dependency.)
    """
    top = {
        "schema": LIBRARY_SCHEMA,
        "vendor": VENDOR,
        "manager": manager_name,
        "path": manager_path.replace("\\", "/"),
        "referenceCount": len(references),
    }
    return yaml.document(_HEADER, top, TOP_KEYS, "references", references, REF_KEYS)
