# Versio — status and route to completion

**Date:** 2026-08-17 · **Commits:** `git rev-list --count HEAD` · **Tests:** 2,493 across seven
projects, 0 skipped — re-derived by running each, not carried forward

| Project | Tests | Time | Needs |
|---|---|---|---|
| `Versio.Core.Tests` | 1,381 | under a second | nothing |
| `Versio.Integration.Tests` | 147 | ~10 s | real git |
| `Versio.App.Tests` | 546 | ~50 s | nothing |
| `Versio.Arch.Tests` | 14 | instant | nothing |
| `Versio.Plc.Codesys.Pure.Tests` | 259 | under a second | nothing |
| `Versio.Plc.Codesys.Parity.Tests` | 92 | ~15 s | `python` on PATH — skips cleanly without it, so check the count |
| `Versio.Plc.Codesys.Tests` | 54 | **~30 min** | **real CODESYS** |

*The first six are the loop to work in — about thirty seconds plus the UI suite. The last is
the gate before a commit. Until 2026-08-17 the fast/slow split was a `--filter` expression over
one integration project; it is three project boundaries now, split by need — nothing / python /
the real toolchain. Of the 386 tests that sat behind the half-hour gate, 334 needed no
toolchain and surface in seconds now; the 52 that remain each launch real CODESYS, which is
the whole cost.*

> **⚠️ Versio removes, renames and moves objects in projects — Phases 2 and 3 of the import
> plan, and the first things it does that destroy work.** Delete a `.st` from the mirror,
> confirm the import, and the POU leaves the project. Read this before anything else in this
> file.
>
> **Renaming is the safe half of that pair and is still worth knowing about**: a file renamed
> in the mirror renames the *object*, keeping its GUID, and any task that ran it is repointed.
> What Versio does **not** fix is a reference held by name that it does not know about — a
> visualisation binding has never been measured. **Decided 2026-08-17: not pursued** — the
> owner does not use CODESYS visualisations, so the measurement is deliberately not taken.
> The sentence stays because the risk does: anyone renaming POUs in a project that *does* use
> visualisations should measure whether bindings hold by name before trusting a rename.
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
> [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md).

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
> findings, and [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md) for what P7 changed in the
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
> full-suite load** — see [`FINDINGS.md`](FINDINGS.md). It passes in isolation and failed once during a complete
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

**The CODESYS import plan is finished, as of 2026-08-17.** All twelve of its gaps are closed or
guarded and both of its remaining tasks are done — G8, where a managed library reference was
recording the exporting machine's installed version or nothing at all, and P11, which measured
which moves CODESYS actually performs and disproved the tidy rule its own first run produced.
What is left in [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md) is three
decisions and no work. **So the next thing to build is item 4 below — the project tree panel**,
which is also what `TaskCatalog.cs` and `LibraryCatalog.cs` are waiting for.

