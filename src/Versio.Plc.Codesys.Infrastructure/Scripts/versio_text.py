# -*- coding: utf-8 -*-
# PURE text helpers. No scriptengine import, no file IO beyond what callers pass in.
# Written to run on BOTH IronPython 2.7 (inside CODESYS) and CPython 3 (for pytest),
# because these are the functions most likely to harbour a silent data-loss bug.

import hashlib
import re

# Separator between declaration and implementation in a single .st file.
# Chosen to be a legal ST comment so the file stays readable in any editor.
IMPL_DELIMITER = u"// --- BEGIN IMPLEMENTATION ---"

# Escape marker for content that itself contains the delimiter.
#
# Found by a randomised round-trip test on the C# side: a DECLARATION containing the
# delimiter made the split cut in the wrong place and silently move code from the
# declaration into the implementation. codescribe has the same shape of bug - its
# `declaration, implementation = content.split(...)` raises ValueError on a second
# delimiter. Normal files contain neither marker, so both replacements are no-ops.
#
# Must stay identical to StTextCodec.cs.
_ESC = u"//!VERSIO-ESC!"


# The characters both codecs trim from the edge of a payload.
#
# EXPLICIT, because the two languages disagree and neither default is checkable from the
# other. CPython 3's str.strip() removes 29 codepoints; C#'s string.Trim() removes 25 - the
# four missing ones are the ASCII separators U+001C-U+001F, which Python calls whitespace
# and .NET does not. Measured by enumerating both sets rather than by reading either
# documentation.
#
# WHAT THE DIVERGENCE COST, so nobody "simplifies" this back to a bare strip(): the exporter
# records `ihash` over split_st()'s implementation, and CodesysImportPlanner re-computes it
# over StTextCodec.Split()'s. Those two hashes are what pair a renamed POU with the file it
# became. A declaration or a body ending in one of those four characters would hash
# differently on the two sides, no rename would be detected, and the object would be
# destroyed and rebuilt with a new GUID - G2's failure mode, reintroduced by a control
# character.
#
# THE SET IS PYTHON'S, not .NET's, and deliberately: the exporter is the writer, every hash
# in every committed manifest was produced by it, and matching the writer leaves them all
# valid. No mirror file in this repository contains any of the four.
#
# It is also spelled out rather than inherited because THE PYTHON THAT RUNS THIS IS
# IRONPYTHON 2.7 and the python that tests it is CPython 3. A bare strip() makes the rule
# whatever the interpreter thinks; naming the characters makes the two agree by
# construction, and PythonFileNameParityTests compares the sets themselves as well as the
# outputs. Public, because versio_import compares live object text against a
# split_st result and has to trim it the same way.
WS = (u"\t\n\x0b\x0c\r\x1c\x1d\x1e\x1f \x85\xa0"
       u"\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a"
       u"\u2028\u2029\u202f\u205f\u3000")


def _escape(s):
    # Order matters: escape the marker itself first, so decoding can tell a literal
    # marker in the source from one we added.
    return s.replace(_ESC, _ESC + u"E").replace(IMPL_DELIMITER, _ESC + u"D")


def _unescape(s):
    return s.replace(_ESC + u"D", IMPL_DELIMITER).replace(_ESC + u"E", _ESC)


def join_st(declaration, implementation):
    """declaration + implementation -> single .st payload.

    Normalisation (strip + single trailing newline) is what makes
    export -> import -> export byte-identical instead of accumulating whitespace.
    """
    decl = _escape((declaration or u"").replace(u"\r\n", u"\n").strip(WS))
    impl = _escape((implementation or u"").replace(u"\r\n", u"\n").strip(WS))
    return decl + u"\n\n" + IMPL_DELIMITER + u"\n\n" + impl + u"\n"


