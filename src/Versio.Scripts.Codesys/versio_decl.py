# -*- coding: utf-8 -*-
# PURE declaration-header parser. No scriptengine import.
#
# Works out which CODESYS factory creates a missing object, from the ST declaration alone:
#   create_pou(name, type, language, return_type, base_type, interfaces)
#   create_dut(name, type, baseType)
#   create_interface(name, baseInterfaces)
#
# Mirrors StDeclarationParser.cs. Anything not confidently recognised comes back as
# UNKNOWN so the importer reports instead of guessing - creating the WRONG kind of object
# is worse than creating none.

import re

KIND_UNKNOWN = "UNKNOWN"
KIND_PROGRAM = "PROGRAM"
KIND_FUNCTION_BLOCK = "FUNCTION_BLOCK"
KIND_FUNCTION = "FUNCTION"
KIND_INTERFACE = "INTERFACE"
KIND_DUT = "DUT"

DUT_NONE = "NONE"
DUT_STRUCT = "STRUCTURE"
DUT_ENUM = "ENUMERATION"
DUT_ALIAS = "ALIAS"
DUT_UNION = "UNION"

_BLOCK_COMMENT = re.compile(r"\(\*.*?\*\)", re.DOTALL)
_LINE_COMMENT = re.compile(r"//[^\n]*")
_ATTRIBUTE = re.compile(r"\{[^}]*\}", re.DOTALL)
_WS = re.compile(r"\s+")

_MODIFIERS = set(["ABSTRACT", "FINAL", "PUBLIC", "PRIVATE", "PROTECTED", "INTERNAL"])
_VAR_BLOCKS = set(["VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_GLOBAL",
                   "VAR_TEMP", "VAR_STAT", "VAR_INST"])


def _strip(text):
    if not text:
        return u""
    t = text.replace(u"\r\n", u"\n")
    t = _BLOCK_COMMENT.sub(u" ", t)
    t = _LINE_COMMENT.sub(u" ", t)
    t = _ATTRIBUTE.sub(u" ", t)
    return t.strip()


def _tokenize(text):
    normalized = _WS.sub(u" ", text)
    tokens = []
    current = []
    for c in normalized:
        if c in u":,;()":
            if current:
                tokens.append(u"".join(current))
                current = []
            if c in u":,":
                tokens.append(c)
            if c in u";(":
                return tokens          # header ends here
        elif c == u" ":
            if current:
                tokens.append(u"".join(current))
                current = []
        else:
            current.append(c)
    if current:
        tokens.append(u"".join(current))
    return tokens


def _result(kind=KIND_UNKNOWN, name=None, dut=DUT_NONE,
            return_or_base=None, extends=None, implements=None):
    return {
        "kind": kind,
        "name": name,
        "dut": dut,
        "returnOrBase": return_or_base,
        "extends": extends,
        "implements": implements or [],
        "canCreate": kind != KIND_UNKNOWN and bool(name),
    }


def _parse_pou(tokens, kind):
    i = 1
    while i < len(tokens) and tokens[i].upper() in _MODIFIERS:
        i += 1
    if i >= len(tokens):
        return _result(kind)

    name = tokens[i]
    i += 1
    return_type = None
    extends = None
    implements = []

    if kind == KIND_FUNCTION and i < len(tokens) and tokens[i] == u":":
        i += 1
        if i < len(tokens):
            return_type = tokens[i]
            i += 1

    while i < len(tokens):
        token = tokens[i].upper()
        if token == u"EXTENDS":
            i += 1
            if i < len(tokens):
                extends = tokens[i]
                i += 1
            while i < len(tokens) and tokens[i] == u",":
                i += 1
                if i < len(tokens):
                    implements.append(tokens[i])
                    i += 1
        elif token == u"IMPLEMENTS":
            i += 1
            while i < len(tokens):
                implements.append(tokens[i])
                i += 1
                if i < len(tokens) and tokens[i] == u",":
                    i += 1
                    continue
                break
        elif token in _VAR_BLOCKS:
            break
        else:
            i += 1

    return _result(kind, name, DUT_NONE, return_type, extends, implements)


def _has_word(text, word):
    return re.search(r"\b" + re.escape(word) + r"\b", text) is not None


def _parse_dut(tokens, text):
    if len(tokens) < 2:
        return _result(KIND_DUT)
    name = tokens[1]
    upper = text.upper()

    # Order matters: a UNION and a STRUCT both hold member lists, and an enum's
    # parenthesised body can appear inside a struct member initialiser.
    if _has_word(upper, u"UNION"):
        return _result(KIND_DUT, name, DUT_UNION)
    if _has_word(upper, u"STRUCT"):
        return _result(KIND_DUT, name, DUT_STRUCT)

    colon = text.find(u":")
    if colon >= 0:
        body = text[colon + 1:].lstrip()
        if body.startswith(u"("):
            return _result(KIND_DUT, name, DUT_ENUM)
        base = body.rstrip()
        end = base.find(u";")
        if end >= 0:
            base = base[:end]
        base = re.sub(r"END_TYPE", u"", base, flags=re.IGNORECASE).strip()
        if base:
            return _result(KIND_DUT, name, DUT_ALIAS, base)

    return _result(KIND_DUT, name, DUT_STRUCT)


def parse_declaration(declaration):
    text = _strip(declaration)
    if not text.strip():
        return _result()

    tokens = _tokenize(text)
    if not tokens:
        return _result()

    head = tokens[0].upper()
    if head == u"PROGRAM":
        return _parse_pou(tokens, KIND_PROGRAM)
    if head == u"FUNCTION_BLOCK":
        return _parse_pou(tokens, KIND_FUNCTION_BLOCK)
    if head == u"FUNCTION":
        return _parse_pou(tokens, KIND_FUNCTION)
    if head == u"INTERFACE":
        return _parse_pou(tokens, KIND_INTERFACE)
    if head == u"TYPE":
        return _parse_dut(tokens, text)
    return _result()
