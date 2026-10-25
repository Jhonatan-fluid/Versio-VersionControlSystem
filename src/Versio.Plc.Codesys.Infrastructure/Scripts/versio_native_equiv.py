# -*- coding: utf-8 -*-
# PURE native-XML comparison. No scriptengine import, no file IO beyond what callers pass in.
# Written to run on BOTH IronPython 2.7 (inside CODESYS) and CPython 3, because the parity
# test that keeps this honest runs on CPython and the product runs on IronPython.
#
# This is the python half of NativeXmlNormalizer.cs, and that file's remarks carry the
# measurement both sides encode: an export -> import_native -> re-export round trip is
# semantically lossless but NOT byte-stable, in exactly three classes.
#
#   1. Counters       UniqueIdGenerator advanced 26 -> 29 on a visualisation.
#   2. Unordered      Element properties came back with UseInitValue and DescriptionTextId
#      collections    swapped - same content, a different serialisation order.
#   3. Reference      EventPOUGuid, GuidInit/GuidReInit/GuidExitX and unnamed link GUIDs are
#      GUIDs          re-minted by CODESYS on import. 49 of 107 differing lines in one
#                     visualisation became identical once GUIDs were masked.
#
# All three are masked IN MEMORY, FOR A COMPARISON. The bytes on disk keep their real GUIDs.
# That is the distinction from codescribe, which broke visualisations by REWRITING GUIDs in
# the file.
#
#
# WHY A SECOND COPY EXISTS, when the C# one is written, tested and works.
#
# Two callers need this verdict from INSIDE the CODESYS process, where the C# cannot be
# reached: Versio.Core targets net9.0 and this runs on IronPython 2.7. It is the same wall
# that stops tools/codesys/Test-RoundTrip.ps1 calling the C# normaliser (vendors/codesys/DRIVING.md section 6), and
# reimplementing was rejected there because PowerShell had no need of its own - here both
# callers do.
#
#   Phase 2 (deletes).  Before removing an object the script must check the live object still
#     matches what was exported; otherwise a stale manifest silently destroys work somebody
#     did in the IDE since. For a textual object that check is a byte comparison and P7
#     proved it holds. For a native object it CANNOT BE: the manifest's hash is
#     content_hash over the exported file bytes, so checking one means exporting the object
#     again - and P7 measured that re-export changing on every cycle with nothing edited. A
#     byte check would refuse every native deletion forever, for a reason with nothing to do
#     with the object. P3 makes this the whole safety story: remove() deletes a referenced
#     object silently, with no exception and no cascade.
#
#   Phase 5 (G10).  The exporter keeps the existing bytes when a freshly exported payload is
#     equivalent to the file already at that path, so the mirror stops churning. That
#     decision is taken mid-export, in this process, on a payload that is not on disk yet.
#
#
# WHAT THIS COSTS, stated plainly because CODESYS.md section 9 counts it.
#
# This is the fifth rule living in two languages, and that arrangement has already drifted
# FOUR times - twice with the C# right and twice with the python right, and comparing the
# two sources by eye caught none of them. So the parity test is not a follow-up: it runs
# BOTH implementations over the same real CODESYS output and compares their outputs, in the
# commit that adds this file.

import re

