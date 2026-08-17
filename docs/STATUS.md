# Versio — status and route to completion

**Date:** 2026-08-16 · **Commits:** `git rev-list --count HEAD` · **Tests:** 1,485 unit + 480 UI + 230 integration, 0 skipped
· *(integration now runs ~30 min, not the 10–16 it did before the import feature checklist added
13 tests; run three times end to end on 2026-08-16 with no flake, plus every affected test
individually — see the flake note in `CLAUDE.md`, which is about earlier runs)*

> **⚠️ Versio removes, renames and moves objects in projects — Phases 2 and 3 of the import
> plan, and the first things it does that destroy work.** Delete a `.st` from the mirror,
> confirm the import, and the POU leaves the project. Read this before anything else in this
> file.
>
> **Renaming is the safe half of that pair and is still worth knowing about**: a file renamed
> in the mirror renames the *object*, keeping its GUID, and any task that ran it is repointed.
> What Versio does **not** fix is a reference held by name that it does not know about — a
> visualisation binding has never been measured.
>
> **It is built as a set of refusals with one door**, because P3 measured that CODESYS's
> `remove()` takes referenced objects with it silently — no exception, no cascade, no warning,
> so there is no toolchain backstop. The planner proposes a deletion only when five
> preconditions hold (a trusted manifest, every file for the GUID gone, export-only records
> neither voting nor vetoing, nothing surviving inside it at any depth, an owner chain recorded
> rather than guessed); the confirmation dialog names every object; and the script makes the
> one check only it can — that the live object still matches what was exported. **An object
> edited in the IDE since the last export is kept**, because a missing mirror file says
> somebody deleted a file, not that nobody touched the object.
>
> **Native objects are deleted too, as of the same day** — a GVL, a visualisation, a native
> POU. The manifest's raw hash could never answer for them: it is over exported native bytes,
> and a re-export of an untouched native object changes every cycle (P7). **Manifest schema 5
> records the hash of the NORMALISED payload as well**, so a re-export has something to be
> compared against, and the old side no longer has to come from the file that was deleted.
> **A native object on a pre-schema-5 mirror stays refused until the next export** — an absent
> hash is not evidence of an unchanged object.
>
> **The dialog says which objects will really go and which will really stay**, separately. It
> did not for one commit: the script grew `remove()` while the dialog went on saying objects
> *will stay*, and a green suite did not notice because the test pinning the wording asserted
> the old sentence. It is the worst defect this project has shipped, it is written up in full
> under Findings, and it is the reason the planner now predicts the script's own refusal.
>
> Proven against real CODESYS: a changed object is refused, an honest one is removed, a GVL
> goes only when both its files check out — and, for the native check specifically, four
> scenarios including one that makes a genuine IDE edit and demands the deletion be refused.
> **Nothing else in the repository fails if this starts deleting the wrong things.**

> **The import is plan-driven as of 2026-08-15 — Phase 1 of the import plan.** What an import
> will do is decided C#-side by `CodesysImportPlanner`, pure, from `_manifest.json` and a
> directory listing, before CODESYS starts; the job file (schema 2) carries the plan and
> `versio_import.py` executes exactly it, deciding nothing. The filename-derived walk — the
> shared root cause of gaps G3–G6 — is deleted, and a planless import job refuses to
> serialize, so there is no door back to it. G3 and G6 are closed and **proven against real
> CODESYS**: `PROGRAM Foo` in `Bar.st` creates `Foo`, and a `README.md` in the mirror is
> warned about instead of being handed to CODESYS as native XML. See
> [`CODESYS-IMPORT-PLAN.md`](CODESYS-IMPORT-PLAN.md).

> **Also 2026-08-15, on branch `GitImportPlan`: Phase 0 of the import plan started, and its
> first probe found a defect that reaches users.** P7 measured what an import → export cycle does
> to an unedited mirror — it churns forever, +3 per cycle, from two causes rather than one — and
> in passing turned up that **every import after the first silently fails to apply task
> configuration** on any machine where the project has been opened in the IDE. Root cause: a
> stale `_project.precompilecache`. One half is fixed (`TransientPatternsFor` now ignores it);
> **the import half is fixed too, later the same day — by Phase 4, which stops handing task
> configuration to `import_native` at all, so the mitigation was never needed and Versio deletes
> nothing of CODESYS's.** All three suites re-run green
> afterwards — **1,385 + 478 + 192, 0 skipped**, integration 6m57s against real CODESYS. See the
> findings, and [`CODESYS-IMPORT-PLAN.md`](CODESYS-IMPORT-PLAN.md) for what P7 changed in the
> plan itself.
>
> **Phase 4 of the import plan landed the same day, and it is the first product change this
> work has produced beyond a `.gitignore` line.** Task configuration is exported as `TASK_BOTH` —
> the native `.xml` for fidelity, a `.tasks.yaml` that is the importable half — and applied back
> through typed property assignment instead of `import_native`. **G7 and G11 are both measured
> closed:** `Core2` stopped moving, and three import cycles came back with zero warnings where
> cycles 2 and 3 used to throw twice each. Versio deletes nothing of CODESYS's; the trigger is
> simply never reached. Proven non-vacuously — editing the YAML and importing changes the project
> and reads back changed.
>
> **And then a second read of Phase 4 found what closing G7 and G11 had opened.** Making the
> `.tasks.yaml` the importable half made the `Task Configuration.xml` beside it a file the export
> writes and the import silently discards — **the exact definition `_coverage.md` exists to
> report** — and nothing reported it: the XML was still recorded `NATIVE_R`, so the coverage
> report skipped it and the panel drew it as an ordinary editable row. It is recorded
> `TASK_NATIVE` now, one-wayness is read from one list per side, and three guards watch it, each
> **verified by breaking it deliberately**. The clearest evidence is that the code already knew:
> `TaskBoth`'s own documentation said "its native XML is export-only" while the predicate 114
> lines below in the same file still answered "libraries only" — a predicate that had **no caller
> and no test in its entire life**. See the finding.
>
> **Phase 0 is now complete — all nine probes measured.** `rename` and `move` preserve identity
> across a save and reopen, so Phase 3 is safe to build; the typed task API is sufficient, so
> Phase 4 is; awkward object names cannot be created at all, which closes two gaps as
> unreachable; and **four of the seven API spellings the plan had were wrong**, because
> reflection says a member exists without saying what it accepts. The one bad answer is P3:
> `remove()` deletes an object other objects reference **silently** — no throw, no cascade, no
> warning — which makes Phase 2's preconditions the only protection there is rather than a
> second layer of one.
>
> **One entry in that work was recorded as a cause before it was tested, and was wrong.** It is
> kept in the finding rather than tidied away, because it is the clearest example in this file of
> the difference between a story that fits the evidence and a cause.

> **All three re-derived on 2026-08-15**, on branch `CodeAuditCleanup`, after the shell
> decomposition: `GitAuthViewModel`, `GitRemoteViewModel` and `LogPanelView` out of
> `ShellViewModel` and `MainWindow.xaml`, which go **5,573 → 4,958** and **1,218 → 936**. The
> **integration suite was re-run in full against real CODESYS — 192 passed, 0 skipped, 7m02s**,
> including `RepositoryWatcherTests.A_deleted_file_is_reported`, the one noted below as flaky,
> which passed. Before that, in the same session: the log console disposal leak, the shells two
> window helpers could not reach, the silent repository-lock release, and the thirteen binding
> errors that had been filling the tracer.
>
> **The code-shape queue is closed** — see *Deferred, and not on the critical path*, and the
> queue itself, for what was declined and why. Four product items are deferred there with the
> reason each one waits and what would bring it back.
>
> Earlier, on 2026-08-14, on branch `GitPanel`: the git-panel plan's **GP-J** (PLC-aware naming
> of the changed-files rows, the change summary and the drafted commit subject), **GP-I** (the
> merge-request button across five forges), **safeguards §5.C** (the import confirmation, which
> the app had been answering for itself), **§5.E** (a way to reach the backups that were being
> written and read by nothing) and **§5.G** (one repository, one Versio).

> **The commit count is the command now, not a number.** It was wrong by twelve when an audit
> first checked it, and wrong again twice in one afternoon afterwards — including once by the
> very commit that corrected it, which is the shape of the problem: a number that changes with
> every commit cannot be written down in a file that every commit touches. The test counts stay
> as numbers because they only move when the tests do, and `dotnet test` prints a total per
> suite when they have.
>
> **And that argument has now failed once, which is worth keeping rather than quietly
> correcting.** The UI count sat at 455 while the suite ran 463: the commit that added
> `CommandNotificationTests` added eight cases and did not re-derive the header. "They only move
> when the tests do" is true and is not the same as "somebody notices when they move" — the
> defence is re-deriving before believing, not the number being slow.

> One integration test, `RepositoryWatcherTests.A_deleted_file_is_reported`, is **flaky under
> full-suite load** — see Findings. It passes in isolation and failed once during a complete
> run. Known, not fixed.

---

## Where the project actually is

The risky half is done. Every hard question the plan raised about CODESYS has been answered by
measurement against a real project, and the domain logic that could lose someone's work is
written and tested without needing CODESYS, git, a clock or a UI to exercise it.

**Versio does the thing it was built for, in both directions, automatically.** A project in the
repository is tracked without ceremony, exported to a reviewable mirror when it changes, and
edits to that mirror flow back into the project — driven by the direction lock, proven end to
end against the real fixture with real CODESYS.

**And the shell has been audited as a UI, not only as a set of features.** Fourteen findings
across the stylesheet, the shell and the dialogs; twelve applied, each with a test that fails
against the old behaviour — keyboard focus made visible everywhere, horizontal scrolling fixed,
screen-reader names on every icon control, the last four `MessageBox` prompts brought into the
theme, and theme and panel sizes finally persisted. The two left open are in the loose ends
below, and both are deliberate. See Findings.

**And it now defends the half a mouse can reach.** Closing, switching branch, switching
repository and pulling all go through one guard; uncommitted work is stashed and offered back
with the sync state it had; an export can be killed at any instant without leaving a project
without a mirror; and what a killed run leaves behind is put right the next time the repository
opens. That is `SAFEGUARDS.md` phases 1–8, and phase 9 is features rather than guards.

```
  CODESYS pipeline    ████████████████████  proven on a real project
  Domain core         ████████████████████  1,485 tests, under 400 ms
  Vendor seam         ████████████████████  enforced from assembly metadata
  Infrastructure      ████████████████████  drives real CODESYS + real git
  Desktop shell       ███████████████████░  full git surface, diff, and a UI audit applied
  Packaging           ██░░░░░░░░░░░░░░░░░░  icon and publish profile only
```

### Proven by measurement, not assertion

| Question | Answer |
|---|---|
| Is the export deterministic? | **Yes** — and now enforced by a test that fingerprints the whole tree across two exports, after a regression proved the by-hand measurement was not enough |
| Does export → edit → import → export round-trip? | **Yes** for textual objects, proven with a deliberate mutation |
| Can native XML be imported without deleting the object? | **Yes** — `NativeImportHandler` replaces in place, GUID preserved |
| Can missing objects be created? | **Yes** — from a parse of the ST declaration header |
| Does the WPF tree survive a real project? | **Yes, but only when virtualized** — 94.7 ms vs 10,635 ms at the defaults |
| How long does a CODESYS launch cost? | **17–31 s.** Paid on every automatic export; the coalescer is what makes that bearable |
| Does an edited `.st` reach the project? | **Yes** — export, edit, import, verified byte-changed on the real fixture |
| Can library references be read at all? | **Yes** — `libman.references`, iterated. Not `get_libraries()`, which returns names only, and not `references[i]`, which indexes by id and raises for a position |
| Is a library reference's identity stable? | **No.** `ref.id` is re-minted on every open: 34 of 34 changed across two runs, with nothing else moving. Not recorded — the same trap as the `__VisualizationStyle` GUID, caught before it shipped this time |
| Does an unedited mirror ever stop churning? | **It does now.** It did not: `UniqueIdGenerator` climbed +3 per import→export cycle and never settled, four of eight native files differing forever from **two** causes rather than one. Phase 5 keeps the existing bytes when the fresh export is equivalent to them, and `Measure-ImportStability.ps1` now prints **`P7: SETTLES`** with the counter flat across three cycles |
| Is the mirror's churn *semantically* nothing? | **Yes** — all four churning files are `NativeXmlNormalizer`-equivalent, with symmetric mask counts on every one, which is what says they differ in the masked positions and nowhere else |
| Does a fresh export still reproduce the committed mirror? | **Yes** — a P7 baseline export into a different directory, from a project with a different filename, was byte-identical to `fixtures/projects/RefrigeratorControl/` on every object file. The only differences were `bindingId` and `project` inside `_manifest.json`, which are the two fields that *should* differ. An incidental re-confirmation of determinism across a path change, which the fingerprint test does not cover |

### Known limitations, carried deliberately

- **Library references are export-only.** They are exported now — that gap is closed — but the
  file is written for review and never applied back: adding a library through the scripting API
  is a different operation from importing an object, and getting it wrong rewires what the
  machine compiles against. Editing `Library Manager.libraries.yaml` in the mirror changes
  nothing in the project. The coverage report says so in its own section, and the importer
  skips the file by suffix rather than feeding YAML to a toolchain that expects XML.
  The edit is not undone either: it sits in the working tree until the next export rewrites
  the file from the project. Worth knowing before somebody stages one — **and the panel says so
  now**, as of GP-J: the changed-files row carries a lock glyph and a tooltip saying the edit
  does not reach the project. That was parked on tree work here for a while, which was the wrong
  home for it: the row somebody is about to *stage* is where the warning has to be, and the
  coverage sentence could never carry it because it only appears when there are gaps and this is
  not one.
- **How a library reference *resolves* is deliberately not recorded.** The file carries what
  the project asks for — placeholder, default resolution, redirection, parameters — and not
  what it resolved to on the exporting machine. Two engineers with different libraries
  installed would otherwise produce different bytes from the same project, and neither would
  be wrong.
- **Managed (fixed-version) library references are untested.** All 34 references in the fixture
  are placeholders, so the branch that reads `version`, `company` and `title` off a managed
  library is written from the API's description and has never run against a real one.
- ~~**Task configuration is diff-only.** `import_native` changes a task's `Core2` from `-1` to
  `-2` unasked.~~ — **no longer true, as of Phase 4 on 2026-08-15.** A task configuration
  exports as `TASK_BOTH` and its `.tasks.yaml` is applied back through typed property
  assignment, so `import_native` never sees the object and `Core2` stops moving. The `Core2`
  measurement itself still stands and is still pinned by a test — but that test reads
  **committed bytes captured before Phase 4**, so it documents what `import_native` did rather
  than guarding what Versio now does. **What is still limited, and deliberately:** the `.xml`
  half is export-only (`TASK_NATIVE`), a task named in the YAML but absent from the project is
  **warned about, never created**, and a task deleted from the YAML is **left alone** — removing
  one is a deletion, and deletions are Phase 2. See G7 and G11 in
  [`CODESYS-IMPORT-PLAN.md`](CODESYS-IMPORT-PLAN.md).
- **Graphical POUs export as native XML**, so their diffs will never read as well as ST.
  Inherent to the format.
- **Interfaces, text lists, image pools** have export rules written and unit tested but never
  exercised — the fixture project contains none.
- **One fixture project.** A compound/safety project and a graphical-heavy one are the obvious
  gaps in the corpus.
- **One vendor implemented.** The seam is real and enforced, but CODESYS is the only toolchain
  with a runner behind it.

---

## What works

### The sync engine

Automatic in both directions. A settled change to a `.project` exports; a settled change under
an export tree imports. Every project found in the repository is tracked automatically —
templates excepted, because a binding whose project is a template exports the template into its
own tree, which is incoherent rather than merely unwanted (R28).

**The locks are the defence, not defence in depth.** With nobody present to refuse anything,
the direction lock, the hash gate, the self-write suppressor and the circuit breaker are all
that stand between a save and an unwanted overwrite. Jobs run through the coalescer, whose
5-second cancel window and 30-second minimum interval mean a save storm produces one export
rather than one per keystroke.

**Import copies the project aside first**, into `.versio/local/backups/<binding>/`, keeping ten
per binding. A failed backup fails the import: doing the operation that can destroy the project
having skipped the step that makes it recoverable is the worst of both.

### The vendor seam

`IPlcRunner`, `IPlcProjectFormat`, `IPlcToolchain`, `PlcJob`, `PlcResult`, and the `PlcFormats`
/ `PlcToolchains` registries. Adding Rockwell, Siemens or TwinCAT means implementing those and
registering them; nothing in `Sync/`, `Git/`, `Locking/`, `Watching/`, `Projects/` or `Diff/`
may change.

`VendorSeamTests` enforces that from assembly metadata rather than describing it, and has been
proven able to fail. A binding records its own vendor; the scanner scans for every registered
format; `.gitattributes` covers every registered vendor's extensions.

### The shell

| | |
|---|---|
| **Project view** | **One panel.** Every project *with something to review* — a change, a mirror behind, a lock held, a failure, or never exported — carrying its full CODESYS-shaped object tree, with every other changed file beside them. A project whose mirror is clean is not listed, and the empty state says so rather than going blank. Search still reaches into a clean project, because the box searches the project and not the diff |
| **Git panel** | **Per-file staging** — a checkbox on every row, tri-state headers, and labels that say what will happen (`Stage 3`, `Stage all`); both panes scroll and a splitter divides them. **One commit button with three behaviours** — it stages what you ticked, or everything, or nothing at all when you have staged deliberately, and the caption says which before you press. Commit message with a 50/72 guide and Ctrl+Enter. **Where the branch stands, in words** rather than two arrows — eleven states covering unborn, detached, no remote, never published, ahead, behind and diverged, each naming its remedy. **Publish** for a branch with no upstream, which a plain push cannot do. Fetch / pull / push / discard. **Branches show where they live** — local only, remote only, or both — and remote-only ones are listed at all for the first time. A delete dialog removes them from this machine and optionally the remote, naming what is not merged and reporting per branch when one refuses. **Conflicts get their own section** with keep-mine / keep-theirs and an abandon, and a diverged branch is offered a rebase or a merge by name — before this, the badge said "pull first" and a fast-forward-only pull refuses a diverged branch, so the only way out was a terminal. **Notifications** that survive the status refresh, with failures sticky until dismissed and git's own words on them. **Credentials**: detected at open, handed off to the engineer's own helper, never stored — and a *forget credential* for the expired token no git GUI lets you clear. **The remote is watched in the background**, by `ls-remote` rather than by fetching, so a colleague's push shows up without a click. **Git's own health**: a launch probe that names PATH when git is missing, and an identity preflight so a commit is refused before a 17–31 s export rather than after it. One operation at a time across every button — a shared counted flag. **Changed files are named as objects** — `FB_Motor` with a `POU` chip and the path beneath, resolved through the export manifest, with a lock glyph on a file whose edits cannot reach the project; a file the manifest does not describe stays a path and is never guessed at. **A change summary and a drafted commit subject** — *"1 GVL and 1 POU changed in RefrigeratorControl"*, and a **Draft** button that writes `Modify FB_Motor, Glob_Var`, both counting objects rather than files and both describing exactly what the commit button will record. **A merge-request button** for GitHub, GitLab, Azure DevOps, Bitbucket and Gitea: it opens the forge's own page pre-filled and submits nothing, upgrades itself to *Go to* when the forge's hidden refs say one already exists — no API token, nothing stored — and shows **nothing at all** for a host it cannot identify. **No stash controls**: removed pending a different design; the auto-stash drives the client operations. See [`GIT-PANEL-PLAN.md`](GIT-PANEL-PLAN.md) |
| **History panel** | **Its own rail button**, not a strip inside the git panel. Every commit with its hash, local date and time, author and subject; expand one for the files it touched; click a file for what that commit did to it, in the shared diff pane. Lazy at four levels — the log is not walked until the panel is first shown, then one page of 50 at a time; a commit's files are read when it is expanded and cached for the session, because a commit's contents cannot change; a patch is read only when a file is clicked, and never cached. A merge says so rather than showing an empty list |
| **PLC state** | **No card, and no buttons.** Toolchain, version and profile were environment rather than repository state and moved to Settings → Help → System information. "Export now" is gone because a stale mirror needs no decision — it is exported when the repository opens. "Acknowledge failure" is gone as a panel-wide control: a failure belongs to one project, so it is reported on that project's row with the reason and a *Try again* beside it. What is left in the panel is transient and hides itself: the coverage warning and the live heartbeat |
| **Branch and clone** | Create a branch from HEAD and switch to it, with the name checked against git's rules while it is typed; clone a repository after verifying the remote, then open it. Both are modal dialogs built from the one stylesheet, and both are covered by tests that construct them against the real theme |
| **Commit gate** | A stale mirror is exported *and staged* before the commit. Push releases the direction locks; commit does not, and the panel says so |
| **Diff view** | Eleven languages, new files rendered entirely green, *Show full file*, rows built only when realised, **per-hunk stage and unstage** |
| **Open in the IDE** | Export runs anyway, from a copy; import waits and names who has the project. Neither marks the binding failed, and the breaker is never fed |
| **Adoption writes `.gitignore`** | Created when absent, appended when present, never overriding an existing decision. Covers `.versio/local/` — locks, backups, scratch copies — and the toolchain's `-AllUsers.opt` and `-<user>-<machine>.opt`, the second of which carries a username and machine name in its filename |
| **Export coverage** | What the export does *not* carry, said out loud: `_coverage.md` in the tree, every skip in the manifest with a stable reason, and a sentence in the panel naming what cannot be relied on. A gap is distinguished from a deliberate omission, from a file that is exported one way, and an old manifest reports "unknown" rather than "complete" |
| **Library references** | One `<manager>.libraries.yaml` per library manager, beside the object that owns it — what the project asks for, never how it resolved here. Hand-written YAML on both sides, no dependency added to either. **Export-only**, and stated where a reader will meet it: the file's own header, `_coverage.md`, the manifest's `form`, **and now the panel** — the changed-files row carries a lock glyph and a tooltip saying the edit does not reach the project. It went there rather than on the tree row (§9.3) because the row somebody is about to *stage* is where the warning has to be. `CoverageSummary` still speaks only when there are gaps, deliberately |
| **Chrome** | One app-wide progress bar with a caption, maximized on open, log console collapsed, heartbeat during long runs. **The title bar follows the theme** — painted with `Color.Surface.Base` through DWM, on every window the app opens and again on every theme switch, so the caption and the top bar are one surface rather than two. Tooltips are themed too, by an implicit style, because there are dozens of them and not one named a style |
| **Saying it is working** | A toolchain run announces itself the moment it passes the last gate, and three surfaces answer three different questions: the **bar** that the app is busy, the **row** which project it is busy with (`exporting…`, in the accent colour), and the **heartbeat** that it is still alive and how far along. A refused, coalesced or throttled save announces nothing, because it is not working |
| **Log panel** | Level as four chips (click to drop, click to restore) and category as a multi-select dropdown carrying a live count each, because the counts are what say where the volume is. **Pause freezes the feed** rather than the scroll, says so in the header, and shows what is queued behind it. The controls appear with the list they filter: collapsed, the strip is a chevron, a count and the paused pill |
| **Logging** | Ring buffer, correlation ids, rolling file from startup, crash handlers on all three .NET channels. An idle app is quiet: a completed git command writes one line, not two, and runs of unchanged status polls are summarised every five minutes instead of three lines per poll. The export narrates itself: the directory swap with both file counts, every skip with its reason, the summary, and a warning when a killed run left a staging folder behind |

---

## Deferred, and not on the critical path

Decided on 2026-08-15, in one place so that a future session finds the decision rather than
re-deriving it. **None of these is blocked, none is hard, and none of them is wrong to pick up** —
they are off the critical path because nothing is broken while they wait, which is a different
statement from "not worth doing".

