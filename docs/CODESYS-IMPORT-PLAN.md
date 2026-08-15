# Versio — closing the CODESYS import gaps

**Date:** 2026-08-15 · **Nothing built yet — Phase 0 is nine measurements, and it comes first**

| | |
|---|---|
| Document | **The plan for making the import side a real bidirectional sync.** Deletes, renames, moves, typed task configuration, and a mirror that stops churning after every import |
| Status | **Planned, not started.** Phase 0 measures nine things against a real project before any product code is written; two of its probes may close gaps outright rather than cost code |
| Related | [`CODESYS.md`](CODESYS.md) — how CODESYS is driven today, and §12, the audit these gaps came from · [`PHASE0-FINDINGS.md`](PHASE0-FINDINGS.md) — what was measured · [`PLAN.md`](PLAN.md) — the architecture · [`SAFEGUARDS.md`](SAFEGUARDS.md) — what Versio refuses or waits for |

---

## Why

The export side is solid — deterministic, byte-identical across runs, nothing silently dropped.
The **import** side only ever updates and creates.

Ten gaps came out of auditing it ([`CODESYS.md`](CODESYS.md) §12). Nine share one root cause:
`versio_import.py` walks the **mirror filesystem** and infers everything from **file names**, never
reading `_manifest.json`. The tenth is about what `import_native` does to the bytes on the way back.

| | Gap | Today |
|---|---|---|
| G1 | Deletes don't propagate | Delete a `.st`, colleague imports → the POU is still there |
| G2 | Renames/moves duplicate | Rename in the mirror → new object created, **old one kept** |
| G3 | New object named from the filename | `create_textual` uses `parts[-1]`; the parsed `info["name"]` is thrown away. `PROGRAM Foo` in `Bar.st` creates an object called `Bar` |
| G4 | Encoded names unresolvable | Looks up the *encoded* stem against raw names → duplicate named `weird%3Cname%3E` |
| G5 | A dot in a name breaks the owner split | `My.Thing.st` → looks for member `Thing` of parent `My` |
| G6 | Stray files go to `import_native` | A `README.md` in the mirror is handed to CODESYS as native XML |
| G7 | Task Configuration mutated on every import | `Core2` `-1`→`-2`. Documented as "diff-only" — it never was |
| G8 | Managed library references unproven | All 34 fixture references are placeholders; the `managed_library` branch has never executed |
| G9 | Total path length unguarded | Stems capped at 80; the **full path** is not, and `.versio_staging` makes it 16 longer |
| **G10** | **Import → export is not byte-stable** | `UniqueIdGenerator` **44→47** measured 2026-08-14 (gate 0.5b saw 26→29). Every import leaves a diff in every native file it touched, and a counter is a high-water mark, so it **climbs again each cycle** — it never settles the way `Core2` does |

**Intended outcome:** the mirror becomes authoritative for object *existence* and *identity*, not
just content; task scheduling stops being changed by a tool nobody asked to change it; and an
import that alters nothing semantically leaves no diff behind.

---

## What the ScriptEngine actually offers

Nobody had checked. Reflecting over `ScriptEngine3.dll` and `ScriptDriverProjects.plugin.dll`, the
API is **considerably richer than the scripts use**, and three gaps exist only because we never
looked:

