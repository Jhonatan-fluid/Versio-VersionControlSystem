# Versio — status and route to completion

**Date:** 2026-08-11 · **Commits:** 8 · **Tests:** 391 unit + 21 integration, 0 skipped

---

## Where the project actually is

The risky half is done. Every hard question the plan raised about CODESYS has been answered by measurement against a real project, and the domain logic that could lose someone's work is written and tested without needing CODESYS, git, a clock or a UI to exercise it.

The unfinished half is assembly: a shell exists and shows real git state, but it is not yet connected to the sync engine sitting behind it.

```
  CODESYS pipeline    ████████████████████  proven on a real project
  Domain core         ████████████████████  391 tests, 43 ms
  Infrastructure      ████████████████████  drives real CODESYS + real git
  Desktop shell       ████████░░░░░░░░░░░░  renders, not yet wired to sync
  Packaging           ██░░░░░░░░░░░░░░░░░░  publish profile only
```

### Proven by measurement, not assertion

| Question | Answer |
|---|---|
| Is the export deterministic? | **Yes** — 3 runs byte-identical across all 11 files, scrubbing `Timestamp` only |
| Does export → edit → import → export round-trip? | **Yes** for textual objects, proven with a deliberate mutation |
| Can native XML be imported without deleting the object? | **Yes** — `NativeImportHandler` replaces in place, GUID preserved |
| Can missing objects be created? | **Yes** — from a parse of the ST declaration header |
| Does the WPF tree survive a real project? | **Yes, but only when virtualized** — 94.7 ms vs 10,635 ms at the defaults |
| How long does a CODESYS launch cost? | **17–31 s.** This is why `On git action` is the default sync mode |

### Known limitations, carried deliberately

- **Task configuration is diff-only.** `import_native` changes a task's `Core2` from `-1` to `-2` unasked. Reproduced three times, pinned by a test, not masked.
- **Graphical POUs export as native XML**, so their diffs will never read as well as ST. Inherent to the format.
- **Interfaces, text lists, image pools** have export rules written and unit tested but never exercised — the fixture project contains none.
- **One fixture project.** A compound/safety project and a graphical-heavy one are the obvious gaps in the corpus.

---

## What remains, in dependency order

### 1. Wire the shell to the sync engine — *the next milestone*

Everything below it exists; this is assembly, not invention.

- Sync panel: direction-lock state in plain language, last export, `_coverage.md`
- Commit gate blocking a stale export, with a blocking progress dialog
- Heartbeat surfaced in the status bar during a 20-second CODESYS run
- `FileChangeWatcher` → `SyncOrchestrator` wiring
- Binding setup: adopt an orphaned project, generate a template

**This is the point at which Versio does what it was built to do.**

### 2. Complete the git surface

- Git side panel: stage/unstage, commit message box, pull/push/fetch, stash
- Branch creation, clone-with-verification dialog
- Per-hunk staging

### 3. Project tree replacing the flat file list

The manifest already carries GUID → path → type. The tree builder is specified in §9.3 and the virtualization is proven; what is missing is the builder itself and parsing `config/*.yaml` back into device nodes.

### 4. Diff view worth reading

Currently a read-only `TextBox`. Needs syntax highlighting for ST, side-by-side, and word-level intra-line diff. AvalonEdit is the specified route.

### 5. Packaging

- `third_party/` vendoring with SHA-256 pinning, offline restore
- Velopack: `Setup.exe`, MSI for managed sites, delta updates
- Code signing — **procurement lead time, not engineering time; start early**

### 6. Corpus breadth

More fixture projects. Every field bug becomes a fixture before it is fixed.

---

## Open decisions

| # | Question | Blocks |
|---|---|---|
| 1 | Who else runs this, and on what kind of machine? | Whether signing and an MSI are needed at all |
| 2 | Budget and legal entity for a code-signing certificate? | External distribution |
| 3 | Bundle PortableGit, or require Git for Windows? | Installer size and GPL compliance work |
| 4 | Where does the update feed live? | Phase 3 packaging |
| 5 | Is a **resident CODESYS session** worth building? | Whether automatic-on-save is ever viable |
| 6 | Can CODESYS run on a shared server? *(a licensing call, not engineering)* | PLC CI, the most valuable Phase 5 item |

---

## The disciplines that are load-bearing

Worth stating because they are what make the rest cheap, and they are easy to erode.

**`Versio.Core` touches nothing external.** No `System.IO`, no `Process`, no `DateTimeOffset.Now`, no UI framework. That is why the lock model, the loop defences and the state machine are tested in 43 milliseconds rather than by causing real crashes.

**Green results are distrusted until they can fail.** This has caught four separate vacuous passes: a round-trip that succeeded because the import silently did nothing, equivalence tests that "passed" by skipping, a tree spike that measured a collapsed tree, and a `Core2` mutation that looked absent because the import had errored. Every one now asserts that the measurement stressed something.

**Findings are written down with their evidence.** `docs/PHASE0-FINDINGS.md` records the numbers, the failures and the dead ends — including the ones that reversed a plan decision.