| | Why it waits | What would bring it back |
|---|---|---|
| **Word-level diff** (§5) | The colours exist and are contrast-checked; `DiffView` tints whole rows. A readability gain on something already correct. | Somebody reading generated XML or ST diffs daily and saying so. It is the strongest candidate of the four. |
| **Ahead/behind previews** (GP-K, §9d) | The badge already says *how many*; this says *which*. `RecentCommitsAsync` already takes a count and a skip, so it is the smallest thing left in the git plan. | An hour, whenever the git panel is next open on the bench. |
| **Retry when the IDE closes** (§3) | Every path already recovers — the next save, and reopening the repository. This closes the gap in between. Convenience, not correctness. | A user noticing the wait, or the vendor lock file turning out to be cheap to watch. |
| **Code signing** (open decision #2) | Nothing external is distributed yet. | **Start this before it is needed, not when.** It is procurement lead time rather than engineering time, and it is the only item on this page that gets worse purely by waiting. |

**Also decided, and more firmly: the code-shape queue is closed.** Staging, the repository
session and `SyncViewModel` were each measured and declined rather than deferred — see the queue
below and the finding. The difference matters: the four above are *later*, those three are *no*
unless something changes.

---

## What remains, in dependency order

> **Picking this up?** Items 1 and 2 are done and kept for their reasoning. Items 3–8 are
> untouched: **item 3 is the smallest**, item 4 is the largest piece of user-visible value, and
> nothing below depends on anything above it except where it says so.
>
> **This list is not the active work.** The import side has its own plan and its own queue —
> [`CODESYS-IMPORT-PLAN.md`](CODESYS-IMPORT-PLAN.md), whose *Handoff* section is the one to read
> first, and whose **feature checklist** is what says which of its 49 behaviours have been run
> against a real project and which have not. **Phases 0–5 there are complete and Phase 6 is part
> done**; what is left of it is **G8 and G9**, and G9 is the one with a real-world failure behind
> it rather than tidy-up.

> **The second queue — the code-shape work — is CLOSED, and three of its items were closed by
> deciding not to do them.** It never had any bearing on what the product does. What was worth
> taking is taken:
>
> 1. ~~**Extract auth and identity out of `ShellViewModel`**~~ — **done.** `GitAuthViewModel`,
>    545 lines; the shell goes 5,573 → 5,251.
> 2. ~~**The remote surface**~~ — **done**, and it went ahead of staging on evidence rather than
>    on the order written here. `GitRemoteViewModel`, 540 lines; 5,251 → **4,958**.
> 3. ~~**The log console, still inside `MainWindow.xaml`**~~ — **done.** `LogPanelView`;
>    `MainWindow.xaml` 1,218 → **936**, and all four panels are out.
>
> **Not doing, and this is a decision rather than a backlog:**
>
> - **Staging.** `GIT-PANEL-PLAN.md`'s GP-E log measured this seam and rejected it — *"a move
>   dressed as a decomposition"* — and re-measuring agreed: it makes **four** calls into the PLC
>   engine including `EvaluateGateAsync`/`SatisfyGateAsync`, so the seam runs straight through
>   the junction where git meets the toolchain. That is the most safety-critical path in the
>   application and the one place where a refactor that is merely *neutral* is still a bad
>   trade. Revisit only if staging grows a genuinely separable rule set the way GP-E's selection
>   logic did — that part already went to Core, which is why the shell grew wiring and shrank
>   rules.
> - **The repository session.** Measured **22 borrows**, worst of the three by a distance, and
>   for a structural reason that will not improve: opening and closing a repository resets
>   everything, so a collaborator would take most of the shell as arguments. That is the exact
>   anti-pattern GP-E named.
> - **`SyncViewModel`, 2,847 lines.** The same shape one size down, and the same answer. It is
>   the PLC engine's view model — the part of the app that can lose somebody's work — and
>   nothing about its size is currently costing a reader more than a re-shaping would risk.
>
> **What the effort actually produced, and it is not the line count.** Two instruments that did
> not exist before: `CollaboratorSeamTests`, because deleting a subscription left the suite green
> **three separate times**; and the git panel's binding-path test, because a renamed binding
> compiles clean, throws nothing and renders an empty control. Both outlast the refactor and both
> catch the failure mode that made it risky. **619 lines came out of the shell; the instruments
> are worth more.**
>
> **Do the smaller thing first if you are new to this code**: ~~`LogViewModel` is leaked by
> thirty tests that construct one inline as a constructor argument~~ — **done**, the shell
> disposes it now and two window helpers that could not reach their shell at all were fixed with
> it; see the finding. What is left of that list is `OrphanScanner.cs:194`, which states *"never
> deleted automatically because it is committed history"* unconditionally — wrong for an export
> tree sitting under an ignored path.

> **Two other plans are tracked separately, and both are ahead of this list.**
>
> **[`SAFEGUARDS.md`](SAFEGUARDS.md)** — what Versio refuses, asks about or waits for.
> **Phases 1–8 are built and verified against real CODESYS**: the guard, the cancellation
> plumbing, the close and context-switch guards, the conflict gate, the git-write window, the
> auto-stash in both directions, and a swap that can no longer leave a project with no mirror.
> **Phase 9's first item is built too** — §5.C, the import confirmation: Versio no longer
> answers its own question. `RequireImportConfirmation` shipped as `true` and was answered
> `Confirmed = true` by its only caller for the whole life of the setting, so a gate that was
> switched on was reached and always passed, for the one direction that writes a binary file
> nobody can diff. An edit to an exported mirror now writes nothing until a person says so.
> **§5.E is built too** — ten copies per project were written before every import and reachable
> by nothing, so after the one operation they exist to undo, the engineer had an unusable
> project and no route to the file that would fix it. Restoring keeps a copy of what it
> replaced, so going back one copy too far is recoverable rather than final. **And §5.G**: a
> second Versio can no longer open a repository the first one has, which is what
> `LockRecovery.HeldByLiveSession` had been promising in a sentence since it was written.
> **Phase 9 is complete**; what is left in §5 is §5.H, the repository vanishing while it is
> open, which nobody has costed. §0 is the handover note.
>
> **[`GIT-PANEL-PLAN.md`](GIT-PANEL-PLAN.md)** — the git panel's functionality and UX.
> **Eleven of its twelve phases are built**: the git presence probe and identity preflight,
> notifications, the remote state machine and publish, credentials, background remote polling,
> per-file staging, the one commit button, conflicts and the way out of a divergence, branches
> and their deletion, and — both on 2026-08-14 — **PLC-aware naming**, which is the item that
> document itself ranks highest and the one no generic git client can have, and the
> **merge-request button** across five forges. **What is left** is GP-K/GP-L: the ahead/behind
> previews and the rest of the small UX list.
>
> *This paragraph said "nothing in it is built" for a day and a half after nine phases had
> shipped, because it was written once and the header above it was re-derived without it. The
> same shape as the hand-maintained counts below: a sentence about state, in a document nobody
> re-reads from the top.*
>
> `SAFEGUARDS.md` also carries a correction worth reading before touching the sync engine:
> **`On git action` is not the default sync mode** and had not been since 2026-08-12. Exports
> follow a detected change, because the mirror exists to be reviewed *before* a commit. The
> commit gate is a backstop, not the trigger.

### 1. ~~Export what is currently missing~~ — **done**

The coverage report named it, which is what turned it from a rumour into a task. On the
fixture: two library managers, **both exported now**, and `_coverage.md` reports **0 gaps**.

- ~~Walk the library manager through the ScriptEngine and write the YAML~~ — `references`,
  iterated; one file per manager, beside the object that owns it (the plan's single
  `config/libraries.yaml` was reversed: a real project has two managers, see §5)
- ~~A YAML writer by hand~~ — `versio_yaml.py` writes it, `LibraryCatalog` reads it, neither
  side has a dependency. About 350 lines for both halves, and the format is deliberately tiny
- **Still open:** one tree node **per library**, so a version bump reads as *that library*
  rather than as "the library manager changed". The data exists and parses now; this is a tree
  feature rather than an exporter one

### 2. ~~Finish the git surface~~ — **done**

- ~~Recent commits with click-to-diff~~, ~~per-hunk staging~~ — **done**. History has since
  moved to a **panel of its own** with per-file diffs and lazy loading throughout; see the
  finding on what the old list cost on every refresh
- **Stash is withdrawn from the UI**, not finished. `StashPushAsync` / `StashApplyAsync` /
  `StashDropAsync` and the list parser are kept, tested and unused: what was wrong was six
  permanent controls in the narrowest column for something reached a few times a month. A
  different design is owed
- ~~Branch creation~~ — a dialog that validates the name **as it is typed**, against git's own
  rules, and creates-and-switches. `BranchName` is 35 unit tests in Core, because the useful
  half is the *reason* a name is refused, and git's own refusal arrives after a process spawn
  in wording written for a terminal
- ~~Clone with verification~~ — URL, **Verify** (`ls-remote`: reachability *and* credentials,
  transferring nothing), destination, then Clone with progress. **Clone stays dead until
  Verify passes**, and editing the URL afterwards shuts it again — the failure the dialog is
  shaped around is verifying one remote and cloning another. `GitCliClient.CloneAsync` is
  static and deliberately not on `IGitClient`: a clone has no repository to run in
- ~~Discard~~ — per **file**, never per list, after a confirmation naming the file. An
  untracked file is refused with an explanation rather than attempted, because git has nothing
  to restore it from. Discarding inside a mirror releases that binding's direction lock
- **What is left:** the branch dropdown still does not refuse a switch while a direction lock
  is held (plan §9.2 asks for that, with a tooltip saying why). Git refuses a dirty-tree
  checkout on its own, which covers the dangerous case but not the explanation

### 3. Retry automatically when the IDE closes — **deferred, not critical path**

Export and import both handle an open project now. The one piece left is the trigger: the
vendor's lock file disappearing is a filesystem event the watcher could see, which would make
closing CODESYS the retry and need no button at all.

Today the next save retries, and reopening the repository retries — the catch-up export runs on
open. What is missing is the case in between: the IDE is closed while Versio stays open, and
nothing notices until the next save. The panel says so rather than going quiet, which is what
`StalenessSummary` is for.

**Deferred deliberately on 2026-08-15.** It is the smallest item here and it remains the obvious
next one, but nothing is broken while it waits: every path already recovers, just later than it
could. It buys convenience, not correctness.

### 4. Finish the project tree

- ~~**Methods and properties under the object that owns them**~~ — **done.** Manifest schema 4
  records each member's owner GUID and the tree nests by it; see the finding. A mirror
  exported before this stays flat until its project is next exported
- **One node per library**, under its manager, parsed from the exported YAML. `LibraryCatalog`
  already reads the file; what is missing is the tree builder taking a catalog and the shell
  handing it one. This is the first piece of the plan's "config nodes parsed from YAML", and
  the only one whose data now exists
- A layer switcher in the diff pane (`ST | native`) — objects already carry several layers and
  there is no way to see the other one
- Context menu, and expansion state persisted per repository. It already survives a refresh;
  it does not yet survive the window closing

### 5. Diff view — **deferred, not critical path**

Side-by-side mode, and word-level intra-line diff.

**Deferred on 2026-08-15**, and it is the one on this list with the strongest case for being
picked up first when the deferrals are revisited: the colours already exist and are
contrast-checked, and the diffs being read are generated XML and ST where a long line often
differs by one identifier. It is a readability improvement to something that already works
correctly, which is what puts it off the critical path rather than low value.

### 6. A second vendor

The seam is ready and unproven. The first real implementation is the only thing that will show
whether `PlcJob` and `PlcResult` are the right shape — TwinCAT is the cheapest test of that,
being CODESYS-derived, and Studio 5000 the harshest.

### 7. Packaging

- ~~Application icon~~ — **done.** `assets/versio.ico`, 8 sizes, generated by
  `tools/New-VersioIcon.ps1` and embedded via `<ApplicationIcon>`
- `third_party/` vendoring with SHA-256 pinning, offline restore
- Velopack: `Setup.exe`, MSI for managed sites, delta updates
- Code signing — **procurement lead time, not engineering time; start early**

### 8. Corpus breadth

More fixture projects. Every field bug becomes a fixture before it is fixed.

**A second one landed 2026-08-16**: `MachineWithMembers`, built against real CODESYS by
`tools\versio_make_member_fixture.py` because the import feature checklist found five rows that
`RefrigeratorControl` cannot reach — it has no member object of any kind, so it produces no
`ST_IMPL`, no `NATIVE_R` and no recorded `parent`. The new one has a folder, a function block
inside it, and a method, an action and a property inside that. **It closed A3, E1 and E3.** What
is still uncovered is a name needing percent-encoding, which P5 measured cannot be created —
it has to arrive from a library or an external-file reference.

---

## Handoff — the UI pass

**Branch:** `UICleanUp`, cut from `GitPanel`. **State:** a full audit of the shell as a *user
interface* rather than as a set of features. Fourteen findings, **twelve applied**, each with a
test that fails against the old behaviour.

**Where to start reading.** The Findings section below, newest first — every one of the twelve
is written up with the evidence and the reasoning, and several of them are more useful as
patterns than as fixes. The two that were reported from the running app during the pass (the top
bar's dropdowns, the unpushed markers in the history) are at the very top.

**What landed, by area.**

| Area | What changed |
|---|---|
| Focus | A shared accent ring across every control, plus per-template outlines for list and tree rows. Keyboard focus was invisible application-wide. |
| Scrollbars | Split into a template per axis. Horizontal bars were arranged 10 px wide and their troughs paged the wrong way. |
| Status bar | Two columns so the path stops overdrawing the state; the health dot bound to `ShellHealth` instead of hardcoded green. |
| Accessibility | `AutomationProperties.Name` on every icon-only control, bound to the tooltip already written for it. |
| Preferences | Theme, log height and panel width persisted — and the write path fixed, which silently reset every field but the one being written. |
| Dialogs | The last four `MessageBox` prompts moved to `GuardPromptWindow`, with real button labels instead of OK. |
| Diff | Selection made visible without losing the row tint; `Ctrl+C` added, in document order. |
| Motion | Four storyboards at `Motion.Fast`, on opacity triggers that already existed. |
| Top bar | The two dropdowns paired with their buttons, made equal, and spanning the bar. |
| History | An `↑` on every commit no remote has, and a count in the summary line. |

**What is owed to a human**, because nothing in the tooling can see the screen: all of the above
in both palettes. The suite measures geometry, opacity, automation names and command state; it
cannot tell you whether the result reads well. Launch with `Start-Process` and check the process
has a window before believing a clean binding trace — see *Verifying the UI* below.

**Two things worth knowing before the next pass.**

1. **A geometry defect is invisible to every other kind of test.** Three separate ways a WPF
   layout can be correct and wrong turned up here — a `DockPanel` whose children all default to
   `Dock=Left`, a `Grid` cell holding two children that overdraw, and `Stretch` silently becoming
   `Center` at a `MaxWidth`. None produces a warning. Only a transformed `Rect`, measured at a
   width where the defect actually occurs, can state it.
2. **Run the application.** Both of the last two findings came from the product owner opening it,
   which is now the fifth time in two branches that has produced a defect no suite had.

---

## Known loose ends, from an audit of the repository

Small, specific, and none of them blocking. Written down because an audit that finds things and
does not record them has to be run again.

- **Launching the app against this repository adopts the 21 `phase0/` scratch projects and
  starts exporting all of them.** Observed 2026-08-15 while doing the `CLAUDE.md` "run the app"
  step: within a second of opening `C:/GIT/Versio` the log reads *"21 newly tracked project(s)
  have never been exported; exporting now"*, the commit gate demands all 21, and it begins
  driving real CODESYS — one project at a time, ~20 s each. It also writes 21 rule blocks into
  `.gitattributes`, 21 into `.gitignore`, and 21 bindings into `.versio/repo.json`.
  - **Not a defect — it is adoption working exactly as specified** on a repository that happens
    to contain 22 tracked `.project` files because Phase 0 committed its probe corpus.
  - **It matters anyway, twice.** It takes the machine-wide CODESYS lock, so it will fight a
    running `Versio.Integration.Tests` and make both look slow; and the adoption files it
    writes are unrelated working-tree changes that are easy to commit by accident. `git
    checkout -- .gitattributes .gitignore .versio/repo.json` afterwards.
  - **The launch itself is clean**: zero `ERR` lines, so the window constructed, the panels
    realised and the bindings resolved. That is what the step is for; the rest is noise from
    the corpus.
- **Cancelling a running toolchain job is not implemented.** A change arriving mid-run is
  queued, which is correct and cheap; restarting instead would be better when the run has
  barely begun, and needs process cancellation plus cleanup of the staging folder a killed
  export leaves behind.
- ~~**Orphan findings are computed and shown nowhere.**~~ — **shown.** `FindingsSummary` is a
  line in the project panel, in the same slot and the same red as the coverage warning, and
  hidden when there is nothing to say. One sentence for one finding; a count and the first one
  for more, with the detail still in the log. They were surfaced rather than deleted because
  every one of them is a repository in a state Versio cannot resolve on its own.
- ~~**Thirteen binding errors at every launch, none of them path errors.**~~ — **fixed, and the
  tracer is clean.** `AutomationProperties.Name` is typed `string` while `ToolTip` and `Tag` are
  `object`, and a style setter is applied *before* the call site sets either — so for that moment
  the binding handed null to a string property, once per control. `TargetNullValue=''` on the
  three setters in `Buttons.xaml`. **13 → 0**, and the accessibility tests still pass, which is
  what proves the names resolve to the tooltips rather than to the empty string the fix
  introduced.
- ~~**`TransientPatternsFor` does not name `_project.precompilecache`.**~~ — **fixed.** It
  returned `[stem + ".~u*", stem + "-*.opt"]`, and its remarks say only observed files are
  listed; this one was not observed, so an adopted repository had no rule for a 277 KB binary
  compile cache CODESYS writes beside every project it opens, and *Stage all* would have
  committed it. This repository was never exposed, because `*.precompilecache` had been typed
  into `.gitignore` by hand — the same way the staging rule was present here and missing
  everywhere else, which is why nobody noticed. The dot of `.project` becomes an underscore
  (`Machine.project` → `Machine_project.precompilecache`), confirmed across six projects, and a
  test asserts that shape specifically: a pattern matching nothing looks exactly like a pattern
  matching a file that is not there. **Deleting the fix leaves three tests red**, two of them
  pre-existing.
- ~~**An import of a mirror that is not there reported success.**~~ — **found and fixed
  2026-08-15, in a review of the change that caused it.** An empty plan is a valid instruction
  meaning "touch no file", so an import whose mirror directory has gone ran, changed nothing,
  and came back `Completed` — which a user reads as "my edits were applied".
  - **It had been caught by accident, and the accident was removed.**
    `Directory.EnumerateFiles` throws on a missing directory and the old plan walk called it
    straight; moving that walk onto `IFileSystem`, whose `EnumerateFiles` answers `[]` for a
    root that is not there, turned a throw into a silent success. **No test noticed**, because
    none had ever pointed an import at a mirror that had gone.
  - `ImportAsync` refuses before the backup now, so a project is not copied aside for an import
    that cannot happen, and `ImportPlanReader` says in the log that the plan is empty *because
    there is nothing there*.
  - **Reaching it honestly took two corrections to the test**, both worth knowing. A fresh
    binding answers `NoChange`, because "never synced" and "the mirror is gone" both hash to
    nothing and the gate cannot tell them apart — correct, and it means the refusal is
    reachable only once a real baseline exists. And deleting every *file* leaves the directory
    behind, on a real filesystem as in the fake, so `FakeFileSystem` gained a `RemoveDirectory`
    — until then the fake could not express "not there" at all, only "empty".
  - **An emptied mirror is a different case and is still open**: the folder exists, the plan is
    empty, everything lands in `MissingFromMirror`, and the run reports `Completed` having
    applied nothing. That is Phase 2's question — it is at least logged, and the sentence names
    the version limitation.
- ~~**`Eventually.Press` did not wait for the command it pressed.**~~ — **fixed 2026-08-15,
  and what it was hiding is worse than the bug.** `AsyncCommand.Execute` is `async void`, so it
  returns to its caller at the **first `await`**, not at the end. `Press` called it and moved
  on, which meant every assertion after a press was racing whatever the command did past its
  first suspension point — and passing only because these commands happened to run
  synchronously up to the interesting part.
  - **Found by adding one `await`.** Deciding the import plan *before* asking about it put a
    suspension point ahead of the confirmation prompt, and `Declining_writes_nothing` went red
    asserting the question had been asked — before it had been. Nothing was broken except the
    test's assumption, which is the tell: a suite that fails when correct code is reorganised
    was measuring the shape of the code, not its behaviour.
  - `Press` now waits on `IsRunning`, which is set before the first await and cleared in a
    `finally`. **All 478 tests passed unchanged afterwards**, so nothing had been depending on
    the fire-and-forget — it was pure latent risk across 35 call sites.
  - **`AsyncCommand<T>` had no public `IsRunning` while `AsyncCommand` always had.** Same
    field, same lifecycle, one property — so the parameterised command could not be asked
    whether it had finished, and there is no other way to ask an `async void`.
- ~~**No app test ever accepted an import.**~~ — **closed 2026-08-15.** `TestShell` wired
  `toolchains: (_, _) => null`, so every test in `ImportConfirmationTests` either declines or
  never presses: the confirmed path — the one that writes into somebody's project — was
  reachable only through a real 17–31 s CODESYS launch. `CapturingToolchain` reports itself
  available and records jobs instead of running them.
  - **Worth knowing before writing the next one:** a toolchain being *present* changes what
    opening a repository does. Versio catches up by exporting, which takes the direction lock
    to `ProjectAuthoritative` and correctly refuses an import afterwards, so the fixture has to
    release it the way the application does. **`SyncTrigger.ForceReleased` has no production
    caller at all** — only `ReleaseAfterPushAsync` and `ReleaseAfterDiscardAsync` reach Clean.
- **Three of the seven C#/python pairs are still compared by nothing** — `NativeXmlScrubber` ↔
  `scrub_native_xml`, `StTextCodec` ↔ `join_st`/`split_st`, and `StDeclarationParser` ↔
  `parse_declaration`. `StTextCodec` is the one that matters: a bug in `split_st` silently
  moves code between a declaration and an implementation, and it is a hand-maintained copy.
  **This is not theoretical** — [`CODESYS.md`](CODESYS.md) §9 counts four drifts across the
  pairs that *are* compared, two with the C# right and two with the python right, and reading
  the two sources side by side caught none of them. The harness exists: `RunPython` in
  `PythonFileNameParityTests` takes a snippet and a JSON payload, and
  `versio_native_equiv.py` ↔ `NativeXmlNormalizer` was added through it on 2026-08-15 in a few
  dozen lines. Listed in the import plan's Phase 6.
  - **And the script inventory in §10 had gone stale unnoticed while this was true**: it said
    ten files and five pure modules when there were twelve and seven, having missed
    `versio_tasks.py` and `versio_yaml.py`'s reader when Phase 4 landed, and **every line count
    in it was wrong** — `versio_import.py` read 450 against an actual 735. Corrected
    2026-08-15, with the one-line re-derivation beside the table, because the numbers will go
    stale again and editing the one you think changed is what produced this.
- ~~**An object whose exported file has gone is accounted for only in the log.**~~ —
  **closed 2026-08-15.** `ImportNarrative.Describe` puts it in the confirmation dialog, named,
  alongside how much will be applied, the planner's warnings, and whether structural changes
  are available at all. The log is not where anybody is looking at the moment they are
  deciding whether to press Import.
  - ~~**In the present tense**, because deletions are planned and executed by nothing: it says
    the objects *will stay*. Two tests pin it, so the wording cannot drift ahead of the
    script.~~ — **this was true when written and false one commit later, and the tests did not
    say so.** The script grew `remove()`; the wording did not move; the pinning assertions were
    on the *old* sentence, so they could only have failed if somebody fixed it. The dialog
    promised objects would stay and the import deleted them. **Fixed 2026-08-15**: the dialog
    names what will really go and what will really stay, separately — see the Finding above.
- ~~**Deletes do not propagate.**~~ — **closed outright 2026-08-15, textual and native.**
  `versio_import.py` runs a delete phase **last**, after everything that adds, because
  `remove()` takes referenced objects with it silently (P3). Proven against real CODESYS in
  one test with three halves: an object edited since the export is kept, the same object with
  an honest hash is removed, and a GVL goes only when **both** of its files check out.
  - **The content check is the script's own**, because only the CODESYS process can see the
    project. It rebuilds what the exporter *would* have written for the live object and
    compares to the manifest's hash; a mismatch means somebody edited it in the IDE since, and
    a missing mirror file is not permission to destroy work that exists nowhere else.
  - ~~**⛔ Native objects cannot be checked, so they are never deleted.**~~ — **closed the
    same day, by manifest schema 5.** The obstacle was real: comparing through
    `versio_native_equiv` needs the old bytes, and for a deletion those were in the file that
    was deleted. Recording the **normalised** hash at export time puts the old side in the
    manifest instead, so a fresh export has something to be compared against. **This limit
    was found by the integration test, not by review**, and the test that found it now
    asserts the opposite.
    - **The design was wrong on first principles and a measurement caught it.** It was going
      to compare with GUIDs unmasked, because `normalize`'s docstring says masking must be off
      when comparing two exports of one project. True — across two exports. **Across a round
      trip CODESYS re-mints GUIDs**: 3 differing lines of 117 in `GlobalTextList.xml`, all
      three `System.Guid` values. Masked, and the residual risk — a GUID-value-only edit
      reading as unchanged — is stated in `versio_types.py` rather than glossed.
    - **Four scenarios against real CODESYS, because a check that refuses everything and one
      that approves everything both look green from one angle**: a clean native delete
      (removed), a genuinely edited object judged against the original manifest (kept,
      `DELETE_CONTENT_CHANGED`), two round trips of churn in between (removed — the raw hash
      moved and `nhash` did not), and a schema-4 mirror (kept, `DELETE_UNVERIFIABLE`).
  - **`removed` had to go into the `dirty` test**, or a run whose only work was a deletion
    closes without saving and the object comes back. Second time this project has nearly paid
    for that: `partial` exists for the same reason.
- **The delete set is planned in C#, before CODESYS starts** (2026-08-15).
  `ImportPlan.Deletions` carries objects the mirror no longer holds any file for, having
  passed five preconditions:
  a trusted manifest, every file for the GUID gone, export-only records neither voting nor
  vetoing, nothing surviving living inside it at any depth, and an owner chain that is
  **recorded rather than guessed**. The script checks the one thing the planner cannot — that
  the live object still matches what was exported — and refuses its own deletion if it does
  not, so the two halves are independent rather than layered.
  - The tripwire test that held the wire shut while only one half existed has **flipped**: it
    now asserts what crosses, and that `deletions` is omitted entirely when there is nothing
    to delete.
  - **The transitive orphan rule was a bug a pre-existing test found**, not a review. Checking
    direct children only would delete a class whose property is gone while the property's
    *accessor* survives, taking both. What went red was a resolution test whose fixture listed
    one file out of three recorded — a mirror that cannot exist, harmless until absences began
    to mean something. **A fixture that could not occur is a fixture that will eventually lie**;
    it describes a real directory now.
  - **The guessed-chain rule came out of a second pass over the same code.** The resolver falls
    back to splitting a file stem when an owner is missing from the manifest, which is a fair
    guess for an update — text applied to the wrong object is something git undoes. The
    identical guess reached by a *deletion* resolves to some other object and destroys it. **A
    shared helper is only as safe as its most dangerous caller**, and nothing about the
    resolver's own code says which callers it has.
- **Phase 2's delete preconditions are now load-bearing, not defensive.** P3 measured what
  `remove()` does to an object others reference: it removes it, saves cleanly, and warns about
  nothing. There is no toolchain backstop. So the manifest-only rule, the content check and the
  named confirmation are the entire safety story for deletion, and each one is worth more than it
  looked when they were written as belt-and-braces.
- ~~**G11 has a measured mitigation and no decision.**~~ — **closed by Phase 4, and the
  decision never had to be made.** Task configuration is exported as `TASK_BOTH` and imported
  through typed property assignment, so `import_native` never touches it and the stale compile
  cache is never reached. Versio deletes nothing of CODESYS's. Measured over three cycles:
  zero warnings, where cycles 2 and 3 used to throw twice each. **`Core2` stopped moving in
  the same change**, which closes G7 with it.
- ~~**The typed task-import path is half covered — the half that writes to CODESYS is not.**~~
  — **closed 2026-08-15, both halves.** The parsing half went first: the committed
  `.tasks.yaml` is parsed by the reader the importer calls, in a test needing no CODESYS.
  **The write half is now covered too** —
  `AdoptAndExportTests.A_task_priority_edited_in_the_mirror_reaches_the_project_and_survives_a_second_import`
  edits a priority in the mirror, imports against real CODESYS, and reads the value back out of
  the project. Verified non-vacuous twice, by deleting each half of Phase 4 in turn: stub the
  `setattr` and it reads back `1` instead of `7`; make `typed_sibling_for` return `None` and it
  reads back `1` as well. **`TaskCatalog.cs`, the C# reader, is still unwritten**, so nothing
  outside python can read the file and the panel cannot show tasks — that part of Phase 4 is
  still open.
- ~~**The committed fixture mirror is a shape the exporter can no longer produce.**~~
  **Closed 2026-08-15 by regenerating it**, and it cost far less than "moves committed bytes"
  implied. **Every existing content file came back byte-identical, `Task Configuration.xml`
  included.** The whole diff is the new `.tasks.yaml`, `_coverage.md`, and one `form` field
  plus `objectCount` in the manifest. `ExportOnlyFiles` 2 → 3 was the single predicted cascade
  and the only one. That the other twelve files did not move is itself worth recording: it is
  the strongest evidence available that the export is still deterministic across everything
  this touched.
- ~~**An import's summary is logged with the exporter's field names.**~~ — **fixed 2026-08-15,
  and it was worse than the bullet said.** `walked` / `written` / `skipped` / `gaps` against an
  import that reports none of them, so every import logged a line of blanks with one true
  number on it. It reads `import: 1 updated, 7 native, 2 unchanged, 3 not applied
  (export-only) — 0 warning(s)` now, keyed on the job's operation rather than on which keys
  happen to be present, with zero-valued fields omitted.
  - **"Read from the code, not seen in a log" is no longer true either** — it was watched
    happening, twice, while building the Phase 4 integration test, which is what turned this
    from a code-reading finding into something with a reproduction.
  - **Four import events had no `case` at all** and fell through to `_log.Debug("· {type}")`.
    The one that matters is **`task_set`**: Versio writing a task's priority, interval or CPU
    core binding logged as the literal string `· task_set` — no task, no field, no value, at
    Debug. That is the single write the whole Phase 4 argument exists for, and it was the least
    legible line in the log. `Info` now.
  - **`ImportLoggingTests` is new**, the sibling `ExportLoggingTests` never had: one test for a
    clean import's numbers, one for **an import that loses a file**. The second exists because
    nothing in the suite had ever run a failing import, so "the summary is wrong when something
    goes wrong" had no way to be caught — the same shape as G11.
  - ~~The `not_imported` line in the same handler hard-codes "change this in the IDE".~~
    **Fixed 2026-08-15** alongside the export-only finding. It was right while a library file
    was the only thing that reached it and wrong the moment a task configuration's XML did —
    and the script had been sending the `sibling` field all along, which the log discarded.
    A **different case in the same `switch`**, so the field-name defect above is untouched.
- ~~**`versio_import.py` does not count a native-import failure.**~~ — **fixed 2026-08-15, and
  it was not one line.** `ReplaceHandler` counts `errors` and `refused` now; a file that lost
  any object is counted in `failed`, taken out of `native`, and reported as a new
  `native_partial` event with how many objects survived.
  - **The hazard the one-line version would have caused.** `main()` decides whether to save
    from `dirty = updated > 0 or native > 0`. Taking a partly-failed file out of `native` would
    have made the run that had *already written to the project* the one that skipped its save,
    dropping the objects that did paste. A `partial` counter is in the `dirty` test for that
    reason. **Fixing a lying counter must not cost somebody the half of the import that
    worked**, and reading the fix as "one line" is what would have.
  - **The `native_partial` branch itself is uncovered and says so in the test.** It needs
    CODESYS to report a per-object exception through the progress callback and continue; the
    only trigger anyone has observed was G11, and Phase 4 removed it. Forcing one with
    malformed XML produces the other route instead — `import_native` raises — which is now
    tested and reports `1 FAILED`.
- **The phase-split save in `versio_import.py` is conditional and skips the case it was written
  for.** `if self.created > 0 or self.updated > 0` — an import touching only native files never
  saves between the phases, which is exactly the shape of run that hit the exception the split
  exists to prevent.
- ~~**No test imports twice, and none imports with the project's sidecars present.**~~ —
  **closed 2026-08-15, and half of it was never true.** One test imports twice now, and the
  compile cache is **asserted** present before the second import rather than assumed — the
  first import writes one, which was inherited from `Measure-ImportStability.ps1` and is
  measured from a test for the first time. The assertion is there so the axis cannot decay
  silently if a future CODESYS stops writing a cache on a headless run.
  - **The `.opt` half of this bullet was wrong, and finding that out is the point.** The test
    was written copying the committed `.opt` pair beside the project, exactly as this bullet
    asked. **Deleting that copy left every assertion green** — so the copy was watching nothing
    and was removed. **The bullet was written by reading the test setup rather than by looking
    at what was on disk afterwards**, which is the same mistake as the first G11 explanation
    one section down: a story that fits, recorded as a fact.
  - **Which tool writes which sidecar, now measured rather than reasoned about.** The test
    lists the project's directory at both import points, and the two lines answer it outright:

    | | Beside the project | Written by |
    |---|---|---|
    | before import 1 | the `.opt` pair, **no cache** | the **export** — it opens the project too |
    | before import 2 | the `.opt` pair **and** the cache | the **import** |

    So *"the first import creates the cache"* holds, and is measured from a test for the first
    time instead of inherited from `Measure-ImportStability.ps1`. And the `.opt` files are the
    export's doing, which is exactly why copying them in changed nothing. **The export was
    never a suspect until the second pass asked which of the two runs had produced them** — the
    original comment asserted the import created both, from the same not-looking that wrote
    the bullet.
  - **A second import needs a second edit**, because re-running the same one is refused as
    `NoChange` by the hash gate, correctly. That turned out to make the test stronger rather
    than weaker: the final assertion reads the *second* edit's value, so it can only pass if
    the cycle that used to fail actually applied something.
- **`ToolchainState` is a public observable property with no view.** It survives because the
  runner uses its text as the "why this is unavailable" explanation. It should be a plain
  string, not a bindable one.
- **The branch dropdown does not refuse a switch while a direction lock is held** (plan §9.2).
  Git refuses a dirty-tree checkout on its own, which covers the dangerous case without
  explaining it.
- ~~**Adoption writes no line-ending rules to `.gitattributes`**~~ — **measured, and fixed.**
  The inference in this bullet was wrong: git reports such a clone as *clean*. What breaks is
  Versio's own hash gate, which reads the bytes on disk. Adoption writes `<mirror>/** text
  eol=lf` now; see the finding for the measurement both ways.
- **The spacing scale is declared and unused** — `Space.1`–`Space.7`, `Pad.Row`, `Pad.Panel` and
  `Pad.Control` have zero references against **246 hardcoded margin and padding literals** (239
  when this was written; re-derived 2026-08-14, and the drift is the point — the gap widens on
  its own every time anything is laid out), three of the six most frequent being off the stated
  scale. Left as one deliberate sweep rather than
  done incrementally, because half-applied is the one state worse than either — and it should end
  with a test that rejects an off-scale literal, so the rule `Spacing.xaml` states is finally one
  that something enforces. Starting with the six repeat offenders covers 79 of the 239.
- **Word-level diff highlighting is not implemented, and its colours are.**
  `Brush.Diff.AddedWord` and `Brush.Diff.DeletedWord` exist in both palettes and are
  contrast-checked by `ThemeTokenTests`; `DiffView` tints whole rows and never asks which
  characters inside a changed line differ. A feature rather than a fix, which is why it sat out
  the UI pass — and the highest-value thing the diff pane has left, because the diffs being read
  are generated XML and ST where a long line often differs by one identifier.
- ~~**The full solution suite flakes under its own load.**~~ — **diagnosed, and the UI half
  fixed.** The cause was not timeouts: **fifty-six tests opened a repository through a
  `ShellViewModel` and never disposed it.** A shell that has opened one owns a
  `FileSystemWatcher` over the working tree, a `RemotePoller` spawning git subprocesses on a
  loop, the sync panel's watchers over every tracked project, and the repository lock — none of
  which stops when the test method returns. With xunit running twenty-eight collections in
  parallel the suite accumulated dozens of live watchers and polling loops, firing against temp
  directories their own test had already deleted and competing for the same git binary as
  whatever was actually running. That is why the failure moved, why it never reproduced alone,
  and why it reproduced on `main`. Measured: four failures in fourteen observed full runs
  before, one in the next fourteen after — roughly 29% down to 7%. **Reduced, not cured** — and
  ~~the remaining suspect is the `LogViewModel` leak in the bullet below~~ **that suspect is now
  closed without the flake closing with it**: the shell disposes its log, and the shells two
  window helpers built inline as arguments — where no `using` could reach them — are disposed
  too. Ten runs after that: eight green, two red, under load this session inflicted on itself,
  so no rate can be read off it. The three tests that failed all drive **real git subprocesses**
  and all dispose correctly, which points the remainder at process and filesystem contention
  rather than at anything leaked. See the finding.
- **The integration suite is not covered by that fix**, and one of the three classes originally
  named — `AdoptAndExportTests` — lives there. `Versio.Integration.Tests` constructs no
  `ShellViewModel` at all, so it leaked none; it drives real CODESYS with real timeouts, and
  `RepositoryWatcherTests.A_deleted_file_is_reported` is separately known to be flaky under load
  (see its finding). A red integration run is still worth re-running a project at a time before
  reading it as a regression.
- **`Versio.App.Tests` shells must be disposed** — `using var shell = ...`. This is the rule the
  bullet above exists to keep; `TestShell` states it where the next test author will read it.
- **Hand-maintained counts in these documents drift.** The commit count was wrong by twelve,
  the project tree's test count by six, and the scenario suite's by twenty-two — all of them
  written once and never re-checked. Re-derive rather than trust: `git rev-list --count HEAD`,
  and `dotnet test --filter FullyQualifiedName~<TestClass>` for a per-area figure.
- **One integration test is flaky under full-suite load** — see the finding below. Diagnosed,
  two fixes attempted and reverted, left alone deliberately.
- **The orphan scanner's sentence is unconditional and sometimes wrong.**
  `OrphanScanner.cs:194` reports an export directory with no bound project as "never deleted
  automatically because it is committed history". That is the usual case and the right default,
  but it is stated as fact: the scanner never asks git. An export tree under an ignored path -
  `phase0/roundtrip/baseline`, written by `tools/Test-RoundTrip.ps1` - was reported with the
  same words while being neither committed nor history.
- **`RefrigeratorControl.~u` is in pushed history.** Untracked and ignored now, but the commit
  that carried a username, machine name and pid to `origin/main` is still there. Removing it
  means rewriting a pushed commit — the owner's call, not a maintenance task.
- ~~**Only four windows are constructed by a test**~~ — **all seven are, and the rule has held.**
  `BranchDeleteWindow`, `ClonePromptWindow` and `BackupRestoreWindow` joined
  `BranchPromptWindow`, `GuardPromptWindow`, `MainWindow` and `SystemInfoWindow`, each with a
  test that builds it against the real stylesheet. The rule is what matters and it stands: **a new window ships with a test that
  constructs and measures it** — twice a window was added without one and twice it threw in
  front of somebody. The next `Popup` needs the same: its content is not built until it opens,
  so a test that constructs the parent does not reach it.
- **A shared `DataTemplate` needs a realised row in every list that uses it**, not only the
  list the feature was written for. GP-J taught the changed-files template about objects and
  blanked the history panel, with every test green — see Findings.
- **A test that builds `MainWindow` must join the `theme` collection.** Found by GP-I: a class
  that laid the window out but sat outside that collection passed alone and failed under load,
  because it raced a test swapping the palette dictionary and its bindings did not resolve. The
  two classes still outside it construct the window without laying it out, so they evaluate no
  bindings and are not exposed — but a third one that measures would be.

---

## The shape of the code, after an audit

An audit read the repository for dead code, duplication and things too large to review. It found
**no dead code and almost no duplication** — no `TODO`, no suppressed warning, no unreferenced
private member, and seven duplicated blocks at eight lines across all of `src`. What it found
instead was concentration: a few files carrying most of the weight, so a change to any one thing
inside them produced a diff touching everything else.

What moved, and why each one is worth knowing before editing it:

| Was | Now | The reason it is split the way it is |
|---|---|---|
| `Controls.xaml`, 1,228 lines | 70 lines + six dictionaries | Each leaf merges `Primitives.xaml` **itself**. A `StaticResource` resolves against its own dictionary and its own merges, never against a sibling the parent merged — this file's own header records the `XamlParseException` that cost. `StyleSheetResolutionTests` renders every keyed style, so a missing merge fails the suite instead of the first render |
| Nine settable `Func<>` dialog hooks across two view models, wired from 115 lines inside `MainWindow`'s constructor | `IUserPrompts`, answered by `WindowPrompts` | The set of questions the app can ask now has a name and one place to read it. `NullPrompts` answers "no" to all of them, which is what a view model with no view attached gets — the same answer a user who cancels everything gives, so the call sites needed no new defaulting |
| `SyncOrchestrator.AttemptAsync`, 275 lines | 112 lines + eleven named gates | The gates above `CoalescerRefuses` only *judge* — they take nothing and release nothing. The coalescer and the breaker **consume** something, which is why they are not in that list and why they report what they took. An `Attempt` record carries the state so every gate judges the same one |
| `MainWindow.xaml`, 2,517 lines | **936 lines + four panels** | `Views/Panels/`. A `UserControl` owns its own name scope, so `window.FindName` cannot see inside a panel; and a collapsed one has not applied its template, so its contents are not in the visual tree until shown once. `RowTemplates.xaml` exists because a `UserControl` cannot see the resources of the `Window` hosting it. The log console was the fourth and last, held back twice because it is the only one with code-behind — and **the margin has to sit on the hosted control, not inside it**, or the named element and the painted one stop being the same rectangle |
| `GitCliClient`, 1,544 lines | 799 lines + three parts | Split on its own existing section markers, and it was already `partial` for `[GeneratedRegex]`. The primary constructor's parameters are in scope across every part, which is what made it free |
| `OnUi` and its `SynchronizationContext`, twice | `ObservableObject` | One rule about the one thread allowed to touch a bound collection |
| The atomic write, four times | `JsonFile` | The temp-then-replace and the BOM-less UTF-8 really were the same rule four times. **The serializer options were not** and were left alone: the settings store has no enum converter because the theme is deliberately a string, and the lock and session stores have no `PropertyNameCaseInsensitive`. Unifying them would have changed what each accepts off disk, on files shipped versions have already written |
| Thirteen hand-built test shells | `TestShell.Build` | And the argument most easily forgotten — `toolchains: (_, _) => null`, without which a test goes looking for CODESYS on the machine running it |

**What is still concentrated, and it is now a decision rather than a backlog.**
`ShellViewModel` is **4,958** lines — down from 5,573, with auth, identity and the remote
surface gone — and still the largest single thing in the repository, at roughly twelve
responsibilities. Its clusters are **not contiguous**: staging
appears in two places, so it cannot be split by marking it `partial` and cutting on line ranges.
**Nothing further is planned, and the two remaining clusters were measured before being
declined.** Staging borrows 8 but makes **four** calls into the PLC engine including the export
gate; the repository session borrows **22**, because opening and closing a repository resets
everything. Both would take most of the shell as constructor arguments, which is the
"move dressed as a decomposition" GP-E named. `SyncViewModel` at 2,847 lines gets the same
answer for the same reason.

**If a future session does take one of them, the pattern the three extractions set is:** map the
cluster before cutting — every hazard named in advance was real and none surprised the work —
**pass collaborators in rather than reaching for them**, **replace calls back into the shell with
one event**, and **add a row to `CollaboratorSeamTests` before believing the move**, because the
suite failed to notice a deleted subscription three separate times.

**Two things the refactor itself taught.** A pure move can still lose coverage: three panel
handlers changed from reading a `_shell` field to asking their own `DataContext`, and disabling
all three left the whole suite green — nothing crossed that seam. And a test can quietly stop
meaning anything: `MainWindowTests` counted two `TreeView`s in one walk of the window, which
would have gone on passing while checking one fewer every time a panel moved out.

---

## Findings

Written down with their evidence, because the reasoning is worth more than the fix.

### The two ST codecs disagreed about four characters, and a rename is what it would have cost

**Found 2026-08-16 by writing the parity test the plan had been asking for**, on its first run.

`StTextCodec` (C#) and `versio_text` (python) both split a `.st` file into a declaration and an
implementation, and both trimmed the edges of each half with their own language's default —
`string.Trim()` and `str.strip()`. Those are not the same set. Enumerated rather than read off
either documentation:

```
python strips 29 codepoints
C#     trims  25
python only:  U+001C U+001D U+001E U+001F   (file, group, record and unit separators)
C# only:      none
```

**Everything else matched exactly**, across the whole range scanned — the two disagreed about
four control characters and nothing else.

**What it would have cost, which is not "a stray character".** The exporter records `ihash` over
python's `split_st(...)` implementation. `CodesysImportPlanner` re-computes that hash over
`StTextCodec.Split(...)`'s, and **the two hashes are what pair a renamed POU with the file it
became** — Phase 3's second pass, the one that handles the ordinary case where a POU's name
lives inside its own declaration. A body ending in one of those four characters hashes
differently on the two sides, the pass finds no match, and the rename is not detected: the
object is destroyed and rebuilt with a new GUID, taking every reference and task assignment
with it. That is G2's failure mode, reached by a control character.

Both C# callers of the codec are **readers** of what python wrote, which is what makes the
direction of the divergence matter rather than being symmetrical.

**Fixed by naming the set once, on both sides, instead of inheriting it.** `versio_text.WS` and
`Versio.Core.Text.PayloadWhitespace` are the same 29 characters, spelled out. Python's set was
adopted because the exporter is the writer and every hash in every committed manifest came from
it.

**And it was not one surface but three, which is why the definition moved out into a type of its
own.** `LineSketch` had the identical divergence with the identical consequence one pass further
along: a line whose edge is trimmed differently hashes differently, so the two sketches share no
digest for it and the similarity is under-reported — losing the rename that the sketch fallback
exists to catch on a mirror git never saw. `versio_import.apply_st` had it too, in a comparison
rather than a hash: it asks whether the live text differs from what `split_st` produced, and two
different trims would report an unchanged object as changed and rewrite it on every import,
forever. `TextSimilarity` has no python mirror but reads the same array now, because
`line_sketch`'s own docstring promises the two "agree about what a line is" — a shared
definition is what makes that true rather than aspirational.

**And that removes an assumption nobody could have tested.** The python that runs inside CODESYS
is **IronPython 2.7**; the python that tests it is CPython 3. A bare `strip()` makes the rule
whatever the interpreter thinks it is, and no test in this repository can see IronPython's
answer. Naming the characters makes the two agree by construction.

**Byte-neutral, verified rather than asserted, and verified twice.** No text file in either
committed mirror contains any of the four. Both fixtures were re-exported and diffed after the
codec change, and again after the `line_sketch` change: every content file identical, and every
recorded `hash`, `ihash`, `shash` and `nhash` identical, with only the `bindingId` differing
because the check passed a different one.

**Both halves of the test were mutation-checked.** Removing `U+001C` from the C# set fails on the
corpus payload that contains it; removing `U+205F` — which no payload contains — fails **only**
the assertion that compares the two sets directly. That second check is the one that earns its
place: the divergence this test actually found was in the corpus only because somebody guessed
it might be worth including.

### A GVL's declaration is applied and then overwritten by its own XML, and the log says it worked

**Found by running the feature checklist rather than by reading anything**, on 2026-08-16, in
the one import path no test had ever asserted: `ST_DECL`.

Edit `Glob_Var.gvl.st` in the mirror, import, re-export. The import log reads

```
applied Device/Plc Logic/Application/Glob_Var.gvl.st (declaration)
...
imported Device/Plc Logic/Application/Glob_Var.gvl.xml as native XML into Device/Plc Logic/Application
import: 1 updated, 7 native, 2 unchanged, 3 not applied (export-only) — 0 warning(s)
```

and **the edit is not in the project.** Both lines are true; the second undoes the first.

**Why.** A GVL exports as `GVL_BOTH`: one object, one GUID, two files — a `.gvl.st` recorded
`ST_DECL` and a `.gvl.xml` recorded plain **`NATIVE`**. `versio_import.py` runs its phases
**textual → native → delete**, so the declaration is written into the object and then
`import_native` replaces that object from an XML holding the declaration as it was at export
time. The later phase wins, every time, on every GVL.

**This is G7 wearing different clothes, and the fix for G7 was modelled on the thing that is
broken.** `TASK_BOTH` has exactly the same shape and does not have the defect, because Phase 4
gave its XML half its own form — `TASK_NATIVE` — and `ExportCoverage.IsExportOnly` names it, so
the importer skips it and the log says *"edit Task Configuration.tasks.yaml instead"*.
`IsExportOnly` names `LIBRARIES` and `TASK_NATIVE` and nothing else. The GVL's XML half was
never given one.

**The intention was written down and never enforced.** `versio_types.py`, on `FORM_TASK_BOTH`:

> Follows GVL_BOTH exactly: one object, two files, one GUID in the manifest. The difference is
> which half is authoritative — **a GVL imports from its `.st`**, and a task configuration
> imports from its `.tasks.yaml`

A GVL does not import from its `.st`. It imports from its `.st` and then from its `.xml`.

**And `_coverage.md` says nothing about it**, which is the part that makes this data loss
rather than an inefficiency. The report names three files as *"exported for review, not applied
back"* — two `.libraries.yaml` and `Task Configuration.xml` — and in a report whose entire job
is to list what an edit will not reach, absence from that list is a promise. `Glob_Var.gvl.st`
is absent from it. **That is the gap `_coverage.md` was built to close, re-opened for the second
time**; the first was closing G7 and G11, and it has its own finding below.

**How it was proven rather than argued.** Two runs differing in exactly one thing: the second
turns the plan's entry for `Glob_Var.gvl.xml` into a skip before the job is handed over, and
leaves the edit and everything else identical. The declaration survives. So the write lands and
the native phase is what removes it — which the log alone could not have told anybody, because
it says `applied` either way.

**✅ FIXED 2026-08-16, the same day.** The `.gvl.xml` is recorded `GVL_NATIVE` and
`ExportCoverage.IsExportOnly` names it, so the import applies the `.gvl.st` alone and the log
says *"edit `…Glob_Var.gvl.st` instead"*. Verified against real CODESYS: the edit is in the
project on the re-export, where the same test failed an hour earlier.

**And the shape is an invariant now, which is the part that matters more than the fix.** G7 was
patched on `TASK_BOTH` and `GVL_BOTH` had the identical defect for months, because the fix was
applied to the instance rather than to the shape.
`An_object_never_writes_two_files_the_import_would_both_apply` asserts against a **real
export** that no object writes two files the import applies — real, because both defects were
in what the exporter wrote and every unit test passed throughout. Removing `GvlNativeForm` from
`IsExportOnly` turns it red, naming both files; that mutation was run rather than assumed.

**It cost the two-file rule its subject, deliberately.** An export-only record neither votes
nor vetoes in the delete planner, so a GVL now has exactly one importable file and `gone ≠
importable` can no longer happen for anything. A GVL became an ordinary single-file object:
deleting `Glob_Var.gvl.st` removes it, exactly as deleting `Simulation.st` has always removed
the POU, and **the content check is what stands in front of it** — `ST_DECL` is verifiable, so
a GVL edited in the IDE since the export is still kept. The rule is kept rather than deleted:
an older mirror still records the XML as `NATIVE` and still reaches it, and a second vendor is
under no obligation to adopt the one-importable-file shape. The integration test that asserted
the old behaviour now asserts the new one, and says both were right at the time.

**The three candidates, and why this one.** They were not equivalent:

- **Give the XML half its own form**, as `TASK_NATIVE` did — **chosen.** Cleanest, and the
  remedy sentence comes free because the exporter finds the sibling by GUID. Its cost is the
  delete rule above, which on inspection makes a GVL *consistent* with every other object
  rather than less safe.
- **Apply textual after native**, which fixes every `BOTH` pair at once and re-orders a phase
  sequence whose current order is load-bearing for creates (`run()` says why).
- **Rebuild the XML from the edited declaration before importing it**, which is the only option
  that keeps both halves authoritative and is by far the most work.

The measurement and the three options are recorded here so the choice is made deliberately
rather than by whoever touches it next.

### "References and task assignments survive" was an inference, and it is false

> **In one line:** a renamed POU keeps its GUID — genuinely the same object — and **its task
> still lists the old name**, because CODESYS stores a task's POU list by NAME and a scripted
> `rename()` does not rewrite it. The claim was written into the README, the plan, `SAFEGUARDS`
> and the **confirmation dialog** on the strength of GUID equality, never measured.
> **✅ Corrected AND fixed 2026-08-15**: the wording no longer over-promises, and the import
> repoints the task so the claim it does make is true.

**It was found by a question, not by a test.** Asked whether a real project had actually been
renamed and imported, the honest answer was "yes, and the GUID was asserted" — which is evidence
that the object survived, and *not* evidence of what that survival buys. The fixture happens to
have `MainTask` listing `Simulation` among its POUs, so the claim was directly checkable. It
was checked. It failed.

```
before   pous: [PLC_PRG, Simulation]        Simulation.st, guid 980a4bef…
rename   Simulation → Renamed               same guid 980a4bef… — same object
after    pous: [PLC_PRG, Simulation]        ← the task still names a POU that no longer exists
```

**This is the live project talking, not a stale file.** `versio_import.py` never writes `pous`;
it applies priority, interval, core binding and watchdog only. So the re-export is reading what
the task actually holds.

**What it costs a user.** Rename a POU in the mirror, import, and the object is renamed properly
— but any task that ran it now references a name nothing answers to. Nothing warns, because
Versio did exactly what it was asked. A delete-plus-create would have broken the same reference,
so this is not a regression; the defect was **claiming otherwise in the one dialog somebody
reads before agreeing**.

**Corrected rather than quietly dropped.** The dialog now says the same object is kept and that
anything referring to it *by name* still uses the old name and needs checking in the IDE. The
narrative test asserts the false phrase is **absent**, not merely that the new one is present —
the same two-sided discipline the deletion wording needed after it lied in the other direction.
The integration test pins the behaviour as it really is, so it will notice if a future CODESYS,
or a fix here, starts updating the reference.

**✅ And then fixed, the same day.** The import repoints the task itself: after a rename it walks
every object carrying a POU list and rewrites entries matching the old name. Measured — `MainTask`
now reads `[PLC_PRG, Renamed]`, with `PLC_PRG` untouched, so the retarget is per entry rather than
a rewrite of the task.

**The API took two probes and every spelling but one is a trap:**

| Call | Result |
|---|---|
| `pous[i] = name` | raises — the list does not support item assignment |
| `pous.remove(name)` | **reports success and does nothing** — a silent no-op, exactly like `move()` |
| `pous.remove(index)` | raises *"not found in the specified Collection"* |
| `pous.replace(name, name)` | raises *"expected int, got str"* |
| **`pous.replace(index, name)`** | **works, and survives a save and reopen** |

**The first probe nearly recorded the wrong answer**, again: it tried `remove` then `add`, saw
both report success, and would have concluded the list was writable that way — while `remove`
had changed nothing and `add` had left a third entry behind. Reading the *value back* after each
call is what caught it. That is the third silent success measured in this API, after `move()`
and `create_folder`.

**The dialog says the narrower true thing.** It names tasks specifically rather than claiming
"references" survive: a visualisation binding may refer to an object by name too, and that has
not been measured. The vaguer word is exactly how the original false claim got in, so
`ImportNarrativeTests` asserts the word *references* is **absent**.

### A rename that renames nothing was reported as a rename — and the test that found it was written to assert the opposite

> **In one line:** exact-content rename matching cannot see a POU rename, because a POU's name
> lives in its own declaration — and when the file alone is renamed the planner emitted a rename
> whose old and new names were equal, which the script counted as a success.
> **✅ CLOSED 2026-08-15** by withdrawing both sides and warning instead.

**The measurement that "proved" renames worked had bypassed the planner.** A scratchpad harness
hand-wrote the rename entry with `newName: "Renamed"` and drove the script with it. That was a
real and useful measurement — it is what proved `rename()` preserves the GUID — but it proved
the **executing** half only, and it read as proof of the whole thing.

**The integration test written afterwards is what caught the difference**, by failing. It asserted
that renaming `Simulation.st` to `Renamed.st` renames the object, and it does not: the file still
opens `PROGRAM Simulation`, so the plan reads that name, old equals new, and there is nothing to
rename it to.

| What somebody does | What the planner sees | What happens |
|---|---|---|
| rename the file only | declaration still names the object — old = new | nothing renamed, both sides withdrawn, warned |
| rename the file *and* the declaration | content changed, no hash matches | delete + create, **GUID lost** — step 2 |

**So step 1 detects a rename only for a form whose content does not carry the name** — actions and
transitions. The fixture has none, which is why unit tests, a hand-built harness and a green
suite all agreed with each other and none of them touched the real case.

**The false success is the fourth in this path this session**, and the one that got furthest: the
script's own `RENAME_NOT_APPLIED` verification could not catch it, because the object never had
to move for the check to pass. **A verification that asks "is it where I expected" cannot notice
that nothing was asked of it.**

**✅ Closed the same day by manifest schema 6.** A full ST record carries `ihash`, the hash of
its implementation alone, and a second pass pairs a departed POU with an arrived one on it. A
real POU rename — file and declaration — now keeps its GUID, measured end to end against real
CODESYS, with the new declaration applied to the renamed object rather than skipped. **The
arrival must be applied**, and that is the difference from the exact-content pass: skip it and
the object wears its new name over its old declaration.

**The first answer written here was also wrong, and is corrected rather than quietly dropped.**
It said a real POU rename could be matched by comparing the implementation exactly and the
declaration with its name masked, needing nothing but the manifest and the mirror. **It cannot**:
the manifest records one `hash` per file and the old file is gone, so there is no old
implementation to compare against. Every fuzzy match needs the departed content, and only two
things have it — a hash recorded at export time (schema 6, `ihash` + `dhash`, the same shape as
`nhash`), or `git show HEAD:<path>`. The first covers a pure rename cheaply; the second is the
only route to a rename that also edits the body.

### `move()` accepted an instruction, did nothing, and reported success

> **In one line:** moving a POU out of `Application` into `Plc Logic` raises nothing and moves
> nothing; the first Phase 3 run reported `renamed: 1`, `failed: 0`, **zero warnings**, and a
> re-export showed the object at its old path. **✅ CLOSED 2026-08-15** by making the script
> re-resolve the object where it was supposed to land and report `RENAME_NOT_APPLIED` when it
> is not there.

**The third silent success in the delete/rename path this session**, after the dialog that
promised objects would stay while deleting them and the log line that described the previous
version's behaviour. The shape is identical every time: a step that cannot fail loudly, and a
counter that is incremented on the assumption it worked.

**What makes this one worse than the others** is that nothing above it could have noticed. The
import verdict was `Completed`, the summary said one object was renamed, the warning list was
empty, and the only way to find out was to export the project again and read the paths. **A
counter incremented next to an operation is not evidence the operation happened** — and this
codebase now has three examples of exactly that.

**✅ The open half of this is now settled, and `move()` is fine.** A folder was created under
`Application` in a throwaway copy and `Simulation` moved into it: no exception, under the folder
in session, still under it **after a save and reopen**, and the GUID `980a4bef…` unchanged
throughout. So `Application` → `Plc Logic` was CODESYS **correctly refusing an illegal parent**
for a POU — the operation is sound and the defect was only ever the silence.

**The verification stays anyway**, and that is the point rather than a leftover: a refusal that
says nothing is indistinguishable from a success no matter *why* it refused, and Versio cannot
enumerate which parents a toolchain considers legal. Checking the outcome costs a walk of names
already in memory.

**`create_folder` returns `None`** — the folder is created regardless and must be found by name.
That silently skipped the entire probe on its first run: the return value was used as the move
destination, so the move never ran, and the result read as "move does not work". **The first
run of a measurement nearly wrote down the opposite of the truth**, which is the same trap as
the harness that proved the executing half and read as proof of the whole feature.

**Rename in place is unaffected and works**: same GUID before and after, no warnings, verified
against real CODESYS.

### The delete path explained itself to nobody — three channels, three different silences

> **In one line:** the dialog said the wrong thing, the plan's log said nothing at all and then
> said something false, and the adapter threw away the script's explanation — so a refusal to
> destroy something reached a person as `DELETE_UNVERIFIABLE: Diagnosis` at best.
> **✅ ALL THREE CLOSED 2026-08-15.** The dialog has its own Finding below; these are the other
> two, found by auditing the change rather than by any test failing.

**`ImportPlanReader` logged nothing about deletions, and had no tests at all.** It logged the
planner's warnings, the schema explanation and the missing-file count — and not the one decision
in a plan that takes work *out* of a project. The removals were logged, but only by the script,
*after* they happened. What had been decided, and what somebody agreed to ~20 s earlier,
appeared nowhere.

**Worse, its missing-file line was actively false.** It read *"deleting objects from the project
is not applied by this version, so they remain in the project"* — written when it was true, left
in place after the import learned to delete. **The identical stale-sentence defect as the
dialog, in a second channel, surviving for the identical reason: no assertion had ever read
it.** `ImportPlanReaderTests` is new and exists because of this; one of its four cases asserts
that exact sentence is *absent* rather than merely replaced, since a test checking only the new
wording goes green again the moment somebody restores the old line beside it.

**And the adapter dropped the reason.** `LocalCodesysRunner` projected script warnings as
`$"{code}: {path}"`, discarding the `detail` the scripts have always emitted. So every refusal
arrived with its explanation removed:

```
DELETE_UNVERIFIABLE: Diagnosis                     ← what a user saw
DELETE_UNVERIFIABLE: Diagnosis — its NATIVE file cannot be compared against
  the live object, so whether it changed since the export is unknowable   ← what the script said
```

**A warning that is present, looks complete and answers nothing is worse than a missing one.**
It mattered least when a warning meant "an object was skipped" and most now that it means
"Versio declined to delete this, and here is what would change that". Fixed in the adapter,
because `SyncOrchestrator.LogWarnings` documents that nothing above it knows what a CODESYS
warning code looks like — and that stays true.

| Guard | Breaks when |
|---|---|
| `ImportPlanReaderTests` (4 cases) | the plan stops naming what it will remove or keep, or the false sentence returns — verified red by disabling the removal line |
| `AdoptAndExportTests` … `_leaves_the_project_and_an_edited_one_does_not` | the adapter drops `detail` again; asserts the reason text against the real script through the real adapter |

### The confirmation dialog told people their objects would stay, and then deleted them

> **In one line:** `670642c` taught `versio_import.py` to run `obj.remove()` and did not touch
> `ImportNarrative`, so for one commit the last thing anybody read before an import said *"This
> version does not remove objects from the project, so they will stay"* — and then the import
> removed them. **✅ CLOSED 2026-08-15**, by splitting the deletion set into what will really go
> and what will really stay, and saying both.

**This is the exact failure the whole import-confirmation chain exists to prevent, pointed the
other way.** The chain is unusually well built: the plan is decided C#-side before CODESYS starts
(Phase 1), carried to the dialog and back so the plan agreed to is the plan executed (Phase 2's
seam step), and `RequireImportConfirmation` defaults to `true` so no watcher-triggered import can
delete anything without somebody being asked. Every one of those gates worked. The sentence they
delivered to the person was false.

**What it cost a user.** Delete `FB_Motor.st`, commit, press Import. The dialog names `FB_Motor`,
says it will stay, and suggests deleting it in the IDE if that is what you want. Press Import —
the POU is gone from the project. There is no undo inside Versio, and the object's own file was
deleted in the same change, so the mirror does not hold a copy either.

**Why a green suite did not notice.** `ImportNarrativeTests` pinned the wording with
`Contains("they will stay")` and `DoesNotContain("will be removed")`, under a comment reading
*"When the script grows remove(), this test is what says the wording has to change with it."* It
does not, and it cannot. **An assertion on the old sentence cannot fail when the behaviour moves
— it can only fail when somebody fixes the sentence.** The tripwire was a lock. The same
assertion sat in `ImportPlanHandoverTests`, and both stayed green across the commit that shipped
deleting, at 1,443 and 480 tests.

**The stale claims trailed it in four more places**, all of them written while they were true and
none updated by the commit that falsified them: `ImportPlan`'s own remarks (*"no wire carries one
yet and nothing executes one"*), `CodesysImportPlanner`'s (*"a deletion is a proposal and nothing
executes it yet"*), this file's bullet recording the wording as correct-and-pinned — **directly
above the bullet announcing that deletes now work** — and the import plan's Phase 2 section.

**The fix is a split, not a re-word.** Flipping the sentence to *"will be removed"* would have
been a fresh lie for native objects, which the script refuses (`DELETE_UNVERIFIABLE`) and which
are **7 of the fixture's 13 objects**. So `PlannedDeletion.CannotVerify` carries the planner's
prediction of the script's own refusal — null when the object will go, a sentence when it will
stay — and the dialog says both things in two sentences, grouped by reason.

**What is watching it now:**

| Guard | Breaks when |
|---|---|
| `ImportNarrativeTests.Objects_the_import_will_remove_are_named_and_said_to_be_going` | the dialog stops promising removal, **or starts promising it falsely** — both sides asserted, which is what the original pair lacked |
| `ImportNarrativeTests.A_mixed_plan_says_both_things_and_does_not_merge_them` | one sentence starts covering both outcomes again |
| `PythonFileNameParityTests.The_planner_and_the_script_agree_about_which_deletions_can_be_checked` | the planner's prediction and `versio_types.can_verify` drift — verified red by adding `FORM_NATIVE` to the python list |

**The parity test is the structural fix.** The planner and the script answer the same question
~20 s apart and had no shared authority; `CAN_VERIFY_FORMS` now lives in `versio_types.py`, which
is pure and therefore runnable under CPython, and the C# mirrors it. A rule living in
`versio_import.py` is a rule nothing can compare, because that module cannot be imported outside
CODESYS.

**Measured end to end**, one import deleting one textual and one native object against real
CODESYS 3.5.22.20: `Simulation` (`ST_FULL`) removed, `Diagnosis` (`NATIVE`) kept with
`DELETE_UNVERIFIABLE`, and a re-export confirming the project afterwards holds the second and not
the first.

### Closing G7 and G11 re-opened the gap `_coverage.md` was built to close

> **In one line:** making `Task Configuration.tasks.yaml` the importable half turned the `.xml`
> beside it into a file the export writes and the import silently discards — and every channel
> that exists to say so reported it as ordinary, because it was still recorded `NATIVE_R`.
> **✅ CLOSED 2026-08-15**, by recording it `TASK_NATIVE` and reading one-wayness from one list
> per side instead of one constant.

Versio has machinery for exactly this. `_coverage.md` carries a section headed *"Exported for
review, not applied back"*; the manifest records a `form`; the changed-files row draws a lock
glyph with a tooltip. `STATUS.md` describes the discipline in four places, in the row about
library references, and the comment in `versio_export.py` above the section states the reason
outright: *a file that is written for review and silently ignored on the way back is its own kind
of gap — editing it looks exactly like editing a POU.*

**The task XML got none of the four.** One-wayness was decided by comparing against a single
constant on each side — `form == FORM_LIBRARIES` in the report, `form == "LIBRARIES"` in
`ObjectIndex` — so the second one-way form was invisible to both by construction.

**The strongest evidence is that the code already said it.** `CodesysExportForm.cs` documents
`TaskBoth` with *"its native XML is export-only"*, and 114 lines below it in the same file sat:

```csharp
// "The predicate the UI needs for the Tier 3 lock glyph … Kept beside the form table so
//  the next one-way form is declared in the same place as the rule that writes it."
public static bool IsExportOnly(ExportForm form) => form is ExportForm.Libraries;
```

The comment names the rule and the function contradicts it. It had **no caller and no test in its
entire life** — the panel is vendor-neutral and reads a manifest, so the live path never touched
this enum at all. That is why nothing went red: the one place that claimed to enforce the rule was
not connected to anything. It is deleted rather than corrected. **A second home for a rule that
cannot be reached from the code that needs it is not a safeguard, it is a comment that compiles.**

**What it cost a user.** Edit `Task Configuration.xml` in the mirror — a watchdog setting, say,
which the YAML does not carry — commit it, import. The edit is discarded, the log says nothing
useful, `_coverage.md` claims the only one-way files are the two library managers, and the panel
shows an ordinary editable row. The change is in the git history and not in the machine.

**The wrong-remedy half.** Both surfaces that *did* speak hard-coded the library manager's answer,
*"change this in the IDE"* — which for a task configuration walks the reader straight past the
`.tasks.yaml` they were meant to edit. The import log discarded the `sibling` field the script was
already sending it. Both now name the sibling when there is one, and the panel's summary sentence
names no remedy at all: the remedy is per file, so it is stated per file and in one place.

**What is watching it now**, since three of these changes are the kind nothing would have failed
on:

| Guard | Breaks when |
|---|---|
| `TypeTableParityTests.Python_and_csharp_agree_on_which_forms_are_never_applied_back` | the two `EXPORT_ONLY_FORMS` lists drift — verified red by removing `FORM_TASK_NATIVE` from the python one |
| `ObjectIndexTests.The_native_half_of_a_task_configuration_is_flagged_and_its_yaml_half_is_not` | the glyph goes back to a single-constant comparison — verified red by restoring it |
| `ExportCoverageFromRealExportTests.The_task_configurations_native_half_is_recorded_and_named_as_one_way` | the exporter records the wrong form, or the report stops naming the file or its sibling |

The third is against a **real export**, because the defect was in what the exporter wrote and
every unit test in the repository passed with the old form. `report.ExportOnlyFiles` moved
**2 → 3** in that suite: the cheapest possible signal, already asserted, and it could not fire
because it was written before there was a second kind of one-way file to count.

**The general lesson, and it is the one this repository keeps re-learning.** Phase 4 was measured
carefully — four snapshots for `Core2`, three cycles for G11, a mutation run to prove the result
was not vacuous. All of it measured *the thing being fixed*. Nothing measured what the fix changed
about a file it was not about. **A change that makes one file authoritative makes the other file
something else, and the something else needs a name.**

### Two bugs that only running the exporter could show

Phase 4's export half added `TASK_BOTH`: task configuration now writes a `.tasks.yaml` beside
its `.xml`. Unit tests were green, the solution built warning-free, and both defects below
were still there.

**One · the walk descended into the task children.** `descends_into_children` decides the
progress pre-count and a separate guard in `handle()` decides the actual recursion. The new
form was added to the first and not the second, so the exporter walked `MainTask` and
`VISU_TASK` as objects in their own right.

**Nothing was written**, because a `TASK` is `FORM_SKIP`. The only symptom was arithmetic:
`counted 18` against `walked 20`, and `skipped 4 → 6`, on a project that had gained no
objects. That is exactly the divergence `descends_into_children`'s own docstring says cannot
happen — *"the count and the walk cannot disagree"* — and it happened the first time a form
was added after that sentence was written. **A guard that lives in two places has to be
changed in two places, and only one of them is named after what it does.**

**Two · `pous` rendered as python's `repr`.** The task's POU list came out as
`pous: "['PLC_PRG', 'Simulation']"` — a quoted string that merely looks like a list, and
would parse back as one on nobody's reader. `versio_yaml`'s subset had no list-of-scalars, so
a python list fell through to the scalar branch, where `_to_text` did what it is supposed to
do.

**This is the same shape as the `FORM_GVL_BOTH` suffix bug**: a value that did not match any
case in a table quietly took the default, and the default produced a file whose contents lied
about their own type. The emitter gained a scalar-list construct — its first addition since it
was written — and its header now records why, because that file states outright that every
construct added is one both halves have to agree about forever.

**Neither would have been caught by a test**, because neither breaks anything a test asserts:
the file count is right, the bytes are deterministic, and every existing assertion passes. The
first shows up only if you read two numbers in the summary against each other, and the second
only if you read the file.

### Every import after the first fails on task configuration, and reports success

> **In one line:** a stale `<project>_project.precompilecache` beside the project made
> `import_native` throw on `MainTask` and `VISU_TASK` while the run reported `failed: 0`.
> **✅ CLOSED 2026-08-15 by Phase 4**, which stops handing task configuration to `import_native`
> at all — three cycles, zero warnings, where cycles 2 and 3 used to throw twice each. **Versio
> never deletes the cache**: the trigger is simply never reached, so the measured cache-deletion
> mitigation was not needed and was not adopted. `Core2` stopped moving in the same change, which
> closes G7 with it.

Probe P7 of [`CODESYS-IMPORT-PLAN.md`](CODESYS-IMPORT-PLAN.md) ran the round trip three times
instead of once, on a scratch copy, with the mirror held still between cycles. Cycle 1 is clean:
`native: 8, failed: 0, warnings: 0`. **Cycles 2 and 3 each throw twice** — on `MainTask` and
`VISU_TASK`, the two children of `Task Configuration`:

```
System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
  at _3S.CoDeSys.ObjectManager.Project.GetUniqueName(...)
  at _3S.CoDeSys.ObjectManager.AbstractPasteOrMergeOperation.DoExecuteObject(...)
```

Nothing is lost — the objects survive and the file is simply not applied.

**The cause is a stale `<project>_project.precompilecache`.** Five runs against the same mirror,
varying one thing each:

| The project directory carries | Result |
|---|---|
| `.project` + `.opt` pair + `_project.precompilecache` | **fails** — `MainTask`, `VISU_TASK` |
| `.project` + `_project.precompilecache` only | **fails** — same two |
| `.project` + `.opt` pair only | passes |
| `.project` alone | passes |
| pristine project, fully round-tripped mirror | passes |

The cache alone is sufficient; the `.opt` files are innocent; the mirror's bytes are irrelevant.
A freshly copied project has no cache and the first import creates one, **so the first import
after a clone works and every one after it fails** — which is why it presents as "the second
cycle", and why it reproduced 4 for 4 across two independent P7 runs.

**This reaches real users and could not reach the suite.** Anyone who has opened their project
in the IDE has a cache beside it. `Versio.Integration.Tests` copied the `.project` and nothing
else, so no test had ever run an import with one present — and both of its real-CODESYS import
tests imported exactly **once**, so they would have stopped one step short even if it did.

> **Both axes closed 2026-08-15** — see the loose end. And the diagnosis in this paragraph is
> half wrong, kept because the correction is the useful part: *copying* sidecars was never the
> missing piece. The **export** writes the `.opt` pair and the **import** writes the cache, both
> now measured from the test itself. So any test that exports, imports, then imports again
> reaches the failing state without help. The gap was only ever the second cycle.

**Deleting the cache immediately before an import fixes it, and the import regenerates it.**
Control with the cache present fails; the same directory with the cache removed passes with zero
warnings. Exporting both afterwards gives `walked 18 / written 12 / skipped 4` either way with
nothing added or removed — the only difference being the two `EventPOUGuid`s, which a *successful*
native import re-mints and the failing one never reached. So it is a working mitigation. Whether
Versio should delete another tool's files on that evidence is a separate question and is not
decided here.

**Related, and fixed on the way past:** `CodesysProjectFormat.TransientPatternsFor` did not name
the cache, so an adopted repository left a 277 KB binary untracked beside the project where
*Stage all* would commit it — see the loose end above.

#### Three smaller defects this exposed — **two and three fixed 2026-08-15, one still open**

**One · the guard written for this exact exception never runs.** The exception is quoted verbatim
in `versio_import.py`'s own `run()` docstring, as the thing the two-phase split with a save
between the phases exists to prevent. The save is conditional on `created > 0 or updated > 0`,
and an import touching only native files has both at zero — every cycle here had
`created: 0, updated: 0`. So **the guard never executed during the measurement that hit the bug
it was written for.** ~~Whether running it would have helped is untested~~ — **one observation
exists now, and it favours the guard.** The Phase-4-reverted run inside
`A_task_priority_edited_…` put the task XML back on the `import_native` path with the cache
present and the exception did not return; that run had `updated > 0`, so the save ran. The
mutation moved two things at once, so this is a lead rather than a result — but it is the first
one. A guard skipped in exactly the case it names is worth fixing regardless, because its
docstring reads as coverage.

**Two · the script's summary contradicts its own warnings.** ~~`ReplaceHandler.progress` catches
the exception and warns but never increments `self.failed`; `handle_file` increments
`self.native` regardless. So the summary reads `native: 8, failed: 0` on a run where two objects
failed.~~ The warnings *do* reach the log at `Warn` through `SyncOrchestrator.LogWarnings`, whose
comment already says the right thing — "the run succeeded but is not complete" — so this was a
wrong one-line answer rather than a silent one. The one-line answer is what gets read.

**✅ Fixed 2026-08-15**, and not as the one-liner it looked like. Counting a partly-failed file
out of `native` also takes it out of `dirty = updated > 0 or native > 0`, which is what decides
whether the run saves — so the naive fix would have made the run that had already written to the
project the one that discarded it. There is a `partial` counter in that test now. **The lesson
generalises past this defect: a counter that decides a side effect is not just a report, and
correcting what it reports can change what it does.**

**Three · nothing in the suite imports twice.** ~~Both real-CODESYS import tests in
`Versio.Integration.Tests` import exactly once, and all of them copy the `.project` without its
sidecars. So the suite could not have caught this on either axis, and a defect that needs a
second cycle has no coverage at all.~~ **Closed 2026-08-15** — and it closed Phase 4's own
missing write coverage with it, because they wanted the same fixture and turned out to be one
test. It imports twice with the compile cache present, asserts the cache is there rather than
trusting that it is, and reads a written priority back out of the project.

> **"and all of them copy the `.project` without its sidecars" was wrong**, which only came out
> when the fix was built. CODESYS writes the `.opt` pair itself on first open, so every test
> that exports before importing already had them; copying them in changed nothing and no
> assertion could see the difference. The cache is the sidecar that mattered, and it is the one
> the five-run table above had already isolated. See the loose end.

> **One thing it measured that contradicts the story above.** Deliberately reverting Phase 4 —
> putting the task XML back on the `import_native` path, with the cache present — did **not**
> bring the exception back. It silently overwrote the typed write instead. The one variable
> that differs from P7 is that this run had `updated > 0`, so the phase-split save in defect
> One above actually ran. Two things moved at once, so this is a lead rather than an answer;
> but it is the first evidence on the question defect One has been carrying, and it points at
> the guard being real rather than decorative.

**The first explanation of this was wrong, and the way it was wrong is worth more than the
finding.** It said this was "the `Core2` mutation seen from the other end" — cycle 1 mutates the
file, cycle 2 chokes on it. That fitted every observation then available, explained the timing,
and named a phase that would fix it. It was written into three documents **before a single
variable had been varied**, and the first experiment that varied one killed it: a fully
round-tripped `Task Configuration.xml` imports cleanly into a project with no cache. A story
that fits one run is a hypothesis, and this one was recorded as a cause.

**Found by running the thing twice.** Ten gaps came out of reading the import path carefully;
this one was invisible to reading, because it does not exist on the first cycle.
`tools\Measure-ImportStability.ps1` is committed so it stays reproducible.

### An import's summary was logged with the exporter's field names, and four of its events with nothing at all

Found while tracing the above. `LocalCodesysRunner.cs:514` handles `case "summary"` with one
message built from `walked`, `written`, `skipped` and `gaps` — which are the **exporter's**
fields. An import summary carries `updated`, `unchanged`, `native`, `created`, `missing`,
`failed` and `notImported`, and `ScriptEvent.Int` returns `int?`, null for an absent key. So
every import logs:

```
walked , wrote , skipped  (of which  not implemented), 2 warning(s)
```

The warning count is the only true number in it, and the six an import actually reports never
reach the log at `Info` at all. ~~**Not observed in a log**, because no import has run through
the app in the retained logs — read from the code rather than from evidence, which is worth
stating rather than blurring.~~ **It was observed on 2026-08-15**, twice, in the logs of the
Phase 4 integration test being built — exactly as written above, blanks and all. Recording that
because the caveat was the right thing to write at the time and the upgrade from "read from the
code" to "watched happening" is the part a reader should be able to see. It is the same class as
the rolling file sink that was never instantiated: a log line that exists, is reached, and says
nothing.

**✅ FIXED 2026-08-15.** The `summary` case switches on the job's operation, and an import reads
`import: 1 updated, 7 native, 2 unchanged, 3 not applied (export-only) — 0 warning(s)`. Keyed on
the operation rather than on which keys are present, so a third script growing a summary is a
compiler error rather than a silently-chosen branch; zero-valued fields are omitted, because
`0 failed` on every line is the noise that let the blank version survive.

**And the same reading found four events with no `case` at all**, falling through to
`_log.Debug("· {type}")`. The worst by a distance was **`task_set`** — Versio setting a task's
priority, interval or **CPU core binding** logged as the literal string `· task_set`, with no
task, field or value. That is the one write the entire G7/G11 argument exists to make deliberate,
and it was the least legible line in the log. `Info` now, naming all three.

**`ImportLoggingTests` is the instrument this finding was missing.** `ExportLoggingTests` had
existed for months; the import had no equivalent, which is the whole reason a line of blanks
survived unnoticed. Two tests — a clean import's numbers, and **an import that loses a file**,
because nothing in the suite had ever run a failing import at all. Both verified by mutation.

### Three moves, three deleted subscriptions, three green suites

The log console was the last of the four panels inside `MainWindow.xaml`, and the one held back
through three earlier passes because it is the only one with real code-behind. `LogPanelView` now
holds it: **`MainWindow.xaml` 1,218 → 936**, its code-behind 412.

**The code-behind turned out to be the interesting part rather than the obstacle.** Two of the
three handlers are the console's own — the list follows its tail on append, the chevron swaps
glyph on expand — and both are view concerns that moved with it. The third could not move at all:
the `GridSplitter` above the console and the `RowDefinition` it resizes belong to the window's
grid, and **a panel cannot own the row it is placed in.** So "the log is open" now moves two
things in two places, and they listen to `IsExpanded` independently rather than one calling the
other, so neither has to know the other exists.

**The geometry test caught something no other check could have.** Moving the card's `Margin`
inside the `UserControl` left the control filling the whole grid cell — flush against the rail —
while the `Border` painted 8 px in. *Nothing looked different*, because the Border still painted
exactly where it always had. But `FindName("LogConsole")` now returns the **container**, so the
named element and the painted element had quietly stopped being the same rectangle, and the
overlap assertion said so. The margin belongs on the host. This is the fourth distinct way a WPF
layout has been correct and wrong in this repository, and the first where the defect was in what
the *test* could see rather than in what the user could.

**And then the number that matters.** Deleting the panel's single wiring line — the
`DataContextChanged` subscription that is the whole reason this panel has code-behind — left
**all 477 tests green**. The chevron would have pointed the wrong way and the log would have
stopped following its own tail, in front of a user, with the suite entirely happy.

That is the **third** time in one session:

| Move | Deleted | Suite |
|---|---|---|
| Auth and identity | `Auth.Changed` subscription | 467 green |
| The remote surface | `Remote.Changed` subscription | 475 green |
| The log console | the panel's `DataContextChanged` wiring | 477 green |

Three for three. **A pure move is the single most likely kind of change to lose its coverage
silently**, because nothing about it looks like new behaviour — there is no new branch to test, no
new failure mode to imagine, and the tests that existed go on passing by construction. The rule
this earns is narrow and worth stating: **after moving a subscription, delete it and run the
suite.** If nothing goes red, the move is not yet finished.

The test that closes this one expands the log **through the window** rather than by constructing
the panel and handing it a context, because `DataContextChanged` is precisely what would not fire
in the second case — a test that built the control directly would have passed against the broken
version.

**Three tests asserted `IsType<Border>` on the console and now get a `UserControl`**, relaxed to
`FrameworkElement` exactly as the history panel's move already required, with the same note:
what those tests want is something to measure, not a particular container.

### The queue said staging; the evidence said the remote surface

`GitRemoteViewModel` — the standing, the background watch and fetch/pull/push/publish, 540
lines. `ShellViewModel` goes **5,251 → 4,958**, under five thousand for the first time.

**It is not what the queue said to do next, and the disagreement was already written down.**
`GIT-PANEL-PLAN.md`'s GP-E log records that a `GitPanelViewModel` extraction — the staging seam —
was measured and deliberately dropped: *"a move dressed as a decomposition"*, because
`CommitAsync` alone reaches eight collaborators. It named the remote surface as the seam worth
cutting instead. That document also states that where the plan and the phase log disagree, **the
phase log is right**.

Re-measured rather than taken on faith, because a note from an earlier phase can go stale:

| Cluster | Borrows | Calls into the PLC engine |
|---|---|---|
| Staging | 8 | **4** — including `EvaluateGateAsync` and `SatisfyGateAsync`, the export gate |
| Remote | 9 | **2** — `ReleaseAfterPushAsync`, `WhileGitRewritesTheTreeAsync` |
| Repository session | **22** | 4 |

Same order of borrow count for the first two; completely different depth. Staging's seam runs
through the junction where git meets the toolchain — committing *runs a CODESYS export* — which
is the most safety-critical path in the application. The remote surface's two calls are both
narrow, and extracting auth had already made it cleaner, because `Auth` is now a collaborator it
can simply hold rather than a tangle it shares.

**The measuring instrument was wrong twice before it was right, and that is worth recording.** A
first pass scored the clusters against a hand-written list of collaborators, so it could only
find what was already on the list — it missed `BranchName` and `ClearForContextChangeAsync`
entirely and reported 8 where the answer was 9. A second pass brace-counted method bodies from
their signature, which runs away on an expression-bodied member: `WakeThePoller() =>
_poller.Wake();` has no opening brace, so the parser swallowed the rest of the file and invented
four borrows the cluster never had. **A measurement that over-reports argues for the wrong seam**,
which is worse than not measuring.

**`ReconcileAsync` stayed behind.** Including it took the borrow count from nine to eleven for
one method: exiting a divergence reads `Conflicted` and `IsMidMerge` and belongs with GP-G's
conflict surface.

**Nine borrows against auth's four, and the poller is all of the difference.** A background watch
*is* its callbacks — what counts as busy, what to do when the remote moves, what to ask on each
probe — and every one of those is the shell's. They were already delegates before this moved,
which is what makes nine honest rather than alarming. Handing over the object that owns them
instead of the delegates would be passing the shell itself, which is precisely the anti-pattern
GP-E named.

**Deleting the `Changed` subscription left all 475 tests green. That is the second time in one
session** — auth was the first, at 467. Two occurrences stop being bad luck and start being a
property of the work: a pure move looks like nothing to a test suite, so nothing gets written to
watch it. The seam test is therefore a **theory over collaborators** now rather than a class per
extraction, and staging and the repository session should each arrive as one more row — which is
far likelier than each remembering to write its own class. Both existing rows fail with their
subscription removed, verified one at a time.

`CommandNotificationTests` needed the same treatment for the same reason. It named
`ShellViewModel` members, so four commands moving to `Remote` would have silently dropped out of
the theory that exists to catch a command missing from the sweep — the failure it guards against,
one level up. It carries an owner beside each command now.

**The blanket XAML rename introduced a bug, and auditing every site rather than trusting the
regex is what caught it.** The branch dropdown's `AheadBehind` lives inside an `ItemTemplate`, so
its DataContext is a `BranchEntry`, which has its own per-branch `AheadBehind`. It never bound to
the shell's and must not bind to the collaborator's. The other fifteen were then each checked for
the same thing rather than assumed — none is inside a template. Worth knowing for the next
rename: **the binding-path test covers the git panel, and this binding was in `MainWindow`**, in
a template that is only realised once the dropdown has branches.

### The first collaborator out of the shell, and the seam nothing was watching

`GitAuthViewModel` — the credential and the git identity, 545 lines. `ShellViewModel` goes
**5,573 → 5,251**. First out because it had the fewest ties into that class's 68 private fields,
not because it is the biggest cluster; auth and identity travel together because they are one
question asked twice — *can this machine speak to the remote, and does git know who is asking.*

**The four hazards were mapped before a line moved, and all four were real.** `_remote` was read
by three unrelated things; the identity feeds the commit gate; the setters called back into the
shell; the commands gate on the shell's busy flag. None of them turned up mid-flight, which is
the whole argument for mapping a 5,500-line file before cutting it rather than discovering its
shape from compiler errors.

`_remote` went **to** the collaborator rather than staying behind and being passed in, and the
reason is worth keeping: it is the credential's subject. What a failure *means* is decided by the
transport, and who a credential belongs to is decided by the host. Forge detection and the
merge-request button read it too, so it could have gone either way — but splitting a value from
the thing that re-reads it on every open is how the two drift apart.

**Then the part that matters. Deleting the `Changed` subscription left all 467 tests green.**

The extraction replaced two direct calls — `RaiseHealth` and `RaiseGitCommandStates` — with one
event and one subscription. That subscription is now the only thing making the status light move
and the commit button re-ask when a credential fails or an identity arrives. Removing it entirely
broke nothing any test could see. This is the **third** sighting of the same shape in this
repository: three moved panel handlers that no test touched, four commands raised from the wrong
setter, and now a brand-new seam. **A pure move is exactly when coverage goes missing, because
nothing about it looks like new behaviour.**

`AuthSeamTests` is written against the *consequences the shell owns* rather than against the
event — an assertion that `Changed` fired would pass while the shell ignored it. Two of its three
cases fail with the subscription removed; the third, which pins that the shell reads the
collaborator's remote rather than a copy, correctly does not.

**Twelve XAML bindings became `Auth.X`, and nothing in the toolchain can check a binding path.**
Each one is a string that compiles clean, throws nothing, and renders an empty control when it is
wrong. `--trace-bindings` cannot answer it either, and the reason is specific: both banners in
that panel are `Collapsed` on any machine whose git is configured and whose credential is
stored — which is every machine this is developed on. A clean trace over a banner nobody showed
is indistinguishable from success, which is the trap `App.xaml.cs` already documents.

So the git panel now has a test that realises it and asserts **no `BindingExpression` is in
`PathError`**, plus a count assertion so a walk that stops reaching bindings cannot pass in
silence. Mutation-checked by restoring one pre-extraction path: it fails naming the control, the
path and the fact that it does not resolve. It covers every binding in that panel, not only the
twelve this change touched.

**Writing that test found a defect in its own file.** `GitPanelChromeTests.Build` read
`using var shell = TestShell.Build();` and then returned that same instance — disposed at the
closing brace, before any test touched it, so all sixteen tests in the class ran against a
torn-down shell. It had been harmless because none of them opens a repository and disposal had
almost nothing to release. It stopped being harmless in the commit immediately before, when the
shell began disposing the log console the window is bound to.

**Verified in the app, not only in the suite**: identity and credential both resolve through the
collaborator on open (`identity: …`, `credential for https://github.com: stored`), zero `ERR`, and
the binding trace is byte-identical to the run before the extraction.

### The log said the lock was taken and never that it was given back

Found by running the app, which is the fifth or sixth time that has produced something no suite
had. The launch window was clean — zero `ERR`, window constructed, repository opened — and the
log ended, on a deliberate close, at *"change detection stopped (2 watcher(s) disposed)"*. That
is the **first** line of the shell's teardown. Everything after it was silent.

Most of that is explained by the code rather than by a defect. But one piece is not:
`JsonSessionMarkerStore.Release` logs when it **refuses** to release a marker belonging to
another session, and logs when the delete **fails** — and says nothing on the path that runs on
every ordinary exit. `Write` announces the claim. So the log of any given run records the
repository being taken and never handed back, and **a clean shutdown leaves the same trace as a
crash.**

The evidence that separates them does exist, in the wrong place: the *next* launch reports
*"the session that left the marker (pid 34692) is gone"*. That is a different run's log, and no
use at all to somebody reading the log of the session that misbehaved — which is the log they
will have.

This is the house rule about silent success rather than silent failure, and it is the same shape
as the rolling file sink that was written, reviewed and never instantiated: the code was correct
throughout. Nothing was broken. It simply did not say so.

**The second test is the one worth keeping.** Asserting the release line appears is nearly free
and would pass against a version that logged it unconditionally — including on the branch where
the release is *refused*, which would have the log stating that a lock this process never held
had been handed back. That is worse than the silence it replaced. So the pair is pinned: the
refusal says "not releasing" and must not say "released", and the marker is still on disk
afterwards, so the log and the disk agree. Mutation-checked by moving the new line above the
refusal branch — the second test fails, the first still passes.

### The disposal rule could not reach the one thing it most needed to

`ShellViewModel.Dispose` stopped the watcher, the remote poller, the sync and history panels and
the repository lock. It did nothing about `LogViewModel` — which is `IDisposable` precisely
because it owns a running `DispatcherTimer` and a subscription to the log sink.

So a test that obeyed `using var shell = ...` **to the letter still leaked a timer and a live
handler, every time.** That is the sharp part: the rule written up after fifty-six undisposed
shells was being followed, and could not have helped, because `TestShell` builds the log, hands
it to the shell and returns only the shell. Nothing the test can reach owns it. Thirty-six call
sites, all leaking, all of them looking correct.

**The cost is not what the previous write-up assumed.** It said the timer "cannot tick on a
thread with nothing pumping a dispatcher, so the practical cost is the subscription rather than
the work". `StaHarness` disproves it in its own remarks: there is **one** long-lived STA thread
running `Dispatcher.Run()` for the entire suite, and it has to be one, because the theme's
`Freezable`s belong to the thread that created them. Every log built inside a windowed test puts
its 100 ms pump on the one dispatcher that actually pumps. A leaked one keeps firing for the rest
of the run — draining its queue, calling `Filters.UpdateCounts(_sink.Snapshot())` and mutating an
`ObservableCollection` on the same thread every later WPF test is using. That is a mechanism for
a failure that is load-dependent, moves between tests, and never reproduces alone.

**The shell disposes the log now, and it is handed one rather than building it** — so this is a
class taking ownership of an object it did not create. Deliberate, and stated on the property.
Nothing else keeps a reference: `App` hands one over and forgets it, `TestShell` returns only the
shell, so there is no second owner to disagree. The alternative — handing the log back so the
caller disposes it too — makes the rule two things to remember at thirty-six sites, and a rule
with an exception is the rule that gets forgotten. Disposed last, because everything above it in
`Dispose` logs on the way out and those lines should still reach the panel.

**More shells were leaking where no rule could have reached them — one per test, across two
classes, seventeen tests between them.**
`AutomationNameTests` and `ShellLayoutTests` each built their shell *inline as an argument* to
`MainWindow`, so it was never in a variable and there was nothing to put `using` on. The helpers
are instance methods recording what they build now, and the classes dispose them; dropping
`static` was the whole edit, so every call site is unchanged.

**Two things the tests themselves taught, both by failing for the wrong reason first.**

The first version asserted `HeldCount == 0` after disposal. It failed against the broken code —
by two and by three — and it would have failed against the fixed code just as surely, because
`Dispose` stops the pump and releases the subscription without emptying the queue: the entries
the shell logged while it was alive are still sitting in it, and nothing drains them without a
dispatcher. **A baseline that survives the fix is not evidence**, and a test that goes red at the
moment you want it to is not thereby going red for your reason. It asserts on a specific entry now, emitted after disposal, not
arriving. Worth knowing for anything similar: the filter path is unusable here, because `Rebuild`
repopulates from `_sink.Snapshot()` directly and would show that entry whether the subscription
was released or not — a test written through it passes against both versions of the code.

**Measured afterwards, and it does not say what it would be convenient for it to say.** Ten
consecutive full runs of `Versio.App.Tests`: **eight green, two red** — a 2-failure run and a
1-failure run. That is not an improvement on the 1-in-14 the previous fix left behind, and it is
not evidence of a regression either; 1/14 against 2/10 is nothing at this sample size.

**The measurement is contaminated, by me.** A second test host was running filtered classes
concurrently while runs 3 to 5 were in flight, and those runs took 1m38s, 1m57s and 2m01s
against 40-50s for the five that followed. Both red runs are inside that window. Since the whole
phenomenon is load-dependent, a measurement taken under self-inflicted extra load establishes
nothing about the rate. **Run the measurement on an otherwise idle machine, and do not touch it
while it runs** — the same discipline the fifty-six-shell measurement needed.

**What the failures do point at.** Every one of them —
`GitHealthTests.Setting_an_identity_from_the_panel_unblocks_the_commit`, and two in
`AutoStashFlowTests` — is a test that drives **real git subprocesses against a real temp
repository**, and all three dispose their shells correctly. So what survives here looks like
contention for the git binary and the filesystem, not a leaked object. That is a different
problem from the one this finding closes, and it wants a different fix.

**So: the leak is closed, and the flake is not.** Those are separate claims and only the first
one is proven — by a test that fails against the old code and passes against the new, which is
the only instrument here that answers a question rather than a mood.

And the disposal in those two window helpers **is watched by nothing**, by construction:
cross-test resource hygiene is invisible to every test in a suite. So it was broken deliberately,
by disposing on the xunit thread instead of the STA one. That throws
`NotSupportedException` — *"does not support changes to its SourceCollection from a thread
different from the Dispatcher thread"* — from `CollectionView`, and fails all thirteen tests in
the class. It also corrected the comment: the reason given for marshalling to the STA thread had
been the timer's thread affinity, which was a guess about which thing objects first, and wrong.

### A dropdown centred in its own column, half a bar away from its buttons

Reported from the app: *"the 2 drop downs are so separated from their buttons, making it look
weird"*.

The top bar gave each dropdown a `*` column and its buttons an `Auto` column beside it, which
looks right and is not. **A control with `HorizontalAlignment="Stretch"` that runs into its
`MaxWidth` stops growing and is then CENTRED in what is left.** On a 2400 px window each `*`
column is far wider than the 420 px and 360 px caps, so each dropdown floated in the middle of
its column with a strip of empty bar between it and the Open button that acts on it — and the
buttons read as belonging to nothing.

**The pairing is now expressed in the layout instead of implied by adjacency.** One column per
group, with `*` for the dropdown and `Auto` for its buttons inside it. The buttons cannot drift
at any width because there is nothing between them any more.

**The first attempt at that over-corrected, and the correction is the more useful half.** It
bounded each group and left-aligned it, which removed the gap and introduced two more: the
dropdowns shrank, and the two caps differed — 500 against 440 — so the bar came out visibly
weighted to one side with dead space down the middle. Reported immediately, and rightly: *"the
drop downs need to cover most of the top bar, and not be 1 side weighted"*. **A cap on a group
is a cap on the dropdown inside it**, which is not what the cap was for.

Two equal `*` columns and no spacer now. The dropdowns match each other at every window size and
together span everything between the brand and Settings. On a very wide monitor that makes each
wider than its contents need — a repository name is short — and that is the trade taken
deliberately: symmetry across the bar over a snug fit around the text. `ShellLayoutTests` pins
the two properties that were wrong rather than the pixel widths: **the dropdowns match each
other**, and **together they span more than 70% of the bar**.

Worth recording for the same reason the earlier bar fix was: this is a **third** distinct way for
a WPF layout to be correct and wrong. A `DockPanel` whose children all default to `Dock=Left`; a
`Grid` cell holding two children that overdraw; and now `Stretch` silently becoming `Center` at a
`MaxWidth`. None of the three is a bug in WPF and none produces a warning — each is a layout that
did exactly what it was told.

All of it is measured at **2400 px**, because that is the only width at which any of it happens:
at the 1000 px the other layout tests use, the columns are narrow enough that nothing binds and
every version of this looks identical.

### The history did not say which commits were still only on this machine

Reported from the app. Every commit in the panel was drawn identically, so nothing on screen
separated work that exists in one place from work a colleague can already see.

The branch badge says *"2 ahead"* — which does not say **which** two, and stops being an answer
at all on a branch with no upstream, which is exactly the branch whose work is most invisible.

**The query is `HEAD --not --remotes`, and the alternative is the interesting part.** The obvious
`@{upstream}..HEAD` answers *nothing* on a branch with no upstream, and would then need a special
case so that a branch cut from a pushed `main` did not report `main`'s own commits as unpushed.
`--not --remotes` asks the question the reader actually has — *can anybody else see this yet* —
and is correct in all three cases: with an upstream, without one, and with no remote configured
at all.

Read **once per refresh, not once per page**: a later page is older history and cannot contain a
commit the first read would have missed, and re-asking would let two pages disagree about the
same branch if a push landed between them. Capped at 2000, because a repository with no remote
answers with its entire history; beyond the cap a commit reads as pushed, which is the safe
direction — it understates how much is local rather than marking ancient history as unsent.

**An arrow, not a label**, and the first version got that wrong. It was a filled pill reading
"unpushed", which is too loud for a list this dense — a word repeated down every row of a run,
when the run is the thing being read. The arrow is the convention every git client uses, it is
the *same* character the branch badge already spends on ahead-ness, and it needs no legend. The
argument for the pill had been "a coloured dot would need a legend", which is true of a dot and
not of `↑`.

`IsUnpushed` is the one mutable thing on `CommitRow`, and deliberately so. The rest of a commit
is immutable — its hash *is* its content, which is what makes the file cache safe — but whether
a remote has it changes on every push and every fetch.

### A UI audit, and what it says about the ones that got through

Fourteen findings across the shell, the stylesheet and the two dialogs. Twelve are fixed; the
two left open are a mechanical sweep and a feature, and both are in the loose ends above.

**What is worth recording is not the list but its shape.** Almost nothing found was a taste
problem — the token layer, the theme-swap contract and the retemplated controls are better than
most WPF applications ever get. What the audit actually found was **the design system's rules
going unenforced**, in exactly the pattern this document has already named twice:

> a comment describing a rule that nothing enforced

`RepoEntry.Location` was the first instance and `BranchEntry.Tracking` the second. The audit
found the third and by far the largest: `Spacing.xaml` opens with *"Scale: 2 4 8 12 16 24 32.
Nothing off-scale."* and `Space.1`–`Space.7`, `Pad.Row`, `Pad.Panel` and `Pad.Control` have
**zero uses** against **239 hardcoded margin and padding literals** — three of the six most
frequent being 6, 10 and 14, sitting beside 8 and 12 in the same rows. That is why the panels
read as slightly restless while every individual screen looks considered.

The same shape, smaller, four more times: `Motion.Fast` and `Motion.Base` declared with an
argument attached and no `Storyboard` anywhere in the codebase; `Brush.Diff.AddedWord` and
`Brush.Diff.DeletedWord` contrast-tested in both palettes and rendered nowhere; `Radius.Dialog`
and `Font.Size.Title` referenced by nothing.

**A token nobody consumes is a claim about the design that no screen has to honour.** The rule
worth taking: a token file needs the same treatment as a style — declared, used, and something
that fails when it stops being either.

### Keyboard focus was invisible in every control in the application

A grep for `FocusVisualStyle`, `KeyboardNavigation` and `IsKeyboardFocused` across
`Versio.App` returned **one hit** — a background change on `FlatTextBox`. Every other
retemplated control had no focused state at all, so WPF fell back to its own adorner: a 1 px
dotted **black** rectangle. On `Surface.Base` at `#1D2026` that is invisible, and on light it is
the exact piece of 2009 chrome the stylesheet's header spends four paragraphs removing.

Practically: tabbing through the git panel gave no way to tell whether Enter would fetch, pull,
push or discard. **Discard is in that row.**

This is the stylesheet's own stated failure mode, one level up. Its header says *"nothing fails
when a control is simply never styled"* about four controls that kept their Aero chrome; the
same sentence is true of a **property** never set on any of them. A control with no focus visual
builds, lays out and renders perfectly.

Fixed with one shared `FocusRing` applied through the styles, an inset variant for the rail —
where a 40 px button in a 44 px column has no room to ring outward — and per-template outlines
for list and tree rows, because an adorner that expands outward is clipped by the viewport and
the first and last visible row would lose the edge being arrowed towards. `FocusVisualTests`,
13 tests, with the rows focused inside a real shown window: **keyboard focus belongs to a
`PresentationSource`, not to the visual tree**, and an element merely measured and arranged in
isolation refuses `Focus()` and reports `IsKeyboardFocused` false forever — which is how the
first version of those two tests failed while the feature under them worked.

### A horizontal scrollbar was arranged ten pixels wide, and three other things on the same axis

The `ScrollBar` style set `Width="10"` as a plain setter and tried to correct for orientation
inside `ControlTemplate.Triggers`, which only ever set `Height`. Nothing released the width.
Measured, through the app's own theme dictionary by pack URI, in a real 400 × 300 `ScrollViewer`:

```
appStyleFound = True
HORIZ:  ActualW = 10      ActualH = 17.33   usesAppStyle = True
VERT :  ActualW = 17.33   ActualH = 282.67  usesAppStyle = True
```

**A 10 px stub in the corner of a 400 px viewport.** Horizontal bars appear in exactly two
places and both matter — the diff view and the log console — so long ST lines and long log
messages could not be scrolled sideways at all.

Three more defects rode along in the same template, and they share one cause: **an axis assumed
rather than stated.** The thumb hardcoded `Width="6"`, which is thickness on one axis and length
on the other. `Track.IsDirectionReversed="True"` is right for vertical and inverts a horizontal
drag. And the trough's page buttons issued `PageUp`/`PageDown` — they are `Opacity="0"` and
therefore **still hit-testable**, so clicking the trough of a horizontal bar scrolled vertically.
That fourth one the audit did not find; it turned up while splitting the template.

It survived because the scrollbar *was* styled. `StockControlStyleTests` exists to catch
controls that kept stock chrome, so a control with a custom template was never on the list — and
nothing in the suite had ever arranged a horizontal one. Now split into a template per axis, the
way WPF's own theme does it, which is what makes each of the four impossible to write again.
`ScrollBarChromeTests`, 7 tests, including a drag through `Track.ValueFromDistance`.

**What this cannot do, stated rather than quietly not happening:** the 10 px is the bar's own
preferred thickness and applies where a `ScrollBar` stands alone. Inside a `ScrollViewer` it does
not win — that template assigns its bars a dimension from `SystemParameters` as a **local
value**, which outranks any style setter — so the gutter stays the system's 17 px with a 6 px
thumb centred in it. Reaching the last 7 px means retemplating `ScrollViewer`.

### Every settings write reset every other setting

`AppSettings`' own remarks nominated the theme as *"the next preference to need persisting"*, and
persisting it turned up something larger. Both write paths built:

```csharp
await _settings.WriteAsync(new AppSettings { Recent = _recent }, CancellationToken.None);
```

Correct **exactly while the record had one field**. The moment it had two, opening any repository
— which promotes a path and rewrites the whole file — would silently reset the other one.

**The failure would have been invisible until the launch after next.** Pick Dark, open a
repository, close, reopen: still Dark, because the write happened after the read. It is the
launch after that which comes back on Follow system, with nothing in the log and nothing on
screen connecting the two. Found only because the field was added and a test asked whether the
recent list survived writing a theme, and vice versa.

The shell holds the whole record now and replaces by field. `PreferencePersistenceTests`, 12
tests, both clobbering directions among them.

Two smaller things travelled with it. **"Follow system" was a snapshot, not a subscription** —
the registry was read once at startup and nothing listened afterwards, so flipping Windows into
dark mode with Versio open left it in the light palette until restart, on the setting that is
the default and that says by name that it follows. And a restored pane size is now **clamped
against the window it is being restored onto**: somebody who dragged the log to 900 px on a 4K
monitor and then opened Versio on a laptop would get a log taller than the window, the panels
squeezed to nothing, and no splitter left on screen to drag it back with.

### A StaticResource that compiled clean and threw on the first render

The motion work needs `StaticResource` and cannot use `DynamicResource`: a `Storyboard` is a
`Freezable`, and a `DynamicResource` inside one resolves once and is never re-resolved — the
trap `ThemeManager.cs` documents at length.

`Theme.xaml` merges `Spacing.xaml` before `Controls.xaml`, which is enough for the BAML compiler
and **not enough at load**. A merged dictionary resolves static references against itself and
its own merges, not against whatever the parent merged alongside it. Zero warnings, then:

```
XamlParseException: Cannot find resource named 'Motion.Fast'
```

the moment anything rendered a rail button.

**This is the stylesheet's own header happening to the stylesheet** — *"a key can hold a template
that resolves fine and throws the first time something renders it"* — and it is the fourth time
in this repository that a XAML reference has compiled and failed at first paint. It cost nothing
this time because `FocusVisualTests` and `StockControlStyleTests` apply and measure rather than
look up, and they went red immediately. Without them it would have shipped and been found by
somebody clicking the rail.

`Controls.xaml` merges `Spacing.xaml` itself now — fifteen immutable primitives, no colours,
nothing theme-dependent, so the duplicate cannot disagree with the original about anything.

### A status light that could only be right by accident

The leftmost pixel in the status bar, shaped exactly like a health indicator:

```xml
<Ellipse Width="7" Height="7" Fill="{DynamicResource Brush.Status.Added}" />
```

Hardcoded green. Green while git was missing, while the tree was mid-merge, while a sync had
failed and while the branch had diverged — and the shell computes every one of those states,
`GitIsMissing`, `IsMidMerge`, `Sync.HasFailure` and `Standing`, and the status bar read none of
them.

**An indicator that cannot be wrong is not an indicator.** The precedence between the seven
conditions lives in `ShellHealth` in Core now, where it is asserted without a repository, a
process or a clock — because the interesting question is never "does it turn amber" but *which
of the three amber reasons does it name*, and several of them are routinely true at once. No
repository open is neutral rather than green: a green light on an empty shell reports the health
of nothing.

The same block held a second defect of the same kind. The status bar's `Grid` had **no
`ColumnDefinitions`**, so the left stack and the right-aligned repository path occupied one cell
and simply drew on top of each other. Invisible on a maximized 2560 px window, which is exactly
why it survived — `MainWindow` opens `WindowState="Maximized"` and the collision only appears
once somebody restores it, a size no test had ever measured at. `ShellLayoutTests` measures at
1000 px now, with a long path and a long message.

### The four dialogs that were still the operating system's

Every window, tooltip and popup follows the palette. `WindowCaption` goes as far as P/Invoking
`DwmSetWindowAttribute` so the title bar is the *exact* `Surface.Base` rather than merely dark,
on the argument that a seam one shade off reads worse than an honest contrast.

And then four flows called `MessageBox.Show`, which is drawn by the operating system, follows
nothing, and lands as a bright system dialog over a dark application. The four were not
marginal: a rejected folder, a repository open elsewhere, **confirming a discard**, and
**confirming an import into a `.project`** — the two most consequential prompts in the product.

`GuardPromptWindow` already did themed prompts with an arbitrary list of options and a
designated cancel. Routing them through it exposed the smaller half of the problem:
`ConfirmDestructive` was `Func<string, string, bool>`, so there was **nowhere to put the button's
words** — every one of these ended in OK and Cancel while the captions were all phrased as
questions. *"Discard changes to this file?"* — OK is not an answer to that. That is the exact
argument `GuardPromptWindow`'s own remarks make about three-way prompts, and it applies no less
to a two-way one.

The delegates take a `DestructivePrompt` carrying its own affirmative label now, so the prompts
say Discard, Abandon, Delete and Import. **Cancel leads**, which is not a style choice: the
window gives the first option the accent and `IsDefault`, so putting Cancel first is what
preserves the property the `MessageBox` got from its `defaultResult` argument — a stray Enter
must not destroy anything.

### Selecting diff lines did nothing, and copying them did nothing

`DiffView` has set `SelectionMode.Extended` since it was written, so click, shift-click and
Ctrl-click all built a real selection. The container style supplied in the shell reduced the row
to a bare `<ContentPresenter />` with no `IsSelected` trigger, so nothing was drawn — and no
`Copy` binding existed anywhere in the application, so Ctrl+C did nothing either. A user dragged
across twelve lines, saw no response at all, pressed Ctrl+C, and got nothing.

**The bare template was half right, and that is the part worth recording.** A selected fill of
the kind `RowItem` uses would paint over `Diff.AddedBg` and `Diff.DeletedBg`, and the row tint
*is* the information. But "must not overwrite the tint" was solved by drawing nothing, which is a
different thing from solving it. Selection is a wash **on top** now — the accent at low opacity
darkens a green or red row without flattening it, so a selected added line still reads as added.

The copy orders by document position, not by `SelectedItems`, which is in the order things were
picked: a Ctrl-click walk upwards through a hunk would otherwise paste the change running
backwards. The marker travels with the line, because what is pasted is an excerpt of a *diff* —
stripping the `+` and `-` turns "here is what changed" into "here is some code", which is the
half worth sending.

### Icon-only controls announced Private Use Area codepoints

`AutomationProperties` appeared **zero times** in the application. `IconButton` puts the glyph in
`Content`, and WPF derives a control's automation name from its content — so Open, Clone, New
branch, Refresh, Settings, the log's Pause and its Clear all announced `U+E8B7`, `U+E896`,
`U+E710` and friends. That is the whole top bar and the whole of primary navigation. Every one of
them had a `ToolTip` written for a sighted user hovering, and **WPF does not use `ToolTip` as an
automation name**.

An icon-only button with no name is unusable by a screen reader and unreachable by voice control:
there is nothing to say to press it.

`IconButton` binds its name to its own `ToolTip` now, so the names already written reach a reader
with nothing new to author and nothing to keep in step — and it follows a tooltip that changes,
which the log's Pause does: it says Resume while paused. The rail takes its short name from `Tag`
and keeps the sentence in `HelpText`, because a name is what a reader says every time focus
lands and *"Project — the repository as CODESYS structures it, with every change marked"* is not
that. `AutomationNameTests` walks the real shell asserting no control's name contains a PUA
codepoint — **through the automation peer, not through the property**, because the peer is what
applies the fallback to content and the fallback was the entire defect. Reading
`AutomationProperties.Name` directly would have returned empty and looked fine.

### A shared row template learned about objects, and blanked the panel that shared it

GP-J taught `ChangeRowTemplate` to show an object — `Primary`, `TypeLabel`, `IsExportOnly` — and
the **history panel was drawing its commit files with that same template**, deliberately, so
that a deletion would be the same letter and the same colour wherever it was seen.

A `CommitFileRow` has none of those properties. **WPF fails a binding to a missing property
silently**, so every row in the history panel rendered with **no filename at all** — and wearing
a lock glyph, because the `Visibility` binding failed too and a failed `Visibility` binding
leaves the element `Visible`. A panel of blank rows, each claiming its file could not be edited.

**Every test was green.** GP-J added realised-row tests precisely because this codebase has been
bitten three times by templates that only fail when a row is built — and they realise rows in
the git panel's own lists. Nothing came to the history panel. **A shared template means every
list that uses it needs a realised row, not only the list the feature was written for**, and
that is the rule worth taking from this.

Found by the product owner opening the app, which is the third time in this branch that running
it has produced a defect no suite had. The history list has its own template now — the glyph and
its colour still come from `ChangeGlyph`, which is where that decision actually lives — and
`HistoryRowChromeTests` builds a commit file row and reads it. Reverting the template fails both
of its tests.

**A commit's files stay paths, deliberately.** Naming them from the object index was tempting and
is wrong: the index describes the *current* export, so a renamed object would put today's name
on yesterday's file.

### The same repository, twice in the dropdown, because git and Windows spell paths differently

`git rev-parse --show-toplevel` answers with forward slashes; the Windows folder picker hands
back backslashes. `RecentRepositories.PathComparer` was `OrdinalIgnoreCase` — case-insensitive
and **separator-sensitive** — so opening one repository both ways produced two entries, with the
same name and the same location and no way to tell them apart.

The comment above that comparer already had the whole argument in it: *"an ordinal comparison
would let the same repository appear twice in the list, once per way somebody happened to type
it."* It was written about case, and separators are another way it gets typed.

`RecentRepositories.Canonical` now settles on backslashes with no trailing separator, applied on
the way in **and** on the way out — the stored spelling matters because the folder picker is
handed it as a starting directory and because `settings.json` is a file people open. An existing
list tidies itself the next time anything is promoted, which is every launch.

### A rule added after a repository was adopted never reached it

Found while checking the previous finding against the reporter's own repository, which is the
only reason it was found at all. Their `.gitattributes` had the two project rules and nothing
about the mirror; their `git config core.autocrlf` said `true`; and `git check-attr` on an
exported `.st` answered `text: unspecified`. The fix written an hour earlier would never have
reached them.

`AutoAdoptAsync` skips a project that is already bound — correctly, since adopting twice is not
a thing — and adoption is the only caller of the two rule planners. So the rules a repository
gets are the rules that existed on the day it was adopted, forever. Three have been added since
this repository started tracking its own fixture: the mirror's `text eol=lf`, and the staging
and trash directories the swap moves through.

Every scan now tops up the rules for **every** binding, not only the ones just adopted. Both
planners already leave an existing rule alone and report no change when everything is covered,
so it writes once after an upgrade and never again — pinned by a test that opens the same
repository twice and compares the bytes.

This is the third time in this session that a rule was correct and unreachable, and the shape
is always the same: the code that decides is fine, and nothing calls it for the case that
matters. The other two were a guard intent nothing asked about and a filter nothing supplied
globs to.

### The mirror arrives with the wrong line endings, and git says it is fine

The loose end this document has carried since the audit read: *"Adoption writes no line-ending
rules to `.gitattributes`. On a machine with `core.autocrlf=true`, a cloned mirror may read as
entirely modified. Measure it before fixing: the repo's own rule is that a plausible inference
is not a measurement."*

Measured, and **the inference was wrong in a way that matters more than being right would
have**. Against real git, a scratch repository, `core.autocrlf=true`:

```
$ git -c core.autocrlf=true clone origin clone-crlf
warning: in the working copy of 'Main.st', LF will be replaced by CRLF …

$ git status --porcelain          # in the clone
                                  # ← nothing. The tree is clean.
```

Git is perfectly happy: it normalises on the way back in, so nothing reads as modified and no
diff appears. **Versio is not**, because the exporter writes LF and the hash gate hashes the
bytes on disk:

| | |
|---|---|
| `origin/Main.st`, as exported | `6fb69762e1887a3f…` |
| the same file in a `core.autocrlf=true` clone | `c58f937fb0ab1dae…` |

Every file in a teammate's fresh clone hashes differently from what an export of the same
project would produce. The engine reads that as a mirror somebody has edited by hand — the one
state that makes the text side authoritative and points the sync the wrong way.

Adoption writes `<mirror>/** text eol=lf` now, beside the `binary -diff -merge` rules for the
project files. It is the opposite rule for the opposite reason, and they belong in the same
file: a project must never be line-merged, and a mirror exists to be. Proven the same way it
was found — with the rule in place, the same clone with `core.autocrlf=true` still on lands
byte-identical to what was committed, `6fb69762…` on both sides.

An existing rule for the same pattern is still never rewritten, so a repository that has
already decided something about its mirror keeps its decision.

### The auto-stash swallowed its own record, and `.gitignore` could not have saved it

Found by the second test written for the flow, not by reading it. Switching away twice stashed
the work correctly both times — and the second stash took `.versio/local/autostash.json` with
it, because a repository with no adopted project has no `.gitignore` rule for that directory,
so `--include-untracked` counted Versio's own record as the user's work.

The consequence is worse than untidy. That record is the **only** thing that knows which
stashes are Versio's and which branch each belongs to, so stashing it away leaves real work
sitting in `git stash list` with nothing pointing at it — and a later `pop` would restore the
*older* record over the newer one.

**`.gitignore` is the wrong instrument for this**, which is the part worth keeping. It is a
tracked file: a stash carries the rule away at exactly the moment the rule is needed, and
writing it means editing a file the user commits and shares. `.git/info/exclude` is outside the
working tree — it cannot be stashed, committed or conflicted — and it is per clone, which is
precisely what this rule is. It is written when a repository opens, before anything reaches
`.versio/local/`. Adoption still writes the `.gitignore` rule, and that is not redundant: that
one is what a *teammate* gets when they clone.

The same gap applied to the direction locks in any repository where no project had been adopted
yet. Nobody had hit it because a lock only exists once there is a binding, and a binding only
exists after adoption has written the ignore rule — the auto-stash is the first thing Versio
writes into a repository *before* adopting anything.

### The IDE's heartbeat was a `git status` every five seconds, forever — and it took two fixes

Found while checking that the log was quiet after the export-echo fix. It was not: with a
repository open and nothing happening, the app ran a `git status` roughly every 2.5 seconds and
wrote a Debug line for each.

The cause is not Versio's own timers. `_deferredTick` fires every five seconds and does nothing
when nothing is deferred, and no watcher event had settled at all. It is the **CODESYS IDE**:

```
ObjectOrientedProgramming(Extended).~u   14:00:07
… six seconds later …
ObjectOrientedProgramming(Extended).~u   14:00:12
```

CODESYS rewrites its lock marker every few seconds for as long as it holds the project. Each
rewrite is a working-tree event, each event refreshes the status, and each refresh spawns
`git.exe`. **The `.~u` file is gitignored** — adoption writes that rule itself — so it cannot
change what `git status` reports, ever. The work is guaranteed to produce the same answer.

The measurement that matters is that this is not an edge case: **an engineer with the IDE open
is the normal state of this tool**, and in that state the app spawns ~24 git processes a minute
and writes ~1,400 log lines an hour, against a design goal of eleven.

Two things make the fix tractable rather than speculative, and both already exist:

- **Versio knows which paths it told git to ignore.** Adoption writes `.versio/local/`,
  `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt` into `.gitignore`, so those are
  already a list rather than a guess.
- **The vendor seam already answers where the lock marker is.** `IPlcProjectFormat` reports it
  — that is how open-project detection works — so the rule stays vendor-neutral: *a path the
  format declares as its lock marker is not a working-tree change.*

**Fixed, and the fix was wrong twice before it was right** — which is the part worth keeping.

`RepositoryChangeFilter` learned about paths **Versio itself put in `.gitignore`**, supplied by
the vendor seam: `TransientPatternsFor` is the same call adoption makes when it writes the
rules, so what the watcher ignores and what git ignores cannot drift apart. That is the one
exception to this class's standing rule that working-tree paths are never filtered by name, and
it is safe for a specific reason — these are not names guessed to be ignored, they are the rules
this app wrote.

Then the app was relaunched and **it polled exactly as before.** Watching the repository
directly for 20 seconds said why:

```
Samples\ObjectOrientedProgramming(Extended).~u~   7
Samples                                          4
Samples\ObjectOrientedProgramming(Extended).~u    4
```

CODESYS does not write the marker in place. It writes `.~u~` — trailing tilde — and renames it
over `.~u`, so the glob covered the less frequent of the two, and **`.~u~` was in nobody's
`.gitignore`**: a file appearing and vanishing in the working tree that git had never been told
to ignore. The pattern is `.~u*` now, which fixes the ignore rule as well as the noise.

Relaunched again, and **it polled exactly as before.** The remaining trigger was the third row:
a folder's own timestamp moves when a child appears or vanishes, so filtering the child by name
left the parent's event arriving at the same rate. Dropped by the same argument the bare `.git`
event already used — the folder's event says only that *something* changed, and the child's own
event says what. A creation, deletion or rename of a folder is not a write and still gets
through.

| | |
|---|---|
| Before | ~2 `git status` per 5 s, 493 log lines in one 50-second window |
| After | **1 `git status` in 45 seconds of idle** — the one at startup |
| Still detected | a new file, in 0.5 s; its deletion, in 0.5 s; both verified in the running app |

What remains over a longer watch is the **20-second backstop tick** — 24 calls in eight idle
minutes, one every 20 s to the millisecond — which is `StatusRefreshPolicy` doing exactly what
it was written to do, since `FileSystemWatcher` drops events silently and a panel that is 20 s
stale beats one that is stale forever. It is worth stating because this document elsewhere
claims an idle app writes about eleven lines an hour: with a repository open it writes about
180, all of them the backstop's own `git status`. That is a design decision, not the bug that
was just fixed, and it is the next thing to look at if idle cost ever matters again.

The lesson is the one this document keeps recording in different clothes: **the rule was correct
in unit tests while the app still spawned git every five seconds, twice, for two different
reasons.** What closed it was a real `RepositoryWatcher` over a real directory, replaying what
CODESYS actually does — write, rename, repeat — and asserting nothing comes out, then that a
real edit beside it still does.

### An export woke the change detector once per file it had just written

Noticed in the log while checking something else, on a real project with 56 exported files: for
about forty seconds the app ran a `git status` and hashed the whole mirror roughly twice a
second, with git invocations overlapping each other — `still running` — and each one ending in
a `BlockedByLock` warning.

The mechanism is one line in the log above the storm:

```
watcher error on …\ObjectOrientedProgramming(Extended) (Access is denied.) — events were lost
```

The export stages into `<mirror>.versio_staging` and **swaps the directory into place**, which
invalidates the `FileSystemWatcher`'s handle on the directory it was watching. The error
handler does the right thing — an overflow means events were lost, so rescan everything — and
the rescan then finds all 56 files changed, because they are new files, and reports each one as
a settled change. Every one of those became a sync job: a conflict probe (a `git status`
process) and a full-tree hash, before the direction lock refused it. **n files cost n status
processes and n full-tree hashes.**

`SelfWriteSuppressor` exists for exactly this and could not do it. It registers *one* path with
*one* expected hash, consumed by the first matching event, and `ExportAsync` registers it
**after** the run completes — tens of seconds after the swap those events describe. One late
registration against dozens of echoes. It had been that way since the suppressor was written,
and it never showed up because **the direction lock was quietly doing its job**: an export only
runs while the project side is authoritative, and a mirror edit is refused in that state anyway.
Nothing was ever lost. It was pure cost, and it is quadratic in the size of the project — worst
exactly where projects are large.

The fix is a window rather than a set of hashes, because that is the shape of the problem: the
mirror is mid-write for a known stretch of time and every event in it is ours.
`BeginOwnWrite(bindingId, side)` is the same mechanism as the git-write window (§5.A), with two
deliberate differences:

- **Per binding, and per side.** A checkout rewrites the whole repository; an export rewrites
  one mirror. Holding every binding off for the 20–31 s a toolchain run takes would drop real
  edits elsewhere, and an engineer saving in the IDE *during* an export must still be seen.
- **Opened before the run and closed after the bookkeeping**, because the echoes are raised by
  the swap in the middle and settle whenever the watcher's quiet period lets them — which is
  long after the toolchain has exited.

Nine tests, five of which fail with the gate reverted. The one that matters is the ninth: it
drives a **real export** through the orchestrator and asks, from inside the run, whether the
window is open — every other test opens it by hand and would pass against an export that never
called it. That is the mistake this repository has now shipped three times, and it fails when
the call is removed.

Not fixed, and worth knowing: the watcher's response to a lost-events error is still to report
every file in the tree individually. The window makes that free for our own writes; a genuine
overflow — a network share, antivirus — still produces one job per file.

### The repository dropdown could not do the one thing it exists for

Reported from the app, with a screenshot of the control open and holding exactly one row:
*"the repo drop down is supposed to store recent repos… currently it doesnt store anything."*

`RecentRepositories` was an `ObservableCollection` on the view model, appended to when a
repository opened and dropped when the process exited. Nothing wrote it anywhere and nothing
read it back, so on every start it contained precisely the repository the app had opened by
itself — and a control whose entire purpose is jumping between repositories could not jump
anywhere. It had looked right in every session where somebody opened a second repository by
hand, which is every session it was ever demonstrated in.

Underneath it was a gap rather than a bug: **Versio had no machine-level state at all.** Every
store it owns belongs to a repository and lives in that repository's `.versio/`. Which
repositories this user has opened is not repository state — it names paths that exist on one
disk — so there was nowhere for it to go. `%LOCALAPPDATA%\Versio\settings.json` is that
place now, beside the logs, written through the same atomic temp-and-replace `JsonLockStore`
uses and read with the same rule: unreadable costs a dropdown's contents and nothing else.
The theme is the next thing that should join it — it is currently reapplied as `System` on
every start, which is a preference the app forgets just as thoroughly.

Worth pairing with the finding below about *Open logs folder*: writes under `%LOCALAPPDATA%`
are redirected when the app runs from an MSIX/AppContainer, so a packaged build and a plain one
can end up with two separate recent lists. Nothing to fix today — packaging is unbuilt — but it
is the same environment fact, and it now applies to a second file.

Three decisions in it are worth keeping:

- **The remote is recorded at open time, not when it is needed.** By the time it is wanted —
  offering a vanished repository back — there is no `.git` left to read it out of. Promoting an
  entry inherits the old URL when the new read comes back empty, so opening a repository while
  its remote is unreachable cannot erase the only thing that could restore it.
- **A missing repository is a question, not a refusal.** Every row in that dropdown is a
  repository the app told the user they could come back to, so one that has vanished offers
  *clone it again*, *remove from list* and *cancel* — where the generic path says "not a Git
  repository" and opens a folder picker. Which options exist is a pure function of whether a
  URL was recorded, because a *Clone it again* button on a repository that never had a remote
  is a button that cannot work.
- **Nothing is removed on discovery, and nothing deletes a folder.** A disconnected network
  drive and a deleted clone are indistinguishable from here, and quietly forgetting the second
  also forgets the URL that could bring it back. The label says *Remove from list*, never
  *Delete*, because deleting the folder is what the user is afraid the button does.

One existing test had to change rather than being made to pass: `A_repository_that_cannot_be_
opened_leaves_the_dropdown_where_it_was` asserted that a dropdown row which will not open
raises `RepositoryRejected`. It now asserts the missing-repository question instead — the
guarantee it was written for, that the dropdown never points at a repository that did not
open, is unchanged and still asserted.

Startup gained a third fallback while this was wired: an explicit path argument, then the
working directory *if it is inside a repository*, then the most recent repository still on
disk. Launched from a desktop shortcut the working directory is wherever Windows put it, and
the app opened on an empty window beside a dropdown that had forgotten everywhere the user had
ever been.

Five of the seven new view-model tests fail when persistence or the missing-repository check is
reverted, and they come in through `OpenAsync` and `SwitchRepositoryAsync` rather than through
the store — this repository has been bitten three times by a method that was perfectly tested
and called by nothing.

**And the first version of this shipped a bug of its own, caught by running the suite.** The
view model defaulted to the real store, so every test that opened a throwaway repository
rewrote the developer's own recent list: two `versio-reset-…` temp folders were sitting in
`settings.json` when it was found. The default is a store that remembers nothing, the real one
is wired by the application host and nowhere else, and there is now a test that opens a
repository through the default constructor and asserts the user's settings file is byte-for-byte
unchanged.

### One field's type disagreed, and the panel lost fifty-six objects without saying so

Reported from the app against a real customer project, with a screenshot of the CODESYS Devices
tree beside a screenshot of Versio: *"is not rendering at all in a correct format."* The panel
showed `0 object(s) · 60 changed file(s)`, `coverage unknown`, and sixty rows of raw
`Device/Plc Logic/BuildingApplication/…` paths where a tree belonged.

`versio_export.py` has always written `warnings` as records — `{"code": …, "path": …,
"detail": …}` — and `ProjectManifest.Warnings` was declared `IReadOnlyList<string>`.
`JsonSerializer.Deserialize` threw on the first record, `Parse` caught the `JsonException` and
returned `Empty` exactly as its own documentation promised, and **one field's type disagreeing
discarded all fifty-six objects**. Everything downstream then behaved correctly on an empty
manifest: no object nodes, so every exported file fell through to the unmapped-file list as a
raw path, and `ExportCoverage.Of` reported schema 1 and answered "unknown".

Three things had to line up for this to survive a whole feature:

- **The fixture produces no warnings.** `RefrigeratorControl` has none, so `"warnings": []`
  is what every test used — and an empty array is valid for both shapes. `_manifest.json` is
  written by IronPython inside CODESYS and read by System.Text.Json, and nothing on either
  side of that boundary had ever seen a non-empty one.
- **The property was read by nothing.** `Warnings` was populated, exposed, and consumed
  nowhere in the app, so no test, no view and no log line ever looked at it. It could only
  ever cost something, never earn it.
- **The failure was silent by design.** Returning `Empty` for an unreadable manifest is the
  right instinct — the window must not come down over a convenience file — but `Empty` is
  indistinguishable from a manifest that genuinely describes nothing, so the panel had nothing
  to report and reported nothing.

Fixed in three parts, and the third is the one that matters. `ExportWarning` is now the record
the exporter writes. `Parse` reads **field by field and element by element**, so a field this
build cannot make sense of costs that field and leaves the rest standing — a killed export that
truncates one object record loses one row rather than the tree. And `Parse(json, out problems)`
names what it skipped, which `SyncViewModel` logs at Warning, gated by the tree signature so a
broken export says so once rather than per refresh.

Verified against the reporter's own manifest: `schema=3 objects=56 skipped=4 warnings=2
problems=[]`, coverage `CompleteByDesign` rather than `unknown`. Seven new tests, each checked
against the mutation that should break it — reverting to a whole-array parse fails the
per-element test, removing the per-field `catch` fails the per-field test.

### A function block's methods were drawn as its siblings, and four InitRooms sat in one folder

Same report, one level down. CODESYS draws `clsLight (FB)` with an expander and its methods
inside it; Versio drew `clsLight`, `Light`, `SetLifeTime` and — in the rooms folder — four
separate rows called `InitRoom`, with nothing saying which room each belonged to.

The export writes a member **beside** its owner, as `clsLight.Light.xml` next to `clsLight.st`,
so ownership was expressed in a filename and nowhere else. The manifest recorded where a
method's *file* sat and not what the method *belonged to*, and a tree assembled from paths
therefore could not have got this right.

The fix is manifest **schema 4**: `record()` writes the owner's GUID as `parent`, and the tree
builder nests by it. A GUID rather than the owner's name or its path, for the reason identity is
a GUID everywhere else here — a rename must not re-parent anything, and reading ownership back
out of `clsLight.Light.xml` is a guess that any name containing a dot defeats. It is a chain
rather than one level, because a property owns its accessors.

Three consequences were not obvious and are each pinned by a test:

- **`PruneUnchanged` tested `IsFolder`.** Objects had no children until now, so an unchanged
  function block was treated as a leaf and dropped — taking an edited method with it. It tests
  for children now.
- **The search had to move after the nesting.** Filtering leaves first would match a method and
  drop the block that leads to it, leaving the match unreachable. A node that matches keeps all
  its members; one that does not survives only for the members that do.
- **A cycle in a manifest is possible.** It is a file on disk and can say anything, and an
  object claiming its own descendant as its owner would recurse forever inside a background
  build — which presents as the panel simply never appearing again. The walk carries a visited
  set.

An older manifest carries no parents, and that reads as a flat project rather than as an error:
a mirror starts nesting when its project is next exported. Verified by exporting the reporter's
project with real CODESYS — `schema=4 objects=56 withParent=22`, every parent GUID resolving to
an object in the same manifest, `InitRoom` owned by `clsBetterRoom`, `clsCellarRoom` and
`clsCheapRoom` respectively.

The committed fixture was re-exported rather than hand-edited, and the fresh export was compared
byte for byte against the committed one: **`_manifest.json` alone differs, by the schema line**.
That is also the strongest determinism check the export has had — eleven files, two runs,
months apart, identical.

**The corpus is the finding underneath both of these.** One fixture project, with no members and
no warnings, cannot see either bug. Every field report should become a fixture, and this one is
the first candidate.

### Two of the three panels used half the card they sat in, because Dock defaults to Left

Reported from the app: *"the git still, notice how is not using the full panel."* The git panel's
rows stopped about 310 px into a 557 px card, leaving a column of dead surface down the right of
every row in it.

One default did it. The card held a `DockPanel`; **`DockPanel.Dock` defaults to `Left`**, and only
the last child fills. The three panels are listed git, history, project — so the *project* panel
filled the card and the other two were docked left at whatever width their content happened to
want. The git panel was 310 px wide because that is what a branch card and two button rows add up
to.

Nothing about it looked broken from the inside: nothing was misaligned, nothing overflowed,
nothing threw, and every test passed. It is a panel correctly sized to the wrong thing — and it
had been that way since the panels were split, hidden while the git panel still carried the stash
controls and enough width to look deliberate.

The card is a two-row `Grid` now: the title, then one filling row that all three panels share,
with exactly one visible at a time. The test arranges each panel *through its rail button* and
asserts the width the panel renders at equals the card's, which is the assertion that would have
caught this at any point in its life. Checked by pinning the git panel back to its content width,
where it fails.

### Two things overlapping, and a test suite that could not see either

Both reported from the running app, and neither was reachable by anything in the suite: it
constructed the window and measured it once, at a comfortable width. **An overlap is not a
failure in WPF.** Two controls sitting on top of each other measure and arrange perfectly well —
the layout did exactly what it was told.

**The log console ran underneath the rail.** The rail lived inside the body *row*, so it stopped
at the bottom of the panels, while the log console — a sibling row — spanned the full width of
the window beneath it. Two surfaces of the same `Surface.Base` meeting at a T-junction under the
rail icons, which is precisely what "the logs bar overlapping the left side bar" describes. The
rail is app-level navigation, so it now lives in the outer grid and spans the body and the log;
the log starts where the content starts. The status bar still spans the full width, which is
where a status bar belongs.

**A section label and its buttons shared one Grid cell.** `NOT STAGED` with `Discard` and `Stage
all`, `STAGED` with `Unstage all`, the panel title with its count badge, `COMMIT MESSAGE` with
its length counter, and `DIFF` with a checkbox and a 420 px path. A right-aligned child in a
shared cell grows *leftwards across* the label rather than pushing it, and neither knows the
other is there — so in a 380 px panel the label and the first button touched. Every one of those
headers is two columns now: the label takes what is left and trims, the buttons keep their width.

The tests assert **rendered geometry** — bounds transformed into a common ancestor, at a 1000 px
window — because that is the only form in which "these two things are on top of each other" can
be written down. They also select the git panel through the rail first: a collapsed panel
arranges to nothing, which would have made every assertion vacuously true. All three fixes were
checked by putting the old layout back, where each fails.

Two smaller things went with it. The branch card's *Fetch / Pull / Push* are compact: at the full
32 px they set the height of the whole card and pushed the file lists down for nothing. And
`Size.Rail.Width` is finally what sets the rail's width, instead of a hardcoded 52 sitting beside
an unused token.

### An editor that corrupted every em-dash in a file it was only reformatting

Not a design finding, and worth recording anyway because the damage was invisible in the diff
that mattered and glaring on screen: the commit-box placeholder rendered as
`Summary of the changeâ€¦`.

`MainWindow.xaml` was edited in a few places with PowerShell's `Get-Content -Raw` /
`Set-Content`, which reads with the system's ANSI codepage and writes UTF-8. Every one of the
file's 18 em-dashes and ellipses — all of them in tooltips and comments — round-tripped into
mojibake, and a BOM was added to a file that never had one. **Nothing caught it.** The build was
clean, all 116 tests passed, the binding tracer was empty, and XAML is perfectly happy to render
a corrupted string. It took a screenshot of the app.

Repaired by re-decoding: the file's only non-ASCII characters were the mojibake sequences
themselves, so reading the UTF-8 bytes back through CP1252 is exact rather than a guess. Verified
by counting `…` and `—` against the previous commit — 18 in both — and by checking the first three
bytes are `3C 57 69` rather than a BOM.

The lesson is narrow and worth keeping: **edit source files with an editor that preserves
encoding, not with a shell round-trip.** If a shell must do it, `[IO.File]::ReadAllText` /
`WriteAllText` with an explicit `UTF8Encoding($false)` is the pair that does not corrupt anything.

### The stylesheet's own argument, not applied to four controls

`Controls.xaml` opens by naming the four things that made the first pass look like a Win32 app:
visible borders on every control, the system highlight brush for selection, hard dividers, and
colour emoji. It then fixes all four — for the controls it retemplates. Four it never touched
kept every one of the problems the header describes.

**Checkboxes and radio buttons were WPF's own.** The settings popup's three theme radios, *Show
full file*, *+ untracked*, and every row of the category dropdown were drawing Aero's shaded
square and shaded circle — sized for a 2009 desktop, lit as though from above — on a dark
surface. Sixty lines each: a 16 px box with `Radius.Control` and the icon font's check glyph, a
16 px ring with an accent dot. The label is inside the hit area now too, so a click no longer
has to find a 13 px square.

**Both TreeViews painted selection in Windows' highlight blue.** `ListBoxItem` was retemplated
in the first pass *because* the system highlight is the loudest tell — and the project tree, the
surface somebody looks at all day, was not a ListBox. It was a TreeView, so it kept the stock
template: highlight-blue selection and the Aero expander triangle. When the history panel was
added it inherited exactly the same thing. `TreeRowItem` is what `RowItem` already is for the
lists — a tinted fill plus an accent bar — with a chevron rotated by a trigger rather than two
glyph keys that can drift apart.

Two details in it are deliberate and worth keeping:

- **`IsSelected`, not `IsSelectionActive`.** The stock template greys the selection out when
  focus leaves the tree, which happens the moment somebody clicks a file to read its diff — so
  the file whose diff is on screen looked unselected. The row stays lit here.
- **`IsExpanded` is not set by the style.** Both trees bind it two-way per row, and that binding
  is what drives the project tree's lazy children and the history's demand-driven file lists. The
  retemplate puts a `ToggleButton` on the same property, so the test that matters asserts the
  round trip in both directions: expanding a node in the UI must still reach the row, or the
  panels quietly stop loading anything and nothing else looks wrong.

**And a style can be right and unused.** The tree tests build their own `TreeViewItem`, which
proves the template and nothing about the app. A second test asserts both TreeViews actually
name it — because the project tree needs a repository open before it has a single row, so a
missing `BasedOn` would have shipped with every test still green. Checked by removing it from
one of the two, where it fails. Same for the implicit styles: they are keyed by type, nothing in
any view refers to them, and deleting one changes dozens of controls without a single lookup
failing.

### A top bar crowded into its left third, with two control heights in one row

Reported from use: *"the repo and branch bars should cover more real estate horizontally so it
looks more balanced, currently very heavy to the left."* Three separate causes, all visible at
once and none of them nameable without opening the file.

**One flexible column, in the wrong place.** Six columns, five of them `Auto`, and the single
`*` sat on the *branch buttons* — so on a 2560 px window the slack piled up in the middle while
both dropdowns stayed pinned at their hardcoded 230 px and 200 px. Two `*` columns now carry
the dropdowns, bounded with `MaxWidth` so a wide window widens them without handing one of them
half the screen, and a third `*` before Settings is what holds the right-hand cluster right.

**Two heights in one row.** `Size.Control.Height` was 28 and `IconButton` hardcoded 32, so every
dropdown in the bar sat four pixels shorter than the buttons beside it. That is the ragged
baseline, and it is the reason the bar read as unfinished even where the spacing was right. Both
read the token now, at 32, with `Size.Control.Compact` (24) named once for controls inside a
strip that is itself dense — the log header, the section-label rows — rather than a third value
picked per view.

The test measures both controls after `Arrange` instead of reading the styles, because two
controls can name the same token and still render differently if one of them also sets a padding
or a border. Checked by putting the old values back: it fails.

**The mark existed and was nowhere.** `assets/versio.svg` — three plates and a status badge — was
drawn for the `.ico` and never appeared in the app, which showed the word "Versio" in accent
blue instead. The geometry is now also `Theme/controls/Brand.xaml` at 20 px, and the SVG's header
names all three places that carry it. A `ControlTemplate` rather than a `DrawingImage`, and not
as a matter of taste: a `Freezable` in a resource dictionary resolves its `DynamicResource` once
and is not re-resolved when the palette underneath is replaced — the trap `ThemeManager` already
documents — so a `DrawingImage` mark would have kept the colours of whichever theme started
first.

**And accent was doing six jobs.** Brand, commit hashes, ahead/behind, watch state, the busy
caption and every category in a scrolling log. A colour that marks six things marks none of
them, so it is now spent on selection, the primary action, the count badge and the progress bar,
and the rest moved to `Text.Secondary` or `Text.Disabled`. The one that gained meaning rather
than losing colour is divergence: `BranchEntry.BrushKey` says `Status.Modified` when a branch is
*behind*, because that is the state a pull answers, and secondary when it is merely ahead —
which is normal and should not be painted like a problem.

### The app themed everything except the strip Windows draws, and every tooltip in it

Reported from use, and it is the first thing anybody sees: *"the top windows bar should follow
color theme, so if its dark theme then it goes dark as well."* A dark shell under a white
caption, with the app's own top bar — `Surface.Base`, an exact colour — immediately below it.

Two surfaces were unreachable from the stylesheet, for two different reasons:

**The caption belongs to the window manager.** No style, template or brush reaches the
non-client area; the only way in is `DwmSetWindowAttribute`. So the colours are handed over
directly — `CAPTION_COLOR`, `TEXT_COLOR` and `BORDER_COLOR`, with `USE_IMMERSIVE_DARK_MODE`
behind them for Windows 10, where the three colour attributes fail and dark mode is the whole
effect. `Surface.Base` is passed rather than settling for immersive dark mode alone, because
that gives a generic dark grey that is *close* to the top bar and not equal to it, and a seam
one shade off looks worse than an honest contrast.

**Tooltips are popups, and there are dozens of them.** Every one is a plain string on the
element it explains, and not one named a style — so every one was the stock WPF tooltip: a
near-white slab with a hard border, opening over a dark application. An implicit style dresses
all of them at once, including the ones nobody has written yet. That is also why the style is
pinned by a test: nothing in any view refers to it, so deleting it changes the look of dozens
of controls and no lookup anywhere fails.

**Two halves, and the second is the one that gets forgotten.** Painting on load is correct
until the user switches theme with windows open, at which point every caption keeps the colour
it was born with until the app restarts — the same shape of half-working theme switch already
recorded below, one strip further out. So `ThemeManager.Apply` repaints every open window as
part of the swap, and a test asserts it: two windows built, theme switched, both repainted.

**The reach is what is tested, not the paint.** The DWM call needs a shown window with an HWND,
and this suite deliberately shows nothing. What the tests assert instead is that every window
which loads gets to the painter — a plain `Window` and a real dialog, separately, because "it
works on the main window" is exactly how this fails — and they do it by raising `Loaded` by
hand, since a class handler fires on `RaiseEvent` whether or not anything is on screen. The
byte order gets its own test: COLORREF is `0x00BBGGRR`, and reversed, the caption comes out a
warm brown that reads as somebody's decision rather than a bug. The interop itself was
confirmed separately against the running app: all four attributes return `S_OK` on 10.0.26200.

**A class handler rather than four constructors.** `EventManager.RegisterClassHandler` on
`Window.Loaded`, registered once at startup, covers the shell, all three dialogs, the spike
window, and the fifth window that would otherwise have been the one somebody forgot. Two
surfaces remain honestly out of reach and are listed rather than hidden: `MessageBox` is a
Win32 dialog, and `OpenFolderDialog` is the shell's own.

### Twenty seconds of work with nothing on screen, and four separate reasons for it

Reported from use, and precisely: *"I save, the PLC tree immediately shows the changed file,
and after a few seconds the changes get highlighted. I'm assuming the export is running, but
there is no visual indication of this."*

The reading was exactly right. The `.project` appears the instant git notices it — about 300 ms
— and the diff people are actually waiting for appears when CODESYS finishes, 17–31 seconds
later. In between, the app looked idle while doing the most expensive thing it ever does.

What made it worth a full investigation is that **every mechanism for showing it already
existed, and all four were disconnected.** None of them was missing; each was wired to
something that could not deliver it.

| Mechanism | Why nothing appeared |
|---|---|
| The app-wide progress bar | `SyncViewModel` takes a `BusyTracker` and **defaults to constructing its own**. The bar binds to the shell's. So the bar never moved for *any* sync work — including the catch-up and commit-gate exports, which both carefully open a scope for it |
| `ProjectRow.Busy` and the `"exporting…"` state | Documented in the class — *"it says exporting… for 17–31 seconds"* — and **assigned by nothing**. `RefreshStalenessAsync` even guards with `!p.Busy`, a condition that could never be true |
| The heartbeat | `HeartbeatService.Track` was called from exactly one place, the commit-gate path. The automatic save path started no tracker, so `Report` found nothing to attach to and returned |
| The progress detail itself | `PlcJob.OperationId` defaulted to a **fresh random value**. The runner tags its phases and counts with the job's id; the heartbeat is keyed by the operation's. Two unrelated strings, so every phase the toolchain reported was dropped — on *every* path, including the one that did start a heartbeat, which could therefore only ever show elapsed seconds |

The last one is the most instructive: an end-to-end pipeline, complete at both ends, discarding
its payload at the final hop because two ids that had to be equal were each independently
correct.

**The announcement is a notification, not a wrapper.** It would be simpler for the caller to
put a bar around its call to `HandleChangeAsync`, and it would be wrong: the overwhelming
majority of calls are refused by a gate in milliseconds — a save that changed nothing, a
coalesced burst, a throttled repeat — and a bar flashing on every settle is worse than no bar
at all. `RunStarted` fires after the last gate, `RunFinished` from a `finally` so a failure or
a throw cannot leave the bar up. A bar that never goes away is indistinguishable from a hang,
which is the very confusion being fixed.

**The row is shown and marked, not hidden.** Hiding it until the export finished was the other
option and it is the wrong one: the `.project` really is modified in git the moment you save,
and a panel that disagrees with `git status` is how somebody concludes Versio lost their work.
One further defect fell out of choosing that: re-exporting a project whose file matches HEAD
made its row **vanish for the whole run**, because the "show what has something to review" rule
had nothing to show. The tree said so in its own log — `1 project(s) hidden as in sync` — while
CODESYS was running against it. A run in flight now counts as a reason to be visible.

### Two loop defences that had never actually run

Found while tracing the above. Both are in the coalescer, both were written correctly, and
neither had anything driving it.

**A queued follow-up was dropped, and the coalescer was left believing a job was running.**
`OnJobCompleted()` returns `true` to mean *a change arrived while that run was going and must
be handled now*, and sets the slot as running again on the strength of the caller obeying it.
The caller discarded the result. So the follow-up never ran — and worse, from that moment every
later change on that binding answered `Queue`, then `Coalesced`, for the rest of the session.

It stayed invisible because **the commit gate is synchronous and skips the coalescer entirely**,
so exporting still worked at the one moment anybody checked. The plan's worked example — four
saves during one export producing two exports — produced one.

**A throttled change waited for something that did not exist.** `TryStartThrottled()` is
documented as *"used by the idle tick"*; there was no idle tick and no caller anywhere. The
pending flag that "guarantees a final export once things go quiet" guaranteed nothing precisely
when things went quiet: the change waited for the next save, or for somebody to commit. There
is a five-second tick now, and the deferred job re-enters the whole pipeline rather than being
replayed, so the hash gate can say the reason to run has gone.

### "Cancel and restart" cancelled nothing, and ran two CODESYS instances instead

The most consequential of the three, and it was found by watching, not by reading.

A save arriving in the first five seconds of a run returned `CancelAndRestart` — *"cheap to
restart this early, and it avoids 40 s of work we know is stale"*. Sound reasoning, and the
mechanism was never built: **no cancellation is plumbed through to a running toolchain job**.
The orchestrator's only way to act on the instruction was to start a second export beside the
first.

Observed doing exactly that. One `git checkout` of the project file produced two settled
changes 1.8 s apart, both inside the window, and the log shows two exports running
concurrently against one project:

```
23:11:23.548  {4da031c5} Project change (automatic, Project)
23:11:25.351  {81e9666a} Project change (automatic, Project)
…
23:12:09.269  {4da031c5} exported 12 object(s) in 45.6s
23:12:32.748  {81e9666a} ExportSucceeded accepted
```

**45.6 s for a job that takes 17–31 s alone** — the two instances contending for the profile
and the shared temp state, which is the same contention already documented for the integration
suite.

The branch is gone. A change arriving during a run is always queued, and the `cancelWindow`
option went with it rather than being left as a knob that does nothing. Queueing costs almost
nothing in return: an export reads the file as it opens it, so a save landing mid-run is
usually captured by that very run, and the follow-up then finds nothing at the hash gate and
costs a file read instead of a toolchain launch. **Restarting is still worth having** — it just
has to cancel the process first, and killing CODESYS mid-export leaves a staging folder the
exporter already warns about. That is a separate piece of work, not a comment.

**The unit test passed throughout.** It asserted that the coalescer *returns* `CancelAndRestart`
in the window, which it faithfully did. A component tested alone cannot see what its caller does
with the answer, and "the caller cannot honour this verdict" is not a property any test of that
component can express. The test now asserts the property that matters and could have caught it:
**a second job is never started while one is in flight.**

### The history was re-read on every refresh, into a panel four rows tall

The commit list lived at the bottom of the git panel, under the stash, with `MaxHeight="150"`
in a 380 px column that also carried the branch card, the commit box, two file lists and six
stash controls. Four rows, and every one of them a single line holding a hash, a subject and a
relative age — the date and time the commit was actually made were not on screen anywhere.

What it cost is the part worth writing down. This ran on every manual refresh, every refresh
triggered by git's own metadata moving, and every branch change:

```csharp
await LoadBranchesAsync();
await LoadCommitsAsync();     // git log
await LoadStashesAsync();     // git stash list
```

**No part of that asked whether anybody was looking.** The rail could be on the project panel —
it is the default, and it is where the work happens — and a commit, a checkout or a fetch still
spawned two git processes to refill a list that was not on screen. The one interaction the list
did offer, clicking a commit, fetched the commit's *entire* patch, because whole-commit was the
only granularity there was. On a commit touching an exported CODESYS tree that is hundreds of
files in one string, to read one of them.

So history moved out to its own rail button, and everything about it is now demand-driven at
four separate levels: the log is not walked until the panel is first shown, and then one page
of fifty at a time; a commit's files are read when it is expanded; a patch is read when a file
is clicked. A refresh behind a hidden panel *marks* it and reads nothing — deferring the cost
without deferring the correctness, which is the only version of lazy that is safe here. A
commit that did not appear the moment it was made would look like a commit that had failed.

The file lists are cached and the patches deliberately are not. **A commit's hash is its
content**, so its file list cannot go stale and may be held for the session; its patches are
unbounded in size and are held only by the diff pane, one at a time. The same caching would be
wrong one panel to the left, where the working tree changes under you.

Three things surfaced while building it:

| Found | Why it mattered |
|---|---|
| `--name-status` quotes paths | Without `-z`, git renders `Kühlschrank Regelung.st` as `"K\303\274hlschrank Regelung.st"` — quotes and octal escapes included. This repository's own fixture corpus has both spaces and umlauts in its object names, so the panel would have asked git for the diff of a path that does not exist. The same class of bug as the mangled subprocess output already recorded here: an encoding assumption at a process boundary |
| *Show full file* would have swapped what the pane was answering | Its re-read keys off the shown path, which for a historical diff is a caption — `PLC_PRG.st @ 1a2b3c4` — not a path. Left alone it would have quietly re-read the *working tree* version of that file and rendered it under a header naming a commit. Caught before it shipped; the pane now records which source it is showing, and a refresh leaves a historical diff alone entirely |
| A collapsed panel passes the window test | `MainWindowTests` constructs and measures the whole window, and would still have passed with a broken `DataTemplate` in it: a `StaticResource` inside a template is resolved when a row is *realised*, not when the XAML is parsed. The new test switches to the panel through the rail button, fills it, expands a commit and asserts a real `TreeViewItem` came out with a height. Written the obvious way it would have been one more vacuous pass on the pile below |

The stash controls came out at the same time and are not replaced: six controls for something
reached for a few times a month, in the column that most needed the room. The client-side
operations — push, apply, pop, drop, and the list parser — are kept and still covered, because
what was wrong with the stash was the shelf it sat on, not the operations.

### A window that never opened, and three instruments that called it clean

The log panel gained a category dropdown - a `ToggleButton`, because it has to stay pressed
while its popup is open - wearing `GhostButton`, whose `TargetType` was `Button`. WPF refuses
that at the moment the style is set:

```
'Button' TargetType does not match type of element 'ToggleButton'
MainWindow.xaml line 1005
```

So `MainWindow`'s constructor threw, and **the application started, opened its log file, probed
the toolchain and never showed a window**. The build was clean and 883 tests were green while it
did that.

What makes it worth writing down is not the mistake, which is ordinary, but that **every
instrument in place reported success**, each for a different reason:

| Instrument | Why it said nothing |
|---|---|
| The compiler | XAML becomes BAML without resolving a single `StaticResource`. A style reference is a key until something asks for it |
| `Versio.App.Tests` | Covered the two prompt dialogs and the stylesheet. `MainWindow` was the one window nothing constructed - on the reasoning that it opens on every launch and could not go unnoticed |
| The resource-key tests | Assert `GhostButton` **exists**, which it did. The defect was a key that does not *fit* the element asking for it, which no lookup can see |
| `--trace-bindings` | Reported nothing, twice, and it was believed. **A window that never opens evaluates no bindings**, so the trace is empty for the same reason it is empty when everything is correct |

That last row is the thirteenth vacuous pass in this document, and the most embarrassing one,
because it was read as evidence *while the app was failing to start*. The diagnosis offered
first was also wrong: the empty trace was blamed on the MSIX filesystem-virtualization finding
already recorded here, on the strength of it being a documented gotcha in the same area. It was
not that - the app's own log was landing at the real path the whole time. **A documented
explanation that fits is still a guess until it is checked**, and checking cost one command:

```
MainWindowTitle  :
MainWindowHandle : 0
```

The fix is one word - `GhostButton` targets `ButtonBase`, so it dresses a `ToggleButton` and a
`Button` alike - and everything derived from it still targets `Button`, which stays legal.

The real fix is `MainWindowTests`: one construction of the real window, measured so templates
are applied, verified to fail on the reverted bug. It is the cheapest possible regression test
for the whole file - every `StaticResource` in `MainWindow.xaml` resolved on every run, in
200 ms - and it should have existed from the day the two dialogs got theirs.

### The same blind spot again, in the dialog added after it was written up

Found only because the crash above forced a real launch. `SystemInfoWindow` uses
`{StaticResource BoolToVisibility}` inside its row `DataTemplate`, and the converter was
declared in **`MainWindow.Resources`** - a different window, which cannot see it. Opening
Settings > Help > System information threw:

```
Cannot find resource named 'BoolToVisibility'. Resource names are case sensitive.
  at FrameworkTemplate.LoadTemplateXaml ... at FrameworkElement.MeasureCore
```

**This one was not introduced by the change that found it.** It shipped with the dialog, and
`Versio.App.Tests` covered the two prompt dialogs and not this one - so the lesson "a modal
nobody has clicked has never been parsed" was recorded, and then the next modal was added
without applying it. A blind spot named in a document is still a blind spot.

Two things it teaches beyond the fix:

- **A resource referenced from inside a template is not resolved until the template is
  applied**, which is Measure, not construction. A test that only constructed the window would
  have passed with the bug in place, so the tests measure and arrange.
- The converter now lives in the stylesheet, declared once, and `MainWindow`'s private copy is
  gone. A shared resource kept in one window's `Resources` is the same shape of bug as the
  private `Theme.xaml` merge that broke the theme switch - resolution finds it from one place
  and nowhere else, and the failure is invisible from the file that looks correct.

### An idle log that was mostly its own noise

Reported from use: the panel was drowning in `git status`. Measured from the log rather than
argued about - the status backstop runs every 20 s forever, and each run wrote **three** Debug
lines: the command, its duration, and a verdict of `status unchanged`.

```
17:29:29.287 DBG [Git] git status --porcelain=v2 --branch --untracked-files=all -z
17:29:29.333 DBG [Git] git status --porcelain=v2 --branch --untracked-files=all -z ok in 0.0s
17:29:29.335 DBG [Git] status unchanged (Backstop)
```

That is **about 540 lines an hour from an app with nothing to do**, and the entries that matter
scroll out behind them. The panel keeps 5,000 rows, so roughly nine hours of idling evicts
everything real.

Both halves were already trying to be quiet and both had picked the wrong lever. The verdict
line had been *demoted to Debug* - which is not the same as not writing it - and the command
pair had never been questioned at all.

- **A completed command needs one line, not two.** The pre-command line only ever earned its
  place for a command that hangs, so that is now the only case that gets it: after one second,
  `git fetch ... - still running`. Every routine call in this app finishes in tens of
  milliseconds, so in normal use it writes nothing.
- **A run of nothing is one fact, not N.** `QuietRunTracker` counts the quiet polls and says so
  every five minutes: `no change in 15 checks over 5m (12 Backstop, 3 WorkingTree)`. An idle
  hour goes from ~540 lines to 11.

**The per-trigger counts are the part worth keeping.** A *Backstop* poll finding nothing is the
definition of routine; a *WorkingTree* poll finding nothing means `RepositoryChangeFilter`
passed an event git does not care about, which is the only signal for tuning it - and it is
visible only in aggregate. A generic "collapse repeated lines" rule in the sink would have cut
the same volume and thrown that away.

Fourteen tests, and deleting the interval gate fails four of them, including both that assert
silence - the property most easily made vacuous, since a tracker returning `null` unconditionally
passes any test that only counts what it suppressed.

### The CODESYS lock marker was committed, and the ignore rule had guessed its name

Reported from use as "why is `control ~u` in my changes?". Three separate things, and only the
first is cosmetic.

**The rule matched nothing.** This repository's hand-written `.gitignore` carried
`*.project.~*`, which is the plausible reading. CODESYS **replaces** the extension:
`RefrigeratorControl.project` is held by `RefrigeratorControl.~u`. The product code has always
had this right - `CodesysProjectFormat.OccupancyMarkerFor` uses `ChangeExtension`, with the
measurement in its remarks, and adoption writes the scoped form for repositories it sets up.
Only this repository was wrong, because its file was typed by hand before that code existed.
Exactly the shape already recorded for `.versio/local/`: **a rule that is true only where
somebody arranged it separately.**

**The file was tracked, so the rule was irrelevant anyway.** `git check-ignore` reported no
match even after the pattern was fixed - ignore rules do not apply to tracked files, and
`git rm --cached` is the other half of the fix. Worth remembering: a corrected `.gitignore` that
appears to do nothing is usually this.

**It had been committed and pushed.** The marker's contents are a username, a machine name and a
pid, and it went to `origin/main` in a commit made while testing. That is the same harm already
documented for the `.opt` files, arriving by a route nobody had guarded. Removing it from
history means rewriting a pushed commit, so it is left as the owner's call - but the guard is
now in place for the next one.

### A panel about changed files, opening with a version number and two dead buttons

Reported from use, and correct on every count. The card at the top of the project panel was
four unrelated things wearing one border:

| What it showed | What it actually was | Where it went |
|---|---|---|
| Toolchain name, version, profile, status dot | Environment — identical for every repository, changing only when somebody installs something | Settings → Help → System information |
| "1 project, tracked." | A fact about the repository | The panel's own summary |
| "The project file is authoritative…" | State of **one** project | The project's row |
| *Export now*, *Acknowledge failure* | Two real actions, disabled almost always | Gone; see below |

**A permanently disabled button teaches people to stop looking at that corner of the screen** —
the same failure mode as a coverage line that congratulates itself on every refresh, which this
codebase had already argued about once.

*Export now* is gone because **nothing about a stale mirror needs deciding.** A project edited
while Versio was closed was never seen by the watcher, and the answer is always "export it", so
that now happens when the repository opens. The ordering is the guard, and it is the whole
feature: toolchain probed → orchestrator rebuilt and its **locks restored from the previous
session** → projects scanned → watcher started → staleness evaluated → *then* export. Exporting
a step earlier would run against a direction lock that had not been read yet. It goes through
the same `SatisfyGateAsync` the commit gate uses, so the lock, the hash gate, the breaker and
the open-project check all apply unchanged. Verified from the log of a real launch:

```
watching 1 tracked project(s) for changes          ← attach finished
commit gate: ExportRequired …                      ← staleness evaluated
catch-up: 1 project(s) changed while Versio was not running; exporting
…19.6 s… exported 12 object(s) → every tracked project is exported and up to date
```

*Acknowledge failure* is gone as a **panel-wide** control, not as a concept. The gate behind it
is deliberate and unchanged — a failed binding syncs no further until a person has seen the
failure — but a repository holds several projects and the panel could not say which one had
failed. The failure is now reported on its own row, and selecting it shows the toolchain's own
words with one button that clears the failure and retries. Acknowledging and retrying are one
button because they are one intention: two would mean clearing a failure and then wondering why
nothing happened.

One wording fix fell out of it. `SatisfyGateAsync` logged "exporting stale bindings before the
commit", which was true when only the commit gate called it and false for the two callers added
since — in a log read precisely when somebody is working out what the app did on its own.

### Recovery forgot what had been exported, and it cost a toolchain run every launch

Reported from use: the project sat in the panel reading "project ahead" with nothing changed
under it, launch after launch. The panel was telling the truth; the state behind it was wrong.

Recovery decides what to do with a lock left by the previous session. When it does **not**
re-adopt — the ordinary case, because the previous session ended cleanly — it replaced the lock
with a blank one. That blank threw away `LastSynced` along with the direction, and the commit
gate compares the project's hash against `LastSynced.ProjectHash`. An empty one reads as *"the
project has changed since it was last exported"* for a project nobody had touched.

The sequence, from one launch's log:

```
NothingToRecover — binding was clean; now Clean        ← LastSynced wiped here
tree: … 1 project(s) hidden as in sync                 ← still correct at this point
ExportRequired — the project has changed since it was last exported
catch-up: 1 project(s) changed while Versio was not running; exporting
```

So every open ran CODESYS for 17–31 s to rebuild a mirror that was already byte-correct, and
left the binding holding a fresh `ProjectAuthoritative` lock — which is why the row then read
"project ahead" and stayed until somebody pushed.

**Releasing the direction and forgetting what was exported are different facts**, and only the
first is recovery's business. The release now carries `LastSynced` and `Baseline` forward.

**It was latent for as long as exporting was a button.** Nobody pressed *Export now* on a
project they had not changed, so nobody saw the gate's answer. Making the export automatic on
open turned a bookkeeping error into twenty seconds of CODESYS on every launch — which is how
it got found, and an argument for automation surfacing bugs rather than creating them.

Verified across two consecutive launches after the fix: `catch-up: every mirror is up to date`,
no toolchain run, and `1 project(s) hidden as in sync`.

### The log said one time in the panel and another in the file

Two renderings of one instant. `LogViewModel.Time` called `ToLocalTime()`; `LogEvent.Format()`,
which is what the file sink writes, formatted the raw UTC stamp. So an entry read as 17:14 in
the app and 00:14 in the file — and the file is the copy somebody opens hours later, while
correlating against the CODESYS log and their own memory of when it happened. The file names
were UTC-dated too, so an afternoon's work in Pacific time lived in a file named for tomorrow.

Both now render local, and the file rolls on the **local** day so its name matches the stamps
inside it. `LogEvent.Timestamp` stays UTC: ordering, rolling and the tests all want one
unambiguous instant, and only the rendering was ever the problem.

**The first test written for it was vacuous, and only breaking the code showed it.** It built
the timestamp with a *local* offset — where `ToLocalTime()` is the identity and both renderings
are identical by construction. It passed against the bug it was written to catch. Rebuilt with
a UTC-offset stamp, as the real logger produces, it fails properly. That is the thirteenth
vacuous pass caught this way, and the second in two days from the same root cause: a test whose
input cannot distinguish the two behaviours it is comparing.

### The theme switch changed a colour nothing was looking at

Reported from use: the Light/Dark buttons in Settings did nothing. Two independent causes, and
the second one is the more interesting.

**One: the main window carried its own copy of the theme.** `MainWindow.xaml` merged
`Theme.xaml` into `Window.Resources`, and `App.xaml` merges it into `Application.Resources`.
WPF resolves upwards and stops at the first hit, so the window found its private palette and
never reached the application's — the one `ThemeManager` swaps. Every dialog, having no private
copy, followed the switch correctly, which is why the branch dialog opened dark over a light
window. The merge is gone: one theme, one place.

**Two: replacing the palette repainted nothing.** Measured with a probe rather than reasoned
about, and the numbers are the finding:

```
after Apply(Dark)   Color.Surface.Base = #FF1D2026      the palette did change
                    Brush.Surface.Base = #FFFFFFFF      the brush did not
                    window background  = #FFFFFFFF      nor did anything painted with it
```

`Semantic.xaml` defines its brushes as `SolidColorBrush` with `Color="{DynamicResource
Color.Surface.Base}"`, and the comment above `ThemeManager` said that is what makes the whole
window re-theme without a restart. It is not: a `Freezable` living in a resource dictionary
resolves its `DynamicResource` once, and replacing a *sibling* dictionary underneath it does
not re-resolve it. So the swap updated a colour that no live brush was reading any more.

**It had never worked, and it looked like it did** — because `Apply(System)` runs at startup
*before* any window exists, so the first resolution of every brush already sees the right
palette. Only a switch made while the app is running has anything to re-resolve, and that is
the one thing nobody had automated.

The fix rebuilds the whole theme dictionary and replaces it in `Application.Resources`, so
every brush is a fresh instance and every `DynamicResource` consumer in an open window is
invalidated. Four tests now cover it, and reverting to the palette-only swap fails three of
them.

### A diagnostic that verified an empty window

`Versio.exe --trace-bindings <file> C:
epo` never opened the repository. The startup code
took "the first argument that does not start with `--`" as the repository path, which is the
trace file.

The cost was not the missing repository — it is logged — but what it did to the instrument.
That flag exists to verify the UI, and every clean binding trace taken with it was a clean
trace over an **empty** window: no repository, no project rows, no git panel contents, so most
bindings had never been evaluated at all. A verification that quietly checks nothing is worse
than no verification, because it gets reported as a pass. Both of this session's earlier
"clean trace" runs were exactly that, and were re-run properly afterwards.

Flags that consume a value are listed rather than inferred: "skip whatever follows a flag"
breaks on `--spike C:
epo`, which is how the spike is actually run.

### A project with nothing to review stayed on screen

Also reported from use: a project sitting in the panel with no changes under it and a badge
reading 0.

It was doing what it was told. A project stayed visible when its sync state was anything other
than `Clean`, and the commonest state in the tool's whole life is `ProjectAuthoritative` — the
direction lock taken by a successful export and held until the work is **pushed**. So a project
that had been exported, committed and left alone sat permanently in a panel whose stated
premise is "everything with something to review", with nothing to review.

The rule now asks whether there is something to *do*: never exported, failed, conflicted, text
ahead (an import is outstanding), or stale. A held lock with everything up to date is a fact
about state, not a task, and it is already reported in the panel header in its own sentence.

### The UI had no automated coverage, and the first test written found a bug

`--trace-bindings` is a good instrument and it has one blind spot that matters: **it can only
report on windows that were opened.** A modal nobody has clicked has never been parsed, so its
XAML has never been checked against the stylesheet at all — a renamed style is not a compile
error, not a binding warning, and not anything until a user clicks the button that opens it.

Adding two dialogs made that gap load-bearing, so `Versio.App.Tests` exists now: a WPF test
project that constructs the real windows against the real `Theme.xaml`, loaded by pack URI from
the app assembly so it cannot drift from what ships. Deliberately breaking a style reference
fails **all ten** of the branch dialog's tests, which is the class of failure that would
otherwise have surfaced weeks later in front of somebody.

**The first run found a real defect.** `BranchPromptWindow` validated on `TextChanged`, which
never fires for a box's initial value, so the dialog opened with a **blank message area and a
dead Create button** — no explanation of what it wanted. Invisible by inspection, because a
blank label looks exactly like an empty one, and invisible to the binding tracer, because
nothing was bound. One call to `Revalidate()` in the constructor.

Three things had to be got right before any of it ran, and each fails in a way that names
something other than the cause:

- **an STA thread** — xunit runs tests on MTA threads, where a `Window` constructor throws;
- **an `Application` carrying the theme** — every `StaticResource` resolves by walking up to
  `Application.Resources`, and without one the XAML parse dies on the first style;
- **the *same* STA thread every time** — the theme's brushes are `Freezable`s owned by the
  thread that made them, so a fresh thread per test dies inside the XAML parser with
  `Dispatcher.VerifyAccess` in the stack and nothing pointing at the real problem. That was the
  first version of the harness.

### `async void` handlers and a test host that aborted

Driving the clone dialog by raising `Click` passed eight tests and then **aborted the test
host** — not failed, aborted, with no test named.

The handlers are `async void`, which is what WPF requires of an event handler. On the UI thread
an `await` posts its continuation back to the dispatcher, so raising a click only *starts* the
work: the rest runs whenever the dispatcher next pumps, which is during some later test, against
a window whose test has already finished.

Two changes, and both are improvements to the product rather than concessions to the test:

- the work moved into `VerifyAsync()` and `CloneAsync()`, leaving the `async void` handlers as
  one-line wrappers that start them. An `async void` that anything else calls is un-awaitable
  by everything, including the next feature that wants to reuse it;
- `DialogResult` is set through a guard, because it exists only on a window shown with
  `ShowDialog` and throws on any other. `ClonedPath` is set first regardless — that is the
  result; closing is how it gets delivered.

The harness gained a `Run(Func<Task>)` that waits **from the calling thread**, leaving the
dispatcher free to pump its own continuations. Waiting on it from inside itself deadlocks
instead.

### Validating a branch name is about the message, not the verdict

Git refuses a bad branch name perfectly well. It does it *after* a process spawn, in a sentence
written for a terminal — `fatal: 'feature branch' is not a valid branch name` — which arrives in
a status bar after the dialog has closed, naming neither the offending character nor what to
type instead.

`BranchName.Validate` is therefore in Core, with the rules transcribed from
`git check-ref-format` and a **reason for every refusal**: which character, or which rule. A
check returning a bare `false` would grey out a button and leave the user with nothing to
change — worse than no validation at all, because git would at least have said something.

It does **not** replace git's check, and the tests say so: a name that passes here can still be
refused for existing already, or for colliding with a ref path — `feature` and `feature/x`
cannot both be branches. The failed create is reported rather than assumed away.

The per-segment rules are the ones worth having a test for. `feature/.hidden` and
`feature/x.lock` are both refused by git, and neither is caught by looking at the name as a
whole: refs are paths, and git applies those rules to each component. Deleting the loop that
walks the segments fails five tests.

### The library reference id changes on every open, and the API is not the one it looks like

Two measurements, both cheap, both of which would have been expensive to discover later.

**The obvious API is the wrong one.** `libman.get_libraries()` exists and returns 21 items for
the fixture's application manager, which looks exactly like success. They are **strings** —
`"#Standard"`, `"#Util"` — with no version, no company, no flags and no parameters. The one
that carries the reference is `libman.references`, and it must be **iterated**, not indexed:
`references[i]` looks up by *id*, so `references[0]` raises on every project whose first
reference does not happen to have id 0. The first attempt read **zero** references out of a
manager holding 21, and reported no error at all, because the probe swallowed the exception.

**`ref.id` is re-minted on every open.** Two consecutive runs over an untouched project:

```
run 1   #Standard  id 1886582003        run 2   #Standard  id 1665927582
run 1   #Util      id 4015974643        run 2   #Util      id 1820137668
```

68 differing lines across the two dumps, and **every one of them was an id**. Nothing else
moved. So it is not recorded — the same decision, for the same reason, as the project-level
`__VisualizationStyle` GUID that broke export determinism hours after it was added. The
difference is that this one was caught by measuring *before* writing the exporter rather than
by a determinism test afterwards, which is the cheaper end of the same lesson.

**What resolution a reference gets is a fact about the machine, not the project.** The fixture
asks for `Standard, * (System)` and resolves it to `Standard, 3.5.22.0 (System)` — a wildcard
answered by whatever is installed here. Recording the resolved value would make the mirror
differ between two engineers sharing one repository, with neither of them wrong, and would move
the hash gate on a machine difference. The file therefore carries the **request** — placeholder,
default resolution, redirection, parameters, flags — and says in its own header what it leaves
out and why. `resolution_info` and `resolver_guid` go the same way.

Verified end to end afterwards: two full exports of the fixture, 14 files each, **byte-identical
across the whole tree**, and `_coverage.md` reporting **0 gaps** where it reported 2.

### A test that passed on broken code and failed on correct code

The reader for `*.libraries.yaml` is hand-written, and so is the writer, and they never meet in
one process. So the parser was broken deliberately to see which tests noticed — and the answer
was a genuinely useful surprise.

**Break: stop closing the nested `parameters:` mapping.** All 24 tests still passed. The test
that claimed to protect it asserted that parameters did not leak into the *next* reference —
which the list dash prevents on its own, whatever the mapping code does. It could not have
failed. The property that actually needs a parser is a **sibling key after the mapping**, and
the fixture cannot exercise it because the emitter writes `parameters` last.

Rewritten against a hand-written document with a key after the map, the new test **failed on
the correct code**: keeping one pending nested map and reading it at flush time discarded it the
moment a sibling key followed. The parser now stores nested maps into the item as they are
found. So the bug was real, the fixture could never have shown it, and the first test written
for it proved nothing — three separate ways to have shipped it.

A second break was more instructive than the fix: deleting a "close the open mapping" line
changed **nothing anywhere**. Indent already decided where a key belongs, and the line was a
second mechanism agreeing with the first — code no test could ever fail on. It was removed
rather than kept as defence, with the reason written where it stood.

And a third: the rule that drops a nameless half-entry survived being deleted, because
truncating the fixture never produces one — `name` is the first key of every item. It has its
own test now, from a document that does.

**The habit this reinforces:** a green suite over a committed fixture proves the fixture, not
the code. Every rule that only fires on input the fixture cannot contain needs a document
written to reach it.

### A log test that broke because the export got better

`ExportLoggingTests` asserted the swap line contained `"0 existing file(s) removed, 12 written
in their place"`, and the summary line `"walked 18, wrote 10, skipped 6"` with `"2 not
implemented"`. Exporting two more files turned a correct improvement into a failing string
comparison, which tells the reader nothing about what changed or whether it was wanted.

The numbers were there for a real reason — the counts have to *track reality*, and a swap that
printed the same pair twice would prove nothing — so they were not simply deleted. They are now
derived from the things they describe: the file count comes from the export directory on disk,
and the summary is checked against the `_manifest.json` **that same run wrote**. Both describe
one export, so they must agree, which is a stronger claim than any literal and one that does
not need editing the next time the exporter learns to write a file. The two properties worth
pinning by hand survive: the two swap lines must **differ**, and the fixture still walks 18
objects.

**The rule of thumb:** assert against another record of the same event, not against a number
typed into the test. A literal is right until the code improves, and then it is noise.

### The export's own narrative never reached the log

Asked directly whether the logs were sound for copying, exporting and replacing an export
directory, the honest answer turned out to be no — and the gap was invisible from the code,
because every line that existed was correct. The problem was the lines that did not exist.

**The most destructive step in an export had no log line at all.** The staging swap deletes the
entire export directory and renames a new one into its place:

```python
if os.path.isdir(out_root):
    shutil.rmtree(out_root)
os.rename(staging, out_root)
```

Nothing emitted, nothing logged. If it half-failed, the log would say the export succeeded and
the directory on disk would disagree, with no line anywhere connecting the two.

**And the script's protocol events were thrown away after being counted.** They drove progress,
the changed-path list and the warnings, and were then summarised as `events=24` — a number that
says something happened and nothing about what. So every skip, every object written and every
summary the exporter produced was absent from the log in readable form, in a codebase whose
stated rule is that a silent failure is a bug even when the code is correct.

Both are fixed. An export now reads:

```
replacing the export directory …\RefrigeratorControl: 12 existing file(s) removed, 12 written in their place
export directory now holds 12 file(s)
walked 18, wrote 10, skipped 6 (of which 2 not implemented), 0 warning(s)
not exported (NOT_IMPLEMENTED): Device/Plc Logic/Application/Library Manager [LIBRARY_MANAGER]
```

Levels are chosen by volume and by who needs them: one line per exported object is Debug,
because a real project has thousands of them; skips, the summary and the filesystem swaps are
Info, because they are bounded and each answers a question somebody asks afterwards. A leftover
staging folder is a **Warn** — it means a previous export was killed, which is worth knowing at
the moment it is discovered rather than never.

Two smaller ones went with it. Pruned project backups are now named as they are deleted, because
a backup that vanished with no line saying so is indistinguishable from one that was never made
— exactly the question being asked when somebody goes looking for it. And the skip path is
computed once and shared by the manifest and the log line, after the log showed
`Device\Plc Logic\Application/Library Manager` with both separators in one path.

The test asserts against the log a real export produces, and it asserts **both** runs: the first
removes nothing because there was nothing there, the second removes what the first wrote. A
count that did not track reality would print the same number twice. Verified to fail with the
swap emit removed.

### Recording a GUID broke export determinism, hours after it was added

`_coverage.md` shipped with the skip records carrying the object's GUID, which looked like free
information. It is not: **CODESYS re-mints the project-level `__VisualizationStyle`'s GUID on
every open.** Two consecutive exports of the same file:

```
run 2   09cd3618-c2e4-4e49-888a-02e46902a7b0
run 3   63754151-d4ae-45bd-9876-9d0db2d723eb
```

So `_manifest.json` differed on **every single export** — a spurious diff in every commit,
forever, which is precisely the noise the `Timestamp` scrub exists to remove. Reintroduced by
the report written about coverage.

**The determinism test added alongside it did not catch this**, because it compared
`_coverage.md`, and the report carries no GUIDs. A test written in the same hour as the feature,
against the same mental model, checked the half that could not fail.

Phase 0's gate 0.2 measured byte-identical exports across three runs **by hand**. Nothing in the
automated suite enforced it, which is how a determinism regression got committed at all. The
test now fingerprints the **whole export tree** across two exports of one project and compares
every file — verified to fail against the reverted fix before it was believed.

The GUID is gone from skip records. A skipped object has nothing pointing at it, so path and
type are identity enough; for an *exported* object the GUID stays, because there it is what
makes a move a move rather than a delete and an add.

**The general shape:** a value that is stable in every case you look at is not thereby stable.
This one was stable for five of six skipped objects, and the sixth only reveals itself if you
export twice and compare — which is the one thing the narrower test did not do.

### Exporting a copy works, and the lock is on the path

Measured, because it decides whether the tool can work at all while an engineer has CODESYS
open — the normal way of working:

| Question | Answer |
|---|---|
| Can the `.project` be copied while CODESYS holds it? | **Yes** — no sharing violation |
| Does exporting the copy work while the original is locked? | **Yes** — 16.7 s, full export, 10 files |
| Is the result identical to exporting the original? | **Yes**, all 11 object files byte-for-byte |

The lock is a sidecar on the path (`<project>.~u`), so a copy at another path is unencumbered.
The only difference between exporting the original and exporting a copy was the unstable GUID
above — which was not caused by the copy, and is now gone.

Two constraints that fall out of this and are easy to get wrong:

- **The copy must keep the same filename.** The manifest records
  `os.path.basename(job["project"])`, so exporting `P.project` instead of
  `RefrigeratorControl.project` would rewrite that field and churn the mirror.
- **The copy belongs in `.versio/local/`**, which is already gitignored, so it never reaches
  git and never appears in the panel.

This makes export-while-open a solvable problem rather than a refusal. Import is not, and
should not be: the IDE holds its own copy in memory and would silently revert an import on the
next Ctrl+S, so it must fail and say so.

### `.versio/local/` was gitignored in three comments and nowhere else

Found by re-reading the plan against the code after building the ignore rules. The engine says
`.versio/local/` "is gitignored" in three separate places, and §5 of the plan is explicit that
it must be: it holds the direction locks, session state, project backups — megabytes of binary
snapshots — and now the scratch copy taken while the IDE has a project open. A committed lock
pulled onto a colleague's machine is, in the plan's words, a bug factory.

Nothing wrote the rule. **This repository had one because a person typed it by hand**, which is
precisely why nobody noticed: every test and every manual check ran here. A repository somebody
else adopted a project into got the vendor's `.opt` rules and nothing else, so their locks and
backups sat untracked, visible in the panel, and one *stage all* from being committed.

The same shape as the library manager this morning: a comment asserting a property that nothing
enforces, true only where somebody had arranged it separately. Adoption now writes the rule, and
the test asserts through git — including that `.versio/repo.json` is **still** reported, because
it is committed on purpose and an ignore rule that swallowed it would break every clone's
baseline.

### What CODESYS actually leaves beside a project, and why it belongs in `.gitignore`

Written down because the first account of it in this document was **wrong**. The scratch folder
after an export was said to hold a `.~u` — inferred from the lock file measured earlier, not
looked at, because the test had cleaned up before anyone could see. Measured properly:

| | |
|---|---|
| Open and close cleanly | **Nothing left.** `<name>.~u` exists only while the project is held, and is removed on close |
| Killed while open | `.~u` survives — the stale case the pid probe already handles |
| Export or open | `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt`, and these **persist** |

The `.opt` files are per-user settings rather than locks, which is why they outlive the session
that made them — and why they matter here. Untracked in a repository, they turn up in
`git status`, in the project panel as loose rows, and in anything that stages everything.

**The per-user one is worse than noise: its filename carries the username and the machine
name.** Committing it writes both into history permanently, and produces a different file for
every engineer who opens the project — a file that can only ever conflict.

So adoption now writes `.gitignore` as well as `.gitattributes`, **creating it when absent**,
which is the ordinary starting point for somebody who has kept PLC projects in a folder until
now. The rules come from the vendor through `IPlcProjectFormat.TransientPatternsFor` and are
**scoped to the project** — `plc/Machine-*.opt`, never a repository-wide `*.opt`. A repository
belongs to other people too, and ignoring every file of an extension on the strength of one
adopted project is not Versio's decision to make.

An existing decision is never overridden, negations included: a repository saying
`!plc/Machine-*.opt` has been configured deliberately, and adding the positive rule underneath
would quietly reverse it. Re-adoption adds nothing, so the file does not grow on every scan.

The integration test asserts against **git**, not against the file it just wrote: it creates
all three leftovers and checks `git status` no longer reports them, while the project itself
still is reported — an ignore rule that swallowed the thing being tracked would be a far worse
bug than the noise it removes. Verified to fail with the patterns removed.

**The habit worth keeping:** the wrong version of this was a reasonable inference from a real
measurement taken minutes earlier, and it read as fact. Looking cost one command.

### Working while the IDE has the project open

Built on the measurements below. The behaviour now:

| | |
|---|---|
| **Export** | Runs. The project is copied to `.versio/local/open/<binding>/` keeping its own filename, and the copy is exported — the vendor lock is a sidecar on the path, so a copy elsewhere is unencumbered |
| **Import** | Waits, and says who has it. It cannot be made to work: the IDE holds its own copy in memory and the next save would silently revert whatever was imported |
| **Either** | `BlockedByOpenProject` — **not** a failure. No failed binding, nothing to acknowledge, and the circuit breaker is never fed |

**The check runs before the coalescer and the breaker**, which a test caught: placed after them, six blocked saves tripped a breaker with a threshold of two and halted the project for the session — turning the wait into exactly the sticky failure the verdict was introduced to remove. It runs *after* the hash gate and the direction lock, because "nothing changed" and "the lock forbids this direction" are more specific answers, and reporting the IDE instead would be noise on every settle.

The staleness rule mirrors `LockRecoveryPolicy`, because the problem is the same. A marker naming a dead process **on this machine** is stale and ignored — a crashed IDE must not block sync until somebody deletes a file they have never heard of. A marker from **another machine** is always believed: its pid means nothing here, and a project on a share really can be open on somebody else's desk. Guessing free would put two writers on one project.

**It sits behind the vendor seam.** `IPlcProjectFormat` gained `OccupancyMarkerFor` and `ParseOccupancy`; `Sync/` knows only that something may be held and by whom. A vendor that cannot answer reports free, so registering a format never makes its projects unsyncable.

Two things the integration test caught that nothing else would have:

- **The scratch folder is emptied, not the copy deleted.** Removing only the file we created left the toolchain's own files behind for good. The first write-up of this named the wrong file — `.~u`, inferred rather than looked at, because the test had already cleaned up. Measured properly afterwards: a clean open/close leaves **nothing**, and it is the *export* that leaves `<name>-AllUsers.opt` and `<name>-<user>-<machine>.opt`. See the finding below.
- **The test's own CODESYS launch used `ArgumentList`**, which quotes the whole `--profile=A B C` pair — the trap already documented in the README and covered by `ArgumentBuildingTests`. It exits 1 in under a second with no output, and the symptom was two silently skipped tests. It now uses `LocalCodesysRunner.BuildArguments`, which also means the test launches CODESYS the same way the product does.

Not built, and deliberately: **automatic retry when the IDE closes.** The `.~u` disappearing is a filesystem event the watcher could see, which would make closing CODESYS the trigger and need no button. Today the next save retries, and so does reopening the repository. Worth doing, not worth blocking this on. *(Written when an "Export now" button existed; it has since been deleted in favour of exporting on open.)*

### A project open in the CODESYS IDE — measured, and it is the normal case

The everyday scenario: an engineer saves in CODESYS and leaves the project open. Versio's
watcher sees the `.project` write and starts an export. What happens was unknown, so it was
measured rather than reasoned about — a second CODESYS holding the project open, then a real
export and a real import against it.

**CODESYS refuses, cleanly, in both directions.**

```
StandardError: The selected project is currently in use by 'elli_' on 'ROG-JVM'.
```

| | |
|---|---|
| Export while open | Fails at `projects.open()` in **6.9 s**. No manifest written, no partial tree |
| Import while open | Fails identically. The `.project` is **byte-for-byte unchanged** — measured, not assumed |
| Reported correctly? | **Yes.** `CODESYS.exe` exits **0** in both cases, but `PlcResult.Succeeded` requires the script's own `rc` *and* its `done` event, so neither is mistaken for success |

That last row is the one that could have gone badly, and it did not: the process exit code is
worthless here, and `Succeeded` already ignored it.

**There is an on-disk lock, and it is cheap to read.** CODESYS writes `<project>.~u` beside the
project while it holds it, and removes it on close:

```
elli_                 user
ROG-JVM               machine
45812                 process id
639221606143479369    timestamp
```

Which is very nearly Versio's own `LockRecord` — machine, user, pid — and carries the same
staleness problem: a CODESYS that crashed leaves the file behind, so the pid has to be probed
before the lock is believed. `LockRecoveryPolicy` already does exactly that reasoning for
Versio's locks.

**The defect this exposes is not the refusal, it is what Versio does with it.** A refusal
arrives as a failed run, and a failed binding stops syncing until somebody acknowledges it
— then a button called *Acknowledge failure* on the panel, now *Try again* on the failing
project's own row. So the most common working state in the tool's whole life — the IDE
open — turns into a sticky failure requiring a button press, over and over. "The project is
open" is not a failure; it is a *wait*.

Worth stating plainly: even if CODESYS allowed the write, importing into an open project would
be wrong. The IDE holds its own copy in memory, and the next Ctrl+S would silently revert the
import. The direction lock cannot help — it arbitrates between Versio's two directions, and the
IDE is a third writer it has never heard of. CODESYS's own lock is what saves us, and it is
luck rather than design that it does.

**The fix, designed but not built:**

1. Pre-flight the `.~u` file before launching CODESYS. It costs a file read instead of 17–31 s,
   and it turns the common case into an instant, quiet refusal.
2. Probe the pid before believing the lock, or a crashed IDE blocks sync until somebody deletes
   a file they have never heard of.
3. A distinct verdict — *blocked by an open project* — that does **not** mark the binding failed
   and does **not** trip the circuit breaker, and that retries when the lock clears.
4. A sentence naming who has it: *"RefrigeratorControl.project is open in CODESYS (elli_ on
   ROG-JVM). Close it and Versio will export automatically."*
