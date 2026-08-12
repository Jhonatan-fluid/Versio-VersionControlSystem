# Versio — status and route to completion

**Date:** 2026-08-12 · **Commits:** 20 · **Tests:** 644 unit + 65 integration, 0 skipped

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
  Desktop shell       ████████████████░░░░  git, diff, tree, automatic sync
  Packaging           ██░░░░░░░░░░░░░░░░░░  icon and publish profile only
```

### Proven by measurement, not assertion

| Question | Answer |
|---|---|
| Is the export deterministic? | **Yes** — 3 runs byte-identical across all 11 files, scrubbing `Timestamp` only |
| Does export → edit → import → export round-trip? | **Yes** for textual objects, proven with a deliberate mutation |
| Can native XML be imported without deleting the object? | **Yes** — `NativeImportHandler` replaces in place, GUID preserved |
| Can missing objects be created? | **Yes** — from a parse of the ST declaration header |
| Does the WPF tree survive a real project? | **Yes, but only when virtualized** — 94.7 ms vs 10,635 ms at the defaults |
| How long does a CODESYS launch cost? | **17–31 s.** Paid on every automatic export; the coalescer is what makes that bearable |
| Does an edited `.st` reach the project? | **Yes** — export, edit, import, verified byte-changed on the real fixture |

### Known limitations, carried deliberately

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
| **Git panel** | Staging, commit message with a 50/72 guide, Ctrl+Enter, fetch / pull / push. One operation at a time across every button — a shared counted flag, so a 17–31 s export also holds them |
| **Commit gate** | A stale mirror is exported *and staged* before the commit. Push releases the direction locks; commit does not, and the panel says so |
| **Project tree** | The project as CODESYS structures it. One node per object with its layers, roll-up badges over descendants, changed-only filter, search, selection driving the diff |
| **Diff view** | Eleven languages, new files rendered entirely green, *Show full file*, rows built only when realised |
| **Changed files** | Follows the working tree by itself; rows reconciled in place so the selection survives |
| **Chrome** | One app-wide progress bar with a caption, maximized on open, log console collapsed, heartbeat during long runs |
| **Logging** | Ring buffer, correlation ids, rolling file from startup, crash handlers on all three .NET channels |

---

## What remains, in dependency order

### 1. Fold the sync panel into the changed-files view

The panel is now a control surface for something that no longer needs controlling. The intended
shape:

- The changed-files panel is the only view; the sync panel disappears from the rail
- A `.project` row does not render a diff — it reports that its export succeeded
- Its export directory renders as a **full** CODESYS-shaped tree: every object, with the
  changed ones highlighted, rather than the changed-only filter that exists today
- Objects render **lazily** — nothing built until the user opens it

`TreeRow` currently builds its whole subtree in its constructor, so lazy expansion is a real
change rather than a flag.

### 2. Finish the git surface

- Stash, and the recent-commits list with click-to-diff
- Branch creation, clone-with-verification dialog
- Per-hunk staging — needs the diff view to own a selection first

### 3. Finish the project tree

- `config/*.yaml` parsed back into device nodes, so a changed EtherCAT cycle time appears under
  the device that owns it rather than as "config/devices.yaml modified"
- A layer switcher in the diff pane (`ST | native`) — objects already carry several layers and
  there is no way to see the other one
- Context menu, and expansion state persisted per repository

### 4. Diff view

Side-by-side mode, and word-level intra-line diff.

### 5. A second vendor

The seam is ready and unproven. The first real implementation is the only thing that will show
whether `PlcJob` and `PlcResult` are the right shape — TwinCAT is the cheapest test of that,
being CODESYS-derived, and Studio 5000 the harshest.

### 6. Packaging

- ~~Application icon~~ — **done.** `assets/versio.ico`, 8 sizes, generated by
  `tools/New-VersioIcon.ps1` and embedded via `<ApplicationIcon>`
- `third_party/` vendoring with SHA-256 pinning, offline restore
- Velopack: `Setup.exe`, MSI for managed sites, delta updates
- Code signing — **procurement lead time, not engineering time; start early**

### 7. Corpus breadth

More fixture projects. Every field bug becomes a fixture before it is fixed.

---

## Findings

Written down with their evidence, because the reasoning is worth more than the fix.

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
measurement used `grep -c $''` and reported that all 34 lines of a `.st` file contained a
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

**Green results are distrusted until they can fail.** Seven vacuous passes have been caught
this way: a round-trip that succeeded because the import silently did nothing; equivalence tests
that "passed" by skipping; a tree spike that measured a collapsed tree; a `Core2` mutation that
looked absent because the import had errored; a scenario suite that simulated text edits by
rewriting the one file no human touches; a shell check that reported success because a
process started rather than because it did its job; and a line-ending measurement whose pattern
matched every line rather than the ones it claimed to count.

**A silent failure is a bug even when the code is correct.** The rolling file sink was written,
reviewed and tested by eye — and never instantiated, so nothing reached disk while the log
panel made it look as though logging worked.

**Findings are written down with their evidence.** `docs/PHASE0-FINDINGS.md` records the
numbers, the failures and the dead ends — including the ones that reversed a plan decision.
