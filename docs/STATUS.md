# Versio — status and route to completion

**Date:** 2026-08-12 · **Commits:** 24 · **Tests:** 694 unit + 90 integration, 0 skipped

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

```
  CODESYS pipeline    ████████████████████  proven on a real project
  Domain core         ████████████████████  644 tests, 290 ms
  Vendor seam         ████████████████████  enforced from assembly metadata
  Infrastructure      ████████████████████  drives real CODESYS + real git
  Desktop shell       ██████████████████░░  one project view, full git surface, diff
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

### Known limitations, carried deliberately

- **Library references are not exported at all.** `LIBRARY_MANAGER` is skipped, and the
  comment beside it promised a `config/libraries.yaml` that no code has ever written. Adding a
  library or bumping a version therefore changes nothing in the mirror, moves nothing through
  the hash gate and appears in no diff. **Now reported rather than silent** — the export writes
  `_coverage.md`, the manifest records every skip with its reason, and the panel says so in the
  one sentence that matters. Fixing it is the next real piece of work.
- **Task configuration is diff-only.** `import_native` changes a task's `Core2` from `-1` to
  `-2` unasked. Reproduced three times, pinned by a test, not masked.
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
| **Git panel** | Staging, commit message with a 50/72 guide, Ctrl+Enter, fetch / pull / push, stash, recent commits with click-to-diff. One operation at a time across every button — a shared counted flag, so a 17–31 s export also holds them |
| **Commit gate** | A stale mirror is exported *and staged* before the commit. Push releases the direction locks; commit does not, and the panel says so |
| **Diff view** | Eleven languages, new files rendered entirely green, *Show full file*, rows built only when realised, **per-hunk stage and unstage** |
| **PLC state** | Toolchain, direction lock, staleness and heartbeat, folded into the project panel as facts rather than controls. Two buttons survive: export what is stale, and acknowledge a failure |
| **Export coverage** | What the export does *not* carry, said out loud: `_coverage.md` in the tree, every skip in the manifest with a stable reason, and a sentence in the panel naming what cannot be relied on. A gap is distinguished from a deliberate omission, and an old manifest reports "unknown" rather than "complete" |
| **Chrome** | One app-wide progress bar with a caption, maximized on open, log console collapsed, heartbeat during long runs |
| **Logging** | Ring buffer, correlation ids, rolling file from startup, crash handlers on all three .NET channels |

---

## What remains, in dependency order

### 1. Stop treating "the IDE is open" as a failure

The most common working state there is — an engineer saves and leaves CODESYS open — currently
ends with the binding marked failed and nothing syncing until somebody presses a button. The
behaviour of CODESYS itself is now measured (it refuses cleanly in both directions, and nothing
is corrupted); what is missing is Versio's response to that refusal. See the finding below for
the measurements and the design.

**Export can be made to work anyway, and that is measured too:** the lock is a sidecar on the
path, the `.project` copies cleanly while held, and exporting the copy produces a byte-identical
tree. So export does not have to refuse at all.

- **Export:** copy the project to `.versio/local/` — **keeping the same filename**, because the
  manifest records the basename — and export the copy. `.versio/local/` is already gitignored,
  so the copy never reaches git or the panel, and the exporter's existing staging swap already
  puts the result in the right place
- **Import:** must still refuse. Even if CODESYS allowed the write, the IDE holds its own copy
  in memory and the next Ctrl+S would silently revert it
- Pre-flight `<project>.~u` rather than paying a 17–31 s launch to be told no, and probe the pid
  in it so a crashed IDE does not block sync forever
- A verdict that means *wait*, not *fail*: no failed binding, no circuit breaker. Retry when the
  lock file disappears — the watcher can see that happen, so closing CODESYS is the trigger and
  no button is needed. Offer one anyway for the case where the watcher missed it
- Behind the vendor seam — `.~u` is a CODESYS fact and `Sync/` may not learn it

### 2. Export what is currently missing

The coverage report now names it, which is the point at which it becomes a task rather than a
rumour. On the fixture: **two library managers, neither exported.**

- Walk the library manager through the ScriptEngine and write `config/libraries.yaml`
- A YAML writer has to be written by hand — `Versio.Core` has no third-party dependencies, and
  that is worth more than the few hundred lines this costs
- Parse it back into device nodes, so a changed library version appears under the device that
  owns it. This is the tree feature that was listed before anyone noticed the data did not exist

### 3. Finish the git surface

- ~~Stash~~, ~~recent commits with click-to-diff~~, ~~per-hunk staging~~ — **done**
- Branch creation, and the clone-with-verification dialog. `CreateBranchAsync` and
  `VerifyRemoteAsync` are both written and tested; neither has a surface
- Discard, with a confirmation. `DiscardAsync` exists and is deliberately unreachable until
  there is a dialog worth trusting in front of it

### 4. Finish the project tree

- A layer switcher in the diff pane (`ST | native`) — objects already carry several layers and
  there is no way to see the other one
- Context menu, and expansion state persisted per repository. It already survives a refresh;
  it does not yet survive the window closing

### 5. Diff view

Side-by-side mode, and word-level intra-line diff.

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

---

## Findings

Written down with their evidence, because the reasoning is worth more than the fix.

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
arrives as a failed run, and a failed binding stops syncing until somebody presses
*Acknowledge failure*. So the most common working state in the tool's whole life — the IDE
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

`Versio.exe --trace-bindings <file>` writes every WPF data-binding failure to a file. A broken
binding in WPF is otherwise **silent** — a mistyped path or a missing converter produces an
empty control and no error anywhere — which made it the one class of failure in this codebase
that was not loud. Every panel is verified clean through it, and the instrument itself was
proven able to fail before its clean result was believed.

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

**Green results are distrusted until they can fail.** Nine vacuous passes have been caught
this way — ten counting the determinism test that compared the one file with no GUIDs in it:
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