5. **It goes behind the vendor seam.** `.~u` is a CODESYS fact, and `Sync/` may not learn it —
   so the question this poses is which contract owns it, most likely `IPlcProjectFormat`.

### A comment claimed a file nobody wrote

`LIBRARY_MANAGER` was skipped by the exporter with the comment *"captured in
config/libraries.yaml instead"*. Nothing has ever written that file. So a library reference —
adding one, bumping a version — changed nothing in the mirror, moved nothing through the hash
gate, and reached no diff and no commit. On the fixture there are **two** library managers, and
neither has ever been exported.

The tool's claim is that git can tell you what changed in a PLC project. For library references
it quietly could not, and nothing anywhere said so: an object that is deliberately not exported
and an object nobody has written the code for look identical from the outside, which is
*absent*.

Reported before it is fixed, and deliberately in that order. Every export now writes
`_coverage.md`, the manifest records each skip with a stable reason code, and the reasons
separate the two cases — `SERVICE_OBJECT`, `TEMPLATE_ONLY` and `COVERED_ELSEWHERE` lose
nothing; `NOT_IMPLEMENTED` is a hole. A stated gap is one somebody can work around. An unstated
one is a wrong answer to the only question the tool exists to answer.

The manifest schema went to 2 for this. An empty skip list means two different things — nothing
was skipped, or nobody was counting — and reading a schema-1 manifest as full coverage would
turn every old export into a clean bill of health.

