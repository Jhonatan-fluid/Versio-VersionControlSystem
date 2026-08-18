# Versio — Phase 2

**Date opened:** 2026-08-17 · **This is the forward queue.** Phase 1 is complete and its four
planning documents are archived in [`phase1/`](phase1/), each marked done at the top. They are
records now — read them for reasoning, never for state. What is still open lives HERE, and only
here: `STATUS.md`'s old "What remains" queue points at this file instead of carrying a copy,
because one claim in two places is the defect Phase 1's documents caught in themselves five
separate times.

**What Phase 1 was**, in one paragraph: the CODESYS round trip made real and bidirectional. A
project exports to a reviewable per-object mirror deterministically; edits flow back through a
plan-driven import that creates, updates, deletes, renames and moves objects, refuses loudly
everywhere it cannot act, and never applies anything a person did not confirm. Thirteen
numbered gaps (G1–G13) were closed, guarded or decided; the feature checklist ran every import
behaviour against a real project; and the last two reachable rows closed on 2026-08-17. The
suite is seven projects, 2,319 tests, split by need — the half-hour CODESYS gate holds only the
53 tests that actually launch the toolchain.

**Documents that stay live** (not archived, because they are logs and references, not plans):

| File | Role |
|---|---|
| [`STATUS.md`](STATUS.md) | Where the project is: what works, the audits, the findings index |
| [`FINDINGS.md`](FINDINGS.md) | Every defect with its evidence, newest first — search it |
| [`SAFEGUARDS.md`](SAFEGUARDS.md) | What Versio refuses, asks about or waits for |
| [`vendors/codesys/DRIVING.md`](vendors/codesys/DRIVING.md) | How CODESYS is driven — API traps, forms, events, manifest schema |

---

## The queue, in order

Items 1–6 are the order the evidence argues for. Items 7 and 8 sit last **by the owner's
decision** (2026-08-17): no second vendor and no packaging until everything above them is
complete.

