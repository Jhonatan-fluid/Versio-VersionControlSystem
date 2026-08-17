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

### 1. The project tree panel — the next piece of work, and the largest user-visible value

Two finished readers are idling with no consumer: `LibraryCatalog.Parse` has parsed the
exported `.libraries.yaml` for weeks, and the import plan's last blocked item —
`TaskCatalog.cs`, the C# reader for `.tasks.yaml` — is deliberately **not built until this
panel exists to consume it** ([`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md), *What is left*,
item 3; note its warning that `LibraryCatalog` already broke the "no reader without a consumer"
rule, so build `TaskCatalog` *with* the panel change, not before it).

What the panel needs, from `STATUS.md` §4 (*Finish the project tree*):

- **One node per library**, under its manager — the data exists and parses; a version bump
  should read as *that library*, not "the library manager changed"
- One node per task, once `TaskCatalog.cs` exists
- A **layer switcher** in the diff pane (`ST | native`) — objects already carry both layers
  and there is no way to see the other one
- Context menu; expansion state persisted per repository (it survives a refresh, not a window
  close)

### 2. The git panel's last phase — GP-K/GP-L

Eleven of twelve phases are built ([`phase1/GIT-PANEL-PLAN.md`](phase1/GIT-PANEL-PLAN.md)).
What is left: the **ahead/behind previews** and the small-UX list. The plan document ranks and
scopes them; nothing else depends on them.

### 3. Retry automatically when the IDE closes — deferred 2026-08-15, still the smallest item

The vendor's lock file vanishing is a watchable filesystem event; seeing it would make closing
CODESYS the retry, with no button. Nothing is broken while it waits — every path recovers on
the next save or on reopen — so it buys convenience, not correctness. `STATUS.md` §3.

### 4. Diff view: side-by-side and word-level intra-line — deferred 2026-08-15

The strongest case among the deferrals for being picked up first: the colours exist and are
contrast-checked, and the diffs being read are generated XML and ST where a long line often
differs by one identifier. A readability improvement to something already correct. `STATUS.md`
§5, and the original design in [`phase1/PLAN.md`](phase1/PLAN.md).

### 5. Corpus breadth, and the rows nobody can reach yet

Every field bug becomes a fixture before it is fixed (`STATUS.md` §8). The checklist's one
remaining NOT REACHED row is **C5** (`DELETE_UNREADABLE` — needs an object that refuses to
yield its text; none has been observed). The `native_partial` branch is likewise uncovered
because its only known trigger (G11) was removed. Both are labelled where they live
([`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md), checklist and honesty-fix 1) and neither can
be manufactured — they close when reality produces a specimen, not before.

### 6. Small, real, and unowned

- **`SAFEGUARDS.md` §5.H** — the repository vanishing while it is open. Nobody has costed it.
- **`OrphanScanner.cs:194`** — states "never deleted automatically because it is committed
  history" unconditionally; wrong for an export tree under an ignored path (`STATUS.md`,
  *Known loose ends*).
- **Stash UI** — withdrawn, not finished. The plumbing (`StashPushAsync` etc.) is kept and
  tested; six permanent controls in the narrowest column was the wrong design, and a better
  one is owed (`STATUS.md` §2).
- **The UI eyeball pass** — everything the UI audit landed is verified by geometry and
  automation tests, in both palettes, **by nothing that can see a screen**. A human pass is
  owed (`STATUS.md`, *Handoff — the UI pass*).
- **Five copies of the CPython-runner plumbing** — `PythonFileNameTests` holds four and
  `PythonLibraryRecordTests` a fifth, each writing three temp files (and each leaking the
  `GetTempFileName` base file its `.py` name is derived from). The fifth copy's own comment
  says consolidating them is its own change; it is, and it belongs to whoever next touches the
  parity project.

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