### Adding a file to the export tree broke the importer, and no test noticed

The importer walks the export tree and treats every file it finds as an object, knowing about
`_manifest.json` by name, in two separate places. Adding `_coverage.md` beside it meant the
importer handed a markdown file to CODESYS as native XML:

```
NATIVE_IMPORT_FAILED  _coverage.md  Data at the root level is invalid. Line 1, position 1.
```

Nothing was corrupted, and that is the trap. The cost is that **every** import would carry a
failure warning that is always present and always meaningless — which is precisely how a real
native-import failure gets read past.

**The existing export→import round-trip test passed with the bug in place.** It was re-run with
the fix deliberately reverted to check, and passed again: it asserts the project is reached and
never looks at the warnings. The reserved names now live in one frozen set beside the exporter's
own type table, so the next file added is added for both halves at once, and a new test asserts
no warning names a bookkeeping file — verified to fail without the guard before it was believed.

The general lesson is about the shape of the mistake rather than the file: **the export tree has
two kinds of contents, and only one of them is the project.** Nothing in the code said so until
now.

### The integration suite had been lucky about running two CODESYS instances

xunit runs test *classes* in parallel by default, and four classes already drove a real
headless CODESYS. Adding a fifth that launches five exports tipped it over:
`ProjectTreeFromRealExportTests` began failing in a full run and passing on its own, with the
export exiting in **0.9 s** instead of 17 — two instances contending on the profile and the
shared temp state, and the loser dying with nothing useful to say.