def split_st(content):
    """Inverse of join_st. Returns (declaration, implementation).

    Splits on the first UNESCAPED delimiter. Any delimiter that came from the content
    was escaped by join_st, so the first one found is always the real separator.
    """
    text = (content or u"").replace(u"\r\n", u"\n")
    idx = text.find(IMPL_DELIMITER)
    if idx < 0:
        # No delimiter: treat the whole file as a declaration (DUTs, GVLs).
        return _unescape(text.strip(WS)), u""
    decl = text[:idx]
    impl = text[idx + len(IMPL_DELIMITER):]
    return _unescape(decl.strip(WS)), _unescape(impl.strip(WS))


def normalize_decl_only(content):
    """For DUT / GVL declaration-only files."""
    return (content or u"").replace(u"\r\n", u"\n").strip(WS) + u"\n"


# CODESYS native export writes a fresh Timestamp on every run. Scrubbing it is what
# makes the export deterministic.
#
# GUIDs and Ids are deliberately NOT touched: codescribe tried that and it broke
# visualisations. They are identity, not noise.
_TIMESTAMP_RE = re.compile(
    r'(?P<pre><Single Name="Timestamp" Type="long">)(?P<val>[^<]*)(?P<post></Single>)')


def scrub_native_xml(text):
    """Remove volatile fields from a native .export/.xml payload.

    Returns (scrubbed_text, replacements_made).
    """
    if text is None:
        return u"", 0
    normalized = text.replace(u"\r\n", u"\n")
    scrubbed, n = _TIMESTAMP_RE.subn(u"\\g<pre>0\\g<post>", normalized)
    if not scrubbed.endswith(u"\n"):
        scrubbed += u"\n"
    return scrubbed, n


# How many line hashes a sketch keeps. Bottom-k: the k SMALLEST, which is what makes two
# sketches comparable without either side seeing the other's text.
#
# 16 is a size, not a quality setting, and it is chosen to keep _manifest.json readable. The
# alternative - recording every line's hash - estimates similarity far better and grows with
# the file: a 400-line POU would add 3 KB to a file people read in pull requests. A sketch is
# 128 characters whatever the POU does.
SKETCH_SIZE = 16


def line_sketch(text, size=SKETCH_SIZE):
    """A bounded fingerprint of a text's DISTINCT lines, for estimating similarity later.

    WHAT IT IS FOR. Pairing a renamed-and-edited file with the one it became needs the
    departed file's content, and the mirror no longer has it. Git has it when the mirror was
    committed; when it was not, this is what the manifest carries instead - recorded at
    export time, while the content still exists.

    Bottom-k over 32-bit line hashes. Two sketches estimate the Jaccard similarity of the
    two line SETS, which is a coarser measure than comparing the texts themselves - it
    cannot see line order or repetition. That is the price of not keeping the text, and it
    is why git is preferred whenever HEAD has the file: an estimate is a fallback, not an
    equal.

    Lines are trimmed and blanks dropped, exactly as TextSimilarity does, so the two
    measures at least agree about what a line is. The C# mirror is Versio.Core.Diff.LineSketch
    and the two are compared by output in PythonFileNameParityTests - a sketch that differed
    between the writer and the reader would silently estimate every similarity as zero.
    """
    if not text:
        return u""

    seen = set()
    for raw in text.replace(u"\r\n", u"\n").split(u"\n"):
        # WS, not a bare strip(): see the comment on WS. The C# mirror trims the
        # same set, and a line trimmed differently hashes differently.
        line = raw.strip(WS)
        if line:
            # First 4 bytes of sha256, as an unsigned 32-bit value. sha256 because both sides
            # already have it and it is identical everywhere; a language's own hash() is not,
            # and IronPython's differs from CPython's within one family.
            digest = hashlib.sha256(line.encode("utf-8")).hexdigest()
            seen.add(digest[:8])

    return u",".join(sorted(seen)[:size])


def sketch_similarity(left, right, size=SKETCH_SIZE):
    """Estimated Jaccard similarity of two line sketches, 0 to 1.

    Both empty is 0 rather than 1, for the reason TextSimilarity gives: an empty file
    resembles every other empty file, so 1 would let a threshold pair any two of them, and
    the caller is asking whether there is EVIDENCE two files are the same object.
    """
    a = set(left.split(u",")) - set([u""])
    b = set(right.split(u",")) - set([u""])
    if not a or not b:
        return 0.0

    # Bottom-k estimator: compare over the k smallest of the union, because beyond that
    # neither sketch is a complete record and their disagreement means nothing.
    union = sorted(a | b)[:size]
    if not union:
        return 0.0

    shared = len([v for v in union if v in a and v in b])
    return float(shared) / len(union)


