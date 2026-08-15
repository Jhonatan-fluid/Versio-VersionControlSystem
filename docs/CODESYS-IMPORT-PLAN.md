# Versio — closing the CODESYS import gaps

**Date:** 2026-08-15 · **Nothing built yet — Phase 0 is nine measurements, and it comes first**

| | |
|---|---|
| Document | **The plan for making the import side a real bidirectional sync.** Deletes, renames, moves, typed task configuration, and a mirror that stops churning after every import |
| Status | **Phase 0 started, no product code yet. P7 is done — see G10, G11 and [`PHASE0-FINDINGS.md`](PHASE0-FINDINGS.md).** Eight probes left; two of them may still close gaps outright rather than cost code |
| Related | [`CODESYS.md`](CODESYS.md) — how CODESYS is driven today, and §12, the audit these gaps came from · [`PHASE0-FINDINGS.md`](PHASE0-FINDINGS.md) — what was measured · [`PLAN.md`](PLAN.md) — the architecture · [`SAFEGUARDS.md`](SAFEGUARDS.md) — what Versio refuses or waits for |

---

## Why

The export side is solid — deterministic, byte-identical across runs, nothing silently dropped.
The **import** side only ever updates and creates.

Ten gaps came out of auditing it ([`CODESYS.md`](CODESYS.md) §12). Nine share one root cause:
`versio_import.py` walks the **mirror filesystem** and infers everything from **file names**, never
reading `_manifest.json`. The tenth is about what `import_native` does to the bytes on the way back.

**An eleventh came out of measuring rather than auditing.** P7 ran the round trip three times
instead of once, and the second one failed — see G11. That is the argument for probes over
inspection in one line: ten gaps were found by reading the code carefully, and the eleventh was
invisible to any amount of reading because it only exists on the *second* cycle.

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
| **G10** | **Import → export is not byte-stable** | `UniqueIdGenerator` **44 → 47 → 50 → 53**, +3 per cycle, measured over three cycles by P7 on 2026-08-15. It never settles. **Two corrections to the original wording**, both from the same measurement. Of the **eight** native files the import touches: three visualisations climb on the counter, `GlobalTextList.xml` churns every cycle with **no counter in it at all** (re-minted `GuidInit`/`GuidReInit`/`GuidExitX`), `Task Configuration.xml` moves once and then settles, and `Glob_Var.gvl.xml`, `PLC_PRG.xml` and `Signals.xml` are byte-stable throughout. So it is **four of eight forever, not "every native file it touched"**, and the counter is **one of two causes, not the cause** |
| **G11** | **Every import after the first fails on task configuration, and reports success** | Found by P7, not predicted here. `import_native` throws `Collection was modified` at `Project.GetUniqueName` on `MainTask` and `VISU_TASK`; the summary still reads `failed: 0`. **Isolated to `<project>_project.precompilecache`** — the cache alone is sufficient, the `.opt` files are innocent, and the mirror's bytes are irrelevant. A fresh clone has no cache, so the first import works and every later one does not. **Not the same root cause as G7**, which is what this row claimed before it was tested |

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
| ~~P7~~ | ~~**Three consecutive import→export cycles, unchanged mirror**~~ | ✅ **done 2026-08-15 — it climbs, +3 per cycle, forever.** `tools/Measure-ImportStability.ps1`. Also found G11 |
| P8 | Deep folder chain near 260 characters | G9 — where it actually breaks |
| P9 | A fixed-version (non-placeholder) library reference | G8 — exercises `managed_library` |

~~**P7 decides whether Phase 5 is needed at all.**~~ **It did, and Phase 5 stays.** The counter
climbed 44 → 47 → 50 → 53 with nothing edited between cycles, so the keep-existing-bytes rule is
justified by measurement. What P7 also showed is that Phase 5 must key on
**normaliser-equivalence and not on the counter**: `GlobalTextList.xml` churns every cycle and has
no counter in it at all.

~~**Still to measure, and it needs no CODESYS:**~~ **Measured.** `NativeXmlNormalizer` over
`phase0/p7/snapshots/002` and `003`: 9 files byte-identical, **4 equivalent — every churning one**,
and only `_manifest.json` left over, which is derived from the four. The reasoning it replaces was
sound and was still reasoning. See Phase 5, including the manifest-hash trap the run exposed.

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
  **For a textual object that is a byte comparison and P7 proved it holds** — zero `.st`
  differences across three round trips. **For a native object it cannot be**, and this was
  nearly written as though it could: the manifest's `hash` is `content_hash(text)` over the
  *exported file bytes* (`versio_export.py:183`), so checking a live object against it means
  exporting that object again — and P7 measured that re-export changing every single cycle
  with nothing edited. A byte check would refuse every native deletion forever, for a reason
  that has nothing to do with the object. **The native half must compare through the
  normaliser, which makes `versio_native_equiv.py` a Phase 2 dependency rather than a Phase 5
  one.**
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