The race was always there. Nothing had changed about it except the odds, which means the suite
had been passing on luck for as long as those four classes have existed.

Found only by running the whole suite **from a fresh clone** rather than from the working tree
it was written in — a habit worth keeping, since it is also the first thing anybody taking this
over will do. The CODESYS-driving classes now share an xunit collection and run one at a time.
The cost is wall clock, and it is the right trade: a slow suite that is believed beats a fast
one that is not.

### `git apply --cached` reads the index, not the working tree

Written down because the first test of per-hunk staging asserted the opposite, and **failed**.
It edited a file after reading its diff, then expected git to refuse the now-stale hunk. Git
applied it happily: `--cached` takes the *index* as the patch's pre-image, so a save landing
between the diff being read and the button being pressed does not invalidate anything — what
gets staged is still exactly the hunk that was on screen, which is the behaviour you want and
not the one the test claimed to protect.

The staleness that does matter is the index moving, and that is refused. Both are now pinned,
and the pair is why `--unidiff-zero` is not passed: git's context check is the only thing
standing between a patch and the wrong offset.

Worth keeping as a finding rather than a fixed test, because a passing test would have
documented a protection that does not exist.

### Lazy tree rows are proportional to what is reachable, not to nothing

`TreeRow` built its whole subtree in its constructor, which was affordable only while the tree
was filtered to what had changed. Rendering the full CODESYS structure that way would build a
row per object — thousands on a real machine — before the window drew anything.

