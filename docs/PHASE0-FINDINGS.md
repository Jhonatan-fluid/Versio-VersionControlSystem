# Phase 0 — Findings

**Date:** 2026-08-11
**Fixture:** `RefrigeratorControl.project` (383 KB, 25 objects)
**Environment:** CODESYS 3.5.22.20, profile `CODESYS V3.5 SP22 Patch 2`, .NET SDK 9.0.317

---

## Verdict: **GO.** Every gate passed.

| # | Gate | Result |
|---|---|---|
| **0.1** | Headless export works | ✅ exit 0, no hidden dialog, no hang |
| **0.2** | **Export is deterministic** | ✅ **byte-identical across 3 runs, all 10 files (2.9 MB)** |
| 0.3 | GUIDs stable, not rewritten | ✅ preserved; only `Timestamp` scrubbed (9 fields) |
| 0.4 | Per-object granularity | ✅ tree mirrored as folders, one file per object |
| 0.6 | Coverage report | ✅ 18 walked, 10 written, 6 skipped, 0 unknown types |
| 0.7 | Cold-start cost | ⚠️ **17–31 s per invocation** — see below |
| 0.10 | SP22 API matches SP11 docs | ✅ `export_native`, `textual_*`, `get_children` all as documented |
| 0.15 | ST round-trip | ✅ verified in code + unit tests |
| **0.5** | **Round-trip losslessness — textual path** | ✅ **proven with a real mutation** (see below) |
| 0.5b | Round-trip — native XML path | ⚠️ **not yet tested** — 8 of 10 files skipped by the importer |
| P7 | Import → export churn over 3 cycles | ⚠️ **climbs +3 per cycle, never settles** — and the import itself fails from cycle 2. See below |

---

## Findings that changed the plan

### 1. The profile name cannot be guessed from the install folder

Install directory is `CODESYS 3.5.22.20`; the profile is **`CODESYS V3.5 SP22 Patch 2`**. Guessing "Patch 20" from the folder fails.

Worse: **`--noUI` refuses to run at all without `--profile`.** It exits 1 with
`When running in --noUI mode, you must specify a profile using --profile="profile name".`

Discovery method that works: `C:\Program Files\CODESYS <ver>\CODESYS\Profiles\*.profile.xml`, or the Start Menu shortcut name. Both gave the correct string.

### 2. Two object-type GUIDs are missing from codescribe's table

Probing the real project produced two `UNKNOWN` types:

| GUID | Object |
|---|---|
| `40b404f9-e5dc-42c6-907f-c89f4a517386` | **Plc Logic** |
| `639b491f-5557-464c-af91-1471bac9f549` | **Application** |

codescribe locates Application *by name* (`find("Application")`), which breaks if a project renames the node. Both are now in `versio_types.py` and `ObjectType.cs`, marked `[VERSIO]`, with a parity test asserting the two tables cannot drift.

### 3. Determinism needs only the Timestamp scrub — confirmed

Three consecutive exports were byte-identical, including a 2.5 MB visualisation XML. 9 `Timestamp` fields were zeroed; GUIDs and Ids were left untouched, exactly as codescribe's warning advised.

**R1 (export non-determinism) is retired with evidence**, not argument.

### 4. Cold start is real: 17–31 s

Measured: 31.5 s first run (cold), 17.6 s subsequent. Every export pays it.

~~**This supports making `On git action` the default sync mode** rather than export-on-save.~~ **The measurement stands; the conclusion was re-reversed by the product owner on 2026-08-12.** Exporting on every detected change is the default, because the mirror exists to be reviewed *before* a commit. The cost is managed by the coalescer and a 10 s minimum interval rather than avoided. An automatic export per save means a CODESYS process running most of the working day. (Plan §7.4.6, open question 11.)

### 5. PowerShell's `-Encoding utf8` writes a BOM that IronPython cannot read

`Out-File -Encoding utf8` in PS 5.1 emits a BOM; `json.load` in IronPython 2.7 fails with
`ValueError: No JSON object could be decoded`.