## Phase 4 — Task configuration, typed both ways *(G7, G11)*

> **Written 2026-08-15, then corrected the same day.** The first version of this note said P7
> had shown this phase to be the fix for G11, and reprioritised it on that basis. **That was
> wrong**, and it was wrong because it was written from one run before anything varied a
> variable: G11 is caused by a stale `_project.precompilecache`, not by task configuration
> being native XML, and a round-tripped `Task Configuration.xml` imports cleanly into a project
> that has no cache beside it.
>
> **What remains true** is that `MainTask` and `VISU_TASK` are the objects that fail, and they
> are reached only through this file — so taking it off the `import_native` path does remove
> *those two* errors. Whether the underlying fault then surfaces on another object is
> **untested**: the attempt to test it could not reproduce the failure at all, because the
> throwaway project it built had no cache beside it. That experiment is worth repeating now
> that the trigger is known.
>
> So this phase keeps its original priority and its original justification — G7, the `Core2`
> mutation — and G11 needs a fix of its own.

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
- ~~Gated by P7: if the counter settles on its own, this phase shrinks.~~ **P7 says it does not,
  and the rule is now measured to be sufficient rather than argued to be.** `NativeXmlNormalizer`
  over two steady-state snapshots of one unedited mirror: **all four churning native files come
  out equivalent** — the three visualisations and `GlobalTextList.xml` — while the other nine
  files are byte-identical and need no rule at all. Masked counts are symmetric on every file
  (695/695, 3656/3656, 521/521 and 30/30 GUIDs), which is what says the two sides differ in the
  masked positions and nowhere else.

> **The trap this measurement exposed, which is not in the sketch above.** `record()` hashes the
> payload the exporter *generated* — `h = content_hash(text)` — and `write_text` writes that same
> `text`. The two agree by construction today, and `write_text`'s docstring states the invariant
> outright: *"The exporter hashes what it writes."* **Phase 5 breaks it.** Writing the existing
> bytes while the manifest has already recorded the new payload's hash leaves a manifest that
> disagrees with the file beside it — and Versio's hash gate reads the bytes on disk, so the
> disagreement is not cosmetic. `_manifest.json` is the one file still differing between the two
> snapshots, and it will only settle if the recorded hash follows the bytes actually written.
> **So the substitution has to happen before the hash is taken, not at write time**, which is a
> different shape from "when writing a native file, write the existing bytes instead".

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
   rather than an opinion. **The harness exists and the baseline is recorded** —
   `tools\Measure-ImportStability.ps1`, which today prints `P7: CHURNS` with four files differing
   per cycle. The acceptance criterion is the same command printing `P7: SETTLES`. Note that it
   must also come back with **zero warnings**, or G11 is still there underneath a clean byte
   comparison.
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
- **Scope.** Phases 1–3 are still worth landing together. **One half of the original claim has
  moved:** Phase 2 needs `versio_native_equiv.py`, which was Phase 5's artifact, so those two
  are no longer separable in that direction. Phase 4 keeps its place — the claim that it had
  become urgent rested on a root cause for G11 that turned out to be wrong. The ordering wants
  rewriting once the remaining probes have run, deliberately not now, because P1–P6 may move it
  again.
- **G11 needs an owner and is not in any phase here.** The trigger is known
  (`_project.precompilecache`) and **deleting it immediately before an import is measured to fix
  it** — the import then regenerates the cache itself, so nothing is lost, and an export
  afterwards is identical bar the GUIDs a *successful* native import re-mints. What is not
  decided is whether Versio should be deleting another tool's files on that evidence. The
  `.gitignore` half is done: `TransientPatternsFor` now names the cache.

## Not doing

- **Two-way library references.** `add_library` / `add_placeholder` / `remove_library` exist, so
  export-only is now a *choice* rather than a limitation — worth revisiting, but it rewires what the
  machine compiles against and deserves its own change.
- Making the import transactional — the pre-import backup covers it, and it is a much larger change.
- Any change to the export layout beyond Phase 4's additive `.tasks.yaml`.
