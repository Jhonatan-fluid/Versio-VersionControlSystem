# -*- coding: utf-8 -*-
# REMEMBER: IronPython 2.7 inside CODESYS.
#
# Walking a CODESYS project tree by name.
#
# A module of its own rather than eight more lines in versio_import.py, because what a
# FOLDER means to a lookup turned out to be a rule with an argument behind it, and the
# importer is not where a reader would look for it. The two other scripts that navigate by
# name - versio_delete_objects.py and versio_probe_native_import.py - keep their own strict
# walk on purpose: one is a probe, and the other deletes what a human typed the path of,
# which is the one place a lookup should refuse to be helpful.
#
# PURE: no scriptengine import. Everything here reaches a node through get_children(),
# get_name() and is_folder alone, which is what lets the parity suite drive it under CPython
# with stand-in objects instead of a 20-second CODESYS launch. `safe` is passed in for the
# same reason - it is the caller's exception swallower, exactly as versio_tasks.read_tasks
# takes it.

def find_child(parent, name, safe):
    """A DIRECT child by name, or None."""
    for child in safe(lambda: parent.get_children(), []) or []:
        if safe(lambda: child.get_name(), None) == name:
            return child
    return None


def matches_through_folders(parent, name, safe, depth=0):
    """Every object called `name` under `parent`, looking THROUGH folders.

    A list rather than the first hit, because the caller must not act on an ambiguous
    answer: this walks past folder boundaries, and the whole justification for doing so is
    that the name identifies one object. Two of them means that assumption does not hold
    here, and guessing which one to overwrite is the kind of guess that costs somebody a
    method body.

    Only FOLDERS are descended into. A POU is not walked past, so a method never answers a
    lookup meant for a program.

    Depth-capped at 32, the same number and the same reason as versio_export's walk and
    versio_import's walk_tasks: a cycle in the object tree would otherwise hang the run with
    no output, and every other recursive walk over this API already says so. Nothing real
    approaches it - a folder chain that deep inside one POU is not a project anybody has.
    """
    if depth > 32:
        return []

    found = []
    for child in safe(lambda: parent.get_children(), []) or []:
        if safe(lambda: child.get_name(), None) == name:
            found.append(child)
        elif safe(lambda: bool(child.is_folder), False):
            found.extend(matches_through_folders(child, name, safe, depth + 1))
    return found


def find_child_folder_blind(parent, name, safe):
    """A child by name, ignoring any folders in the way.

    WHY A FOLDER IS NOT A PLACE. CODESYS lets a function block's methods be grouped into
    folders, and lets a folder be dropped around objects at any level afterwards. Neither
    is a namespace: `_M_Reset` is FB_Alarm's method whichever folder it is shown in, its
    name is unique across the POU either way, and dragging it between folders changes no
    code and no reference.

    So a chain built from the manifest names objects, not folders, and this is what makes
    the two agree. It only ever runs when the direct child is absent, and "absent" is the
    DANGEROUS answer here rather than the safe one: the owner in the chain resolved, so the
    importer is holding a valid parent and a file it was told to apply. import_native both
    replaces and creates, so what this prevents is a second _M_Reset pasted into the POU's
    root beside the folder holding the original. (The textual path stops one step earlier,
    at CANNOT_IDENTIFY_DECLARATION - a METHOD header is not a creatable kind - which is luck
    rather than a second guard.)

    An ambiguous answer returns None: today's behaviour, deliberately, rather than a coin
    toss dressed up as a lookup.
    """
    direct = find_child(parent, name, safe)
    if direct is not None:
        return direct

    found = matches_through_folders(parent, name, safe)
    return found[0] if len(found) == 1 else None


def find_by_path(proj, parts, safe):
    """Navigate the object tree by name. Returns None when any segment is missing."""
    node = proj
    for part in parts:
        node = find_child_folder_blind(node, part, safe)
        if node is None:
            return None
    return node


def application_of(node, safe, is_application):
    """The nearest enclosing Application of `node`, or None when it has none.

    Walked through `parent`, the one upward link a ScriptObject offers, and capped like
    every other walk here. `is_application` is the caller's type test, passed in so this
    module keeps no type table of its own.

    WHY THE QUESTION EXISTS. A task's POU list is kept BY NAME, and the name is unique
    within one Application - not across a project. Two Applications in one project may
    each own a program called Simulation and each run it from their own task, and renaming
    one of them is nobody else's business. The retargeting that follows a rename used to
    walk the WHOLE project and repoint every task entry carrying the old name, so renaming
    ApplicationA's Simulation rewrote ApplicationB's task to call a program ApplicationB
    does not have (the 2026-10-24 review's item 31). The walk starts here now.
    """
    current = node
    for _ in range(64):
        if current is None:
            return None
        if safe(lambda: bool(is_application(current)), False):
            return current
        current = safe(lambda: current.parent, None)
    return None


def tasks_under(node, safe, is_application, depth=0):
    """Every object under `node` that carries a POU list, stopping at a nested Application.

    Found by probing for the attribute rather than by looking for a TASK type GUID. The
    type table is incomplete by nature - that is written down in versio_types - and a task
    under an unmapped container would be missed by a type walk while still holding a
    reference the importer has to fix.

    Depth-capped for the same reason the export's walk is: a cycle in the object tree
    would otherwise hang an import with no output.

    A nested Application is not descended into: its tasks are its own, and a task entry is
    a bare name that means a program OF THAT Application. The first version said so in
    this docstring and did not do it - the recursion went into every child alike - so
    renaming a parent Application's Simulation also repointed a nested Application's task
    at a program it never had (the second-round review's item 3). `is_application` is the
    caller's type test, passed in as application_of takes it, so this module keeps no type
    table of its own.
    """
    if depth > 32:
        return

    for child in safe(lambda: node.get_children(), []) or []:
        if safe(lambda: bool(is_application(child)), False):
            continue

        if safe(lambda: child.pous, None) is not None:
            yield child

        for found in tasks_under(child, safe, is_application, depth + 1):
            yield found