Fix: `[System.IO.File]::WriteAllText(path, json, UTF8Encoding($false))`. Worth remembering — this will recur anywhere the C# host writes a file the scripts read.

### 6. Real coverage on this project

```
POU x3          Simulation → .st (textual)
                PLC_PRG, Signals → .xml (graphical: no textual implementation)
GVL x1          Glob_Var → .gvl.st + .gvl.xml
VISUALISATION x3 → .vis.xml   (Live_Visu alone is 2.5 MB)
TASK_CONFIGURATION → .xml recursive
GLOBAL_TEXTLIST → .xml
skipped: VisualisationManager, Target/WebVisualisation, VisualisationStyle x2,
         LibraryManager x2, ProjectSettings, Task x2, CallToPou x3
```

The per-type dispatch was exercised for real: **2 of 3 POUs are graphical** and correctly fell back to native XML rather than being written as `.st` with their layout silently discarded.

> **Later annotation (2026-08-12).** Read that skip list again: `LibraryManager x2`. The
> evidence that library references never reach the mirror was sitting in this section on the
> day the gate passed, and nobody drew the conclusion — because the list does not separate a
> service object the template carries from an object nobody has written the code for. Both
> read as "skipped". The export now records a reason per skip and writes `_coverage.md`, and
> `docs/STATUS.md` carries the finding. Gate 0.6 was recorded as a pass, and on its own terms
> it was one; it was measuring that the walk completed, not that the output was sufficient.
>
> **Closed, same day.** `LibraryManager x2` is now `LIBRARIES x2` — one
> `<manager>.libraries.yaml` beside each manager, carrying 21 and 13 references respectively.
> Re-measured coverage on this project: **12 files written, 4 skipped, 0 gaps**, and two full
> exports byte-identical across the whole tree. The measurement that mattered was taken before
> a line of the exporter was written: `ref.id` is re-minted on every open (34 of 34 changed
> across two runs), so it is not recorded — see STATUS.

Export size: 2.9 MB of text from a 383 KB binary — native visualisation XML dominates.

---

## Gate 0.5 — round-trip losslessness (textual path)

**Result: PASSED, non-vacuously.**

The first run of the harness reported `Lossless: True` and it **meant nothing**: the import summary read `updated: 0, unchanged: 1`, with 8 files skipped. The import had done essentially nothing, so re-exporting trivially reproduced the baseline. A green result that cannot fail is worse than a red one.

Re-run with `-MutateFirst`, which edits a comment into an exported `.st` before importing:

```
MUTATING Simulation.st
import  -> {"updated":1,"unchanged":1,"missing":0,"failed":0}
compare -> 10 files, 0 differing
PROBE SURVIVED the round trip
```

- `Simulation.st` baseline and round-trip: **identical SHA-256**, 1450 bytes both
- The probe comment came back **out of CODESYS verbatim**, in the correct position beneath the delimiter
- The whole export tree was byte-identical afterwards

So for objects with a textual form, `export → edit → import → export` is **lossless and measured**, not asserted.

**The importer's design is why.** Rather than codescribe's remove-and-recreate cycle, ST objects are located in the tree and updated **in place** via `textual_declaration.replace()` / `textual_implementation.replace()`. The object keeps its GUID and everything CODESYS hangs off it. No conflict resolution, no deletion, no `import_native` paste semantics. This is both safer and simpler than the plan assumed.

---

## Gate 0.17 — `NativeImportHandler` replaces in place ✅

**codescribe's central assumption is wrong when you supply a handler**, and that removes the most dangerous step in the whole import design.

They report that `import_native` pastes rather than replaces, and work around it by **removing the existing object first**. That is only true of the *default* handler — they pass `handler=None`. Probed on 3.5.22.20 with a custom `NativeImportHandler` returning `NativeImportResolve.replace`:

```
verdict             REPLACED_IN_PLACE
childCountBefore    11        childCountAfter  11
added               []        removed          []
targetGuidBefore    b18a62c7-546d-4b2a-a116-1c9d0f44cbbb
targetGuidAfter     b18a62c7-546d-4b2a-a116-1c9d0f44cbbb
guidPreserved       true      conflictCallbacks 1
```

