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
_RESERVED_WIN = set("""CON PRN AUX NUL COM1 COM2 COM3 COM4 COM5 COM6 COM7 COM8 COM9
                       LPT1 LPT2 LPT3 LPT4 LPT5 LPT6 LPT7 LPT8 LPT9""".split())


def safe_file_stem(name):
    """CODESYS object names are freer than Windows file names.

    Percent-encode the illegal characters rather than dropping them, so the mapping
    stays reversible and two objects cannot collide onto one file.
    """
    def repl(m):
        return u"%%%02X" % ord(m.group(0))
    stem = _INVALID_WIN.sub(repl, name or u"")
    stem = stem.rstrip(u" .")                       # Windows strips these silently
    if stem.upper() in _RESERVED_WIN:
        stem = stem + u"%20"
    return stem or u"_unnamed"


def unsafe_file_stem(stem):
    """Inverse of safe_file_stem."""
    if stem.upper().endswith(u"%20") and stem[:-3].upper() in _RESERVED_WIN:
        stem = stem[:-3]
    return re.sub(u"%([0-9A-Fa-f]{2})", lambda m: unichr(int(m.group(1), 16))  # noqa: F821
                  if str is bytes else chr(int(m.group(1), 16)), stem)