def content_hash(text):
    """Stable hash of canonical content. sha256 over UTF-8 bytes, no BOM."""
    if isinstance(text, bytes):
        data = text
    else:
        data = text.encode("utf-8")
    return u"sha256:" + hashlib.sha256(data).hexdigest()


def object_path(rel_dir, name):
    """How one object is spelled when a PERSON is going to read it.

    ONE SPELLING, and it exists because there were two. The exporter builds this
    string in five places - a skip record, a task configuration's object path, a
    library manager's, and two warnings - and the three that reach _manifest.json
    normalised it while the two that reach `warnings` did not. So the same object
    was `App/Thing` in the skips and `\\App/Thing` in the warnings, mixing
    separators and keeping a leading slash from an empty rel_dir at the root.

    The comment beside the skip version said exactly why that must not happen -
    "a reader comparing the log against _manifest.json sees different strings for
    the same object and has to work out whether that means anything" - and the
    rule was applied to the three callers written beside it and to neither of the
    others. A rule stated in a comment is a rule with as many copies as it has
    call sites; this is the copy that runs.

    It matters more than tidiness now that ExportCoverage reads EXPORT_FAILED:
    the panel names the failed object in a sentence, and a name with a stray
    backslash in it is a name somebody cannot search the mirror for.

    Here rather than in versio_export.py because that module imports scriptengine
    at import time and therefore cannot be loaded under CPython at all - a rule
    living in it is a rule nothing can test.

    AN EMPTY NAME IS A REAL CASE, not defensive padding. UNNAMED_OBJECT fires when
    get_name() itself raises, so the only thing the caller has left is the directory
    - and that caller was the one site still passing a raw os.path.join result when
    the other four had been converted. Handled as its own branch rather than by
    stripping a trailing slash off the joined form, because a CODESYS object name
    may legitimately contain "/" and an rstrip would silently eat one.

    IT CHANGES ONE EXISTING ANSWER, said plainly rather than glossed as a no-op. An
    object whose get_name() RETURNS "" - as opposed to raising, which is the case
    above - used to spell as "Dir/" and now spells as "Dir". Nothing in either
    fixture has one, so no committed byte moves; the new answer is the better of the
    two; and it is written down because "identical for the callers that had a name"
    was the claim, and it is true only for names that are not empty.
    """
    if not name:
        return rel_dir.replace(u"\\", u"/").lstrip(u"/")

    return (rel_dir + u"/" + name).replace(u"\\", u"/").lstrip(u"/")


# G9. Two limits, and they are NOT the same rule at two thresholds.
#
# WINDOWS' CEILING. 260 characters for a path, in the default configuration that
# every engineering laptop this will run on is in. P8 measured that CODESYS itself
# imposes nothing - ten nested 40-character folders, ~420 characters, no complaint -
# so the whole of this gap is ours to guard.
MAX_TOTAL_PATH = 260

# WHAT THE EXPORT MAY PRODUCE, relative to the mirror root.
#
# 200 is not a taste. It is the length past which the mirror cannot work on ANY
# machine: the shortest realistic checkout is something like C:\r (4) plus
# \plc\<project>.versio_staging\ (~41 for a 20-character project name), which
# leaves ~215. A relative path longer than that is a file nobody can open no matter
# where they clone, which is a different and much worse thing than "your checkout is
# too deep" - and it is the only half of G9 that can be decided the same way
# everywhere.
MAX_REL_PATH = 200