**No removal. No duplicate. GUID preserved.** The same call also *creates* an object that does not exist yet, so it solves object creation for native types at the same time.

Also settled: **`import_native([path], filter, handler)` — a LIST — works first try.** The reported SP21 failure (`Could not cast: System.String in System.Char`) is the bare-string overload being iterated into characters, exactly as suspected.

With this wired in, the importer went from 8 skipped files to `native: 8, failed: 0, warnings: 0`.

---

## Gate 0.5b — native round-trip is semantically lossless, but **not byte-stable**

Re-exporting after a native import produced **5 of 10 files differing at identical byte lengths**. Characterising every differing line showed three volatility classes, none of them semantic:

| Class | Example | Scale |
|---|---|---|
| **Counters** | `UniqueIdGenerator` 26 → 29 | 1 per visualisation |
| **Unordered collections reordered** | `UseInitValue` and `DescriptionTextId` swapped, same content | ~58 of 107 lines in one visu |
| **Internal reference GUIDs re-minted** | `EventPOUGuid`, `GuidInit`/`GuidReInit`/`GuidExitX` | 49 of 107 lines in one visu |

`NativeXmlNormalizer` masks these **for comparison only — never on disk**, and resolves **4 of the 5 files**. (Note the distinction from codescribe's warning: they broke visualisations by *rewriting* GUIDs in the exported file. This masks them in memory during a comparison; the bytes on disk keep their real GUIDs.)

**Consequence for the design: the round-trip fidelity check of §3.4 cannot be a byte comparison for native objects.** It must be semantic. The export direction is unaffected — repeated exports remain byte-identical, so git diffs stay clean.

### ⚠️ One real deviation that is NOT volatile

`Task Configuration.xml` still differs after normalisation, on exactly one line:

```
Core2: -1  ->  -2      (directly beside an unchanged  Core: -1)
```

That is a task's **CPU core affinity**, and `import_native` changed it without being asked. It is deliberately **not** masked. It is pinned by a test asserting the deviation is *exactly* this one field, so the suite fails the moment it grows.

**Recommendation: treat Task Configuration as diff-only (Tier 3) until this is understood on more projects.** It may be a benign sentinel normalisation, but "probably benign" is not a basis for writing to someone's task scheduling.

> **Superseded on 2026-08-15 by P7 below.** Diff-only is not a safe resting place: the very
> next import of a mirror this deviation produced **fails**, and the failure is silent in the
> summary. The recommendation now is Phase 4 of [`CODESYS-IMPORT-PLAN.md`](CODESYS-IMPORT-PLAN.md)
> — typed task configuration, so `import_native` never sees this file again.

---

## P7 — the churn does not settle, and it is narrower than G10 assumed

**Measured 2026-08-15**, `tools/Measure-ImportStability.ps1`, on a scratch copy of the fixture.
Three consecutive import → export cycles over **one** mirror directory, re-exported into,
**nothing edited between cycles**. Every difference below is therefore produced by the round
trip and by nothing else, which is the whole reason for holding the mirror still.

This is probe P7 of [`CODESYS-IMPORT-PLAN.md`](CODESYS-IMPORT-PLAN.md), and it exists to decide
one thing: whether the counter climbs forever or settles the way `Core2` does. Until now that
was an argument.

```
Cycle  Textual  ExportOnly  Native  Bookkeeping  Structural
  1       0          0        5          1           0
  2       0          0        4          1           0
  3       0          0        4          1           0
```

| File | `UniqueIdGenerator`, by cycle | |
|---|---|---|
| `Diagnosis.vis.xml` | 44 → 47 → 50 → 53 | **climbing** |
| `Live_Visu.vis.xml` | 26 → 29 → 32 → 35 | **climbing** |
| `Visualization.vis.xml` | 26 → 29 → 32 → 35 | **climbing** |
| `PLC_PRG.xml` | 347 → 347 → 347 → 347 | flat |
| `Signals.xml` | 149 → 149 → 149 → 149 | flat |

**Verdict: it climbs, linearly, +3 per cycle, and never settles.** G10 is measured rather than
argued, and Phase 5 is justified.

Three things the measurement changed:

- **Only visualisations climb, and the churn set is smaller than G10 claimed.** G10 said an
  import leaves a diff in "every native file it touched". It touches **eight**, and they split
  four ways:

  | | Files |
  |---|---|
  | climb on the counter, every cycle | `Diagnosis.vis.xml`, `Live_Visu.vis.xml`, `Visualization.vis.xml` |
  | churn every cycle with **no counter** | `GlobalTextList.xml` |
  | move once, then settle | `Task Configuration.xml` |
  | byte-stable throughout | `Glob_Var.gvl.xml`, `PLC_PRG.xml`, `Signals.xml` |

  So the steady state is **four of eight, forever** — not all eight, and not only the ones with
  a counter in them.
- **`Task Configuration.xml` settles after one cycle.** `Core2` −1 → −2 plus two `EventPOUGuid`
  re-mints in cycle 1, then byte-identical in cycles 2 and 3. It is the one file that behaves
  the way the counter was assumed to.
- **`GlobalTextList.xml` churns every cycle and carries no counter at all.** Its diff is three
  lines — `GuidInit`, `GuidReInit`, `GuidExitX` — which is the re-minted-internal-GUID class
  above, at identical byte length. So the steady-state churn has **two** causes, not one, and
  **a fix aimed at the counter would leave a file diffing forever.** This is the concrete reason
  Phase 5's rule has to key on normaliser-equivalence rather than on the counter, and it is
  worth more than the counter measurement it came in beside.

**And the textual path held.** Zero `.st` differences across three round trips, zero
export-only differences, and nothing added or removed at any cycle. Gate 0.5 was proven once
with one mutation; this is the same claim survived three times unattended.

### ⚠️ The import fails from cycle 2 onward, and says it succeeded

Not what P7 went looking for, and more important than what it did.

Cycle 1 imports the pristine mirror: `native: 8, failed: 0, warnings: 0`. Cycles 2 and 3 import
a mirror that a previous round trip produced, and **both throw twice**, on `MainTask` and
`VISU_TASK` — the two children of `Task Configuration`:

```
System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
  at _3S.CoDeSys.Utilities.LDictionary`2.ValueCollection.Enumerator.MoveNext()
  at _3S.CoDeSys.ObjectManager.Project.GetUniqueName(...)
  at _3S.CoDeSys.ObjectManager.AbstractPasteOrMergeOperation.DoExecuteObject(...)
```

That is the **exact** exception `versio_import.py`'s `run()` docstring names, and the two-phase
split with a save between them was written to prevent it. It did not, because the save is
conditional on `created > 0 or updated > 0` and both were **0 in every cycle here** — an import
that only touches native files never saves between the phases at all, so the guard the docstring
describes did not run once during this measurement.

Nothing is destroyed: the objects survive and the file is simply not applied. What is wrong is
what the run then reports. `ReplaceHandler.progress` warns and never increments `self.failed`,
and `handle_file` increments `self.native` regardless — so the summary reads `native: 8,
failed: 0` on a run where two objects failed. The warnings themselves do reach the log at `Warn`
through `SyncOrchestrator.LogWarnings`, so it is not silent; the one-line answer is just wrong,
and the one-line answer is what gets read.

**This is the same defect as `Core2`, seen from the other end.** Both exist only because task
configuration is round-tripped as native XML. Phase 4 removes the file from `import_native`'s
reach and takes both with it — which is why it is no longer a phase that can ship later.

---

## A data-loss bug the round-trip harness caught

`Glob_Var.gvl.xml` was **238 bytes of structured text** — not XML at all.

`FORM_GVL_BOTH` was missing from the python `SUFFIX` table, so `file_name_for()` fell through to the `.xml` default. A GVL writes two files; both resolved to the same path, and the ST write **destroyed the native export**, leaving a file whose extension lied about its contents.

The uncomfortable part: **the C# `PathMapper` had this right**, with a passing test (`A_gvl_writes_both_forms_under_distinct_names`) asserting `.gvl.st` ≠ `.gvl.xml`. Only the python had drifted — and the parity test compared **GUID tables only**, so it sailed straight past. Parity tests now cover the `SUFFIX` and `INFIX` tables too.

Export went from 10 files to 11 once fixed.

---

## Three real bugs the unit tests found

All three were found by tests, not by running the exporter. Each would have been a silent failure in production.

### Bug 1 — delimiter inside a declaration corrupted the split ⚠️

A randomised round-trip test generated a declaration containing the literal
`// --- BEGIN IMPLEMENTATION ---`. Split cut at the first occurrence — inside the declaration — **silently moving code from the declaration into the implementation.**

codescribe has the same shape of bug: its `declaration, implementation = content.split(...)` raises `ValueError` when a second delimiter appears.

Fixed with a self-escaping scheme (`//!VERSIO-ESC!`) applied to both halves. Ordinary files are untouched — verified: the escape marker appears nowhere in real export output, and re-running the export after the fix produced byte-identical files.

### Bug 2 — coalescer livelock 🔁

The cancel-and-restart window was measured from the *last restart*. Saves arriving faster than the window (every 1 s against a 5 s window) restarted the export **forever — it would never complete.**

Fixed by measuring from the start of the *burst*. Thrash is now bounded by the cancel window, after which it settles into queueing. Regression test: 60 saves at 1 s intervals produce at most 5 restarts.

### Bug 3 — double-reported casing mismatch

A binding whose on-disk path differed only in case was reported as *both* `CasingMismatch` and `OrphanedProject`, sending the user hunting for a second problem that did not exist.

---

---

## The C# runner — two bugs that only appear against the real thing

`Versio.Infrastructure` now drives CODESYS from C#. Both failures below passed every unit test and only surfaced when a real process was launched.

### 1. `ProcessStartInfo.ArgumentList` produces a command line CODESYS rejects

`ArgumentList` applies .NET's standard quoting, which wraps the **whole** argument when it contains a space:

```
"--profile=CODESYS V3.5 SP22 Patch 2"      <- .NET; CODESYS exits 1 in 0.6 s, no output
--profile="CODESYS V3.5 SP22 Patch 2"      <- what CODESYS actually parses
```

The quotes must wrap the **value**, not the pair. The failure is silent and fast — no error text, no events, just exit code 1 — which makes it very hard to attribute. The command line is now built by hand, which is affordable precisely because the job payload is a file: exactly one path crosses the boundary.

Also worth noting the CLI is not internally consistent: `--runscript=` takes an equals sign, `--scriptargs:` takes a colon. Both are pinned by tests.

### 2. `Mutex` has thread affinity and does not survive `async`/`await`

Acquiring the machine-wide mutex inside `Task.Run` and releasing it in a `finally` after an `await` lands on a *different* pool thread:

```
System.ApplicationException: Object synchronization method was called
from an unsynchronized block of code.
   at System.Threading.Mutex.ReleaseMutex()
```

The whole guarded section now runs on one dedicated long-running thread, so acquire and release happen on the same thread.

**A named `Semaphore` would have dodged the affinity problem and been the wrong fix**: it gives up abandonment recovery, so a process dying while holding it would leave the count permanently decremented and every future export on the machine would block forever. `Mutex` raises `AbandonedMutexException` instead — which is exactly the case that has to survive, since Versio's own crash-recovery story depends on it.

---

## What runs today

```
C:\GIT\Versio\
├─ src\Versio.Scripts.Codesys\  probe, export, import, types, text, decl  (+ 2 probe helpers)
├─ src\Versio.Core\             pure domain logic - no IO, no processes, no UI
│   ├─ Bindings\ Codesys\ Equivalence\ Locking\ Model\
│   └─ Serialization\ Sync\ Watching\ Abstractions\
├─ src\Versio.Infrastructure\   the only project that touches real processes and disk
│   ├─ Codesys\   LocalCodesysRunner, CodesysLocator, ScriptExtractor
│   └─ Platform\  SystemClock, MachineIdentity, ProcessProbe
├─ tests\Versio.Core.Tests\        302 unit tests, ~270 ms
├─ tests\Versio.Integration.Tests\  11 tests against real CODESYS, ~22 s
├─ tools\                       Run-Codesys.ps1, Compare-Exports.ps1, Test-RoundTrip.ps1
└─ fixtures\projects\           RefrigeratorControl.project
```

```bash
cd C:\GIT\Versio && dotnet test
```

The integration suite drives a real CODESYS install through `ICodesysRunner`:

```
An_export_runs_end_to_end_through_the_runner   PASSED  18.2 s
  exit=0  events=21  warnings=0
  summary: {"walked":18,"written":10,"skipped":6,"warnings":0}
```

It skips cleanly on a machine without CODESYS or the fixture, so the suite still runs anywhere.

---

## Still open

| Item | Status |
|---|---|
| **Task Configuration `Core2` mutation** | **Confirmed reproducible.** Investigate before enabling task-config import; diff-only until then |
| **Recreated objects get NEW GUIDs** | `create_pou` mints a fresh GUID, so anything referencing the old one (task config `EventPOUGuid`) is left stale. Fine when recreating a deleted object; needs thought for cross-machine sync |
| 0.16 gap types (Interface, TextList, ImagePool…) | Rules written and unit tested; **not exercised** — this project contains none |
| 0.18 `system.prompt_answers` | Not needed yet — no dialog encountered under 10 headless runs |
| 0.19 template generation | Not attempted |
| 0.20 corpus | 1 fixture. Needs a compound/safety project and one that is graphical-heavy |
| 0.8 licensing | No nag dialog or demo timeout across 10 headless runs |

## Object creation ✅ — including textual objects

`import_native` creates native objects for free. Textual ones now work too, and **without** the native-XML-seed workaround: everything `create_pou` needs is already stated in the ST declaration, so it is parsed rather than guessed.

`create_pou(name, type, language, return_type, base_type, interfaces)` is driven from a header parse: `PROGRAM` / `FUNCTION_BLOCK` / `FUNCTION x : INT` / `INTERFACE` / `TYPE … STRUCT|UNION|(enum)|alias`, plus `EXTENDS` and `IMPLEMENTS`. Attribute pragmas and comments are stripped first, since real declarations open with `{attribute 'qualified_only'}`. **An unrecognised header is reported, never approximated** — creating the wrong kind of object is worse than creating none. 30 unit tests, mirrored in `versio_decl.py`.

**Measured end to end:** deleted `Simulation` (ST program) and `Glob_Var` (GVL) from a project, then imported the export back.

```
created: 2   updated: 2   native: 8   failed: 0   warnings: 0
Simulation.st    identical = True   (1422 bytes)
Glob_Var.gvl.st  identical = True   (238 bytes)
```

Both objects were recreated and their content came back **byte-identical**.

### ⚠️ Import must run in two phases, and the failure it prevents is silent

The first attempt threw, twice:

```
System.InvalidOperationException: Collection was modified;
enumeration operation may not execute.
  at _3S.CoDeSys.ObjectManager.Project.GetUniqueName(...)
  at ...AbstractPasteOrMergeOperation.DoExecute(...)
```

Creating an object mutates CODESYS's internal name dictionary; running `import_native` immediately afterwards makes its conflict resolution enumerate that dictionary while it is still being modified.

**The dangerous part is not the exception — it is that `import_native` swallows it into the progress callback.** The import silently did nothing, the re-export therefore matched the baseline, and the run *looked like a clean pass*. It was only visible because the handler forwards callback exceptions as warnings.

Fixed by splitting the importer into **phase 1 (textual creates and updates) → save → phase 2 (native imports)**. Result: `warnings: 0`.

This also retroactively explains an earlier observation: a run where `Task Configuration.xml` came back identical did so **because its import had failed**, not because it round-tripped. With the ordering fixed and the import genuinely running, the `Core2: -1 → -2` mutation reappears — confirming it is real and reproducible, not intermittent.