Children are now built on first access. **That is not zero cost, and it is worth being precise
about why:** WPF evaluates `Children` when it realises a container, because it cannot decide
whether to draw an expander without asking. A visible collapsed folder therefore still costs
one row per child; its grandchildren cost nothing. The win is O(objects in the project) →
O(visible rows × branching), which is the whole point, and claiming more than that would be
the same kind of decorative virtualization the diff view already had once.

### A view that rebuilds on every refresh drops the selection

The project tree is rebuilt whenever git's status is re-read — which, now that it is the only
view, is several times a minute while somebody works. Most of those refreshes find nothing:
they fire because a file was saved in an editor.

Rebuilding anyway would clear the TreeView's selection over and over, which is precisely the
regression the changed-files list was reconciled in place to avoid; the fold would have
reintroduced it one panel to the left. The tree is now derived from a signature — the changed
paths, the search text, the bindings and each manifest's write time — and an identical
signature rebuilds nothing. Measured live: **five refreshes, two rebuilds**, and the three
no-op saves in between rebuilt nothing.

Expansion state is kept separately, keyed by path, and records both what was opened and what
was closed. One set cannot express "the user deliberately shut a folder the default rule wants
open", and the default rule changes its mind every time that folder's change state moves.

### A file under an export tree that the manifest does not describe