def too_long_for_any_machine(rel_path):
    """Reason this mirror path cannot work anywhere, or None.

    MACHINE-INDEPENDENT ON PURPOSE, and that is the entire design of this half.
    It reads only the path the export produced - never the repository root - so
    every developer exporting the same project gets the same answer, which is what
    makes it safe to record in _manifest.json. See too_long_for_this_machine for
    the half that cannot be, and why recording that one would undo Phase 5.

    MEASURED, so the numbers here are not hypothetical - and derived by a test
    rather than counted, because the hand count was wrong twice. safe_file_stem
    returns up to 87 characters, not the 80 _MAX_STEM advertises: the shortened
    branch adds "~" plus six hex. A member's path carries two of those - its owner's
    directory and its own file - so ONE path is 190 characters at worst, a TRANSITION
    exported as native XML.

    That worst case did not move when the layout did. Until schema 9 the two stems
    met in one FILE NAME, `Owner.Member.xml`; now they meet as `Owner/Member.xml`,
    which is the same characters with the dot as a separator. What changed is only
    where the boundary falls, and a folder inside a POU can add a third.

    With the ordinary Device/Plc Logic/Application chain and a ONE-character project
    name, that leaves 19 characters for the repository root. C:\GIT\Versio is 13 and
    fits by six; anything under a user profile does not. G9 was written up as needing
    "a deep folder chain" and it does not need one at all.

    IT DOES NOT SHORTEN, and that is a decision rather than an omission. Shortening
    keyed on a total path would make the file name depend on where the repository is
    checked out, so two developers would produce different names for one object and
    git would see renames that are not renames. The mirror has to be a function of
    the project alone. Shortening that IS safe already exists one function up, in
    safe_file_stem, because it keys on the object's own name and nothing else.
    """
    if len(rel_path) <= MAX_REL_PATH:
        return None

    return (u"%d characters, past the %d this export may produce. A path this long "
            u"cannot be checked out on any machine, wherever the repository lives."
            % (len(rel_path), MAX_REL_PATH))


def too_long_for_this_machine(abs_path):
    """Reason this path breaks on THIS machine, or None.

    THE HALF THAT DEPENDS ON WHERE THE REPOSITORY IS, which is why its caller must
    emit it rather than warn() it. warn() appends to Exporter.warnings and that list
    is written into _manifest.json - so a machine-dependent sentence in there would
    make the manifest differ between two developers exporting the same project, and
    the mirror would churn on every export by whoever has the longer path. That is
    precisely the churn Phase 5 closed, arriving through a new door. It goes to the
    protocol and the log, where PROJECT_CLOSE_FAILED already goes, and no further.

    Measured against the STAGING path, which is the longer of the two: the export
    builds in <mirror>.versio_staging and swaps afterwards, so a name that fits in
    the mirror by fifteen characters still fails on the way in.
    """
    if len(abs_path) <= MAX_TOTAL_PATH:
        return None

    return (u"%d characters, past Windows' %d. The object's own path is not the "
            u"problem here - the repository is checked out too deep. Move it nearer "
            u"the root of the drive."
            % (len(abs_path), MAX_TOTAL_PATH))


_INVALID_WIN = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
# Long enough for any object name a person types.
#
# IT DOES NOT BOUND THE PATH, and it said it did until 2026-08-16: "short enough that
# a deep CODESYS folder chain plus this still clears Windows' 260-character path
# limit". Two things make that false, and both were found by measuring rather than by
# reading it. The shortened branch below returns tail[:80] + "~" + six hex, so the cap
# is really 87. And a METHOD, PROPERTY, ACTION or TRANSITION meets two of these on one
# path - its owner's directory and its own file - so that is up to 190 characters, which
# leaves 19 for the repository root with the ordinary Device/Plc Logic/Application chain
# and no deep chain at all. (Before schema 9 the same two stems met in one file name as
# `Owner.Member.xml`; the arithmetic is unchanged, the separator is a slash now.)
#
# The path is bounded by too_long_for_any_machine and too_long_for_this_machine above.
# This number bounds one segment, which is all a per-name rule can honestly bound.
_MAX_STEM = 80

_RESERVED_WIN = set("""CON PRN AUX NUL COM1 COM2 COM3 COM4 COM5 COM6 COM7 COM8 COM9
                       LPT1 LPT2 LPT3 LPT4 LPT5 LPT6 LPT7 LPT8 LPT9""".split())


