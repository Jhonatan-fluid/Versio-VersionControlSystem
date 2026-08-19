# Handoff — `UxPlan`, merged into `Siemens`

**Date:** 2026-08-19 · **Merged:** `UxPlan` (`cd79bdc`) into `Siemens` (`77ed0c9`).
**Not on `main`.** [`UX-PLAN.md`](UX-PLAN.md) is the plan and the running record; this page is
what somebody taking the combined branch over needs in front of them.

A UX pass over the running shell, plus one piece of lock-model work the pass turned up as
necessary, plus the TIA Portal pack that `Siemens` already carried. The two lines of work
shared `main` (`a40900b`) and had not been merged until this session.

---

## Before this goes to `main`

Nothing below is a defect in the merge. They are the three items the original `UxPlan` handoff
named, with what actually happened on 2026-08-19.

| | What | Status |
|---|---|---|
| **1** | **Run `Versio.Plc.Codesys.Tests`** — the ~30 min real-CODESYS gate | **In this session.** `LOCK-1` changes `SyncOrchestrator` and the direction lock: the sync path that drives the toolchain. The standing rule skips that gate only for changes touching no vendor code, and this is the case the rule names as the exception. Counts land in the table at the foot once the run finishes. |
| **2** | **Look at the UI** | **Done 2026-08-19**, owner, on the merged `Siemens` build opened against this repository. Explorer is back on the rail; Fetch / Pull / Push and the branch overflow are where they were. The merge dialog itself was not walked. |
| **3** | **Decide UX-12** | **Still owed.** Amend-while-unpushed was approved by the owner and never built. |

---

## What was looked at

The suite proves these resolve, lay out and are reachable. It cannot say whether they look right;
the owner saw the merged window on 2026-08-19.

- **The fourth rail button** (repo explorer). Lists the open clone as a folder tree; grafted PLC
  projects use the same `BuildProjectNode` / `TreeRow` / `ShowNode` as the Project panel;
  clicks visualize and never ask git for a diff.
- **The rail badge** (git icon, top-left). Appears only with working-tree changes. It has **no
  background** — deliberately, because the tile behind it is transparent at rest,
  `Brush.Surface.Hover` under the pointer and `Brush.Accent.Muted` while the panel is showing,
  and no single colour works against all three.
- **The branch overflow** — the chevron at the right-hand end of the Fetch / Pull / Push row.
  The popup carries its own window handle so it inherits none of the app's palette. The button
  is a bare glyph because the caption would not fit there (measured at 260 px: 49 px available,
  `Branch ⌄` wanted 68).
- **Fetch / Pull / Push** stay on that row. A later uncommitted UI-audit on `Siemens` had
  collapsed them while in sync; that overlay is in `stash@{0}` and was **not** applied, because
  it would have fought this merge.

Still worth a look if a later session opens the dialogs: the merge picker
(`Branch ⌄ → Merge a branch into …`), row context menus, and the staged / not-staged headers
dragged to 260 px.

---

## The shape of the risk

**The UI work is low risk.** Counts, menus, a keyboard shortcut, a layout fix. Worst case is
something looks wrong, and the owner has now seen the window.

**LOCK-1 is not.** It changes what happens to the direction lock — the mechanism that exists to
stop Versio destroying somebody's PLC work — and it does so on a path that previously did not
exist. It is covered by eight Core tests, every rule mutation-checked, and it was read four
times. **It had never been run against real CODESYS until the gate in this session.**

### Four passes, four defects, all in LOCK-1 and its neighbours

Recorded because the pattern is the useful part: **none was findable by running the suite**, and
each came from asking a different question rather than from looking harder.

| pass | question | found |
|---|---|---|
| 1 | is the new command's gate right? | `MergeBranchCommand` was live on a **detached HEAD** — it would have made a merge commit reachable from no branch |
| 2 | does the code do what the comment claims? | the merge was **adopting after all**, recording a project/mirror disagreement as synced, while the comment said it did not |
| 3 | what happens on the *undo*? what does this return value mean? | abandoning a merge **left the lock with no way out**; and `NoteMergedTextAsync` **silently did nothing** when the project already had unexported edits |
| 4 | what happens when it *throws*? | a cancelled merge ran **straight past the marking**, leaving the merged mirror open to the export gate |