`_manifest.json` changes on every export and describes the other files rather than appearing
among them, so no object node claims it. Dropping it would have been worse than showing it: a
file git reports as changed, in a directory Versio owns, appearing nowhere in the panel is
indistinguishable from Versio having lost it. Such files hang off their project as plain rows.

The unit test that asserts every changed path is represented exactly once did **not** fail when
the rule was deliberately removed — the loose-file fallback caught the file at the top level
instead, so coverage held by a second mechanism while placement was wrong. Placement is
asserted separately, and the coverage test was re-verified against a different break (removing
the claim on manifest files, which duplicates every object) before it was believed.

### The hash gate could not see a text edit

Change detection for the mirror hashed `_manifest.json` — a file written by an export and by
nothing else. An engineer editing `Simulation.st`, the entire purpose of the mirror, left it
byte-identical, so the gate returned `NoChange` and the import was refused before the direction
lock was ever consulted. **Import could not have worked at all.**

The 24 scenario tests could not catch it: they simulated a text edit by rewriting
`_manifest.json`, the one file no human ever touches, so both sides agreed about a signal that
does not occur in the field. Fixed with `ContentHash.OfTree` over the tree's real contents, and
`ManifestHash` renamed to `MirrorHash` because the old name described the proxy rather than the
thing — which is how the bug got its blast radius.