def safe_file_stem(name):
    """CODESYS object names are freer than Windows file names.

    Percent-encode the illegal characters rather than dropping them, so the mapping
    stays reversible and two objects cannot collide onto one file.

    EXCEPT when the name is not a name at all. CODESYS gives an external-file
    reference an identity like

        {00000000-0000-0000-0000-000000000000}|C:\\Users\\m.lamp\\Documents\\license.txt

    and percent-encoding that produces a 120-character filename carrying somebody's
    username and directory layout into git, in a repository they will share. Measured
    on a real project: the mirror held a file called
    `00000000-0000-0000-0000-000000000000%7CC%3A%5CUsers%5Cm.lamp%5CDocuments%5C
    license.txt.xml`. That is the same class of leak as the `-<user>-<machine>.opt`
    files adoption gitignores, arriving by a different door.

    So a name that looks like a path is shortened to its last segment plus a short
    digest of the whole name: `license.txt~3f9a1c`. The digest is what keeps two
    external files with the same basename from colliding onto one file, and the whole
    name still reaches the reader - the manifest records `name` verbatim, which is
    where the panel and _coverage.md read it from.

    Reversibility is not lost by this, because it was never used: `unsafe_file_stem`
    exists as the inverse and nothing calls it. An import resolves objects through the
    project tree and the native XML's own identity, not by parsing filenames back.
    """
    raw = name or u""

    if _looks_like_a_path(raw):
        return _shortened(raw)

    def repl(m):
        return u"%%%02X" % ord(m.group(0))
    stem = _INVALID_WIN.sub(repl, raw)

    # Windows silently strips a trailing space or dot, so it cannot be left as it is. It is
    # ENCODED rather than removed.
    #
    # This used to be stem.rstrip(u" ."), which mapped "Pump " and "Pump." onto the same
    # stem - the exact collision the encoding exists to prevent, quietly losing one of two
    # real objects. The C# PathMapper had it right and this did not; found while making the
    # two agree, which is what the parity test now enforces.
    #
    # Only the LAST character, matching PathMapper.EncodeStem: "a  " becomes "a %20", not
    # "a%20%20". Rare either way, but the two halves have to make the same rare choice.
    if stem and stem[-1] in u" .":
        stem = stem[:-1] + (u"%%%02X" % ord(stem[-1]))

    if stem.upper() in _RESERVED_WIN:
        stem = stem + u"%20"

    # Long names are shortened for a second reason: Windows still has a 260-character
    # path limit in most configurations, and a deep CODESYS folder chain plus a
    # percent-encoded name reaches it. A file nobody can open is worse than a file
    # whose name is abbreviated.
    if len(stem) > _MAX_STEM:
        return _shortened(raw)

    return stem or u"_unnamed"


def _looks_like_a_path(name):
    """A name carrying a directory separator or a drive letter is not a name."""
    return (u"\\" in name) or (u"/" in name) or (u":" in name)


def _shortened(name):
    """Last segment of a path-like name, plus a digest of the whole thing."""
    tail = re.split(u"[\\\\/]", name)[-1].strip() or name
    tail = _INVALID_WIN.sub(u"", tail).rstrip(u" .")

    # Six hex characters: enough that two external files with the same basename in
    # different folders do not collide, short enough to read past.
    digest = hashlib.sha256(name.encode("utf-8")).hexdigest()[:6]

    if not tail or tail.upper() in _RESERVED_WIN:
        tail = u"object"

    return u"%s~%s" % (tail[:_MAX_STEM], digest)


def unsafe_file_stem(stem):
    """Inverse of safe_file_stem."""
    if stem.upper().endswith(u"%20") and stem[:-3].upper() in _RESERVED_WIN:
        stem = stem[:-3]
    return re.sub(u"%([0-9A-Fa-f]{2})", lambda m: unichr(int(m.group(1), 16))  # noqa: F821
                  if str is bytes else chr(int(m.group(1), 16)), stem)