Every one is written up in [`FINDINGS.md`](FINDINGS.md).

**The honest inference is about the writing, not the reviewing.** Four defects in one feature is
a signal that a lot of code was produced quickly in the subsystem this repository calls its most
dangerous. The reviews caught them, which is the process working — but the same process wrote
them, and a fifth pass by the same reader is worth less than the CODESYS gate.

---

## Known gaps, measured rather than suspected

- **The App-level wiring from `plan.Affected` into the lock has no test.** Mutating
  `adoptResult: !plan.NeedsImport` to `true` leaves **every suite green**. Covering it needs a
  temp repository carrying a real binding and orchestrator, which `TempRepo` does not build.
- **P-M2 has not run.** Whether the commit gate skips the export after a merge is inference from
  reading the gate, not a measurement. It needs a real export.
- ~~**`GitPanelGeometryTests` builds its shell with `using var`**~~ — **fixed on `UxPlan` before
  the merge.** The helper no longer disposes at its closing brace; shells are held until the
  fixture's `Dispose`, on the STA thread that built them.

A merge-only defect showed up on `Siemens` after the join: Fetch opened a log scope, then the
refresh it always runs opened a second one, so one click could not be filtered as one
operation. Refresh now `JoinOrBeginOperation`s (`177cd48`). Watcher ticks still get an id of
their own.

---

## What landed (and what did not)

The git log is the documentation; each message says why, what it cost, and what was rejected.

**From `UxPlan`:** UX-2 + UX-11 (the change count moved to the rail, the panel-title pill
deleted), UX-4 (list headers carry their count and stop overflowing at 260 px), UX-5 (row
context menus and double-click to stage), UX-3 + UX-1 (the `Branch ⌄` overflow and the merge
verb), UX-7 partly (`Ctrl+1/2/3/4`), UX-9 (`+2 more` is a button), UX-10 (detached HEAD offers
an exit), UX-13 (the guard-intent comment), LOCK-1 (a merge puts the mirror in charge; the
export gate is refused before the toolchain runs), the repo explorer, and the log-panel
interaction fixes.

**From `Siemens`:** the TIA Portal pack, tree-payload support, CleaningStation as the gate
fixture, Openness driving notes. Item 7.1 in [`PHASE2.md`](PHASE2.md) is still open: generate
does not overwrite.

**Not built:** UX-6 (search boxes), UX-8 (copy a commit id), UX-12 (amend). All still described
in [`UX-PLAN.md`](UX-PLAN.md) with their evidence.

**Found and fixed on the way, unrelated to the plan:** `merge=lfs` was cancelling the `-merge`
that protected every project file — a live defect in the generated *and committed*
`.gitattributes`, measured on two throwaway repositories. That one is worth reading before
anything else in `FINDINGS.md`.

**Measured on the way:** probe P-M1. A merge really does commit a project its mirror disagrees
with — 41 mirror files, two carrying content the kept project does not have. That measurement is
the reason LOCK-1 exists.

The uncommitted UI-audit from 2026-08-19 (empty states, TabChip, health summary, Fetch collapse)
is **`stash@{0}`**, not on this branch. Do not pop it onto `Siemens` without a diff against the
merged git panel and rail.

---

## Suites

Re-derived after the merge's fast loop, 2026-08-19. The full solution run (including the
CODESYS gate and the TIA Portal gate) is in flight in the same session; this table is updated
when it finishes.

| Project | Count |
|---|---|
| `Versio.Core.Tests` | 1,432 |
| `Versio.Integration.Tests` | 148 |
| `Versio.Arch.Tests` | 14 |
| `Versio.App.Tests` | 612 |
| `Versio.Plc.Codesys.Pure.Tests` | 260 |
| `Versio.Plc.Codesys.Parity.Tests` | 92 |
| `Versio.Plc.TiaPortal.Pure.Tests` | 15 |
| `Versio.Plc.TiaPortal.Tests` | 4 — import/re-export Fact is **red** (generate does not overwrite) |
| `Versio.Plc.Codesys.Tests` | 54, **gate running this session** |

Build is warning-free. `Versio.App.Tests` still flakes on git-subprocess tests; a lone red run
is worth re-running before believing it.