```
  CODESYS pipeline    ████████████████████  proven on a real project
  Domain core         ████████████████████  1,296 tests, under 400 ms
  Vendor seam         ████████████████████  enforced by the project graph + metadata
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
| Where does a fixed-version library's version come from? | **From the reference's own name, and not from `managed_library`.** That object exists, and every field on it raises `NullReferenceException` for a library the exporting machine does not have installed — while `is_managed` still reads `True`. What it answers when it does answer is *installation* state, which two engineers would disagree about. Measured both ways, 2026-08-17 |
| Which moves does CODESYS actually perform? | **Not the ones the destination would suggest.** An empty folder moves into `Plc Logic` or into another folder; a POU does not move into `Plc Logic`; the same folder stops moving there once a POU is inside it; a GVL moves to the project root. **Nine moves, six performed and three declined, and not one call raised** — so the import's read-back after every move is the only signal that exists. Measured 2026-08-17 (P11), twice: the first run's ninth step never executed and nobody read the row that said so — see the probe finding |
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
  [`phase1/IMPORT-PLAN.md`](phase1/IMPORT-PLAN.md).
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

**Widened 2026-08-16, because it was checking a third of what it claimed.** It read signatures
only, so three leaks living as strings sat under it untouched; and it read `Versio.Core` only,
so `Versio.Infrastructure` was never examined at all. It now reads constants and compiled string
literals as well, unwraps generic and array arguments, and `VendorSeamInfrastructureTests` covers
the second assembly. See [`FINDINGS.md`](FINDINGS.md).

**And the registration mechanism has now actually run.** `PlcFormats.Register` and
`PlcToolchains.Register` had zero callers repository-wide until `SecondVendorTests` and
`SecondVendorToolchainTests`, which stand up a second vendor, prove it is recognised beside
CODESYS, and cover the case both docstrings promise: a format registered with no toolchain, whose
projects are reported as unsupported rather than silently ignored. Both registries gained the
`Unregister` they had always lacked.

### The shell

| | |
|---|---|
| **Project view** | **One panel.** Every project *with something to review* — a change, a mirror behind, a lock held, a failure, or never exported — carrying its full CODESYS-shaped object tree, with every other changed file beside them. A project whose mirror is clean is not listed, and the empty state says so rather than going blank. Search still reaches into a clean project, because the box searches the project and not the diff. **Rows carry a context menu** (2026-08-17) — Stage, Discard through the git panel's own confirmed flow, Reveal in Explorer, Copy path — and **expansion survives the window now**, not only the refresh: `.versio/local/tree.json`, written when the repository detaches, defaults when unreadable |
| **Git panel** | **Per-file staging** — a checkbox on every row, tri-state headers, and labels that say what will happen (`Stage 3`, `Stage all`); both panes scroll and a splitter divides them. **One commit button with three behaviours** — it stages what you ticked, or everything, or nothing at all when you have staged deliberately, and the caption says which before you press. Commit message with a 50/72 guide and Ctrl+Enter. **Where the branch stands, in words** rather than two arrows — eleven states covering unborn, detached, no remote, never published, ahead, behind and diverged, each naming its remedy. **Publish** for a branch with no upstream, which a plain push cannot do. Fetch / pull / push / discard. **Branches show where they live** — local only, remote only, or both — and remote-only ones are listed at all for the first time. A delete dialog removes them from this machine and optionally the remote, naming what is not merged and reporting per branch when one refuses. **Conflicts get their own section** with keep-mine / keep-theirs and an abandon, and a diverged branch is offered a rebase or a merge by name — before this, the badge said "pull first" and a fast-forward-only pull refuses a diverged branch, so the only way out was a terminal. **Notifications** that survive the status refresh, with failures sticky until dismissed and git's own words on them. **Credentials**: detected at open, handed off to the engineer's own helper, never stored — and a *forget credential* for the expired token no git GUI lets you clear. **The remote is watched in the background**, by `ls-remote` rather than by fetching, so a colleague's push shows up without a click. **Git's own health**: a launch probe that names PATH when git is missing, and an identity preflight so a commit is refused before a 17–31 s export rather than after it. One operation at a time across every button — a shared counted flag. **Changed files are named as objects** — `FB_Motor` with a `POU` chip and the path beneath, resolved through the export manifest, with a lock glyph on a file whose edits cannot reach the project; a file the manifest does not describe stays a path and is never guessed at. **A change summary and a drafted commit subject** — *"1 GVL and 1 POU changed in RefrigeratorControl"*, and a **Draft** button that writes `Modify FB_Motor, Glob_Var`, both counting objects rather than files and both describing exactly what the commit button will record. **A merge-request button** for GitHub, GitLab, Azure DevOps, Bitbucket and Gitea: it opens the forge's own page pre-filled and submits nothing, upgrades itself to *Go to* when the forge's hidden refs say one already exists — no API token, nothing stored — and shows **nothing at all** for a host it cannot identify. **Stashes are a section that exists only while there are any** (2026-08-17, replacing the withdrawn six-permanent-controls design): Apply keeps the stash, Drop confirms by name and re-resolves the positional ref by SHA, and there is no save button — the auto-stash owns taking, and the deliberate case belongs to a terminal. See [`phase1/GIT-PANEL-PLAN.md`](phase1/GIT-PANEL-PLAN.md) |
| **History panel** | **Its own rail button**, not a strip inside the git panel. Every commit with its hash, local date and time, author and subject; expand one for the files it touched; click a file for what that commit did to it, in the shared diff pane. Lazy at four levels — the log is not walked until the panel is first shown, then one page of 50 at a time; a commit's files are read when it is expanded and cached for the session, because a commit's contents cannot change; a patch is read only when a file is clicked, and never cached. A merge says so rather than showing an empty list |
| **PLC state** | **No card, and no buttons.** Toolchain, version and profile were environment rather than repository state and moved to Settings → Help → System information. "Export now" is gone because a stale mirror needs no decision — it is exported when the repository opens. "Acknowledge failure" is gone as a panel-wide control: a failure belongs to one project, so it is reported on that project's row with the reason and a *Try again* beside it. What is left in the panel is transient and hides itself: the coverage warning and the live heartbeat |
| **Branch and clone** | Create a branch from HEAD and switch to it, with the name checked against git's rules while it is typed; clone a repository after verifying the remote, then open it. Both are modal dialogs built from the one stylesheet, and both are covered by tests that construct them against the real theme |
| **Commit gate** | A stale mirror is exported *and staged* before the commit. Push releases the direction locks; commit does not, and the panel says so |
| **Diff view** | Eleven languages, new files rendered entirely green, *Show full file*, rows built only when realised, **per-hunk stage and unstage** — **word-level emphasis inside a changed pair and a side-by-side mode** (2026-08-17): the changed identifier lights up inside the row tint, computed as a token-aligned prefix/suffix trim because the corpus is generated XML where a long line differs by one identifier; the split view folds the same parsed diff rather than computing a second one, so the two modes cannot disagree — and a **layer switcher** in the header (2026-08-17): one chip per layer of the shown object (`ST | native`, `tasks`, `libraries`), following the shown path rather than the tree selection, so the native half of a GVL and the export-only XML of a task configuration stopped being unreachable |
| **Open in the IDE** | Export runs anyway, from a copy; import waits and names who has the project. Neither marks the binding failed, and the breaker is never fed |
| **Adoption writes `.gitignore`** | Created when absent, appended when present, never overriding an existing decision. Covers `.versio/local/` — locks, backups, scratch copies — and the toolchain's `-AllUsers.opt` and `-<user>-<machine>.opt`, the second of which carries a username and machine name in its filename |
| **Export coverage** | What the export does *not* carry, said out loud: `_coverage.md` in the tree, every skip in the manifest with a stable reason, and a sentence in the panel naming what cannot be relied on. A gap is distinguished from a deliberate omission, from a file that is exported one way, and an old manifest reports "unknown" rather than "complete" |
| **Library references** | One `<manager>.libraries.yaml` per library manager, beside the object that owns it — what the project asks for, never how it resolved here. Hand-written YAML on both sides, no dependency added to either. **Export-only**, and stated where a reader will meet it: the file's own header, `_coverage.md`, the manifest's `form`, **and now the panel** — the changed-files row carries a lock glyph and a tooltip saying the edit does not reach the project. It went there rather than on the tree row (§9.3) because the row somebody is about to *stage* is where the warning has to be. `CoverageSummary` still speaks only when there are gaps, deliberately. **And the tree draws one row per reference** (2026-08-17): under its manager, version beside the name, chosen libraries before system ones — and against HEAD's copy of a changed catalog, the mark lands on the reference that moved, so a version bump reads as *that library*. **The task configuration gets the same treatment** the same day, through `TaskCatalog.cs` — one row per task in the configuration's own order, schedule and priority beside the name, a priority edit marked on that task — with the difference that the `.tasks.yaml` those rows read is the importable half, so the row is exactly what the next import applies |
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
| **Code signing** (open decision #2) | Nothing external is distributed yet. | **Start this before it is needed, not when.** It is procurement lead time rather than engineering time, and it is the only item on this page that gets worse purely by waiting. |

**Also decided, and more firmly: the code-shape queue is closed.** Staging, the repository
session and `SyncViewModel` were each measured and declined rather than deferred — see the queue
below and the finding. The difference matters: the four above are *later*, those three are *no*
unless something changes.

---

## What remains — moved to PHASE2.md

> **The forward queue lives in [`PHASE2.md`](PHASE2.md) as of 2026-08-17**, when Phase 1
> closed and its four planning documents were archived to [`phase1/`](phase1/). Items 1 and 2
> below are kept as records of work done; the old items 3–8 were superseded by PHASE2's queue
> and removed here rather than left to drift — one queue, one place, because a claim in two
> places is the defect this file's own history keeps demonstrating. The blocks below about the
> code-shape queue, `SAFEGUARDS.md` and `GIT-PANEL-PLAN.md` stay as dated records; their open
> remainders are PHASE2 queue items too.

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
> - **Staging.** `phase1/GIT-PANEL-PLAN.md`'s GP-E log measured this seam and rejected it — *"a move
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
> open — **costed 2026-08-17, in §5.H itself, and deliberately not built**: the hard part is
> classifying a mid-run toolchain failure against a vanished share as environmental rather
> than feeding the circuit breaker, and that classification should not be reshaped in
> passing. §0 is the handover note.
>
> **[`phase1/GIT-PANEL-PLAN.md`](phase1/GIT-PANEL-PLAN.md)** — the git panel's functionality and UX.
> **Eleven of its twelve phases are built**: the git presence probe and identity preflight,
> notifications, the remote state machine and publish, credentials, background remote polling,
> per-file staging, the one commit button, conflicts and the way out of a divergence, branches
> and their deletion, and — both on 2026-08-14 — **PLC-aware naming**, which is the item that
> document itself ranks highest and the one no generic git client can have, and the
> **merge-request button** across five forges. ~~**What is left** is GP-K/GP-L: the ahead/behind
> previews and the rest of the small UX list~~ — **built 2026-08-17, which closes the plan at
> twelve of twelve**: the ↑/↓ badges are buttons opening commit previews (the incoming one
> naming the objects a pull would touch, through the same index the summary uses), a large
> staged file with no LFS rule is asked about before the commit rather than at the push that
> rejects it, a clean tree is one sentence instead of two empty boxes, the branch card names
> where push goes, the subject counter colours itself at 50 and 72, and Ctrl+Shift+P pushes.
> See PHASE2.md item 2 — including the two §10 items that turned out to be already built.
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
- ~~**Still open:** one tree node **per library**, so a version bump reads as *that library*
  rather than as "the library manager changed"~~ — **done, 2026-08-17, first item of PHASE2's
  queue.** One row per reference under its manager, chosen libraries sorted before system
  ones, and — because the shell fetches HEAD's copy of a changed catalog through
  `ShowAtHeadAsync` — the mark lands on exactly the reference whose pin moved: bumped is `M`,
  added is `A`, and a reference deleted from the catalog still gets a row, marked `D`, from
  the baseline's copy of it

### 2. ~~Finish the git surface~~ — **done**

- ~~Recent commits with click-to-diff~~, ~~per-hunk staging~~ — **done**. History has since
  moved to a **panel of its own** with per-file diffs and lazy loading throughout; see the
  finding on what the old list cost on every refresh
- ~~**Stash is withdrawn from the UI**, not finished~~ — **rebuilt 2026-08-17** as a section that exists only while `git stash list` has rows; see the git panel row above. The record below stands as what was withdrawn and why. `StashPushAsync` / `StashApplyAsync` /
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

*(Items 3–8 lived here until 2026-08-17; their live versions, reordered by the owner and with
the second vendor and packaging deliberately last, are [`PHASE2.md`](PHASE2.md)'s queue. The
old text is in git history.)*

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

- ~~**Launching the app against this repository adopts the 21 `phase0/` scratch projects and
  starts exporting all of them.**~~ — **closed 2026-08-17, and the closing found a defect.**
  `.versio/repo.json` has always had an `ignore` array that `Adoption` and `OrphanScanner` both
  honour, and it was empty. `phase0/**` is in it now, along with the deliberately frozen
  `MachineWithMembers` fixture and its mirror. Opening this repository is quiet: **23 projects
  seen, 0 findings, 1 watched, nothing exported, zero `ERR`**.
  - **It cost the frozen fixture twice before it was closed**, both times in a launch done to
    check something else — the second time while the owner was looking at the panel.
  - **And `phase0/**` alone only took 56 findings to 43.** The ignore list was filtering orphaned
    *projects* and not orphaned *export trees*, so every ignored project's mirror was still
    reported. See the Finding: half a rule, and nothing in the suite could have caught it because
    the existing test only ever supplied `ProjectFiles`.
- **`RefrigeratorControl.project` is both the gate's fixture and the owner's daily working
  project, and the gate cannot survive that.** Three consecutive 32-minute runs on 2026-08-18
  each measured a *different* project: it went from 26 objects to 45 to 48 while the runs were
  in flight, `FB_Alarm` moved into `Library/Alarm/POUs`, and a method the tests named by hand
  was renamed between two of them. Six tests went red and not one of them was a regression.
  - **Half the answer is already applied**: those tests now assert rules over the whole
    manifest rather than named paths and counts — `EveryMemberLivesInsideItsOwner` checks the
    invariant over every recorded member and insists there are some, and the export-only count
    is derived from the manifest instead of typed in. That is better testing regardless of
    who is editing what.
  - **The other half is a decision nobody has taken**: either the fixture is frozen the way
    `MachineWithMembers` is and the owner's real work moves to a project outside `fixtures/`,
    or the gate accepts that it can only be believed when the IDE is closed. Today it was the
    second, by asking. `walked N` literals in `ExportLoggingTests` are the remaining hostages —
    they have been re-typed three times in two days.
- **Regenerating the committed fixture mirror by hand has three arguments that are all easy
  to get wrong, and no coverage.** It is done through `tools/codesys/Run-Codesys.ps1` when the
  exporter's output shape changes, and on 2026-08-17 the third one was got wrong and shipped:
  1. `-Profile` is mandatory (`--noUI` refuses without it) and **its name is not the install
     folder's** — read it from `…\CODESYS <ver>\CODESYS\Profiles\*.profile.xml`.
  2. `out` must be the **real mirror path**, not a temp directory. `preserve_equivalent`
     compares against the bytes already there to suppress the native-XML churn that never
     settles otherwise; exporting elsewhere and copying back puts a diff nobody made into
     several `.xml` files.
  3. `bindingId` must be **the id `.versio/repo.json` actually binds that directory with**
     (`6d3f8855e205`), not a readable placeholder. Passing `"fixture"` produced a
     `ManifestMismatch` finding on the next launch — which is the safeguard working exactly as
     intended, and was the *only* thing that caught it: the whole suite was green, because
     every test builds its own binding and none reads the committed `repo.json`.

  The owner usually has the project open in CODESYS, so the export must run against a **copy**
  of the `.project` — into the real `out` above — rather than closing their IDE.
- **A *graphical* member inside an in-POU folder is imported into the POU's root, not back
  into the folder.** Found on 2026-08-17 while fixing the foldered-member export, by reading
  `handle_native` rather than by any test. The textual path is fully covered — it resolves
  the object itself, folder-blind, and updates it in place — but `handle_native` imports into
  the *owner* it computed from the chain, which is the POU, and `import_native` both replaces
  and creates. Whether CODESYS matches the incoming GUID across the folder boundary and
  replaces in place, or pastes a second copy, is **unmeasured**. Narrow (a method is normally
  ST, and a duplicate would be visible rather than silent), so it is written down rather than
  fixed on an unverified guess. Measuring it needs one probe and a gate run.
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
- **~~Three~~ Two of the seven C#/python pairs are still compared by nothing** —
  `NativeXmlScrubber` ↔ `scrub_native_xml` and `StDeclarationParser` ↔ `parse_declaration`.
  ~~`StTextCodec` is the one that matters: a bug in `split_st` silently moves code between a
  declaration and an implementation, and it is a hand-maintained copy.~~ — **closed
  2026-08-16, and it mattered exactly as much as this said**: the parity test found a
  divergence on its first run, worth a renamed POU's GUID. See the Finding.
  **This is not theoretical** — [`CODESYS.md`](vendors/codesys/DRIVING.md) §9 counts four drifts across the
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
    names what will really go and what will really stay, separately — see [`FINDINGS.md`](FINDINGS.md).
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
  `phase0/roundtrip/baseline`, written by `tools/codesys/Test-RoundTrip.ps1` - was reported with the
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
  blanked the history panel, with every test green — see [`FINDINGS.md`](FINDINGS.md).
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

Every defect worth understanding, written down with its evidence, lives in
**[`FINDINGS.md`](FINDINGS.md)** — 107 of them, newest first. (Said 99 until 2026-08-17,
when it was re-derived rather than incremented: the number had gone stale during the split
itself, which is the one edit that could not have been an oversight.)

It moved out on 2026-08-17. It was 3,681 lines of this document's 4,782, so the file that
answers *where is the project now* was 77% engineering log, and `CLAUDE.md`'s opening
instruction to read these before starting had stopped being something anybody could
actually do.

The full list, newest first:

- [Properties export as readable text perfectly, and the round trip empties every one](FINDINGS.md#properties-export-as-readable-text-perfectly-and-the-round-trip-empties-every-one)
- [An import destroyed a method, and the summary said seven native, zero warnings](FINDINGS.md#an-import-destroyed-a-method-and-the-summary-said-seven-native-zero-warnings)
- [Ownership was spelled three ways in one tree, and the panel could only read one](FINDINGS.md#ownership-was-spelled-three-ways-in-one-tree-and-the-panel-could-only-read-one)
- [A folder inside a POU stripped the owner off every method in it](FINDINGS.md#a-folder-inside-a-pou-stripped-the-owner-off-every-method-in-it)
- [A process parked inside the mirror made every export fail at the swap](FINDINGS.md#a-process-parked-inside-the-mirror-made-every-export-fail-at-the-swap)
- [The project row stuck on "exporting…", and the await that widened a race until it hit](FINDINGS.md#the-project-row-stuck-on-exporting-and-the-await-that-widened-a-race-until-it-hit)
- [The expansion default missed the correction the pruner already carried](FINDINGS.md#the-expansion-default-missed-the-correction-the-pruner-already-carried)
- [`import_native` duplicates an external-file object instead of replacing it](FINDINGS.md#import_native-duplicates-an-external-file-object-instead-of-replacing-it)
- [The probe that caught two of its own lies was carrying a third, and three documents believed it](FINDINGS.md#the-probe-that-caught-two-of-its-own-lies-was-carrying-a-third-and-three-documents-believed-it)
- [`move()` declines three moves of nine, raises for none, and the destination is not what decides](FINDINGS.md#move-declines-three-moves-of-nine-raises-for-none-and-the-destination-is-not-what-decides)
- [A managed library reference recorded whichever version this machine had installed](FINDINGS.md#a-managed-library-reference-recorded-whichever-version-this-machine-had-installed)
- [Four tool scripts pointed one directory above where they now live](FINDINGS.md#four-tool-scripts-pointed-one-directory-above-where-they-now-live)
- [The ignore list hid the projects and kept every one of their mirrors](FINDINGS.md#the-ignore-list-hid-the-projects-and-kept-every-one-of-their-mirrors)
- [The guard I wrote for the seam was weaker than the claim I made for it](FINDINGS.md#the-guard-i-wrote-for-the-seam-was-weaker-than-the-claim-i-made-for-it)
- [A `git mv` moves a test; it does not move what the test reads](FINDINGS.md#a-git-mv-moves-a-test-it-does-not-move-what-the-test-reads)
- [The architecture tests PLAN promised for months had never been written](FINDINGS.md#the-architecture-tests-plan-promised-for-months-had-never-been-written)
- [The tag that split the suite was doing a project boundary's job](FINDINGS.md#the-tag-that-split-the-suite-was-doing-a-project-boundarys-job)
- [The engine reached for a global registry, and no seam test could see it](FINDINGS.md#the-engine-reached-for-a-global-registry-and-no-seam-test-could-see-it)
- [The vendor seam is a project boundary now, not a namespace and a reflection test](FINDINGS.md#the-vendor-seam-is-a-project-boundary-now-not-a-namespace-and-a-reflection-test)
- [The registries were constants with a mutator bolted on](FINDINGS.md#the-registries-were-constants-with-a-mutator-bolted-on)
- [One table held two vocabularies, and the next vendor would have inherited both](FINDINGS.md#one-table-held-two-vocabularies-and-the-next-vendor-would-have-inherited-both)
- [The manifest could not say who wrote it, and its vocabulary belongs to somebody](FINDINGS.md#the-manifest-could-not-say-who-wrote-it-and-its-vocabulary-belongs-to-somebody)
- [PlcVendor was a closed enum, so you could not name what you were told to register](FINDINGS.md#plcvendor-was-a-closed-enum-so-you-could-not-name-what-you-were-told-to-register)
- [The seam test passed for a year because a string is not a signature](FINDINGS.md#the-seam-test-passed-for-a-year-because-a-string-is-not-a-signature)
- [Register had no callers, and no way back](FINDINGS.md#register-had-no-callers-and-no-way-back)
- [The cap that said it bounded the path bounded one segment, and was off by seven](FINDINGS.md#the-cap-that-said-it-bounded-the-path-bounded-one-segment-and-was-off-by-seven)
- [An export that lost an object reported full coverage, and Warnings had no reader](FINDINGS.md#an-export-that-lost-an-object-reported-full-coverage-and-warnings-had-no-reader)
- [The two ST codecs disagreed about four characters, and a rename is what it would have cost](FINDINGS.md#the-two-st-codecs-disagreed-about-four-characters-and-a-rename-is-what-it-would-have-cost)
- [A GVL's declaration is applied and then overwritten by its own XML, and the log says it worked](FINDINGS.md#a-gvls-declaration-is-applied-and-then-overwritten-by-its-own-xml-and-the-log-says-it-worked)
- ["References and task assignments survive" was an inference, and it is false](FINDINGS.md#references-and-task-assignments-survive-was-an-inference-and-it-is-false)
- [A rename that renames nothing was reported as a rename — and the test that found it was written to assert the opposite](FINDINGS.md#a-rename-that-renames-nothing-was-reported-as-a-rename--and-the-test-that-found-it-was-written-to-assert-the-opposite)
- [move() accepted an instruction, did nothing, and reported success](FINDINGS.md#move-accepted-an-instruction-did-nothing-and-reported-success)
- [The delete path explained itself to nobody — three channels, three different silences](FINDINGS.md#the-delete-path-explained-itself-to-nobody--three-channels-three-different-silences)
- [The confirmation dialog told people their objects would stay, and then deleted them](FINDINGS.md#the-confirmation-dialog-told-people-their-objects-would-stay-and-then-deleted-them)
- [Closing G7 and G11 re-opened the gap _coverage.md was built to close](FINDINGS.md#closing-g7-and-g11-re-opened-the-gap-_coveragemd-was-built-to-close)
- [Two bugs that only running the exporter could show](FINDINGS.md#two-bugs-that-only-running-the-exporter-could-show)
- [Every import after the first fails on task configuration, and reports success](FINDINGS.md#every-import-after-the-first-fails-on-task-configuration-and-reports-success)
- [An import's summary was logged with the exporter's field names, and four of its events with nothing at all](FINDINGS.md#an-imports-summary-was-logged-with-the-exporters-field-names-and-four-of-its-events-with-nothing-at-all)
- [Three moves, three deleted subscriptions, three green suites](FINDINGS.md#three-moves-three-deleted-subscriptions-three-green-suites)
- [The queue said staging; the evidence said the remote surface](FINDINGS.md#the-queue-said-staging-the-evidence-said-the-remote-surface)
- [The first collaborator out of the shell, and the seam nothing was watching](FINDINGS.md#the-first-collaborator-out-of-the-shell-and-the-seam-nothing-was-watching)
- [The log said the lock was taken and never that it was given back](FINDINGS.md#the-log-said-the-lock-was-taken-and-never-that-it-was-given-back)
- [The disposal rule could not reach the one thing it most needed to](FINDINGS.md#the-disposal-rule-could-not-reach-the-one-thing-it-most-needed-to)
- [A dropdown centred in its own column, half a bar away from its buttons](FINDINGS.md#a-dropdown-centred-in-its-own-column-half-a-bar-away-from-its-buttons)
- [The history did not say which commits were still only on this machine](FINDINGS.md#the-history-did-not-say-which-commits-were-still-only-on-this-machine)
- [A UI audit, and what it says about the ones that got through](FINDINGS.md#a-ui-audit-and-what-it-says-about-the-ones-that-got-through)
- [Keyboard focus was invisible in every control in the application](FINDINGS.md#keyboard-focus-was-invisible-in-every-control-in-the-application)
- [A horizontal scrollbar was arranged ten pixels wide, and three other things on the same axis](FINDINGS.md#a-horizontal-scrollbar-was-arranged-ten-pixels-wide-and-three-other-things-on-the-same-axis)
- [Every settings write reset every other setting](FINDINGS.md#every-settings-write-reset-every-other-setting)
- [A StaticResource that compiled clean and threw on the first render](FINDINGS.md#a-staticresource-that-compiled-clean-and-threw-on-the-first-render)
- [A status light that could only be right by accident](FINDINGS.md#a-status-light-that-could-only-be-right-by-accident)
- [The four dialogs that were still the operating system's](FINDINGS.md#the-four-dialogs-that-were-still-the-operating-systems)
- [Selecting diff lines did nothing, and copying them did nothing](FINDINGS.md#selecting-diff-lines-did-nothing-and-copying-them-did-nothing)
- [Icon-only controls announced Private Use Area codepoints](FINDINGS.md#icon-only-controls-announced-private-use-area-codepoints)
- [A shared row template learned about objects, and blanked the panel that shared it](FINDINGS.md#a-shared-row-template-learned-about-objects-and-blanked-the-panel-that-shared-it)
- [The same repository, twice in the dropdown, because git and Windows spell paths differently](FINDINGS.md#the-same-repository-twice-in-the-dropdown-because-git-and-windows-spell-paths-differently)
- [A rule added after a repository was adopted never reached it](FINDINGS.md#a-rule-added-after-a-repository-was-adopted-never-reached-it)
- [The mirror arrives with the wrong line endings, and git says it is fine](FINDINGS.md#the-mirror-arrives-with-the-wrong-line-endings-and-git-says-it-is-fine)
- [The auto-stash swallowed its own record, and .gitignore could not have saved it](FINDINGS.md#the-auto-stash-swallowed-its-own-record-and-gitignore-could-not-have-saved-it)
- [The IDE's heartbeat was a git status every five seconds, forever — and it took two fixes](FINDINGS.md#the-ides-heartbeat-was-a-git-status-every-five-seconds-forever--and-it-took-two-fixes)
- [An export woke the change detector once per file it had just written](FINDINGS.md#an-export-woke-the-change-detector-once-per-file-it-had-just-written)
- [The repository dropdown could not do the one thing it exists for](FINDINGS.md#the-repository-dropdown-could-not-do-the-one-thing-it-exists-for)
- [One field's type disagreed, and the panel lost fifty-six objects without saying so](FINDINGS.md#one-fields-type-disagreed-and-the-panel-lost-fifty-six-objects-without-saying-so)
- [A function block's methods were drawn as its siblings, and four InitRooms sat in one folder](FINDINGS.md#a-function-blocks-methods-were-drawn-as-its-siblings-and-four-initrooms-sat-in-one-folder)
- [Two of the three panels used half the card they sat in, because Dock defaults to Left](FINDINGS.md#two-of-the-three-panels-used-half-the-card-they-sat-in-because-dock-defaults-to-left)
- [Two things overlapping, and a test suite that could not see either](FINDINGS.md#two-things-overlapping-and-a-test-suite-that-could-not-see-either)
- [An editor that corrupted every em-dash in a file it was only reformatting](FINDINGS.md#an-editor-that-corrupted-every-em-dash-in-a-file-it-was-only-reformatting)
- [The stylesheet's own argument, not applied to four controls](FINDINGS.md#the-stylesheets-own-argument-not-applied-to-four-controls)
- [A top bar crowded into its left third, with two control heights in one row](FINDINGS.md#a-top-bar-crowded-into-its-left-third-with-two-control-heights-in-one-row)
- [The app themed everything except the strip Windows draws, and every tooltip in it](FINDINGS.md#the-app-themed-everything-except-the-strip-windows-draws-and-every-tooltip-in-it)
- [Twenty seconds of work with nothing on screen, and four separate reasons for it](FINDINGS.md#twenty-seconds-of-work-with-nothing-on-screen-and-four-separate-reasons-for-it)
- [Two loop defences that had never actually run](FINDINGS.md#two-loop-defences-that-had-never-actually-run)
- ["Cancel and restart" cancelled nothing, and ran two CODESYS instances instead](FINDINGS.md#cancel-and-restart-cancelled-nothing-and-ran-two-codesys-instances-instead)
- [The history was re-read on every refresh, into a panel four rows tall](FINDINGS.md#the-history-was-re-read-on-every-refresh-into-a-panel-four-rows-tall)
- [A window that never opened, and three instruments that called it clean](FINDINGS.md#a-window-that-never-opened-and-three-instruments-that-called-it-clean)
- [The same blind spot again, in the dialog added after it was written up](FINDINGS.md#the-same-blind-spot-again-in-the-dialog-added-after-it-was-written-up)
- [An idle log that was mostly its own noise](FINDINGS.md#an-idle-log-that-was-mostly-its-own-noise)
- [The CODESYS lock marker was committed, and the ignore rule had guessed its name](FINDINGS.md#the-codesys-lock-marker-was-committed-and-the-ignore-rule-had-guessed-its-name)
- [A panel about changed files, opening with a version number and two dead buttons](FINDINGS.md#a-panel-about-changed-files-opening-with-a-version-number-and-two-dead-buttons)
- [Recovery forgot what had been exported, and it cost a toolchain run every launch](FINDINGS.md#recovery-forgot-what-had-been-exported-and-it-cost-a-toolchain-run-every-launch)
- [The log said one time in the panel and another in the file](FINDINGS.md#the-log-said-one-time-in-the-panel-and-another-in-the-file)
- [The theme switch changed a colour nothing was looking at](FINDINGS.md#the-theme-switch-changed-a-colour-nothing-was-looking-at)
- [A diagnostic that verified an empty window](FINDINGS.md#a-diagnostic-that-verified-an-empty-window)
- [A project with nothing to review stayed on screen](FINDINGS.md#a-project-with-nothing-to-review-stayed-on-screen)
- [The UI had no automated coverage, and the first test written found a bug](FINDINGS.md#the-ui-had-no-automated-coverage-and-the-first-test-written-found-a-bug)
- [async void handlers and a test host that aborted](FINDINGS.md#async-void-handlers-and-a-test-host-that-aborted)
- [Validating a branch name is about the message, not the verdict](FINDINGS.md#validating-a-branch-name-is-about-the-message-not-the-verdict)
- [The library reference id changes on every open, and the API is not the one it looks like](FINDINGS.md#the-library-reference-id-changes-on-every-open-and-the-api-is-not-the-one-it-looks-like)
- [A test that passed on broken code and failed on correct code](FINDINGS.md#a-test-that-passed-on-broken-code-and-failed-on-correct-code)
- [A log test that broke because the export got better](FINDINGS.md#a-log-test-that-broke-because-the-export-got-better)
- [The export's own narrative never reached the log](FINDINGS.md#the-exports-own-narrative-never-reached-the-log)
- [Recording a GUID broke export determinism, hours after it was added](FINDINGS.md#recording-a-guid-broke-export-determinism-hours-after-it-was-added)
- [Exporting a copy works, and the lock is on the path](FINDINGS.md#exporting-a-copy-works-and-the-lock-is-on-the-path)
- [.versio/local/ was gitignored in three comments and nowhere else](FINDINGS.md#versiolocal-was-gitignored-in-three-comments-and-nowhere-else)
- [What CODESYS actually leaves beside a project, and why it belongs in .gitignore](FINDINGS.md#what-codesys-actually-leaves-beside-a-project-and-why-it-belongs-in-gitignore)
- [Working while the IDE has the project open](FINDINGS.md#working-while-the-ide-has-the-project-open)
- [A project open in the CODESYS IDE — measured, and it is the normal case](FINDINGS.md#a-project-open-in-the-codesys-ide--measured-and-it-is-the-normal-case)
- [A comment claimed a file nobody wrote](FINDINGS.md#a-comment-claimed-a-file-nobody-wrote)
- [Adding a file to the export tree broke the importer, and no test noticed](FINDINGS.md#adding-a-file-to-the-export-tree-broke-the-importer-and-no-test-noticed)
- [The integration suite had been lucky about running two CODESYS instances](FINDINGS.md#the-integration-suite-had-been-lucky-about-running-two-codesys-instances)
- [git apply --cached reads the index, not the working tree](FINDINGS.md#git-apply---cached-reads-the-index-not-the-working-tree)
- [Lazy tree rows are proportional to what is reachable, not to nothing](FINDINGS.md#lazy-tree-rows-are-proportional-to-what-is-reachable-not-to-nothing)
- [A view that rebuilds on every refresh drops the selection](FINDINGS.md#a-view-that-rebuilds-on-every-refresh-drops-the-selection)
- [A file under an export tree that the manifest does not describe](FINDINGS.md#a-file-under-an-export-tree-that-the-manifest-does-not-describe)
- [The hash gate could not see a text edit](FINDINGS.md#the-hash-gate-could-not-see-a-text-edit)
- [The vendor seam was documented but not true](FINDINGS.md#the-vendor-seam-was-documented-but-not-true)
- [The diff view's virtualization was decorative](FINDINGS.md#the-diff-views-virtualization-was-decorative)
- [CreateNoWindow does not hide a GUI child](FINDINGS.md#createnowindow-does-not-hide-a-gui-child)
- [A wrong diagnosis, and the vacuous check that confirmed it](FINDINGS.md#a-wrong-diagnosis-and-the-vacuous-check-that-confirmed-it)
- [objectCount in the manifest is a file count](FINDINGS.md#objectcount-in-the-manifest-is-a-file-count)
- [The repository dropdown was blank over a list of one](FINDINGS.md#the-repository-dropdown-was-blank-over-a-list-of-one)
- [Fifty-six shells nobody disposed, and a suite that failed at random](FINDINGS.md#fifty-six-shells-nobody-disposed-and-a-suite-that-failed-at-random)
- [A flaky watcher test, and two fixes that made it worse](FINDINGS.md#a-flaky-watcher-test-and-two-fixes-that-made-it-worse)
- [Committed configuration has to be byte-stable](FINDINGS.md#committed-configuration-has-to-be-byte-stable)
- [A vacuous check, caught in the act](FINDINGS.md#a-vacuous-check-caught-in-the-act)
- [The fake filesystem was not a filesystem](FINDINGS.md#the-fake-filesystem-was-not-a-filesystem)

---

## Verifying the UI, not just the engine

Two instruments, covering different halves of the problem.

**`Versio.App.Tests` constructs the real windows against the real stylesheet.** A WPF test
project on an STA thread with `Theme.xaml` loaded by pack URI from the app assembly, so a style
renamed in the stylesheet and not in a dialog fails the build rather than the first click. It
covers what the tracer cannot: a modal nobody has opened has never been parsed. 480 tests; a deliberately broken style reference fails the dialog tests outright.

**Applied and MEASURED, not looked up — that distinction has now earned itself twice.** A style
key can hold a template that resolves fine and throws the first time something renders it, which
is what happened to `Motion.Fast` (see [`FINDINGS.md`](FINDINGS.md)), and a control can carry a perfectly valid
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
being true. The test itself then had to be widened for the same reason: it read signatures and
one assembly, and was green while three brands sat in neutral namespaces as strings.

**Green results are distrusted until they can fail.** Sixteen vacuous passes have been caught
this way — most recently the vendor seam test itself, which read signatures and was green while
`CommitGate`, `OrphanScanner` and `LogCategory` each named CODESYS in a namespace forbidden to,
and before that an empty `--trace-bindings` file read as a clean UI while the window was
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

**Findings are written down with their evidence.** `docs/phase1/PHASE0-FINDINGS.md` records the
numbers, the failures and the dead ends — including the ones that reversed a plan decision.