> **Items 1–4 and 6 are DONE, all on 2026-08-17, in one session on branch `Phase2_Begin`** —
> each struck through below with what actually happened. What remains open in this file:
> **item 5** (corpus rows that wait on reality producing a specimen — C5 and `native_partial`
> cannot be manufactured), the **human eyeball pass** (item 6's one unownable entry, now with
> a session's worth of new UI to look at), and **items 7–8**, which the owner sequenced last
> on purpose. The gate before believing any of it: the CODESYS suite, run at the end of the
> session over the Core changes it can see.

### 1. The project tree panel — the next piece of work, and the largest user-visible value

~~Two finished readers are idling with no consumer~~ — **both consumed as of 2026-08-17**:
`LibraryCatalog.Parse` feeds one row per reference, and `TaskCatalog.cs` — the import plan's
last blocked item, deliberately not built until the panel existed to consume it
([`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md), *What is left*, item 3) — was built *with*
its rows in one change, honouring that document's warning that `LibraryCatalog` had already
broken the "no reader without a consumer" rule.

What the panel needs, from `STATUS.md` §4 (*Finish the project tree*):

- ~~**One node per library**, under its manager — the data exists and parses; a version bump
  should read as *that library*, not "the library manager changed"~~ — **done, 2026-08-17.**
  One row per reference, chosen before system, version beside the name; the shell pairs a
  changed catalog with HEAD's copy via `ShowAtHeadAsync`, so the mark lands on the reference
  that moved — bumped `M`, added `A`, and a removed reference keeps a row marked `D` from the
  baseline. Guarded end to end: Core builder tests, an integration test over the committed
  fixture's two real catalogs, and an App flow test proven by deleting the shell's forwarding
- ~~One node per task, once `TaskCatalog.cs` exists~~ — **done, 2026-08-17, and the reader
  landed WITH its consumer**, as the warning above demanded. `TaskCatalog.cs` parses the
  emitter's one shape `LibraryCatalog` cannot — a nested scalar list, where a naive walk
  would read one task per POU — and the tree hangs one row per task under the two-file
  configuration leaf, in the catalog's own order, marked against HEAD the same way libraries
  are. A task deleted from the YAML keeps a `D` row deliberately: the import leaves the live
  task alone, so the reviewer needs to know the project still runs it
- ~~A **layer switcher** in the diff pane (`ST | native`) — objects already carry both layers
  and there is no way to see the other one~~ — **done, 2026-08-17.** Chips in the pane's
  header, one per layer of the shown object, labelled by what the file is (`ST`, `native`,
  `tasks`, `libraries`). Owned by the *shown path* rather than the tree selection —
  `ShowPathAsync` keeps the strip when the new diff is one of its layers and clears it for
  anything else, so every route into the pane behaves without knowing the strip exists.
  Proven by mutation: removing that reconcile turns exactly the two ownership tests red
- ~~Context menu; expansion state persisted per repository (it survives a refresh, not a window
  close)~~ — **done, 2026-08-17, and item 1 closes with it.** The menu carries Stage, Discard,
  Reveal in Explorer and Copy path — Stage and Discard only on rows with changed files of
  their own, because a library or task row shares its catalog's file and "stage this
  reference" would silently stage everything beside it. Discard reuses the git panel's own
  confirmed flow, generalised to name every file it will touch and only the ones that
  actually changed. History and Open-in-CODESYS from the original plan's menu are deliberately
  not here: per-file history filtering and launching the vendor IDE are their own features,
  not menu items. Expansion now survives the window via `.versio/local/tree.json`, written in
  `Detach` — the one funnel switching, closing and disposing all pass through — and restored
  before the first build, because rows take their expansion at construction. The menu needed
  its own theme style: a ContextMenu is a popup with its own HWND, the ToolTip lesson one
  control along, and Aero's white slab was what appeared until it got one

### 2. ~~The git panel's last phase — GP-K/GP-L~~ — **done, 2026-08-17. Twelve of twelve.**

~~Eleven of twelve phases are built ([`phase1/GIT-PANEL-PLAN.md`](phase1/GIT-PANEL-PLAN.md)).
What is left: the **ahead/behind previews** and the small-UX list.~~ What landed, and what
turned out to be already built:

- **§9d, the previews.** The ↑/↓ glyph split into two buttons, each opening its own list —
  what a push would send, what a pull would bring. The incoming half also names the objects
  the pull touches, through the same `ObjectIndex` and sentence the commit summary uses, and
  its diff range is **three dots on purpose**: two would count this machine's own unpushed
  edits as "arriving" on a diverged branch. Direction proven against real git on a real
  divergence; the strip follows the counts it explains — reloaded when they move, closed
  when its direction reaches zero.
- **§9h, the large-file preflight.** Asked at the last cheap moment — after the gate's own
  staging, before the commit — over a fresh `git status`. `LargeFilePreflight` is a stated
  subset of gitattributes semantics; a rule without `filter=lfs` deliberately does not count,
  because `binary -diff -merge` stops merge corruption and does nothing for a host's size
  limit. A question, never a refusal.
- **§10: items 2, 3, 5, 6 built** — the clean-tree sentence instead of two empty boxes, the
  remote's name in the branch card (`origin · host/org/repo`, parsed so a credential in an
  https URL can never reach a label), the subject counter coloured at 50 and 72, and
  Ctrl+Shift+P with both shortcuts in tooltips — on the *live* explanation only, because a
  key advertised at the moment it does nothing is noise, which the chrome test caught.
  **Items 1 and 4 were already built**: path trimming shipped with GP-J, and the unborn
  branch has had its own standing headline since GP-B's eleven-state machine — the archived
  plan's §10 predated both.

### 3. ~~Retry automatically when the IDE closes — deferred 2026-08-15, still the smallest item~~ — **done, 2026-08-17**

~~The vendor's lock file vanishing is a watchable filesystem event; seeing it would make closing
CODESYS the retry, with no button.~~ It does now, and with no button: the occupancy marker —
resolved through `IPlcProjectFormat`, so `Sync/` never learns a vendor's spelling — is watched
by the same `FileChangeWatcher` as everything else, and its deletion re-submits **both
directions through the ordinary gates**, exactly as if the user had saved. Deliberately not a
second door: the direction lock, hash gate and import confirmation all still answer, so a
side with nothing waiting is refused quietly and a blocked import comes back as the same
`AwaitingConfirmation` offer a person still confirms. The marker's rewrite storm — CODESYS
replaces it every few seconds while it holds the project — is told from a release by one
`File.Exists`, and stays silent, because it fires all day and nothing happened. Proven end to
end through the real watcher: create the marker, delete it, and the retry announces itself
with nothing but the filesystem in between.

### 4. ~~Diff view: side-by-side and word-level intra-line — deferred 2026-08-15~~ — **done, 2026-08-17**

~~The strongest case among the deferrals for being picked up first: the colours exist and are
contrast-checked~~ — they did, `Brush.Diff.AddedWord`/`DeletedWord` in both palettes, unused
since the day they were checked — and both halves are built:

- **Word-level emphasis.** Within a changed pair, the part that moved gets the word brush on
  top of the row tint and the syntax colours. A token-aligned common prefix/suffix trim,
  deliberately not an LCS: linear where LCS is quadratic, on inputs whose longest lines are
  exactly where the feature matters most, and identical for the corpus's own shape — one
  identifier changed in a long generated line. `Counter1 → Counter2` lights the whole
  identifier, because half a name is not a shorter version of it; a pair with nothing in
  common lights nothing, because painting every character says less than the tint already
  does.
- **Side-by-side.** A fold of the same parsed unified diff, never a second diff computation —
  the parser's own rule, extended: two view modes disagreeing about one file would be worse
  than one mode. Spanning rows keep the hunk header whole (and its stage button); a pure
  insertion is blank on the left rather than padded with a fake pairing. Split rows stretch
  to the viewport and clip long lines, with unified mode one click away — the trade is stated
  on the checkbox. Copy from the split view pastes the unified excerpt, markers on.
- `DiffView` is unsealed for one stated reason: a ListBox outside a window never realises a
  container, so the only honest test of what a row *paints* is a subclass that calls the row
  builder — the paint proven by mutation against the emphasis pass.

### 5. Corpus breadth, and the rows nobody can reach yet

Every field bug becomes a fixture before it is fixed (`STATUS.md` §8). The checklist's one
remaining NOT REACHED row is **C5** (`DELETE_UNREADABLE` — needs an object that refuses to
yield its text; none has been observed). The `native_partial` branch is likewise uncovered
because its only known trigger (G11) was removed. Both are labelled where they live
([`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md), checklist and honesty-fix 1) and neither can
be manufactured — they close when reality produces a specimen, not before.

### 6. Small, real, and unowned — **all four owned items closed 2026-08-17**

- ~~**`SAFEGUARDS.md` §5.H** — the repository vanishing while it is open. Nobody has costed
  it.~~ — **costed, in §5.H itself, and deliberately not built**: detection and the suspended
  state are roughly a day; the hard part is classifying a mid-run toolchain failure against a
  vanished share as environmental rather than feeding it to the circuit breaker, which
  touches the engine's most safety-critical classification and should not be reshaped as a
  side quest. Moves up the first time a real share drop bites somebody.
- ~~**`OrphanScanner.cs:194`**~~ — **reworded.** The finding claimed "because it is committed
  history", which a pure scan cannot know and which was false for an export tree under an
  ignored path. The behaviour was always right for a simpler reason, and the message says
  that now: never deleted automatically, what it is worth is a person's call, and it *may*
  be committed history.
- ~~**Stash UI** — withdrawn, not finished~~ — **rebuilt as the inversion of what was
  withdrawn**: zero permanent controls, a section that exists only while `git stash list`
  has rows (the conflicts section's own rule), Apply that keeps and Drop that confirms and
  re-resolves the positional ref by SHA. No save button — the auto-stash owns taking, and
  the deliberate case belongs to a terminal.
- **The UI eyeball pass** — still owed to a human, and this session added to what it covers:
  library and task rows, the layer chips, the context menu (and its newly themed
  ContextMenu style), the ahead/behind previews, the clean-tree line, the coloured subject
  counter, the emphasis colours and the split view, and the stash section.
- ~~**Five copies of the CPython-runner plumbing**~~ — **one now**: `CPythonHarness`, with
  the deadlock-avoiding stream drain every copy should have had, one temp directory deleted
  whole (closing the `GetTempFileName` base-file leak), and the python probe cached per
  process. The parity suite dropped from ~15 s to ~8 s with it.

### 7. A second vendor — deliberately last (decided 2026-08-17)

**Not started until everything above is complete** — the owner's call: the first vendor's
product surface gets finished before the seam takes on a second. When it does start: the seam
is ready and unproven, and only a real second implementation shows whether `PlcJob` and
`PlcResult` are the right shape. TwinCAT is the cheapest test (CODESYS-derived), Studio 5000
the harshest. `STATUS.md` §6, `README.md` *Vendor boundaries*, and the rule in `CLAUDE.md`:
nothing in `Sync/`, `Git/`, `Locking/`, `Watching/`, `Projects/` or `Diff/` may change.

### 8. Packaging — deliberately last (decided 2026-08-17)

Same call as the second vendor: not until the rest is complete. `third_party/` vendoring with
SHA-256 pinning; Velopack (`Setup.exe`, MSI, delta updates); **code signing — procurement lead
time, so it is the one piece worth starting early once the queue above is nearly done**.
`STATUS.md` §7.

---

## Decisions taken, and why — the part a newcomer must not re-litigate

Each of these was closed **on purpose**, with the reasoning recorded. Reopening one is allowed;
reopening one *unknowingly* is how Phase 1's documents kept catching themselves, so they are
listed here even though every one lives in an archived document.

| Decision | Why | Where the full reasoning lives |
|---|---|---|
| **G9's 190-character file name: report-only, permanently** (2026-08-17) | Preventing it means shrinking `MaxStem` or dropping the parent prefix — either renames committed member files in every mirror and makes every file name worse, to prevent a case the two guards already report. Decided *now* deliberately, because "later" was quietly "never" | [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md), *What is left* item 1 |
| **E2 (percent-encoded stems): closed as measured-unreachable** (2026-08-17) | Every character the encoding exists for is illegal in a Windows filename, refused by CODESYS's validator on both creation doors (P5, P12), or triggers the shortened form. No producer exists on 3.5.22.20. The machinery stays, proven by unit tests | [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md), E2 row and P12/P13 |
| **Visualisation bindings after a rename: unmeasured, by decision** (2026-08-17) | The owner does not use CODESYS visualisations. The risk sentence stays in every document that carried it — a project that *does* use them inherits the question before trusting a rename | `STATUS.md` (import block), [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md) §Phase 3 notes |
| **External-file objects: export-only (`EXTERNAL_NATIVE`), IDE-managed** (2026-08-17) | Measured, not chosen: `import_native` **duplicates** the object instead of replacing it — one more copy per import (G13). No importable sibling exists; the payload is metadata nobody could meaningfully edit | `FINDINGS.md` (G13 entry), [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md) G13 row |
| **Libraries: export-only; tasks: never created by an import** | Rewiring what the machine compiles against, and adding to what it runs, are changes an import must not smuggle in. Both deliberate, both announced when skipped | [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md), *Not doing* and Phase 4 |
| **Deletion: named confirmation, no cap — and refusals as the architecture** | P3 measured `remove()` dangling referenced objects silently, so the five planner preconditions and the in-process content check are the *only* protection that exists. A threshold was declined deliberately | [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md), *Decisions taken* and Phase 2; `SAFEGUARDS.md` |
| **The phase-split save: unconditional** (2026-08-17) | The old condition (`created or updated`) had the dirty-test bug's shape — it did not count renames, which mutate the very dictionary the guard protects. A condition that cannot be wrong beats one that every new phase must remember to extend | git log `f62eb05`; `versio_import.py`'s comment at the save |
| **Three vendor test projects, split by need** (2026-08-17) | 334 of the old gate's 386 tests needed no toolchain and hid behind a half-hour wall. Need — nothing / python / real CODESYS — is the same axis the repo already splits Core/Integration/App on | `STATUS.md` table; git log `d480afe` |
| **Staging not extracted; `SyncViewModel` not split; repository session not extracted** | Measured borrow counts, not taste: each is "a move dressed as a decomposition" (GP-E's phrase). The instruments the effort produced (`CollaboratorSeamTests`, the binding-path test) outlast any line count | `STATUS.md`, *the code-shape queue* |
| **Stash withdrawn from the UI** | Wrong design, not wrong feature; plumbing kept and tested | `STATUS.md` §2 |
| **Second vendor and packaging: last, after everything else** (2026-08-17) | The owner's call: finish the first vendor's product surface before the seam takes on a second, and package what is finished rather than what is moving | This file, queue items 7–8 |

---

## How to start

1. Read `CLAUDE.md` end to end — the rules are load-bearing and each one names what it cost.
2. Read `STATUS.md` *Where the project actually is* and *Known loose ends*.
3. Pick from the queue above — item 1 unless something changed.
4. The loop: `Versio.Core.Tests` + `Versio.Integration.Tests` + `Versio.Arch.Tests` +
   `Versio.Plc.Codesys.Pure.Tests` + `Versio.Plc.Codesys.Parity.Tests`, about thirty seconds;
   `Versio.App.Tests` when the UI is in play; `Versio.Plc.Codesys.Tests` is the gate before a
   commit — half an hour, every test launches real CODESYS.
5. When something surprises you, search `FINDINGS.md` before deriving it from scratch — an
   audit once re-derived, over an hour, what was already written there by name.
