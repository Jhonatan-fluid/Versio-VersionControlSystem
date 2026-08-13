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
    decl = _escape((declaration or u"").replace(u"\r\n", u"\n").strip())
    impl = _escape((implementation or u"").replace(u"\r\n", u"\n").strip())
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
        return _unescape(text.strip()), u""
    decl = text[:idx]
    impl = text[idx + len(IMPL_DELIMITER):]
    return _unescape(decl.strip()), _unescape(impl.strip())


def normalize_decl_only(content):
    """For DUT / GVL declaration-only files."""
    return (content or u"").replace(u"\r\n", u"\n").strip() + u"\n"


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


def content_hash(text):
    """Stable hash of canonical content. sha256 over UTF-8 bytes, no BOM."""
    if isinstance(text, bytes):
        data = text
    else:
        data = text.encode("utf-8")
    return u"sha256:" + hashlib.sha256(data).hexdigest()


_INVALID_WIN = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
# Long enough for any object name a person types, short enough that a deep CODESYS
# folder chain plus this still clears Windows' 260-character path limit.
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
    stem = stem.rstrip(u" .")                       # Windows strips these silently
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
