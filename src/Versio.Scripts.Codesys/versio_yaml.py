# -*- coding: utf-8 -*-
# PURE YAML emitter. No scriptengine import, no file IO, no third-party imports.
# Written to run on BOTH IronPython 2.7 (inside CODESYS) and CPython 3.
#
# VENDOR-NEUTRAL, and living in a vendor folder only because that is where python lives
# today. Nothing in here knows what a PLC is. The second toolchain's scripts want this
# same emitter, so when Versio.Scripts.<Vendor> becomes plural this file moves to
# whatever both of them can import - and it stays this small so that move is trivial.
#
# WHY BY HAND, and why so little of YAML:
#
# Versio has no third-party dependencies on either side of the C# <-> python boundary.
# The python half runs inside CODESYS's own IronPython 2.7, where "pip install pyyaml"
# is not something to ask of a controls engineer's workstation, and the C# half
# (Versio.Core) is deliberately dependency-free so it stays testable in milliseconds.
# So both halves of this file format are hand-written: this emitter, and
# LibraryCatalog.Parse on the C# side.
#
# That is affordable only because the SUBSET is tiny and fixed. This emits exactly:
#
#   key: scalar                     a mapping of scalars
#   key:                            a nested mapping of scalars
#     inner: scalar
#   key:                            a list of SCALARS
#     - one
#     - two
#   key:                            a list of mappings
#     - a: 1
#       b: 2
#
# No anchors, no aliases, no multi-line scalars, no flow style, no tags, no second
# document. Anything outside that shape is a bug here rather than a feature request:
# the format exists to be read by humans in a diff and by one specific parser, and
# every construct added is one both halves have to agree about forever.
#
# THE SCALAR LIST IS THE ONLY CONSTRUCT ADDED SINCE THIS FILE WAS WRITTEN, and it was
# not a preference. A task's POU list is an ordered list of names and the order is what
# they run in; without this the emitter fell through to the scalar branch and wrote
# python's repr - pous: "['PLC_PRG', 'Simulation']" - which is a string that merely looks
# like a list, would parse back as one on nobody's reader, and is exactly the kind of
# silent lie this subset exists to prevent.
#
# DETERMINISM IS LOAD-BEARING. The export is byte-identical across runs and this file
# is part of it, so nothing here may depend on dict iteration order (IronPython 2.7 and
# CPython 3 do not agree about it), on a clock, or on anything outside its arguments.
# Mapping keys are emitted in an order the CALLER supplies; nested mappings are sorted.

INDENT = u"  "

# Scalars that would read back as something other than a string if left bare.
# A library namespace of "No" or "Y" is not far-fetched enough to risk: YAML 1.1
# readers turn several of these into booleans.
_RESERVED = frozenset([
    u"true", u"false", u"yes", u"no", u"on", u"off", u"null", u"none", u"~",
    u"y", u"n",
])

_FIRST_CHARS = u"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_"


def _is_plain_safe(text):
    """True when a string can be written bare, without quotes.

    Deliberately CONSERVATIVE: the rule is "obviously an identifier", not "anything
    YAML would technically accept". A wrong answer here is silent corruption on the
    way back in, and the cost of quoting something that did not need it is one pair
    of quotes in a diff.
    """
    if not text:
        return False
    if text.lower() in _RESERVED:
        return False
    if text[0] not in _FIRST_CHARS:
        return False
    for ch in text:
        if not (ch.isalnum() or ch in u"_.-"):
            return False
    # A bare token that parses as a number would come back as one.
    try:
        float(text)
        return False
    except (TypeError, ValueError):
        return True


def quote(text):
    """Double-quote and escape a string scalar.

    Only the escapes this subset can produce are handled. Control characters become
    hex escapes rather than being written raw: a raw control character is unreadable
    in a diff and a parse error waiting to happen.
    """
    out = []
    for ch in text:
        if ch == u"\\":
            out.append(u"\\\\")
        elif ch == u'"':
            out.append(u'\\"')
        elif ch == u"\n":
            out.append(u"\\n")
        elif ch == u"\r":
            out.append(u"\\r")
        elif ch == u"\t":
            out.append(u"\\t")
        elif ord(ch) < 0x20:
            out.append(u"\\x%02x" % ord(ch))
        else:
            out.append(ch)
    return u'"' + u"".join(out) + u'"'


def _to_text(value):
    """Coerce to text on either python.

    Never raises: this is called on values that crossed the .NET boundary, and one
    unprintable object must not bring down a whole export.
    """
    try:
        return u"%s" % (value,)
    except Exception:
        return u"<unprintable>"


def scalar(value):
    """Render one scalar: bool, int, None or string.

    None becomes an EMPTY value rather than the token `null`, because what it means
    here is "this field does not apply to this object" - a placeholder reference has
    no version - and an empty value reads better in a diff than a null does.
    """
    if value is None:
        return u""
    if isinstance(value, bool):
        return u"true" if value else u"false"
    if isinstance(value, int):
        return u"%d" % value
    text = _to_text(value)
    return text if _is_plain_safe(text) else quote(text)


def emit_mapping(lines, mapping, keys, depth):
    """Append `mapping` to `lines` at `depth`, keys in the order given.

    Keys the caller did not list are NOT emitted, and that is deliberate: the key
    order is this file's contract with its reader and with every diff anybody looks
    at. Falling back to dict order would make the bytes depend on which python is
    running, which is exactly the class of thing that puts a spurious diff in every
    commit.
    """
    pad = INDENT * depth
    for key in keys:
        if key not in mapping:
            continue
        value = mapping[key]
        if isinstance(value, dict):
            if not value:
                continue                      # an empty map says nothing; omit it
            lines.append(u"%s%s:" % (pad, key))
            for inner in sorted(value.keys()):
                lines.append(u"%s%s%s: %s"
                             % (pad, INDENT, scalar(inner), scalar(value[inner])))
        elif isinstance(value, (list, tuple)):
            # NOT sorted: a scalar list is ordered because its caller's data is ordered.
            # The one that exists - a task's POUs - lists them in the order they run.
            if not value:
                continue                      # an empty list says nothing; omit it
            lines.append(u"%s%s:" % (pad, key))
            for item in value:
                lines.append(u"%s%s- %s" % (pad, INDENT, scalar(item)))
        else:
            rendered = scalar(value)
            lines.append(u"%s%s:%s" % (pad, key, (u" " + rendered) if rendered else u""))


def document(header, top, top_keys, list_key, items, item_keys):
    """Render the whole document: comment header, a mapping, then a list of mappings.

    One function rather than a general emitter, because Versio has exactly one document
    shape and a general emitter is a far larger thing to keep in agreement with a
    hand-written parser on the other side.

    Returns LF text with a single trailing newline - the same normalisation every other
    exported file gets, so git sees no line-ending churn.
    """
    lines = []
    for line in (header or u"").split(u"\n"):
        lines.append((u"# " + line).rstrip())

    emit_mapping(lines, top, top_keys, 0)

    lines.append(u"%s:" % list_key)
    for item in items:
        rendered = []
        emit_mapping(rendered, item, item_keys, 1)
        if not rendered:
            continue
        # The item's first line carries the dash; the rest align under it.
        lines.append(u"  - " + rendered[0].strip())
        for extra in rendered[1:]:
            lines.append(u"  " + extra)

    return u"\n".join(lines) + u"\n"