# One regex per volatility class, character-for-character the same as NativeXmlNormalizer's.
#
# Named groups are (?P<x>...) in python and (?<x>...) in .NET; that spelling is the only
# permitted difference, and val is kept named on both sides even though neither replacement
# reads it, so the two patterns stay diffable against each other by eye.
_GUID_RE = re.compile(
    r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")

_COUNTER_RE = re.compile(
    r'<Single Name="(?P<name>[^"]*(?:UniqueIdGenerator|Timestamp)[^"]*)"'
    r'(?P<attrs>[^>]*)>(?P<val>[^<]*)</Single>')

# Element names whose child order carries no meaning, so a reordering must not read as a
# change. The ORDER of this tuple matters: a line naming two of them takes the first match,
# and the C# takes the first match of its array. Keep them in step.
UNORDERED_CONTAINERS = (u"List", u"Properties", u"Dictionary")


def _ordinal_key(line):
    """Sort key giving C#'s StringComparer.Ordinal, not python's default.

    C# compares strings by UTF-16 code unit; CPython 3 compares by code point. The two agree
    across the whole BMP and disagree above it, because a surrogate pair (U+10000 and up)
    sorts below U+E000..U+FFFF as code units and above it as code points. Encoding to
    big-endian UTF-16 and comparing the bytes reproduces the code-unit order exactly, on
    both interpreters - IronPython 2.7 strings are already UTF-16, so it is a no-op there
    and a correction on CPython.

    Whether any CODESYS payload reaches that far is not the point: this file exists to give
    the same answer as the C# one, and "the corpus we happen to have does not contain the
    disagreement" is how the four drifts in CODESYS.md section 9 all stayed invisible.

    A lone surrogate would raise here, and cannot arrive: callers read these payloads with
    io.open(..., encoding="utf-8"), which refuses one.
    """
    return line.encode("utf-16-be")


# One tag, enough to count depth: a close, an open, or a self-closing one. The same
# expression as the C#'s Tag(), and it has to stay the same: the chunking below is what
# decides which lines travel together, and two normalisers that cut differently disagree
# on the text while agreeing on every verdict the corpus happens to ask.
_TAG_RE = re.compile(r"<(/?)[^\s/>!?][^\s/>]*[^>]*?(/?)>")


def _sorted_siblings(body):
    """The lines of a container's body, regrouped into whole sibling elements and sorted.

    Falls back to the old line sort when the body cannot be cut cleanly - a close with no
    open, which only malformed input produces - so a strange payload degrades to the
    previous answer rather than throwing. Mirrors SortedSiblings in the C#.
    """
    chunks = []
    current = []
    depth = 0

    for line in body:
        current.append(line)
        for match in _TAG_RE.finditer(line):
            if match.group(2):
                continue                        # <x/> opens and closes on the spot
            depth += -1 if match.group(1) else 1

        if depth < 0:
            flat = list(body)
            flat.sort(key=_ordinal_key)
            return flat

        if depth == 0:
            chunks.append(current)
            current = []

    if current:
        chunks.append(current)                  # unterminated: kept whole, sorted with the rest

    joined = []
    for chunk in chunks:
        if len(chunk) > 1:
            chunk = _sort_unordered_lines(chunk)
        joined.append(u"\n".join(chunk))
    joined.sort(key=_ordinal_key)

    result = []
    for chunk in joined:
        result.extend(chunk.split(u"\n"))
    return result


def _sort_unordered_blocks(text, whole_siblings=True):
    """Sort sibling ELEMENTS inside containers whose order is not meaningful.

    Deliberately line-based rather than a full XML parse: these payloads reach 2.5 MB and are
    compared on every import, and the reordering CODESYS performs is at the level of whole
    sibling elements.

    Whole siblings, not lines. The first version sorted the container's LINES, which threw
    away which line belonged to which child: two multi-line children with their values
    swapped between them sorted to the same bag of lines, read as equivalent, and the export
    kept the old file on disk - a real change never reached the mirror (the 2026-10-24
    review's item 20). See _sorted_siblings.

    whole_siblings=False is the pre-item-20 line sort, kept ONLY so normalized_forms can
    reproduce the hash an older export recorded; nothing else may pass it.
    """
    return u"\n".join(_sort_unordered_lines(text.split(u"\n"), whole_siblings))


def _sort_unordered_lines(lines, whole_siblings=True):
    result = []
    count = len(lines)
    i = 0

    while i < count:
        line = lines[i]

        container = None
        for name in UNORDERED_CONTAINERS:
            if (u"<" + name + u" ") in line or (u"<" + name + u">") in line:
                container = name
                break

        # A self-closing container has no body to sort. The test is on the WHOLE line, not
        # on the tag, matching the C#: a line carrying both an opening container and some
        # self-closing element is left alone by both sides.
        if container is None or u"/>" in line:
            result.append(line)
            i += 1
            continue

        closing = u"</" + container + u">"
        end = i + 1
        while end < count and closing not in lines[end]:
            end += 1

        # Unterminated: truncated or malformed input must degrade, not hang or throw. The
        # opening line is emitted as ordinary content and the scan resumes just after it,
        # so a missing close costs one unsorted block rather than the rest of the file.
        if end >= count:
            result.append(line)
            i += 1
            continue

        result.append(line)
        body = lines[i + 1:end]
        if whole_siblings:
            result.extend(_sorted_siblings(body))
        else:
            # The body's LINES sorted - the normalisation every export before 2026-10-24
            # recorded its nhash under, blind to values swapped between two multi-line
            # children (item 20). Reproduced here for those recorded hashes and for nothing
            # else: see normalized_forms.
            body.sort(key=_ordinal_key)
            result.extend(body)
        result.append(lines[end])
        i = end + 1

    return result


# The GUID placeholder. Braces, not angle brackets: "<masked-guid>" read to _sorted_siblings
# as an element that opens and never closes, so every chunk after the first GUID was
# swallowed into one and a reordered payload compared different with masking on - the only
# setting the delete check uses (the second-round review's item 7). The old spelling is kept
# beside it because every nhash recorded before that fix was taken over it, and a delete
# check that could no longer reproduce those hashes refused every native deletion in every
# existing mirror as DELETE_CONTENT_CHANGED (the third round's item 4): see normalized_forms.
MASKED_GUID = u"{masked-guid}"
_LEGACY_MASKED_GUID = u"<masked-guid>"


def normalize(xml, mask_guids=True):
    """Produce a comparison form of a native XML payload.

    Returns (text, guids_masked, counters_masked). The counts are what make an equivalence
    result explainable rather than a bare true or false - and asymmetric counts across a
    pair are the signal that one side carried something the other did not while the verdict
    hid it, which is how the mirror's churn was shown to be masked-positions-only and
    nowhere else.

    mask_guids is required for an import round-trip comparison and must be OFF when
    comparing two exports of the same project: GUIDs are stable across exports, so a
    difference there is genuinely meaningful and masking it away would hide a real change.
    """
    return _normalize(xml, mask_guids, MASKED_GUID, True)


def normalized_forms(xml):
    """Every comparison form a manifest's nhash may have been taken over, current first.

    The delete check compares a fresh re-export of the live object against the nhash the
    manifest recorded at export time (versio_import.why_not_delete). That hash was taken
    over whatever normalisation the exporting Versio had, and the normalisation has changed
    twice since nhash exists: the sibling sort replaced the line sort on 2026-10-24 (the
    first review round's item 20) and the GUID placeholder changed on 2026-10-25 (the
    second's item 7). Each change made every hash recorded before it unreproducible, so an
    unchanged object exported by an older Versio was refused as DELETE_CONTENT_CHANGED -
    "edited since the export" - when nothing had touched it. Both committed fixture mirrors
    are such older exports, every native record of them (the third round's item 4).

    So the check accepts a match under any of these. Nothing is lost by it: a recorded hash
    was produced by exactly one of them, and under the other two agreement is a SHA-256
    collision. What an older form cannot see (the line sort's blindness to swapped values,
    item 20) it could not see when that hash was recorded either, and the next export
    records the hash under the current form, which closes that gap for that mirror. The
    alternative - a version number beside nhash - would have changed the manifest's shape
    for every reader and still needed these three functions to compare the old ones.

    python only, deliberately: the C# normaliser compares two payloads it holds in memory
    and never a recorded hash, so it has no older form to reproduce.
    """
    return [
        _normalize(xml, True, MASKED_GUID, True)[0],
        _normalize(xml, True, _LEGACY_MASKED_GUID, True)[0],
        _normalize(xml, True, _LEGACY_MASKED_GUID, False)[0],
    ]


def _normalize(xml, mask_guids, placeholder, whole_siblings):
    if not xml:
        return (u"", 0, 0)

    text = xml.replace(u"\r\n", u"\n")

    # Boxed in a list because IronPython 2.7 has no nonlocal, and a counter that silently
    # stayed at zero would turn the explainability above into a lie.
    counters = [0]
    guids = [0]

    def mask_counter(match):
        counters[0] += 1
        return u'<Single Name="%s"%s><masked/></Single>' % (
            match.group("name"), match.group("attrs"))

    # Counters BEFORE guids, as in the C#. A counter's value is replaced wholesale, so a
    # payload with a GUID inside one is masked once rather than twice, and the guid count
    # reports what survived into the comparison rather than what was in the file.
    text = _COUNTER_RE.sub(mask_counter, text)

    if mask_guids:
        # MASKED_GUID, as in the C#; the legacy spelling only through normalized_forms.
        def mask_guid(match):
            guids[0] += 1
            return placeholder

        text = _GUID_RE.sub(mask_guid, text)

    text = _sort_unordered_blocks(text, whole_siblings)

    return (text, guids[0], counters[0])


def are_equivalent(a, b, mask_guids=True):
    """Two payloads are equivalent when their normalised forms match."""
    return normalize(a, mask_guids)[0] == normalize(b, mask_guids)[0]
