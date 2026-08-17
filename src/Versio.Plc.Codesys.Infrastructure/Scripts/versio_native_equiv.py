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


def _sort_unordered_blocks(text):
    """Sort sibling lines inside containers whose order is not meaningful.

    Deliberately line-based rather than a full XML parse: these payloads reach 2.5 MB and are
    compared on every import, and the reordering CODESYS performs is at the level of whole
    sibling elements on their own lines.
    """
    lines = text.split(u"\n")
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
        body.sort(key=_ordinal_key)
        result.extend(body)
        result.append(lines[end])
        i = end + 1

    return u"\n".join(result)


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
        def mask_guid(match):
            guids[0] += 1
            return u"<masked-guid>"

        text = _GUID_RE.sub(mask_guid, text)

    text = _sort_unordered_blocks(text)

    return (text, guids[0], counters[0])


def are_equivalent(a, b, mask_guids=True):
    """Two payloads are equivalent when their normalised forms match."""
    return normalize(a, mask_guids)[0] == normalize(b, mask_guids)[0]