| Member (`IScriptObject` unless noted) | Closes |
|---|---|
| `rename(stNewName)` | **G2** — renames are supported outright |
| `move(new_parent, new_index)` | **G2** — so are moves |
| `remove()` | **G1** |
| `find(String[] namePath)`, `find(name, recursive)` | **G4, G5** — let CODESYS resolve paths instead of parsing file names |
| `parent`, `index`, `guid` properties | G1–G2 bookkeeping |
| `ScriptTaskObject`: `set_core_binding`, `set_priority`, `set_interval`, `set_interval_unit`, `watchdog`, `pous`, `event_pou_guid` | **G7 at the root** |
| `ScriptLibManObject`: `add_library`, `add_placeholder`, `remove_library` | not in scope — see [Not doing](#not-doing) |

> **Method note.** A string search of the binary reported that `move` did not exist. Reflection
> says it does. **Reflection is authoritative**, and the string search nearly wrote a wrong
> constraint into this plan. Anyone re-checking the API should reflect, resolving dependencies
> from `Common`, `Common32` and `PlugIns`.

**The `Core2` mutation is self-inflicted.** It happens only because task configuration is
round-tripped as native XML, and a typed API for exactly those fields has been there all along.

### Decisions taken

- **Renames and moves: implemented properly**, GUID preserved. A "detect and refuse" fallback was
  considered and is moot — the API exists.
- **Task Configuration: typed export *and* import.** Fixed at source rather than avoided.
- **G10: on export, keep the existing bytes when the new export is normaliser-equivalent.** The
  mirror stops churning; nothing about what CODESYS reads changes.
- **Deletions: named confirmation, no cap.** **Consequence, stated plainly:** empty the mirror and
  the dialog lists every object; one click removes them all. The remaining protections are the
  pre-import backup, the direction lock and the Phase 2 preconditions — not a threshold.

---

## Architecture: C# plans, Python executes

Today the script decides what to do. It should be told.

```
_manifest.json  ─┐
mirror files    ─┼──►  ImportPlanner.Plan()  ──►  ImportPlan { Update, Create, Delete, Rename, Move }
git (HEAD)      ─┘         (pure, C#)                        │
                                                             ├──► confirmation dialog names it
                                                             └──► job file → versio_import.py
                                                                   executes exactly this
```

**Why C#-side.** The confirmation dialog must name deletions *before* CODESYS starts, and it is
built in `SyncViewModel`. A plan discovered inside the script arrives 17–31 s too late to be agreed
to. It also makes the decision a pure function — unit-testable in milliseconds, the way
`CommitGate`, `ExportFormRules` and `SyncNarrative` already are.

**Side benefit.** `StDeclarationParser` and `PathMapper` currently have **no production caller**.
This gives them one, so the parity tests start protecting live code rather than a specification.

---

## Phase 0 — Empirical baseline against a real project

**No product code.** Runs on a **scratch copy** of `RefrigeratorControl.project` through
`tools/Run-Codesys.ps1`. Two purposes: confirm the API behaves as reflection claims, and establish
**which gaps are even reachable** — if CODESYS refuses to create a POU named `My.Thing`, G5 closes
as "cannot occur" rather than costing defensive code.

| # | Test | Answers |
|---|---|---|
| P1 | `rename` an object; re-read its GUID | Does identity survive? The whole design rests on it |
| P2 | `move` an object to another folder; re-read GUID and `index` | Same, plus what `new_index` means |
| P3 | `remove()` an object another object references | Throws, cascades, or dangles? |
| P4 | `find([...namePath])` on a name needing percent-encoding | G4 — can CODESYS resolve what we cannot? |
| P5 | `create_pou` with `My.Thing`, `weird<name>`, trailing space | **Reachability** of G3/G4/G5 |
| P6 | Read `core_binding`/`priority`/`interval` via `ScriptTaskObject`; set one back | G7 — is the typed path sufficient? |
| P7 | **Three consecutive import→export cycles, unchanged mirror** | **G10** — does `UniqueIdGenerator` climb each time, or settle? |
| P8 | Deep folder chain near 260 characters | G9 — where it actually breaks |
| P9 | A fixed-version (non-placeholder) library reference | G8 — exercises `managed_library` |

**P7 decides whether Phase 5 is needed at all.** If the counter settles after one cycle, the fix is
smaller than assumed. If it climbs, the keep-existing-bytes rule is justified by measurement rather
than by argument.

---

## Phase 1 — Manifest-driven importer *(foundation; G3–G6)*

**New `src/Versio.Core/Projects/ImportPlanner.cs`** — pure:
`ImportPlan Plan(ProjectManifest manifest, IReadOnlyList<MirrorFile> mirror)`

- Manifest path present in mirror → **Update** (GUID, the manifest's verbatim `name`, `type`,
  `form`, `parent`).
- Mirror path absent from manifest → **Create** (name from `StDeclarationParser`, falling back to
  `PathMapper.DecodeStem`; warn when they disagree — that is G3).
- `form == LIBRARIES` or a not-importable type → **skip, announced**.
- Neither object nor bookkeeping → **skip with a warning**, never `import_native` (G6).
- Manifest below `ExportCoverage.ReportingSchema`, or absent/unparseable → plan **Update and Create
  only**, stating that deletes, renames and moves are unavailable until re-export. Never guess.

**Script:** resolve with `find(namePath)` using the manifest's real names (G4, G5).
`create_textual` takes its name from the plan (G3).

**Job file:** `CodesysJob` gains `plan`; bump `schema` 1 → 2, and refuse an unknown schema rather
than half-executing it.

**Files:** `ImportPlanner.cs` (new), `CodesysJob.cs`, `versio_import.py`, `versio_types.py`.

---

## Phase 2 — Deletes *(G1)*

**Delete set:** manifest entries whose file is gone from the mirror **and** whose object still
exists in the project. The preconditions are correctness, not caps — they are what make the delete
set *mean* anything:

- **Only objects the manifest recorded.** One added in the IDE since the last export is not in the
  manifest, so its absence from the mirror is not evidence of a deletion. Never touched.
- **A missing or unparseable manifest disables deletion entirely**, with a stated reason.
- **Content check before removal, in the script:** if the live object no longer matches the
  manifest's recorded hash, it changed since the export — refuse *that* deletion and warn.
- A GVL is two files under one GUID: delete only when **every** file for that GUID is gone.

**Confirmation:** extend `PendingImportDetail` in `SyncViewModel` to name each deletion —
`FB_Motor · POU`, resolved through `ObjectIndex` the way changed files already are.
`DestructivePrompt.Body` is free text, so no model change is strictly required.

**Script:** `obj.remove()` for exactly the planned deletions, emitting `{"t":"removed"}` — the event
`versio_delete_objects.py` already uses.

---

## Phase 3 — Renames and moves *(G2)*

**Detection in `ImportPlanner`, in order:**

1. **Exact content match** — a Create candidate whose hash equals a Delete candidate's manifest
   hash. Covers rename-without-editing and needs no old content.
2. **Similarity, when the mirror is committed** — fetch the old content with `git show HEAD:<path>`
   for the rename-and-edit case. `DetectRenamesAsync` is *not* usable here: it runs
   `git diff HEAD`, which sees only tracked files, so a freshly created rename target is invisible
   to it.
3. Unmatched → plain Delete + Create.

Same directory, different name → **Rename**. Same name, different directory → **Move**.

**Execution:** `obj.rename(new_name)` and `obj.move(new_parent, new_index)`. The GUID is preserved,
so references, task assignments and visualisation bindings survive — which is the entire reason for
doing this rather than delete-plus-create.

**Files:** `ImportPlanner.cs`, `IGitClient`/`GitCliClient` (a `ShowAtHeadAsync` if absent),
`versio_import.py`.

---

## Phase 4 — Task configuration, typed both ways *(G7)*

Mirrors the library-reference design exactly, which is the precedent to copy:

- **New `versio_tasks.py`** — pure, no `scriptengine` import, handed objects and a `safe` probe,
  exactly like `versio_libraries.py`. Reads priority, interval, interval unit, core binding,
  watchdog, POUs and event POU GUID.
- **Rendered through the existing `versio_yaml.document(...)`** — already vendor-neutral, already
  deterministic, already has key-order discipline.
- **New form `TASK_BOTH`**, following `GVL_BOTH`: native `.xml` for fidelity *and*
  `<name>.tasks.yaml` for readability. The **native XML becomes export-only**; the YAML is the
  importable form. That is what stops `import_native` touching task config, and therefore what
  stops the `Core2` mutation.
- **Import** through the typed setters, so the mirror's value wins and the project converges to
  what was reviewed.
- **New `src/Versio.Core/Projects/TaskCatalog.cs`** — a C# reader mirroring `LibraryCatalog`, so the
  panel can show tasks.

Task configuration stops being an opaque XML blob in the diff, which is the point of the tool.

---

## Phase 5 — A mirror that stops churning *(G10)*

In `versio_export.py`, when writing a native file: if the freshly exported payload is
**`NativeXmlNormalizer`-equivalent** to the file already at that path in the *current* mirror, write
the **existing bytes** into staging instead of the new ones.

- The comparison logic exists and is tested. What is new is a python-side equivalent, so it joins
  the parity discipline — `versio_native_equiv.py` ↔ `NativeXmlNormalizer`, compared by output in
  `PythonFileNameParityTests`.
- Read the old file from `out_root` **before the swap**; staging is empty by construction.
- **The determinism gate still holds.** Two exports into two empty directories stay byte-identical,
  because neither has an existing mirror to preserve. Only export-into-an-existing-mirror is
  affected, and that is the case that matters.
- Gated by P7: if the counter settles on its own, this phase shrinks.

---

## Phase 6 — Remaining gaps, tests and docs

- **G8** — a fixture variant carrying at least one fixed-version reference, asserting the
  `managed_library` branch. If it cannot be produced quickly, mark the branch unproven in
  `versio_libraries.py` and stop.
- **G9** — guard the **total** path in the exporter, measured against the staging path since it is
  the longer of the two. Shorten deeper segments or adopt `\\?\`; warn rather than fail silently.
- `ImportPlannerTests` — pure and exhaustive: delete, rename, move, rename-and-edit, GVL two-file
  GUID, stale manifest, missing manifest, object-added-in-IDE, encoded names, dotted names.
- Extend `PythonFileNameParityTests` with an **`StTextCodec` ↔ `versio_text` codec parity test**.
  `RunPython` already takes a snippet and a JSON payload, so this is a few lines — and it is the
  highest-consequence uncompared pair in the repo, since a bug in `split_st` silently moves code
  between a declaration and an implementation.
- Integration, on a copy of the fixture: delete an object, rename one, move one, change a task
  priority, and assert the project afterwards — including that GUIDs survived.
- Docs: [`CODESYS.md`](CODESYS.md) §1.3 (the API this uncovered), §3.1 (job schema 2), §5.2
  (`TASK_BOTH`), §6 (plan-driven import), §7 (G10, and what "deterministic" now means), §12 D1–D6
  resolved rather than described. [`PHASE0-FINDINGS.md`](PHASE0-FINDINGS.md) gate 0.5b annotated:
  the `Core2` deviation is fixed, not tolerated.

---

## Verification

1. `dotnet test tests\Versio.Core.Tests` — pure, ~0.3 s. Carries the bulk of the new coverage.
2. `dotnet test tests\Versio.Integration.Tests --filter PythonFileName` — parity, ~9 s.
3. `tools\Test-RoundTrip.ps1 … -MutateFirst` — the per-layer verdict stays **PASS**, and
   `Task Configuration.xml` drops out of the native-differs list once Phase 4 lands.
4. **The G10 acceptance test:** three consecutive import→export cycles on an unchanged mirror must
   leave it **byte-identical**. This is what turns "import is deterministic" into a measured claim
   rather than an opinion.
5. **Regression gate:** a normal export stays byte-identical to the committed mirror for every file
   Phase 4 does not deliberately change.
6. Full integration suite against real CODESYS (~7 min).

Build with `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"` — `dotnet` on PATH is runtime-only.
Close Versio.exe first; it locks its own DLLs.

---

## Risks

- **Deletion is irreversible in the project.** Mitigated by the pre-import backup (ten copies), the
  confirmation naming every object, and the content check — not by a cap, which was declined
  deliberately.
- **A stale manifest is the main correctness dependency.** The content check catches the common
  case; the direction lock and commit gate cover the rest. Confirm during Phase 1 exactly when the
  manifest is refreshed relative to an import.
- **`remove()` behaviour with referenced objects is unknown** until P3 answers it.
- **Phases 4 and 5 move committed bytes** — Phase 4 adds a `.tasks.yaml` per task configuration,
  Phase 5 changes when files are rewritten. Each wants its own review.
- **Scope.** Phases 1–3 are worth landing together. Phases 4 and 5 are separable and could ship
  after.

## Not doing

- **Two-way library references.** `add_library` / `add_placeholder` / `remove_library` exist, so
  export-only is now a *choice* rather than a limitation — worth revisiting, but it rewires what the
  machine compiles against and deserves its own change.
- Making the import transactional — the pre-import backup covers it, and it is a much larger change.
- Any change to the export layout beyond Phase 4's additive `.tasks.yaml`.