**The first draft of the test written to prove the fix passed against the broken gate.** A
fresh binding has never recorded its mirror, so it reports a change whatever the gate does. It
now starts from a synced harness. That is the same vacuous pass one level up, found only by
deliberately breaking the code and re-running.

### The vendor seam was documented but not true

`SyncOrchestrator` was built directly on `ICodesysRunner`, `CodesysJob`, `CodesysOperation`,
`CodesysResult` and `CodesysProgress`, and `Abstractions/` — a folder whose entire purpose is
neutrality — held two brand-named interfaces. A second vendor would have meant rewriting the
engine, which is exactly what the rule existed to prevent. `IPlcToolchain` had been declared
from the start and never implemented, so the application assembled a toolchain out of vendor
parts instead: a locator, a profile resolver, an install record and a runner, five brand names
in a view model with no business knowing any of them.

A rule nothing checks is a rule that decays. It is now enforced from assembly metadata.

### The diff view's virtualization was decorative

`DiffView` is a virtualized `ListBox` whose class comment claimed rows were built on a
background thread. Neither was doing anything: `Rebuild` eagerly constructed a `Grid`, two
gutters and a syntax-tokenized `TextBlock` for every line before the list saw them. Selecting
the fixture's 2.5 MB, 33,644-line visualisation locked the window.

Made visible by the new-file fix that came before it in the same session — until untracked
files produced a diff at all, that file rendered nothing and returned instantly.

### `CreateNoWindow` does not hide a GUI child

It sets `CREATE_NO_WINDOW`, which suppresses the console a *console-subsystem* child would
allocate. `CODESYS.exe` is a GUI application and under `--noUI` still created a top-level
window. `WindowStyle.Hidden` passes `STARTF_USESHOWWINDOW` with `SW_HIDE`, which is what a GUI
child honours. Both flags are set: they cover different subsystems and neither replaces the
other.

### A wrong diagnosis, and the vacuous check that confirmed it

"Open logs folder" reported *Location is not available* for a folder that plainly existed. The
cause was **filesystem virtualization**: the app had been launched from inside an
MSIX/AppContainer, where writes under `%LOCALAPPDATA%` are redirected into that package's
`LocalCache`. Versio wrote to a real folder; Explorer, outside the container, was handed a path
that does not exist from where it stands.

The check that "confirmed" the first diagnosis was vacuous: `Start-Process explorer.exe`
reported success because Explorer *launched* — it then failed on its own and put up its own
dialog. **Process-started is not folder-opened**, and that was the entire question. Launch
context is part of the environment, alongside the per-user .NET install already recorded here.

### `objectCount` in the manifest is a file count

`versio_export.py` writes `len(records)`, and there is one record per *file*. It therefore
differs from the number of distinct objects exactly when an object exports to more than one
layer — which the fixture does, via its GVL. Found by an assertion that trusted the field's
name. Nothing depends on it today; the integration test asserts against distinct GUIDs.

### The repository dropdown was blank over a list of one

The control rendered empty while the list behind it held exactly the right entry, selected
nothing, and reported no error anywhere.

`RecentRepositories.Canonical` exists precisely for this — its own remarks record the sibling
defect, *"git answers `rev-parse --show-toplevel` with forward slashes and the Windows folder
picker hands back backslashes, so opening one repository both ways put two entries in the list
for it"*. That fix was applied **on the way in**: `Promote` canonicalises, so the list correctly
refuses to hold one repository twice.

The dropdown then looked the selection up **on the way out** with an ordinal `==`:

```csharp
SelectedRepository = RecentRepositories.FirstOrDefault(r => r.Path == select);
```

The stored spelling is canonical. The opened spelling is whatever opened it. `C:\GIT\Versio`
and `C:/GIT/Versio` are not the same string, `FirstOrDefault` returned null, and a
`ComboBox` with a null `SelectedItem` draws nothing. Half an argument, applied to half a
problem, leaves a list of one with nothing selected.

**Nothing was watching it, in three separate ways.** No binding failed — the binding tracer over
the running app reported thirteen errors and not one of them on this control, because a
successful binding to null is not an error. No test covered it: the recent-list tests assert on
what is *stored*, which was always right. And the app itself logged `1 recent repositor(ies)
restored`, which was also true.

Found by launching the app and looking at it. It needed nobody to type anything unusual — a
forward-slash path is what git itself answers — and it had been reachable since the canonical
fix went in.

All three lookups go through one `RowFor` now, and the test opens a repository by the other
spelling and asserts the row is selected. It fails with the canonicalisation removed.

**The tell worth keeping.** A control that is blank rather than wrong is the shape of a
successful binding to a null, and no instrument in this codebase reports one — not the binding
tracer, not the log, not a test that asserts on the view model's inputs. The only thing that
catches it is opening the app.

### Fifty-six shells nobody disposed, and a suite that failed at random

The UI suite failed roughly one full run in three, on a **different test each time**, and every
one of them passed when its class was run alone. It had been recorded here as flakiness under
load — three named classes, timeouts, real git and real CODESYS running concurrently — and left
undiagnosed on the grounds that a red run should not be read as a regression.

That was the wrong conclusion, and the reason it survived is worth more than the fix: **a
symptom that moves looks environmental.** Three classes failing on three different runs reads
as load; one class failing every time reads as a bug. It was a bug.

**Fifty-six tests opened a repository through a `ShellViewModel` and never disposed it.** A
shell that has opened one owns:

- a `FileSystemWatcher` over the working tree,
- a `RemotePoller` that spawns `git` subprocesses on a loop,
- the sync panel's own watchers over every tracked project and its export tree,
- and the repository lock.

None of that stops when the test method returns, and `Dispose` is the only thing that ends it.
With xunit running twenty-eight collections in parallel — every class outside the `theme`
collection is its own — the suite accumulated dozens of live watchers and polling loops. They
fired against temp directories their own test had already deleted, and competed for the same
`git` binary as whatever test was actually running.

Every symptom follows from that. **The failure moves** because it lands on whichever test is
unlucky enough to be waiting on git when the noise peaks. **It never reproduces alone** because
alone there is nothing else running. **It reproduces on `main`** because the leak predates every
branch that inherited it.

**Measured rather than argued, and it is a reduction rather than a cure.** Before: four failures
across fourteen observed full runs, on `main` as well as the working branch — roughly one run in
three. After `using var shell = ...` at all fifty-six sites: one failure across the next
fourteen, the first ten of them consecutive. So about 29% down to about 7%.

**Something is still leaking, and the candidate is named below.** Do not read a single red run as
a regression yet; do read a second one as this, still.

Two things this leaves behind.

~~**`LogViewModel` is still leaked**, thirty times over, by tests that construct one inline as a
constructor argument.~~ — **fixed, and the sentence that followed it here was wrong.** It read:
*"the timer cannot tick on a thread with nothing pumping a dispatcher, so the practical cost is
the subscription rather than the work"*. `StaHarness` runs **one** long-lived STA thread with a
live `Dispatcher.Run()` for the whole suite — it has to, because the theme's `Freezable`s belong
to the thread that created them — so every log built inside a windowed test puts its 100 ms pump
on the one dispatcher that does pump, and a leaked one goes on doing the work for the rest of the
run. The instinct about ownership was right, though: the fix was changing who owns the log, not
restructuring thirty call sites. See the finding above.

**`CA2000` was tried as a guard and rejected.** It fires fifty-four times on the suite *after*
the fix, almost all of them on that `new LogViewModel(sink)` argument, and it cannot see
ownership through a factory like `TestShell.Build` — so it would have missed the actual defect
while demanding fifty-four suppressions. The rule is stated in `TestShell`'s own remarks
instead, where somebody about to write the fifty-seventh test will read it.

**The general lesson, which this codebase already half-knew.** `Eventually.Press` exists because
of the same class of problem — a press landing while something else is running does nothing, and
the wait after it times out looking like a real defect. Its remarks say it "passed in isolation
and failed under full-suite load, twice, before the cause was clear". That is the second sighting
of the same tell. **A test that passes alone and fails in company is not being slow; something
alongside it is still running.**

### A flaky watcher test, and two fixes that made it worse

`RepositoryWatcherTests.A_deleted_file_is_reported` failed once during a full run and passes
in isolation every time. The root cause is a fixed `Thread.Sleep(250)` waiting for
`FileSystemWatcher` to arm — an assumption that holds on an idle machine and stops holding
under the full suite, where CODESYS exports are hammering the same disk.

Replacing the sleep with a deterministic arming probe — write a file, wait for the watcher to
report *it*, then act — made the test fail **consistently**. Windows raises a single
directory-level notification when several files in one folder change at once, so the probe and
the test's own file coalesced into one event for `src` rather than two for the files, and the
assertion looks for a file by name. Moving the probe to the repository root did not help
either.

**Reverted.** A flake observed once is not worth destabilising a working test for at a
stopping point, and two attempts had already turned "fails rarely" into "fails always". The
diagnosis is recorded here so the next attempt starts from it rather than from the sleep.

It is also a second sighting of the directory-level coalescing already documented for `.git`:
it applies to working-tree folders too, and any test asserting on a per-file event is making
an assumption Windows does not guarantee.

### Committed configuration has to be byte-stable

`repo.json` is committed by design — it is how a teammate gets the same project mapping — and
two things made it churn:

- **CRLF.** `System.Text.Json` indents with `Environment.NewLine`, so it was written CRLF on
  Windows while `.gitattributes` stores it LF. The file showed as modified the moment anyone
  cloned, and again every time Versio touched it.
- **Enums by ordinal.** Without a converter, `vendor` would have serialised as `0`. A reviewer
  reading that in a diff learns nothing, and inserting a vendor into `PlcVendor` —
  alphabetically, say — would have silently repointed every existing binding at a different
  toolchain, first noticed as an export driven by the wrong tool.

Both fixed and pinned by a test that reads the bytes back.

### A vacuous check, caught in the act

While investigating whether the export's line endings would churn through git, the first
measurement used `grep -c $'
'` and reported that all 34 lines of a `.st` file contained a
carriage return. They contain none: the pattern matched every line rather than CR-containing
ones. Measured again with `tr -cd` **and a known-CRLF control file**, the export is LF
throughout and round-trips through git unchanged.

The fix that was almost applied — rewriting the export's line endings — would have churned a
proven pipeline to solve a problem that did not exist. The control file is what separated the
two, and it cost one line.

### The fake filesystem was not a filesystem

`FakeFileSystem.EnumerateFiles` was a bare prefix match, so `plc/A.project` enumerated as part
of its own export tree `plc/A`. A test written against that would have proven the opposite of
what it claimed.

---

## Verifying the UI, not just the engine

Two instruments, covering different halves of the problem.

**`Versio.App.Tests` constructs the real windows against the real stylesheet.** A WPF test
project on an STA thread with `Theme.xaml` loaded by pack URI from the app assembly, so a style
renamed in the stylesheet and not in a dialog fails the build rather than the first click. It
covers what the tracer cannot: a modal nobody has opened has never been parsed. 441 tests across
48 classes; a deliberately broken style reference fails the dialog tests outright.

**Applied and MEASURED, not looked up — that distinction has now earned itself twice.** A style
key can hold a template that resolves fine and throws the first time something renders it, which
is what happened to `Motion.Fast` (see Findings), and a control can carry a perfectly valid
property that produces the wrong geometry, which is what happened to the horizontal scrollbar.
Neither is reachable by a test that asks whether a resource exists. The UI classes worth knowing
about, all of which apply a style and then read the result:

| Class | What it holds still |
|---|---|
| `ShellLayoutTests` | Rendered geometry at 1000 px and 2400 px: nothing overlaps, every panel fills its card, and a dropdown keeps its buttons beside it |
| `StockControlStyleTests` | No control falls back to WPF's own Aero chrome or the system highlight |
| `FocusVisualTests` | Keyboard focus is visible, in the app's accent, on every control and both row kinds |
| `ScrollBarChromeTests` | The scrollbar works on both axes — geometry, thumb, direction and page commands |
| `MotionTests` | The storyboards resolve, render, and settle at full opacity in both directions |
| `AutomationNameTests` | No control in the shell announces a Private Use Area codepoint |
| `DiffSelectionTests` | Diff selection and copy: document order, markers kept, command state |
| `PreferencePersistenceTests` | A setting survives the launch after the one that set it, and no write resets another |
| `HistoryRowChromeTests` | A realised commit-file row, after a shared template blanked the panel |

> **Two of these exist because a geometry defect is invisible to every other kind of test.** An
> overlap, a control centred in its own column and a scrollbar arranged 10 px wide all measure,
> arrange and render without a warning — they are layouts that did what they were told. The only
> form in which "these two things sit on top of each other" can be written down is a transformed
> `Rect`, at a width where it actually happens. `ShellLayoutTests` measures at 1000 px for the
> overlaps and at 2400 px for the top bar, because each is invisible at the other's width.

**`Versio.exe --trace-bindings <file>`** writes every WPF data-binding failure to a file. A
broken binding in WPF is otherwise **silent** — a mistyped path or a missing converter produces an
empty control and no error anywhere — which made it the one class of failure in this codebase
that was not loud.

> ⚠️ **Its clean result means nothing unless a window actually opened.** The tracer writes only
> when a binding fails, so "no file" is produced both by a clean window and by *no window at
> all* — and the second case happened: with `MainWindow` throwing during construction, two
> consecutive runs produced an empty trace that was read as a pass. Check that the process has
> a window before believing it:
>
> ```powershell
> Get-Process Versio | Select-Object MainWindowTitle, MainWindowHandle
> ```
>
> A handle of `0` means the window never opened and the trace is worthless. **Launch with
> `Start-Process`**, not from a detached shell — a background-launched instance was alive,
> logging and window-less, which is the same empty trace for a third reason.

The app must be launched **outside** the tooling container for UI work; see the virtualization
finding above.

---

## Open decisions

| # | Question | Blocks |
|---|---|---|
| 1 | Who else runs this, and on what kind of machine? | Whether signing and an MSI are needed at all |
| 2 | Budget and legal entity for a code-signing certificate? | External distribution |
| 3 | Bundle PortableGit, or require Git for Windows? | Installer size and GPL compliance work |
| 4 | Where does the update feed live? | Phase 3 packaging |
| 5 | Is a **resident CODESYS session** worth building? | Now that export is automatic, this is the difference between 17–31 s and sub-second on every save |
| 6 | Can CODESYS run on a shared server? *(a licensing call, not engineering)* | PLC CI, the most valuable Phase 5 item |
| 7 | Which vendor comes second? | Whether `PlcJob`/`PlcResult` are the right shape |

---

## The disciplines that are load-bearing

Worth stating because they are what make the rest cheap, and they are easy to erode.

**`Versio.Core` touches nothing external.** No `System.IO`, no `Process`, no
`DateTimeOffset.Now`, no UI framework. That is why the lock model, the loop defences and the
state machine are tested in milliseconds rather than by causing real crashes.

**Vendor knowledge stops at the seam.** Enforced by a test that reads assembly metadata, not by
a paragraph in a README — because it was a paragraph in a README, and it had already stopped
being true.

**Green results are distrusted until they can fail.** Fifteen vacuous passes have been caught
this way — most recently an empty `--trace-bindings` file read as a clean UI while the window was
failing to construct at all, and a style-instantiation test that passed with the style deleted,
because a missing resource casts to a null `Style` and assigning null is legal — counting the determinism test that compared the one file with no GUIDs in it, and
the two the library reader gave up when it was broken on purpose: a parameters test that could
not have failed because a list dash already prevented the leak it asserted, and a
drop-the-nameless-entry rule that survived deletion because the fixture cannot produce a
nameless entry. Before those:
a round-trip that succeeded because the import silently did nothing; equivalence tests
that "passed" by skipping; a tree spike that measured a collapsed tree; a `Core2` mutation that
looked absent because the import had errored; a scenario suite that simulated text edits by
rewriting the one file no human touches; a shell check that reported success because a
process started rather than because it did its job; a line-ending measurement whose pattern
matched every line rather than the ones it claimed to count; and a coverage assertion over the
workspace tree that survived the deliberate removal of the rule it existed to protect, because
a fallback one level up caught the file in the wrong place; and an export-import round trip that
passed with the importer feeding a markdown file to CODESYS, because it asserted the project was
reached and never read the warnings.

The instruments get the same treatment. The clean `--trace-bindings` run over the folded panel
was believed only after a deliberately misspelt binding was added, the app rerun, and the
listener seen to write the error — because the tracer reports nothing at all when it is happy,
and "no file" is otherwise indistinguishable from "no listener attached".

**A silent failure is a bug even when the code is correct.** The rolling file sink was written,
reviewed and tested by eye — and never instantiated, so nothing reached disk while the log
panel made it look as though logging worked.

**Findings are written down with their evidence.** `docs/PHASE0-FINDINGS.md` records the
numbers, the failures and the dead ends — including the ones that reversed a plan decision.
