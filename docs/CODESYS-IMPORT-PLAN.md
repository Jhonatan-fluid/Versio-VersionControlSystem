# Versio — closing the CODESYS import gaps

**Date:** 2026-08-15 · **Phases 0, 4, 1 and 2 are built and Phase 2 is COMPLETE**, along with
`versio_native_equiv.py`, the shared prerequisite Phase 5 also needs. **Versio deletes objects
now — textual and native alike.** G1 is closed outright: delete a `.st` and the POU goes,
delete a `.gvl.xml` and the GVL goes, and an object edited in the IDE since the export is kept
either way. Proven against real CODESYS four ways, including one that makes a real edit and
demands the deletion be refused. Phase 4 closes G7 and G11; Phase 1 closes G3 and G6 outright,
G4's resolution half and G5's recorded-object half. All three honesty fixes are done.

**Phase 3 is next** — renames and moves — and nothing blocks it. Phases 5 and 6 follow. One
item is open and it is not on the critical path: `TaskCatalog.cs`, so nothing outside python
can read a `.tasks.yaml`.

> **This header said "nothing in phases 1–6 is built" for the whole of the session that built
> Phase 4**, while the [build order](#build-order) below it and every G7/G11 row already said
> the opposite. It is the second time this file's header has claimed less than the file — commit
> `f391e55` fixed the first. A header is what a reader trusts before they have read anything, so
> it is the one line in a plan that cannot be left to catch up later.
>
> **Nearly a third time on 2026-08-15**: the header above was rewritten for Phase 2 and the
> **Status row two blocks below it still read "phases 2, 3, 5, 6 not started"**. Caught by
> re-reading the top of the file rather than by a test, because nothing can test this. **The
> header and the Status row are one claim in two places** — change either and check the other.
>
> **It held on the fourth pass**, when Phase 2 was completed: header, Status row, the G1 row
> and the build order were edited together and checked against each other afterwards. The rule
> works; it just has to be applied deliberately, because nothing will apply it for you.
>
> **And the same failure had already escaped this file into the product.** The confirmation
> dialog kept saying objects *will stay* for a commit after the script began deleting them —
> the identical "one claim in two places" defect, in the one place where a stale sentence
> destroys work rather than misleading a reader.

| | |
|---|---|
| Document | **The plan for making the import side a real bidirectional sync.** Deletes, renames, moves, typed task configuration, and a mirror that stops churning after every import |
| Status | **Phases 0, 4, 1 and 2 done and Phase 2 complete, with all three honesty fixes; phases 3, 5 and 6 not started.** Two gaps closed as unreachable, four API spellings corrected, six live defects fixed — G7 and G11 by Phase 4, G3 and G6 by Phase 1, **G1 by Phase 2 outright, textual and native**, with G4's resolution half and G5's recorded-object half beside them, and the confirmation dialog corrected after it spent a commit promising the opposite of what an import did. The import is **plan-driven end to end**: decided C#-side from the manifest, agreed to before CODESYS starts, refused without a plan, executed by a script that decides nothing — and it now **removes** objects as well as adding them, saying beforehand which ones will really go |
| Related | [`CODESYS.md`](CODESYS.md) — how CODESYS is driven today, and §12, the audit these gaps came from · [`PHASE0-FINDINGS.md`](PHASE0-FINDINGS.md) — what was measured · [`PLAN.md`](PLAN.md) — the architecture · [`SAFEGUARDS.md`](SAFEGUARDS.md) — what Versio refuses or waits for |

> **These phase numbers are identifiers, not an order**, and they are **not** `PLAN.md`'s phases —
> that document numbers the whole project 1–5 and means something different by every one of them.
> The order to build these in is in [Build order](#build-order), and it is not 1, 2, 3. The numbers
> are kept because `PHASE0-FINDINGS.md` and `STATUS.md` cite them, and renumbering would silently
> repoint every one of those citations.

---

## Why

The export side is solid — deterministic, byte-identical across runs, nothing silently dropped.
The **import** side only ever updates and creates.

Ten gaps came out of auditing it ([`CODESYS.md`](CODESYS.md) §12). Nine share one root cause:
`versio_import.py` walks the **mirror filesystem** and infers everything from **file names**, never
reading `_manifest.json`. The tenth is about what `import_native` does to the bytes on the way back.

**An eleventh came out of measuring rather than auditing.** P7 ran the round trip three times
instead of once, and the second one failed. That is the argument for probes over inspection in one
line: ten gaps were found by reading the code carefully, and the eleventh was invisible to any
amount of reading, because it does not exist on the first cycle.

| | Gap | State after Phase 0 |
|---|---|---|
| G1 | **Deletes don't propagate** — delete a `.st`, colleague imports, the POU is still there | ✅ **CLOSED OUTRIGHT by Phase 2, 2026-08-15, textual and native.** Delete a `.st` and the POU leaves the project; delete a `.gvl.xml` and the GVL leaves it too, because manifest schema 5 records the **normalised** hash of a native payload and a re-export can be compared against that. An object edited in the IDE since is kept either way — measured with a real edit, not a synthetic one. A native object on a pre-schema-5 mirror stays refused until the next export. P3 is why every rule is a refusal rather than a check: `remove()` dangles silently |
| G2 | **Renames/moves duplicate** — rename in the mirror, a new object is created and the old one kept | Open, and **cleanly solvable**: P1/P2 proved identity survives both operations |
| G3 | **New object named from the filename** — `create_textual` uses `parts[-1]` and throws away the parsed `info["name"]`, so `PROGRAM Foo` in `Bar.st` creates `Bar` | ✅ **CLOSED by Phase 1, 2026-08-15.** The plan carries the declaration's name and the script takes it; the disagreement is a warning naming both. **Proven live**: a `Bar.st` declaring `PROGRAM Foo` creates `Foo` against real CODESYS, in `ImportLoggingTests` |
| G4 | **Encoded names unresolvable** — looks up the *encoded* stem against raw names, giving a duplicate named `weird%3Cname%3E` | ✅ **Resolution CLOSED by Phase 1, 2026-08-15**: chains are real decoded names from the manifest, built C#-side where `DecodeStem` lives and is parity-tested. What remains is the shortened-stem residue — a digest tail decodes to nothing, and such an object is reported as unresolvable rather than guessed at. Creation was already unreachable (P5) |
| G5 | **A dot in a name breaks the owner split** — `My.Thing.st` looks for member `Thing` of parent `My` | **Unreachable by creation** (P5), and ✅ **the existing-object half CLOSED by Phase 1**: ownership comes from the manifest's parent-GUID chain, and for pre-schema-4 mirrors the recorded name disambiguates — a name equal to the whole stem is never split. Only a manifest-absent dotted *create* still splits, and P5 says CODESYS would refuse it anyway |
| G6 | **Stray files go to `import_native`** — a `README.md` in the mirror is handed to CODESYS as native XML | ✅ **CLOSED by Phase 1, 2026-08-15.** A file that is neither a recorded object, bookkeeping, nor a recognisable source is skipped with a warning and never handed to the toolchain — including unrecorded YAML, and files appearing between planning and execution (`NOT_IN_PLAN`). **Proven live** with a real `README.md` in `ImportLoggingTests` |
| G7 | **Task Configuration mutated on every import** — `Core2` `-1`→`-2`; documented as "diff-only", which it never was | ✅ **CLOSED by Phase 4, 2026-08-15.** `Core2` reads `-2,-1` across four consecutive snapshots where it used to move on the first cycle, and `Task Configuration.xml` has dropped out of the churn set entirely. **`-2,-1` is `MainTask,VISU_TASK`, and only the second number was ever the defect** — the baseline capture has MainTask at `-2` already, so **VISU_TASK's `-1` is the one G7 destroyed** and the one an integration test now asserts. Worth knowing before reading a diff of this file: a run where both read `-2` is the bug, not a run where either does |
| G8 | **Managed library references unproven** — every fixture reference is a placeholder, so the `managed_library` branch has never executed | **Half closed**: P9 produces a real managed reference in one call, so no hand-built fixture is needed. (This row used to say 34 references; the Library Manager reports **21**) |
| G9 | **Total path length unguarded** — stems capped at 80, the full path is not, and `.versio_staging` makes it 16 longer | Open, and **entirely ours**: P8 shows CODESYS imposes no limit of its own |
| **G10** | **Import → export is not byte-stable.** `UniqueIdGenerator` **44 → 47 → 50 → 53**, +3 per cycle, never settling | Open, **measured**. Of the eight native files touched: three visualisations climb on the counter, `GlobalTextList.xml` churns with **no counter at all**, `Task Configuration.xml` moves once then settles, three are byte-stable. **Four of eight forever, from two causes** — not "every file", and not just the counter |
| **G11** | **Every import after the first fails on task configuration and reports success.** `import_native` throws `Collection was modified` at `Project.GetUniqueName` on `MainTask` and `VISU_TASK`; the summary still reads `failed: 0` | ✅ **CLOSED by Phase 4, 2026-08-15.** Three import→export cycles with the compile cache present: **zero warnings on every one**, where cycles 2 and 3 used to throw twice each. `native: 8 → 7`, the task XML skipped as `TYPED_SIBLING`. **Versio never deletes CODESYS's compile cache** — the trigger is simply never reached |

**Intended outcome:** the mirror becomes authoritative for object *existence* and *identity*, not
just content; task scheduling stops being changed by a tool nobody asked to change it; and an
import that alters nothing semantically leaves no diff behind.

---

## What the ScriptEngine actually offers

Nobody had checked. Reflecting over `ScriptEngine3.dll` and `ScriptDriverProjects.plugin.dll`, the
API is **considerably richer than the scripts use**, and three gaps existed only because we never
looked.

**Every row has now been called** (P0–P9, 2026-08-15). The members are all there — and **four of
the seven spellings were wrong**, struck through below with what actually works. Reflection says a
member exists; it does not say what the member accepts, and that is where every error was.

| Member (`IScriptObject` unless noted) | Closes | Measured |
|---|---|---|
| `rename(stNewName)` | **G2** | ✅ works, and **the GUID survives a save/close/reopen** |
| ~~`move(new_parent, new_index)`~~ → **`move(new_parent, -1)`** | **G2** | ✅ GUID survives. **`new_index` must be `-1`** unless the parent has sorted children — `0` raises `ValueError: index must be -1 if the new parent node does not have sorted children`, and the one-argument form raises a `NullReferenceException` |
| `remove()` | **G1** | ✅ works — **and removes a referenced object silently.** See P3 |
| ~~`find(String[] namePath)`~~ → **`find("a", "b", "c")` varargs** | G4, G5 | ✅ varargs resolves a full path, spaces included. **A python list raises `TypeError: expected Array[str], got list`.** `find(name, True)` is recursive; `find(name)` alone returns an empty list |
| `parent`, `index`, `guid` properties | G1–G2 bookkeeping | ✅ all three read |
| ~~`ScriptTaskObject`: `set_core_binding`, `set_priority`, …~~ → **plain property assignment** | **G7 at the root** | ✅ all seven fields read. **`set_priority()` does not exist** (`AttributeError`). Assignment works, and **the types differ per field**: `priority` wants a **`str`** (`"2"`; an int raises `TypeError: expected str, got int`), `core_binding` wants an **`int`**. Both persist across a reopen |
| ~~`ScriptLibManObject`: `add_library(name, version)`~~ → **`add_library("Name, 1.2.3.4 (System)")`** | G8 | ✅ **one argument, not two.** Yields a `ScriptManagedLibraryReference` with `is_managed: true` — the branch that had never executed. It has **no** `effective_resolution`; `is_managed` / `is_placeholder` are the discriminators |

> **Method note, now three deep.** A string search of the binary reported that `move` did not
> exist; reflection said it does. **Reflection beats a string search, and calling beats
> reflection** — all four corrections above are about argument types and values, which reflection
> cannot tell you.
>
> **And introspection from inside the script is worthless here.** `dir()` on a `ScriptObject`
> returns an **empty list**, and `GetType().GetMembers()` omits `get_children`, `get_name` and
> `textual_declaration` — three members the exporter has called on every run for months, which
> arrive through *extenders* at runtime. A probe concluding "the typed task API does not exist"
> from that listing would have been wrong: all seven fields read fine. **Absence proves nothing.
> Call the thing.**

**The `Core2` mutation is self-inflicted.** It happens only because task configuration is
round-tripped as native XML, and **a typed API for exactly those fields has been there all
along** — P6 has now called every one of them. G7 is not a CODESYS defect Versio has to work
around; it is a consequence of a choice Versio made, and Phase 4 unmakes it.

### Decisions taken

- **Renames and moves: implemented properly**, GUID preserved. A "detect and refuse" fallback was
  considered and is moot — the API exists and P1/P2 prove it keeps identity.
- **Task Configuration: typed export *and* import.** Fixed at source rather than avoided.
- **G10: on export, keep the existing bytes when the new export is normaliser-equivalent.** The
  mirror stops churning; nothing about what CODESYS reads changes.
- **Deletions: named confirmation, no cap.** **Consequence, stated plainly:** empty the mirror and
  the dialog lists every object; one click removes them all. The remaining protections are the
  pre-import backup, the direction lock and the Phase 2 preconditions — not a threshold. **P3 makes
  those preconditions the only protection that exists**, because CODESYS will not object.
- **G11: fixed by Phase 4, not by deleting CODESYS's compile cache.** Deleting the cache was
  measured to work, and is not needed: taking task configuration off the `import_native` path
  removes the fault without Versio deleting another tool's files.

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

**Side benefit — realised.** `StDeclarationParser` and `PathMapper` had **no production
caller**; the planner is now one for both, so the parity tests protect live code rather than a
specification.

---

## Phase 0 — done

**One scratch copy per probe**, through `tools\Probe-ImportApi.ps1` (P0–P9 except P7) and
`tools\Measure-ImportStability.ps1` (P7). One copy each rather than one shared, because every probe
mutates and P3 removes a referenced object — what it leaves behind is not a project anything else
should be measured against.

| # | Test | Answer |
|---|---|---|
| P0 | What does the API look like from inside? | **Nothing** — `dir()` is empty and `GetMembers()` omits members the exporter calls daily |
| P1 | `rename` an object; re-read its GUID | ✅ **identity survives**, in session and across save/close/reopen |
| P2 | `move` an object; re-read GUID and `index` | ✅ **identity survives.** `new_index` **must be `-1`** for an unsorted parent |
| P3 | `remove()` an object another object references | ⚠️ **dangles, silently** — no throw, no cascade, no warning, 25 → 24 objects |
| P4 | `find` on a name needing percent-encoding | ✅ **varargs works**, spaces included; a python list raises `TypeError` |
| P5 | `create_pou` with awkward names | ✅ **all four refused** — `My.Thing`, `weird<name>`, `Pump `, `Pump.` |
| P6 | Typed task fields, read and write | ✅ **sufficient**, `core_binding` included; both writes persisted |
| P7 | Three import→export cycles, unchanged mirror | ⚠️ **climbs +3 per cycle, forever**; found G11 |
| P8 | Deep folder chain near 260 characters | ✅ **CODESYS is not the limit** — ten 40-char levels, ~420 characters, no complaint |
| P9 | A fixed-version library reference | ✅ **one call**, yields `is_managed: true` |

**Phase 0 had two purposes and both paid off, in opposite directions.** Confirming the API saved
a day of debugging inside Phases 3 and 4, because four of its spellings were wrong. Establishing
reachability closed two gaps outright: `My.Thing` really is refused, so G5 costs no defensive code
at all — though not as completely as that hope was written; see the caveat under P5.

### The two results that changed the plan

**P3 — `remove()` dangles silently, the worst of the three possible answers.** Removing the
`Glob_Var` GVL, which the POUs read from, produced no exception, no cascade and no warning. The
save succeeded and it was still gone after a reopen.

**This raises the stakes on Phase 2 rather than lowering them.** Its preconditions are not
belt-and-braces around a toolchain that would catch the mistake anyway — **they are the only thing
between a stale manifest and silently breaking somebody's project.** Nothing downstream will
object.

**P5 — the awkward names cannot be created, which is *not* the same as cannot exist.** All four
were refused with *"The name is not valid for this object."* So **G4 and G5 cannot be reached by
creating an object**, and `create_textual` needs no defence against them.

**Do not read that as "encoded names cannot occur", and do not delete the encoding on the strength
of it.** Two reasons, both already in this repository:

- **External-file references are named `{GUID}\|C:\Users\<user>\...\file.txt`** — a real object
  name in `README.md`'s own list, carrying `|`, `:` and `\`. It is exactly what `safe_file_stem`
  shortens, and it did not come from `create_pou`.
- **P5 tested one door.** Names also arrive from the IDE, from imported libraries and device
  descriptions, and from `import_native`.

So the honest conclusion is narrower than "G4 and G5 are closed": **the importer will never be
asked to create such an object, but resolution and file-name encoding still have to handle one that
already exists.**

---

## Build order

**Not 1, 2, 3.** The numbering is historical; this is the order the measurements argue for.

| # | What | Why here |
|---|---|---|
| ~~1st~~ | ✅ **Phase 4 — typed task configuration.** Landed 2026-08-15, both halves. **G7 and G11 measured closed** | The only phase that fixes **two live, user-facing defects**: G7 changes CPU core affinity nobody asked to change, and G11 makes every import after the first silently fail. P6 proved the typed path works; a direct experiment proved removing the file from `import_native` fixes G11 without relocating the fault. Independent of everything else here |
| ~~2nd~~ | ✅ **The task XML's one-wayness, recorded.** Landed 2026-08-15, immediately after | **Not planned — found by re-reading Phase 4 as if somebody else had written it.** Making the YAML importable made the `.xml` a file the export writes and the import discards, and nothing said so: not `_coverage.md`, not the manifest, not the panel's lock glyph. It went first because it is a **live silent-data-loss path introduced by the commit that had just landed**, where the honesty fixes below make *future* failures visible. See the finding in [`STATUS.md`](STATUS.md) |
| ~~3rd~~ | ✅ **The committed fixture mirror, regenerated.** Landed 2026-08-15 | It was asserting against a mirror the current exporter cannot produce. **The feared cost did not materialise: every existing content file came back byte-identical**, `Task Configuration.xml` included — the only changes are the new `.tasks.yaml`, one `form` field (`NATIVE_R` → `TASK_NATIVE`), `objectCount` 12 → 13, and the coverage report. One assertion moved, exactly as predicted. It also unlocked the reader test below |
| ~~4th~~ | ✅ **`.tasks.yaml` parsed by its own reader, against committed bytes.** Landed 2026-08-15 | The cheap half of the typed path's missing coverage, and it needs **no CODESYS** — the same shape as the library test that has run for months. Verified non-vacuous by perturbing an expected value. ~~The expensive half, a real mutation applied to a project, stays in Phase 6~~ — it did not: it came next instead, as the row below |
| ~~5th~~ | ✅ **The write, tested against a real project — and the two axes G11 hid behind, in the same run.** Landed 2026-08-15 | **Pulled forward from Phase 6, where it was scheduled dead last.** It was the third honesty fix and the expensive half of Phase 4's coverage, and they turned out to be **one test**: the same fixture, the same adopt-and-export scaffolding, the same CODESYS launches. Splitting them would have paid for that setup twice. It is also the item every other document already called load-bearing — the single step the whole G7/G11 argument rests on. See below |
| ~~6th~~ | ✅ **The other two honesty fixes.** Landed 2026-08-15 | The uncounted native-import failure, and the import summary logged in the exporter's vocabulary — plus **four import events that had no handler at all**, `task_set` among them, so Versio changing a task's CPU core binding logged as the literal string `· task_set`. Each makes the *next* failure visible instead of silent |
| ~~7th~~ | ✅ **Phase 1 — manifest-driven importer.** Landed 2026-08-15, both halves: the pure planner behind the seam, then schema 2 and the script executing exactly the plan | The foundation. The walk that inferred everything from file names is deleted, not bypassed — a planless import job now refuses to serialize, so there is no door back to it |
| ~~8th~~ | ✅ **`versio_native_equiv.py`.** Landed 2026-08-15, with its parity test in the same commit | A shared prerequisite, not a phase. Phase 2's native content check needs it and so does Phase 5 — it was written down as a Phase 5 artifact and is needed earlier. **Moved up from 8th to next for that reason, then built** |
| ~~9th~~ | ✅ **Phase 2 — deletes.** Landed 2026-08-15 in six commits: the seam, the delete set, the dialog, the executing half, the dialog's honesty, the native half | Planned to land with Phase 3 and split from it deliberately. Deletion is the first thing Versio does that destroys work, and the safety argument is entirely its own — five planner preconditions and a content check only the CODESYS process can make. Renames share the detection input, not the risk |
| ~~10th~~ | ✅ **The dialog corrected.** Landed 2026-08-15 | **Not planned — found by re-reading the executing half as if somebody else had written it.** The script grew `remove()` and no sentence moved, so the confirmation dialog told people their objects would stay and then deleted them. Same shape as the 2nd row: a live defect introduced by the commit that had just landed. See the finding in [`STATUS.md`](STATUS.md) |
| ~~11th~~ | ✅ **The native content check — Phase 2's open half.** Landed 2026-08-15, manifest schema 5 | It went before Phase 3 because it makes deletion **whole** rather than a feature that works on 2 of the fixture's 13 objects, and because the alternative design would have dragged git into the script to serve a phase that had not been written yet. Verified four ways against real CODESYS, including a falsification case |
| **NEXT** | **Phase 3 — renames and moves** | `rename()` and `move(parent, -1)` preserve the GUID, measured. Detection goes in the planner, which already has the manifest, the mirror and the GUID index. **It splits**: exact-content-match detection needs no git at all, and only the rename-and-edit similarity pass wants `git show HEAD:<path>`. The shared-prerequisite argument for that is gone — the native check took the other option, so `ShowAtHeadAsync` has exactly one consumer |
| **12th** | **Phase 5 — a mirror that stops churning** | Wants Phases 1–3 settled first, because it changes when files are rewritten and every earlier phase's diffs are easier to read without it |
| **13th** | **Phase 6 — remaining gaps, tests, docs** | G8 and G9, the parity tests, the integration tests |

### The three honesty fixes — all three done

None is a phase. All three came out of G11's investigation, all three are small, and each one is
the reason a future defect will be seen rather than missed.

> **The third is done, and it absorbed Phase 4's missing coverage on the way.** It was scheduled
> here and the Phase 4 write test was scheduled in Phase 6, at opposite ends of this list — and
> they were the same test. Both wanted an adopted repository, a real export, an edit to the
> mirror and a real CODESYS import; building them apart would have paid for that twice and left
> the more valuable one until last. **When two items on a build order want the same fixture, that
> is usually the list being wrong about how many items there are.**

1. ~~**`versio_import.py` does not count a native-import failure.**~~ — ✅ **FIXED 2026-08-15.**
   `ReplaceHandler` counts `errors` and `refused`; a file that lost any object is counted in
   `failed`, is no longer counted in `native`, and emits a new `native_partial` event carrying
   how many objects made it and how many did not.
   - **One hazard found while doing it, and it is the reason this was not a one-line change.**
     `main()` computes `dirty = updated > 0 or native > 0` and skips the save when it is false.
     Taking a partly-failed file *out* of `native` would therefore have made the run that had
     already written to the project the one that did not save it — losing the objects that did
     paste. There is a `partial` counter in the `dirty` test for exactly that. **A fix to a
     lying counter must not cost somebody the half of the import that worked.**
   - ⬜ **The `native_partial` branch itself is not covered, and says so in the test.** It needs
     CODESYS to report a per-object exception through the progress callback and carry on; the
     only observed trigger was G11, which Phase 4 removed. Manufacturing one with malformed XML
     was tried and produces the *other* route — `import_native` raises, landing in the
     pre-existing `NATIVE_IMPORT_FAILED` path. That route is now tested.
2. ~~**`LocalCodesysRunner`'s `summary` case logs an import using the exporter's field names.**~~
   — ✅ **FIXED 2026-08-15.** It read `walked` / `written` / `skipped` / `gaps` against an import
   that reports `updated` / `unchanged` / `native` / `created` / `missing` / `failed` /
   `notImported`, and `ScriptEvent.Int` returns null for an absent key, so **every import in the
   application's history logged `walked , wrote , skipped  (of which  not implemented), 0
   warning(s)`** — four blanks and one number, and the number was true only because `warnings`
   is common to both scripts. It now reads `import: 1 updated, 7 native, 2 unchanged, 3 not
   applied (export-only) — 0 warning(s)`.
   - **Keyed on the job's operation, not on which keys are present.** Sniffing the payload works
     today and would silently pick a branch the day a third script grows a summary.
   - **Zero-valued fields are omitted rather than printed.** `0 failed` on every line is the
     noise that let the blank line survive unnoticed; the counts that matter are the ones that
     are not zero.
   - **And four import events had no `case` at all**, so they fell through to
     `_log.Debug("· {type}")`. The worst was **`task_set`** — Versio changing a task's priority,
     interval or CPU core binding logged as the literal string `· task_set`, with no task, no
     field and no value. It is `Info` now, and it is the one line in an import that records a
     change to what the machine runs. `updated`, `native_imported`, `native_partial`,
     `phase_saved`, `saved` and `nothing_to_save` are handled too; `native_partial` is the only
     `Warn` among them.
   - **Covered by a new `ImportLoggingTests`**, the sibling `ExportLoggingTests` never had. Two
     tests: a clean import reports honest numbers, and **an import that loses a file says so** —
     the second because nothing in the suite had ever run a *failing* import, which is how "the
     summary is wrong when something goes wrong" had no way to be caught. Both verified by
     mutation: deleting the summary branch and disabling the `task_set` case each turn one red.
   - *(The `not_imported` case in the same `switch` had a related defect — hard-coded "change
     this in the IDE" — and was fixed earlier the same day.)*
3. ~~**No test imports twice, and none imports with the project's sidecars present.**~~ —
   ✅ **CLOSED 2026-08-15.**
   `AdoptAndExportTests.A_task_priority_edited_in_the_mirror_reaches_the_project_and_survives_a_second_import`
   exports, edits a priority in the mirror, imports, edits again, imports again, and reads the
   value back out of the project through a fresh export. **The compile cache is asserted
   present before the second import**, not assumed — the first import writes one, which is now
   measured from a test rather than inherited from `Measure-ImportStability.ps1`, and asserted
   so the axis cannot decay silently if a future CODESYS stops writing it.
   - **The `.opt` half of this gap did not exist.** The test was first written copying the
     committed `.opt` pair beside the project, as this row asked for — and **deleting that copy
     left every assertion green.** CODESYS writes its own pair the first time it opens the
     project, which is the first *export*, so every test that exports before importing already
     had them. The copy was removed as an unwatched path. What was genuinely uncovered is the
     cache and the second cycle.
   - **A second import needs a second edit.** Re-running the same import is refused as
     `NoChange`, correctly, by the hash gate. That is not a workaround: G11's trigger was the
     cache and the measurement recorded that the mirror's bytes are irrelevant. It also makes
     the assertion stronger — the final value is the *second* edit's, so it can only be right
     if the cycle that used to fail actually applied.

There is a fourth, and it is a judgement call rather than a fix: **the phase-split save in
`versio_import.py` is conditional on `created > 0 or updated > 0`**, so an import touching only
native files never saves between the phases — which is the shape of run that hit the exception the
split exists to prevent. Whether running it would have helped is untested. A guard skipped in
exactly the case its docstring names is worth revisiting either way.

---

## Phase 4 — Task configuration, typed both ways *(G7 and G11)* · ✅ **built 2026-08-15**

Mirrors the library-reference design exactly, which is the precedent to copy:

- ✅ **`versio_tasks.py`** — pure, no `scriptengine` import, handed objects and a `safe` probe,
  exactly like `versio_libraries.py`. Reads priority, interval, interval unit, core binding,
  watchdog and POUs. **The event POU GUID is deliberately not recorded** — it was observed
  changing during a native import, and its meaning for an interval-driven task is
  unestablished; a value that moves on its own does not belong in a diff, which is the same
  rule that keeps `ref.id` out of the library file.
- ✅ **Field types measured, not assumed** (probe P10): `priority` is a **string**, and
  `interval` is `"t#20ms"` on one task and `"100"` on another **in the same project**. Both
  are recorded verbatim — a normaliser would rewrite one of them and put a diff in front of
  somebody that they did not make.
- ✅ **`core_binding` is the XML's `Core2`** — confirmed on both tasks rather than assumed,
  because this phase's whole justification rests on it. There is a `Core` field beside
  `Core2`; had it been that one, the typed path would have been a detour rather than a fix.
- **Rendered through the existing `versio_yaml.document(...)`** — already vendor-neutral, already
  deterministic, already has key-order discipline.
- ✅ **New form `TASK_BOTH`**, following `GVL_BOTH`: native `.xml` for fidelity *and*
  `<name>.tasks.yaml`. The `.xml` keeps the name it has always had, so the change is
  **additive** and no committed file is renamed.
- ✅ **The native XML is export-only and the YAML is the importable form.** A native file is
  skipped when a typed file sits **beside** it — adjacency rather than a suffix of its own,
  so no committed file had to be renamed. The event names the winner
  (`TYPED_SIBLING`, `sibling: Task Configuration.tasks.yaml`) rather than going quiet.
- ✅ **And the manifest says so — added 2026-08-15, after the rest of Phase 4 had shipped
  without it.** Making the YAML importable made the `.xml` beside it a file the export
  writes and the import discards, which is the precise definition `_coverage.md` exists to
  report. It reported nothing: the XML was recorded `NATIVE_R`, the same form a property's
  recursive export carries, so the coverage report skipped it and the panel drew it as an
  ordinary editable row. **Recorded `TASK_NATIVE` now**, one-wayness is read from a single
  list on each side (`EXPORT_ONLY_FORMS` ↔ `ExportCoverage.IsExportOnly`), and the two are
  compared in a unit test. See the finding in [`STATUS.md`](STATUS.md).
  - **A side effect worth having:** the importer currently probes the disk for a typed
    sibling, with a comment saying the check can go once Phase 1 reads the manifest. The
    manifest now carries the answer, so Phase 1 inherits it rather than having to derive it.
- ✅ **`versio_yaml` gained a reader.** Nothing python-side had ever read a document it
  wrote — `.libraries.yaml` is export-only and the C# `LibraryCatalog.Parse` was the only
  parser in existence. Emitting and re-reading in one process is now a test, and it covers
  the quoting rules, the scalar list, values that would parse as a bool or a number, and
  the escapes. **Values come back as text, always** — no type inference, because CODESYS
  wants a string for `priority` and an int for `core_binding` and a parser that guessed
  would be wrong for one of them.
- ✅ **Import through property assignment**, not the setter methods this plan used to name.
  **The types differ per field**: `priority` takes a `str`, `core_binding` takes an `int`.
  A field that refuses is **reported, never swallowed** (`TASK_FIELD_REFUSED`), because a
  file that looks editable while one of its fields is quietly ignored is the exact gap
  `_coverage.md` exists to close.
- ✅ **A task is never created.** An entry naming a task the project does not have warns
  (`TASK_NOT_FOUND`) and moves on. Adding a task changes what the machine runs, and this
  phase is about stopping unasked-for changes rather than introducing a new kind.
- ⬜ **New `src/Versio.Core/Projects/TaskCatalog.cs`** — a C# reader mirroring `LibraryCatalog`,
  so the panel can show tasks. **It needs the scalar list**, which `LibraryCatalog.Parse`
  does not handle: no library field is a list, so the construct never reached it.
- ✅ **Regenerate the committed fixture mirror** to carry the new file. **Done 2026-08-15,
  and it cost far less than this plan feared.** "Moves committed bytes" turned out to mean
  three files: the new `.tasks.yaml`, `_coverage.md`, and `_manifest.json` — where the only
  object change is one `form` field. **Every other file, including `Task Configuration.xml`
  itself, came back byte-identical**, which is also the strongest available evidence that
  the export is still deterministic across everything this touched.
- ✅ **An integration test that changes a task priority and asserts the project afterwards.**
  ~~Listed under Phase 6, and worth pulling forward: the write step is still proven by a
  hand-run mutation and by nothing automated, which is the same shape as the coverage gap
  that let G11 live.~~ **Pulled forward and landed 2026-08-15.** The "worth pulling forward"
  turned out to understate it: it was the same test as the third honesty fix, so building it
  here cost one fixture rather than two.
  - **Split in two, because the halves cost wildly different amounts.** ✅ **The cheap half
    landed 2026-08-15** — `The_committed_task_file_parses_with_the_reader_that_ships_beside_it`
    parses the committed `.tasks.yaml` with the reader the importer calls, needs **no CODESYS**,
    and runs in ten seconds. It was impossible until the fixture above carried the file.
  - ✅ **The expensive half landed 2026-08-15 too**, pulled forward from Phase 6 because it was
    the same test as the third honesty fix. It writes a priority through the API and reads it
    back out of the project, so **the write is watched now** — the single step the entire
    G7/G11 argument rests on.
    - **Verified non-vacuous twice, by deleting each half of Phase 4 in turn.** Stubbing out
      `setattr(obj, prop, wanted)` reads back `1` instead of `7`. Making `typed_sibling_for`
      return `None` — the pre-Phase-4 world, where the XML goes back to `import_native` — also
      reads back `1`.
    - **And the second mutation says something the plan did not know.** It failed on the
      priority assertion and **not** on the warnings: with the XML back on the `import_native`
      path and the cache present, the native import silently overwrote the typed write *without
      throwing*. G11's exception did not come back. The difference from P7 is that this run had
      `updated > 0`, so the phase-split save — the guard written for that exact exception, and
      the "fourth honesty fix" below — actually **ran**. That is a lead and not a conclusion,
      because the mutation moved two things at once. But it is the first evidence on the
      question that fix has been carrying unanswered: *whether running the guard would have
      helped*.

**Why this is first.** It is the only phase that fixes something a user is living with today, and
it fixes two:

- **G7** — Versio changes a task's CPU core affinity on every import, without being asked. That is
  a change to what the machine does.
- **G11** — every import after the first silently fails to apply task configuration on any machine
  where the project has been opened in the IDE, and reports `failed: 0`.

**G11 being fixed here is measured, not hoped.** With the compile cache present, importing a mirror
with `Task Configuration.xml` removed gives **7 native files, zero `NATIVE_IMPORT_ERROR`, zero
other warnings** — the fault does not relocate to another object. So Versio does **not** need to
delete CODESYS's compile cache, and the uncomfortable question of whether it should is closed by
not needing an answer.

> **The earlier version of this note got G11 wrong**, and the way it was wrong is kept because it
> is instructive. It claimed Phase 4 fixed G11 because G11 *was* the `Core2` mutation seen from
> the other end — cycle 1 mutates the file, cycle 2 chokes on it. That fitted every observation
> then available and was written into three documents before a single variable had been varied.
> The first experiment that varied one killed it: a fully round-tripped `Task Configuration.xml`
> imports cleanly into a project with no cache beside it. **Phase 4 does fix G11 — for a different
> reason than the one first written down**, and only a direct test could tell those apart.

---

## Phase 1 — Manifest-driven importer *(foundation; G3–G6)*

~~**New `src/Versio.Core/Projects/ImportPlanner.cs`**~~ → ✅ **built 2026-08-15 as
`src/Versio.Core/Plc/Codesys/CodesysImportPlanner.cs`**, and the location change is a seam
correction, not a preference. The planner owns the suffix tables, the stem codec and the form
dispatch — all CODESYS knowledge — and `Versio.Core.Projects` is one of the namespaces
`VendorSeamTests` keeps vendor-free. So the plan **types** are neutral
(`Versio.Core.Plc/ImportPlan.cs`), the entry point is on the existing seam
(`IPlcProjectFormat.PlanImport`, beside `NeedsTextToPlan` so the caller can load text for
exactly the files the vendor names without knowing why), and the reasoning lives behind it.
This document's original path would have been the third vendor leak the seam test exists to
catch, written into the plan before the test could see it.

- ✅ Manifest path present in mirror → **Update** (GUID, the manifest's verbatim `name`,
  `form`-dispatched route: `ST_*` → text, `TASK_BOTH` → typed, `NATIVE*` → native, an unknown
  form → refused with a sentence rather than guessed at).
- ✅ Mirror path absent from manifest → **Create** (name from `StDeclarationParser`, falling back
  to `PathMapper.DecodeStem`; warn when they disagree — that is G3). `DecodeStem` had **no
  production caller** until this; its parity tests now protect live code.
- ✅ `form == LIBRARIES` / `TASK_NATIVE` → **skip, announced**, with the importable sibling read
  from the manifest by shared GUID — the disk probe for a typed sibling can go once the script
  runs from the plan.
- ✅ Neither object nor bookkeeping → **skip with a warning**, never `import_native` (G6).
- ✅ Manifest below `ExportCoverage.ReportingSchema`, or absent/unparseable → plan **Update and
  Create only**, stating that deletes, renames and moves are unavailable until re-export. Never
  guess — but a recorded name is still believed, because staleness is about what a manifest
  does *not* list.
- ✅ **Owner chains resolve by GUID, and the manifest name disambiguates the dotted-stem
  split** — `My.Thing.xml` whose recorded name is `My.Thing` is *not* split into owner and
  member, which the filename alone can never decide. G5's surviving half (names that already
  exist) is handled, not just declared unreachable.
- ✅ 29 pure tests in `CodesysImportPlannerTests`, including the committed fixture planned end
  to end, each G-numbered rule exercised with the input that used to break it, and three
  mutations (G3, G5, G6) verified to turn the suite red.

✅ **Script, rebuilt 2026-08-15: `versio_import.py` executes a plan and decides nothing.**
The walk is gone — `object_name_from_file`, the typed-sibling disk probe, and the
"everything else must be native XML" fallback went with it, and `versio_types.py` lost
`is_export_only` / `is_typed_form` / `typed_sibling_candidates`, whose own docstring had
promised to go when this landed. What survives script-side is what only that process can do:
the ScriptEngine calls, the two-phase order with its save, and the per-suffix *how* of a
textual apply. Resolution stayed on `find_by_path`'s name walk rather than moving to `find`
varargs — same result, chain-driven either way, and one less API shape to be wrong about.
Files the plan never mentions are warned (`NOT_IN_PLAN`) and untouched, so the race window
between planning and execution does not reopen G6.

✅ **Job file: schema 1 → 2**, `ProjectJob` carries the plan, and both sides refuse rather
than half-execute: `ToJson` throws on a planless import before the 17–31 s cold start is
paid, and the script dies on an unexpected schema before it opens the project.

✅ **`LocalCodesysRunner` computes the plan when its caller did not supply one** — the IO
half (manifest read, directory walk, text loads) beside the pure planner, so every import
is plan-driven whichever door it came through, a test driving the runner directly included.
The planner's warnings reach the log at Warn, and a mirror with files missing gets the
sentence that answers "I deleted the file, why is the object still there". **Phase 2 lifts
plan computation into the orchestrator** so the confirmation dialog and the execution share
one plan; the carriage for that (plan on the job) already exists.

**Proven live, not just in unit tests** — `ImportLoggingTests`' failing-import run now also
carries a `README.md` and a `Bar.st` declaring `PROGRAM Foo`: the markdown is warned about
and never handed to CODESYS (G6), and the project gains an object named **Foo**, with the
file name appearing only as the thing being corrected (G3). Summary:
`1 updated, 1 created, 6 native, 3 unchanged, 3 not applied (export-only), 1 FAILED — 2 warning(s)`.

**Files:** `CodesysImportPlanner.cs` + `ImportPlan.cs` (new), `CodesysJob.cs`,
`LocalCodesysRunner.cs`, `versio_import.py`, `versio_types.py`.

---

## `versio_native_equiv.py` — a shared prerequisite · ✅ **built 2026-08-15**

Not a phase. **Phase 2 needs it and so does Phase 5**, and it was written down as belonging to the
latter, which would have made Phase 2 unbuildable in the stated order.

A python-side equivalent of `NativeXmlNormalizer`, joining the parity discipline —
`versio_native_equiv.py` ↔ `NativeXmlNormalizer`, compared by output in
`PythonFileNameParityTests`. The C# side already exists and is tested.

✅ **`normalize(xml, mask_guids=True) -> (text, guids_masked, counters_masked)` and
`are_equivalent(a, b, mask_guids=True)`**, a line-for-line mirror of the C#: the two regexes,
the three unordered-container names, counters masked before GUIDs, and the same degrade-rather
-than-throw behaviour on a truncated payload. Pure, no `scriptengine`, runs on IronPython 2.7
and CPython 3.

✅ **Three parity tests, in the same commit as the module** — the rule the whole of §9 of
[`CODESYS.md`](CODESYS.md) argues for, and the first pair in this repository never to have had
an uncompared moment:

- **18 measured cases, both maskings.** `NativeXmlNormalizerTests`' own corpus, because every
  entry in it is a real difference measured on 3.5.22.20 rather than one somebody imagined.
  The **normalised text** is compared, not a verdict: two implementations can agree on
  "equivalent" for every pair in a corpus while masking different things, and a false-true on
  both sides is indistinguishable from agreement.
- **2.9 MB of uncurated real output** — the equivalence captures and every native file in the
  committed mirror, `Live_Visu.vis.xml` at 2.5 MB included, which is where the container
  reordering actually happens at scale. Compared by hash, with the mask counts and the length
  beside it so a failure still says something.
- **The pinned verdicts.** `GlobalTextList` equivalent with symmetric 30/1 masks; the task
  configurations **not** equivalent, differing on exactly one line, and it is `Core2: -1 → -2`.
  That last one is the point: **a python normaliser that masked `Core2` would call the file
  equivalent, and Phase 2 would then delete an object whose live state had genuinely changed.**

> **Seven deliberate defects, and the two that mattered were the ones that did not fail.**
> Swapping the mask order, dropping a container name, masking `Core2`, dropping the
> self-closing guard and ignoring the `mask_guids` flag each turn the suite red. **Deleting the
> ordinal sort key did not** — C# sorts by UTF-16 code unit and CPython 3 by code point, they
> agree across the whole BMP, and the corpus was entirely BMP. A case above it was added and
> the mutation goes red. The **order** of the container table was unwatched the same way, since
> no line named two of them. Both were claims the module's own comments made, with nothing
> checking them; a green suite is not the double pass.

**Measured sufficient.** Run over two steady-state snapshots of an unedited mirror: 9 files
byte-identical, **4 equivalent — every churning one** — and only `_manifest.json` left, which is
derived from the four. The masked counts are symmetric on every file (695/695, 3656/3656, 521/521
and 30/30 GUIDs), which is what says the two sides differ in the masked positions and **nowhere
else**; an asymmetric count would mean one side carried something the other did not and the verdict
was hiding it.

---

## Phase 2 — Deletes *(G1)* · ✅ **built 2026-08-15, textual objects only**

**Delete set:** manifest entries whose file is gone from the mirror **and** whose object still
exists in the project. The preconditions are correctness, not caps — they are what make the delete
set *mean* anything, and **P3 makes them the only protection there is**:

- **Only objects the manifest recorded.** One added in the IDE since the last export is not in the
  manifest, so its absence from the mirror is not evidence of a deletion. Never touched.
- **A missing or unparseable manifest disables deletion entirely**, with a stated reason.
- **Content check before removal, in the script:** if the live object no longer matches the
  manifest's recorded hash, it changed since the export — refuse *that* deletion and warn.
  - **Textual: a byte comparison, and P7 proved it holds** — zero `.st` differences across three
    round trips.
  - **Native: it cannot be.** The manifest's `hash` is `content_hash(text)` over the *exported file
    bytes* (`versio_export.py:183`), so checking a live object against it means exporting that
    object again — and P7 measured that re-export changing every cycle with nothing edited. A byte
    check would refuse every native deletion forever, for a reason with nothing to do with the
    object. **The native half compares through `versio_native_equiv.py`.**
    - ✅ **CLOSED 2026-08-15 by recording the comparison form at export time** — option (1) of
      the two that were written up. An equivalence comparison needs *two* payloads and the
      second one lived in the file that was just deleted; the manifest now carries it, as
      `nhash`, so the fresh export has something to point at. **Schema 5, additive**: no
      existing mirror is invalidated and no committed file changed shape — regenerating this
      repository's own mirror touched `_manifest.json` and nothing else.
- A GVL is two files under one GUID: delete only when **every** file for that GUID is gone.

**Confirmation:** extend `PendingImportDetail` in `SyncViewModel` to name each deletion —
`FB_Motor · POU`, resolved through `ObjectIndex` the way changed files already are.
`DestructivePrompt.Body` is free text, so no model change is strictly required.

### ✅ The seam step, landed 2026-08-15 — the plan now crosses to the dialog and back

**Deletes are not built. The carriage for them is**, and it went first because the confirmation
is worthless without it: a dialog naming three objects it will delete, followed by an import
that recomputes its own plan, is a dialog that told the truth about something that then did not
happen.

- **`SyncJob.Plan` and `PlcProjectJob.Plan`** — both vendor-neutral, `ImportPlan` already being
  so. The CODESYS job has carried a plan since Phase 1, but that is *below* the seam and the
  orchestrator that builds a `PlcJob` is above it, so a plan computed for a dialog had no way
  down.
- **`ImportPlanReader`** (`Versio.Core/Plc/`) — the IO half of planning, on `IFileSystem`, so it
  sits in Core and is tested without a disk. **Shared, not copied**: the orchestrator needs a
  plan before the dialog and `LocalCodesysRunner` needs one when a caller supplies none, and
  those were about to become two walks with one convention between them.
- **`SyncOrchestrator.PlanImportAsync`** decides before CODESYS starts; `ImportAsync` uses the
  agreed plan when there is one and computes only when there is not — the watcher's path, where
  nobody was asked.
- **`SyncViewModel` computes the plan before the question**, not after the answer.

> **The link nothing was watching was the last one.** Cutting `SyncJob.Plan`,
> `PlcProjectJob.Plan`, `ToNative`'s copy, or the mirror-relative path convention each turns a
> suite red. Cutting the **view model's** hand-over turned nothing red at all — the whole app
> suite passed with the agreed plan discarded, because the orchestrator silently computes a
> replacement and an identical mirror yields an identical plan.
>
> The two are only distinguishable if the mirror moves in between, so
> `ImportPlanHandoverTests` moves it **from inside the confirmation prompt** — the one moment
> guaranteed to sit between deciding and running. That is not a contrivance: a dialog is open
> for as long as somebody takes to read it, and Versio watches the mirror throughout. With the
> hand-over cut, the test now fails naming `App/Appeared.st · Create` — a file imported that
> nobody agreed to.
>
> **Reaching that test cost two fixes of its own**, both in this repository's own scaffolding
> rather than in the product, and both recorded in [`STATUS.md`](STATUS.md): `Eventually.Press`
> was fire-and-forget, and no app test had ever *accepted* an import.

### ✅ The delete set and its preconditions, landed 2026-08-15 — proposed, executed by nothing

`ImportPlan.Deletions` is the vetted answer; `MissingFromMirror` stays beside it as the raw
input, and **the two differ on purpose**. Pure, in the planner, with nothing downstream acting
on it: the job file deliberately does not carry deletions yet, and a test asserts that so
whichever half of the script change lands first, the other is named as missing.

| Precondition | What it stops |
|---|---|
| **Only a trusted manifest.** Absent, or below `ReportingSchema` → no deletions at all | What a stale manifest does not list cannot be told apart from what it never recorded, so every absence would look like a deletion |
| **Every file for the GUID must be gone** | A GVL is a `.gvl.st` and a `.gvl.xml` under one identity. A per-file rule sees the `.st` gone and removes the object the surviving `.xml` still describes. One half missing is somebody tidying or a partial checkout — announced, never acted on |
| **Export-only records neither vote nor veto** | Their files are rewritten by every export and applied by nothing, so absence is not death — *and* presence is not life. Counting a task configuration's `TASK_NATIVE` half would make it undeletable for a reason unrelated to the object |
| **Nothing surviving may live inside it, at any depth** | P3 as a precondition. `remove()` dangles referenced objects silently, and ownership is the one kind of reference the manifest records |
| **Its owner chain must be recorded, not guessed** | `ResolveKnown` falls back to splitting the file stem when an owner is missing from the manifest. For an *update* that is a fair guess — the worst case is text applied to the wrong object, which git undoes. For a *deletion* the identical guess resolves to some other object and destroys it |

> **The transitive half of that last rule was a bug, and a pre-existing test found it rather
> than a review.** Checking direct children only would have deleted a class whose property was
> gone from the mirror while the property's **accessor** was still there — taking both with it.
> Chains are two and three deep in an ordinary project, since a property owns its `Get` and
> `Set`. What went red was a *resolution* test, because its fixture listed one file out of three
> recorded — a mirror that cannot exist, harmless until absences started meaning something. The
> fixture describes a real directory now, and the grandchild case has a test of its own.

> **The guessed-chain rule came out of a second pass, not the plan.** This document's Phase 2
> section lists the delete set, the manifest gate, the content check and the GVL pairing; it
> does not mention resolution, because on the update path a guessed chain is an acceptable
> risk. **The same code reached by a deletion is not the same risk**, and the resolver is shared
> — which is exactly the kind of thing that is invisible until you re-read the call site asking
> what it does when it is wrong.

Five deliberate defects, one per rule, each failing exactly the test written for it.

### ✅ The dialog says it, landed 2026-08-15

`ImportNarrative.Describe(plan)` — pure, in `Versio.Core/Sync/` beside `SyncNarrative` and for
its reason: this is the wording on the one dialog that precedes writing into a binary file
nobody can diff afterwards, so it is part of the feature and is asserted rather than eyeballed.
The confirmation body is the banner's sentence plus what *this* import would do: how much will
be applied, how many objects are new, **every object with no exported file, named**, the
planner's own warnings, and — when the manifest cannot be trusted — that structural changes are
off the table, without naming a schema number at somebody who asked about their project.

**Built where the plan is, not where the banner is.** The banner refreshes on every watcher
event and must not walk a mirror to do it; the dialog runs once, when somebody has asked, which
is the only moment the walk is worth paying for.

> ~~**Present tense, and that is the whole discipline of it.** Deletions are planned and
> executed by nothing, so the dialog says the objects **will stay**. Two tests pin the wording;
> when the script grows `remove()`, they are what say the wording changes with it.~~
>
> ⛔ **They did not, and this is the worst defect this plan has produced.** The script grew
> `remove()` in the very next commit. The wording did not move, and **the whole suite stayed
> green at 1,443 + 480**, because both pinning assertions were on the *old* sentence — an
> assertion that a dialog says `will stay` cannot fail when the behaviour changes underneath
> it, only when somebody fixes it. For one commit Versio named an object, said it would stay,
> and then deleted it. Written up as a Finding in [`STATUS.md`](STATUS.md).
>
> **The lesson is about the shape of the tripwire, not the diligence of the author.** A test
> written to catch a future change has to assert the property that will become false, and
> "the dialog does not yet promise removal" is not that property. Both sides are asserted now.

### ✅ The dialog tells the truth about two outcomes, landed 2026-08-15

A deletion has two possible endings and the dialog has to name which one each object gets,
because the script refuses any deletion it cannot check and **7 of the fixture's 13 objects are
native**. Re-wording to *"will be removed"* would have swapped one lie for another.

`PlannedDeletion.CannotVerify` is the planner's prediction of the script's own refusal — null
when the object will really go, a sentence when it will really stay — and `ImportNarrative`
emits one sentence per outcome, the second grouped by reason so *"export again"* is only
suggested where it would help.

> **The two sides now share an authority.** `CAN_VERIFY_FORMS` lives in `versio_types.py`, is
> mirrored by `CodesysImportPlanner.WhyUnverifiable`, and the two are compared by output in
> `PythonFileNameParityTests` — verified red by adding `FORM_NATIVE` to the python list. It is
> in `versio_types` rather than `versio_import` for a reason worth keeping: the importer
> imports `scriptengine` and therefore **cannot be loaded outside CODESYS at all**, so a rule
> living there is a rule nothing can compare.

### ✅ The executing half, landed 2026-08-15 — Versio deletes things now

The `deletions` key crosses the wire, `versio_import.py` grew a delete phase, and an object
whose file left the mirror leaves the project. **Proven against real CODESYS**, three ways in
one test (`An_object_whose_file_is_deleted_leaves_the_project_and_an_edited_one_does_not`,
~2 min, five launches).

- **Deletions run last.** Everything else puts objects in; this takes them out, and P3 measured
  that `remove()` takes a referenced object with it silently. Last means the project is
  otherwise complete at the moment anything is destroyed.
- **`removed` is in the `dirty` test.** A run whose only work was a deletion would otherwise
  close without saving — an import that reported success, removed something, and changed
  nothing. That is the same defect `partial` was added for, and the second time this project
  has nearly paid for it.
- **The content check is the script's own, because only it can make one.** The planner cannot
  see the project. The script rebuilds what the exporter *would* have written for the live
  object — `join_st` for `ST_FULL`, `normalize_decl_only` for the others — hashes it, and
  compares to the manifest. A mismatch means the object was edited in the IDE since the export,
  so the missing file is **not** permission to destroy that work: `DELETE_CONTENT_CHANGED`,
  kept, warned.
- **A refusal is not a failure.** The import still completes; marking the binding failed would
  stop all syncing over Versio declining to destroy something.
- **`removed` is logged at Info, near the front of the summary.** It is the one line in an
  import that records work leaving a project.

> ~~**⛔ Native objects are refused, and this is the open half of Phase 2.**~~ — ✅ **closed
> 2026-08-15**, by option (1): **record a normalised hash at export time**, so a fresh export
> can be normalised and compared without the old bytes. Option (2), fetching them from
> `git show HEAD:<path>`, was rejected — it works only for a committed mirror and drags git
> into a script that has no business knowing about it. See the section below.
>
> **This limit was found by the test, not by design review.** The refusal case originally used
> the GVL and reported `DELETE_UNVERIFIABLE` where `DELETE_CONTENT_CHANGED` was expected.

### ✅ The native half, landed 2026-08-15 — a GVL and a visualisation are deleted now

The manifest records `nhash` beside `hash` for every native record — `content_hash` over the
`versio_native_equiv`-normalised payload — and the delete check re-exports the live object,
normalises it the same way, and compares. **Manifest schema 5.** It closes G1 outright.

**Why the raw hash cannot be used, measured rather than argued.** Three export/import round
trips of the fixture with nothing edited: **4 native files came back byte-different every
time**, and their normalised form did not move at all. `hash` is over the bytes on disk, so a
re-export never reproduces it and a comparison against it would refuse every native deletion
forever, for a reason with nothing to do with the object.

> **GUIDs are masked, and that is the subtle decision in this whole change.**
> `versio_native_equiv.normalize` says masking must be **off** when comparing two exports of
> the same project, because GUIDs are stable across exports — and that is true: two exports
> into two empty directories are byte-identical, measured. **But this is not that comparison.**
> The recorded hash and the fresh export are separated by however many round trips have
> happened since, and **across a round trip CODESYS re-mints GUIDs**: measured on
> `GlobalTextList.xml`, exactly **3 differing lines out of 117, all three `System.Guid`
> values** — `GuidInit`, `GuidReInit`, `GuidExitX`. That is the round-trip case the docstring
> says masking is *required* for. An unmasked hash was the first design here and the
> measurement killed it before any code was written on top of it.
>
> **What it costs, stated rather than glossed.** An edit that changes GUID *values* and
> nothing else would read as unchanged. Masking is positional — each GUID becomes
> `<masked-guid>`, so an added or removed one still shows — and no realistic IDE edit changes
> only the value of an existing GUID. It is the one way this check can be wrong, and being
> wrong here destroys work nothing else holds a copy of.

**Absent is not "unchanged".** A schema-4 manifest has no `nhash` because nothing wrote one,
and reading that as agreement would delete work on every mirror exported before today. A native
object without one stays unverifiable, and the dialog says *"export the project again and this
will resolve itself"* — the same shape the planner already uses for a manifest below the
reporting schema.

**Verified against real CODESYS 3.5.22.20, four ways**, because a check that refuses everything
and a check that approves everything both look like a passing test from one angle:

| | Scenario | Expected | Result |
|---|---|---|---|
| A | export, delete the `.vis.xml`, import | removed | ✅ removed |
| B | **edit the object through the mirror, then delete and judge against the original manifest** | kept, `DELETE_CONTENT_CHANGED` | ✅ kept |
| C | **two round trips of churn, then delete against the original manifest** | removed | ✅ removed, raw hash moved and `nhash` did not |
| D | strip `nhash` to simulate a schema-4 mirror | kept, `DELETE_UNVERIFIABLE` | ✅ kept |

**B is the falsification test and the one that matters.** Masking GUIDs is what makes C work,
and a masker that also hid a real edit would delete work while reporting success. The edit is a
font name — `Arial` → `Courier New`, neither a GUID nor a counter — applied through the real
import path, and the check still refuses. **C is what the design rests on**: the recorded hash
has to survive the project being saved and reopened arbitrarily often, because that is the
normal life of a project between an export and somebody deleting a file.

**`TASK_BOTH` is still refused**, deliberately. Its `.tasks.yaml` is rendered through the typed
task API rather than read off the object, and a task configuration is the one object whose
accidental removal takes every task with it. An untested branch that says so is honest.

The re-export writes to the **system temp directory, not the mirror**: the mirror is a git
working tree with a watcher on it, and a check that exists to observe the project has no
business raising a change event in it.

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

**Execution:** `obj.rename(new_name)` and **`obj.move(new_parent, -1)`**. The GUID is preserved —
**measured, in session and across a save and reopen** — so references, task assignments and
visualisation bindings survive, which is the entire reason for doing this rather than
delete-plus-create.

> **`-1` is not a stylistic choice.** `move(dest, 0)` raises `ValueError: index must be -1 if the
> new parent node does not have sorted children`, and every parent in the fixture is unsorted. The
> one-argument `move(dest)` raises a `NullReferenceException`. This plan previously wrote
> `move(new_parent, new_index)` as though the index were a position to choose.

**Files:** `ImportPlanner.cs`, `IGitClient`/`GitCliClient` (a `ShowAtHeadAsync` if absent),
`versio_import.py`.

---

## Phase 5 — A mirror that stops churning *(G10)*

In `versio_export.py`, when exporting a native object: if the freshly exported payload is
**`versio_native_equiv`-equivalent** to the file already at that path in the *current* mirror, use
the **existing bytes**.

- Read the old file from `out_root` **before the swap**; staging is empty by construction.
- **The determinism gate still holds.** Two exports into two empty directories stay byte-identical,
  because neither has an existing mirror to preserve. Only export-into-an-existing-mirror is
  affected, and that is the case that matters.
- **Justified by measurement, not argument.** P7: the counter climbs 44 → 47 → 50 → 53 with nothing
  edited between cycles, and reproduces identically on a re-run.
- **Key on equivalence, never on the counter.** `GlobalTextList.xml` churns every cycle and has no
  counter in it at all — a fix aimed at counters would leave a file diffing forever and look like
  it worked.

> **The trap, which is not in the one-line rule.** `record()` hashes the payload the exporter
> *generated* — `h = content_hash(text)` — and `write_text` writes that same `text`. They agree by
> construction today, and `write_text`'s docstring states the invariant outright: *"The exporter
> hashes what it writes."* **Writing the existing bytes at write time breaks it**, leaving a
> manifest that disagrees with the file beside it — and Versio's hash gate reads the bytes on disk,
> so the disagreement is not cosmetic. `_manifest.json` is the one file still differing between two
> steady-state snapshots, and it settles only if the recorded hash follows the bytes actually
> written. **So the substitution happens before the hash is taken, not at write time.**

---

## Phase 6 — Remaining gaps, tests and docs

- **G8** — a fixture variant carrying at least one fixed-version reference, asserting the
  `managed_library` branch. **P9 makes this cheap**: `add_library("Standard, 3.5.17.0 (System)")`
  produces a `ScriptManagedLibraryReference` with `is_managed: true`. Note the managed type has
  **no** `effective_resolution` — the two reference types carry different fields, and
  `is_managed` / `is_placeholder` are how they are told apart. **If it still turns out awkward,
  mark the branch unproven in `versio_libraries.py` and stop** — an untested branch that says so
  is honest; one that is quietly assumed to work is the thing this gap already was.
- **G9** — guard the **total** path in the exporter, measured against the staging path since it is
  the longer of the two. **P8 confirms nothing upstream will help**: CODESYS accepted ten nested
  40-character folders, ~420 characters, without complaint. Shorten deeper segments or adopt
  `\\?\`; warn rather than fail silently.
- `ImportPlannerTests` — pure and exhaustive: delete, rename, move, rename-and-edit, GVL two-file
  GUID, stale manifest, missing manifest, object-added-in-IDE, encoded names, dotted names.
- Extend `PythonFileNameParityTests` with an **`StTextCodec` ↔ `versio_text` codec parity test**.
  `RunPython` already takes a snippet and a JSON payload, so this is a few lines — and it is the
  highest-consequence uncompared pair in the repo, since a bug in `split_st` silently moves code
  between a declaration and an implementation.
- Integration, on a copy of the fixture: delete an object, rename one, move one, and assert the
  project afterwards — including that GUIDs survived. ~~Change a task priority.~~ ~~**And import
  twice, with the sidecars present**, which is the pair of axes G11 hid behind.~~ **Those two
  landed early, on 2026-08-15, as one test** — see the build order. What is left here is the
  delete/rename/move half, which needs Phases 2 and 3 to exist first. **The scaffolding is
  already written**: `A_task_priority_edited_in_the_mirror_…` adopts, exports, edits the mirror
  and imports against real CODESYS with sidecars, so the remaining cases extend it rather than
  starting over.
- Docs: [`CODESYS.md`](CODESYS.md) §1.3 (the API this uncovered), §3.1 (job schema 2), §5.2
  (`TASK_BOTH`), §6 (plan-driven import), §7 (G10, and what "deterministic" now means), §12 D1–D6
  resolved rather than described. [`PHASE0-FINDINGS.md`](PHASE0-FINDINGS.md) gate 0.5b annotated:
  the `Core2` deviation is fixed, not tolerated.

---

## Handoff — picking this up after Phase 2

**Where you are.** The import is plan-driven end to end and **Versio now deletes objects**.

```
SyncViewModel decides the plan ─► asks ─► SyncJob.Plan ─► PlcProjectJob.Plan
   ─► ToNative ─► ProjectJob.Plan ─► job file (schema 2) ─► versio_import.py
```

- `CodesysImportPlanner` (pure, `Versio.Core.Plc.Codesys`) decides **what**: updates, creates,
  skips, and deletions behind five preconditions.
- `ImportPlanReader` (`Versio.Core/Plc/`, on `IFileSystem`) does the **IO half** — manifest,
  directory walk, text for the files the format asks for. Shared by the orchestrator and the
  runner, so there is one walk rather than two.
- `SyncViewModel` computes the plan **before** the confirmation question and hands it on;
  `ImportNarrative` turns it into what the dialog says. The plan agreed to is the plan
  executed — nothing recomputes after the answer.
- `versio_import.py` executes and decides nothing. A planless import refuses to serialize.
- `versio_native_equiv.py` answers "are these two native payloads the same thing" from inside
  the CODESYS process. **Still no caller** — Phase 5 is its other one.

**Next, in order:**

1. **Phase 3 — renames and moves.** Detection goes in the planner, which already has the
   manifest, the mirror and the GUID index; `rename()` and `move(parent, -1)` preserve the
   GUID, measured.
   - **It splits, and the first half needs no git at all.** Detection step 1 (exact content
     match — a Create candidate whose hash equals a Delete candidate's manifest hash) and
     step 3 (unmatched → Delete + Create) are pure planner work over data already in the
     manifest, and step 1 covers rename-without-editing, which is most renames. Only step 2,
     the rename-and-edit similarity pass, wants `git show HEAD:<path>`.
   - **The shared-prerequisite argument for it is gone.** It used to be worth pairing with
     option (2) for the native content check; the check took option (1) instead, so
     `ShowAtHeadAsync` now has exactly one consumer. Do not adopt (2) to justify sharing.
   - `IGitClient` has **no** `ShowAtHeadAsync` today — checked, all 40-odd members. Only
     `DetectRenamesAsync`, which runs `git diff HEAD` and so cannot see an untracked rename
     target.
2. **Phase 5, then Phase 6.** Phase 5 has its prerequisite already: `versio_native_equiv`, and
   now also the proof that normalisation is stable across round trips.
3. **Still open from Phase 4:** `TaskCatalog.cs`, the C# reader. **Land it with the panel
   change that consumes it**, not before — a reader with no production caller is a
   specification pretending to be code, which is exactly what `DecodeStem` and
   `StDeclarationParser` were until the planner became their first caller.

**What to be careful of, learned building Phase 2:**

- **The manifest is written with `sort_keys`**, so an object's members are alphabetical and
  `hash` comes *before* `path`. A test helper that searched forward from a path to find "its"
  hash poisoned the **next object's** record, the deletion went ahead, and the failure looked
  like a defect in the script. Nothing about the code under test was wrong.
- **A counter that decides whether to SAVE must count every way the project can be written
  to.** `removed` is in the `dirty` test for the same reason `partial` is; without it a
  delete-only run closes without saving and the object comes back.
- **`exported_text_of` must stay exactly in step with `versio_export.py`.** The content check
  means "unchanged" only while the two agree on how a form is rendered; the day they diverge it
  starts meaning "exported by a different version" and refuses every deletion for a reason
  that has nothing to do with the object.
- **Reach for an integration test earlier than feels comfortable here.** The script imports
  `scriptengine` at module level, so none of the delete path can be loaded under CPython — no
  parity test, no unit test, nothing but real CODESYS. The native-refusal limit was found by
  that test rather than by review.
  - **The fix for that is to put the rule where CPython can reach it.** `CAN_VERIFY_FORMS`
    and `can_verify` live in `versio_types.py`, which is pure, so the planner's copy of the
    rule can be compared against the real one without CODESYS. A rule that lives in
    `versio_import.py` is a rule nothing can compare.
- **Measure the premise before building on it, and measure the right thing.** The native check
  was designed with GUIDs unmasked, on the strength of `normalize`'s own docstring saying
  GUIDs are stable across exports. They are — across two exports. **Across a round trip they
  are not**, and the first measurement taken was of two exports into empty directories, which
  is the determinism gate and shows no churn at all. The design was wrong for one measurement
  and right for the next; both took ten minutes and neither cost any code.

**What to be careful of, learned building `versio_native_equiv.py`:** a parity test can be
green and watching nothing. Two of its rules — the ordinal sort key and the container table's
order — were claims the module's comments made that no case exercised, and only deliberately
breaking each one found that out. **Write the mutation list before believing the suite.**

**What to be careful of, learned building Phase 1:**

- The plan document originally placed the planner in `Versio.Core/Projects/` — a vendor leak
  the seam test would have caught late. New import machinery goes behind
  `IPlcProjectFormat` / `Versio.Core.Plc.Codesys` unless it is genuinely vendor-neutral.
- The wire tokens (`text` / `typed` / `native` / `create` / `skip`, the skip reasons, and now
  the `deletions` block) are dispatch strings in `versio_import.py`. `ScriptProtocolTests`
  pins them; a C# enum rename must not reach the JSON. **`deletions` is omitted entirely when
  there is nothing to delete** — an always-present empty list would put the most dangerous word
  in the protocol into every job file, and a key that is normally there is a key nobody looks
  twice at on the day it is not empty.
- Mutation-check anything that looks like a pure move. Three rules here (G3, G5, G6) were
  each deliberately broken to prove a test goes red before the suite was believed.

## Verification

1. `dotnet test tests\Versio.Core.Tests` — pure, ~0.3 s. Carries the bulk of the new coverage.
2. `dotnet test tests\Versio.Integration.Tests --filter PythonFileName` — parity, 51 tests,
   ~11 s. Needs `python` on PATH and skips cleanly without it, so **a green run on a machine
   with no python has verified nothing** — check the count, not the colour.
3. `tools\Test-RoundTrip.ps1 … -MutateFirst` — the per-layer verdict stays **PASS**, and
   `Task Configuration.xml` drops out of the native-differs list once Phase 4 lands.
4. **The G10 acceptance test:** `tools\Measure-ImportStability.ps1` must print **`P7: SETTLES`**
   **and report zero warnings.** Two halves, and only one is done. **The zero-warnings half passes
   as of Phase 4** — all three cycles clean, where cycles 2 and 3 used to throw twice each. It
   still prints `P7: CHURNS`, and correctly: the four files left are the three visualisations and
   `GlobalTextList.xml`, which is G10 and Phase 5's job. A settled mirror with
   `NATIVE_IMPORT_ERROR x2` underneath it would be G11 hiding under a clean byte comparison, which
   is exactly how it went unnoticed in the first place.
5. **Regression gate:** a normal export stays byte-identical to the committed mirror for every file
   Phase 4 does not deliberately change.
6. **The Phase 2 acceptance test**, and it is the one that matters most now:
   `dotnet test tests\Versio.Integration.Tests --filter An_object_whose_file_is_deleted` —
   five CODESYS launches, ~2 min. It is the only thing in the repository that fails if Versio
   starts deleting objects it should keep, or stops deleting ones it should not. **Read the
   three halves as one claim**: a refusal test that never lets anything through and a delete
   test that never refuses are each satisfied by a broken implementation.
7. **The Phase 4 acceptance test**, and it is the cheapest of these to run:
   `dotnet test tests\Versio.Integration.Tests --filter A_task_priority_edited_in_the_mirror` —
   four CODESYS launches, 72–78 s, and it is the only thing in the repository that fails if the
   typed write stops reaching the project.
8. Full integration suite against real CODESYS (~10 min).

Build with `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"` — `dotnet` on PATH is runtime-only.
Close Versio.exe first; it locks its own DLLs.

---

## Risks

- **Deletion is irreversible in the project, and CODESYS will not object.** P3 measured it:
  removing a referenced object throws nothing and warns about nothing. Mitigated by the pre-import
  backup (ten copies), the confirmation naming every object, and the content check — not by a cap,
  which was declined deliberately.
- **A stale manifest is the main correctness dependency.** The content check catches the common
  case; the direction lock and commit gate cover the rest. **Partly answered:** a successful import
  does not re-export, so the manifest only refreshes on the next export — which is the safe
  direction for deletes, since an object created by an import is absent from the manifest and
  therefore never deletable on the strength of a missing file.
- **Phases 4 and 5 move committed bytes** — Phase 4 adds a `.tasks.yaml` per task configuration,
  Phase 5 changes when files are rewritten. Each wants its own review.
- **P5's reachability result is about creation only.** If a project acquires an object whose name
  needs encoding by some other route, the encoding and resolution paths must still work. Do not
  delete them.
- **Scope.** Phase 4 first and alone. Phases 1, 2 and 3 land together, after
  `versio_native_equiv.py`. Phase 5 and Phase 6 follow.

## Not doing

- **Two-way library references.** `add_library` / `add_placeholder` / `remove_library` exist and
  P9 proved `add_library` works, so export-only is now a *choice* rather than a limitation — worth
  revisiting, but it rewires what the machine compiles against and deserves its own change.
- **Deleting CODESYS's compile cache before an import.** Measured to fix G11, and **not needed**
  once Phase 4 lands. Kept here because it is the fallback if Phase 4 is ever deferred, and because
  the measurement was expensive to get.
- Making the import transactional — the pre-import backup covers it, and it is a much larger change.
- Any change to the export layout beyond Phase 4's additive `.tasks.yaml`.
