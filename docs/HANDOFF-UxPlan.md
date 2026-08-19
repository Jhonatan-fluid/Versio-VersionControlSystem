# Handoff — branch `UxPlan`

**Date:** 2026-08-18 · **Base:** `a40900b` (main) · **Read this before merging the branch.**

A UX pass over the running shell, plus one piece of lock-model work the pass turned up as
necessary. [`UX-PLAN.md`](UX-PLAN.md) is the plan and the running record; this page is only what
somebody taking the branch over needs in front of them.

---

## Do not merge to main until these three are done

Nothing below is a defect. They are things this branch could not do for itself.

| | What | Why it matters |
|---|---|---|
| **1** | **Run `Versio.Plc.Codesys.Tests`** — the ~30 min real-CODESYS gate | The branch changes `SyncOrchestrator` and the direction lock: the sync path that drives the toolchain. The standing rule skips that gate only for changes touching no vendor code, and this is the case the rule names as the exception. **It has not been run.** |
| **2** | **Look at the UI** | Every visual change here has been asserted arithmetically and **none of it has been seen**. Details below. |
| **3** | **Decide UX-12** | Amend-while-unpushed was approved by the owner and never built. It is the one agreed item this branch owes and did not deliver. |

---

## What to look at, specifically

The suite proves these resolve, lay out and are reachable. It cannot say whether they look right.

- **The rail badge** (git icon, top-left). Appears only with working-tree changes. It has **no
  background** — deliberately, because the tile behind it is transparent at rest,
  `Brush.Surface.Hover` under the pointer and `Brush.Accent.Muted` while the panel is showing,
  and no single colour works against all three. Check it reads in all three states, and in both
  themes.
- **The branch overflow** — the chevron at the right-hand end of the Fetch / Pull / Push row.
  Two things to judge: the popup carries its own window handle so it inherits none of the app's
  palette (check it is not a white slab), and **the button is a bare glyph** because the caption
  would not fit there. Measured at 260 px: 49 px available, `Branch ⌄` wanted 68, so Push sat
  underneath it. The meaning moved to the tooltip. If an anonymous chevron reads badly, the
  alternative is to put it back on its own line with its caption.
- **The staged / not-staged headers at 260 px.** Drag the panel divider fully left. The header
  should trim with an ellipsis and the buttons stay inside the card.
- **The merge dialog** (`Branch ⌄ → Merge a branch into …`). Never opened by a human.
- **The row context menus** (right-click a changed file) and **double-click to stage**.

---

## The shape of the risk

**The UI work is low risk.** Counts, menus, a keyboard shortcut, a layout fix. Worst case is
something looks wrong.

**LOCK-1 is not.** It changes what happens to the direction lock — the mechanism that exists to
stop Versio destroying somebody's PLC work — and it does so on a path that previously did not
exist. It is covered by eight Core tests, every rule mutation-checked, and it was read four
times. **It has still never been run against real CODESYS.**

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
them, and a fifth pass by the same reader is worth less than the two checks in the table at the
top.

---

## Known gaps, measured rather than suspected

- **The App-level wiring from `plan.Affected` into the lock has no test.** Mutating
  `adoptResult: !plan.NeedsImport` to `true` leaves **every suite green**. Covering it needs a
  temp repository carrying a real binding and orchestrator, which `TempRepo` does not build.
- **P-M2 has not run.** Whether the commit gate skips the export after a merge is inference from
  reading the gate, not a measurement. It needs a real export.
- **`GitPanelGeometryTests` builds its shell with `using var`** and hands the disposed instance
  to every test — the identical defect already fixed in `GitPanelChromeTests`. Pre-existing,
  left alone deliberately, spawned as its own task.

---

## What is on the branch

Twenty-one commits. The git log is the documentation; each message says why, what it cost, and
what was rejected.

**Built:** UX-2 + UX-11 (the change count moved to the rail, the panel-title pill deleted),
UX-4 (list headers carry their count and stop overflowing at 260 px), UX-5 (row context menus
and double-click to stage), UX-3 + UX-1 (the `Branch ⌄` overflow and the merge verb),
UX-7 partly (`Ctrl+1/2/3`), and LOCK-1 (a merge puts the mirror in charge; the export gate is
refused before the toolchain runs).

**Not built:** UX-6 (search boxes), UX-8 (copy a commit id), UX-9 (`+2 more` is a dead end),
UX-10 (detached HEAD offers no exit), UX-12 (amend). All still described in
[`UX-PLAN.md`](UX-PLAN.md) with their evidence.

**Found and fixed on the way, unrelated to the plan:** `merge=lfs` was cancelling the `-merge`
that protected every project file — a live defect in the generated *and committed*
`.gitattributes`, measured on two throwaway repositories. That one is worth reading before
anything else in `FINDINGS.md`.

**Measured on the way:** probe P-M1. A merge really does commit a project its mirror disagrees
with — 41 mirror files, two carrying content the kept project does not have. That measurement is
the reason LOCK-1 exists.

---

## Suites, re-derived by running them

| Project | Count |
|---|---|
| `Versio.Core.Tests` | 1,410 |
| `Versio.Integration.Tests` | 147 |
| `Versio.Arch.Tests` | 14 |
| `Versio.App.Tests` | 581 |
| `Versio.Plc.Codesys.Pure.Tests` | 260 |
| `Versio.Plc.Codesys.Parity.Tests` | 92 |
| `Versio.Plc.Codesys.Tests` | **54, unverified — not run on this branch** |

Build is warning-free. `Versio.App.Tests` flakes about one run in eight on a stash test that
drives git subprocesses; it passes in isolation and on a re-run, and it is the documented flake
rather than anything on this branch.
