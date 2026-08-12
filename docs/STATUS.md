# Versio — status and route to completion

**Date:** 2026-08-12 · **Commits:** 12 · **Tests:** 644 unit + 64 integration, 0 skipped

---

## Where the project actually is

The risky half is done. Every hard question the plan raised about CODESYS has been answered by measurement against a real project, and the domain logic that could lose someone's work is written and tested without needing CODESYS, git, a clock or a UI to exercise it.

**Versio now does the thing it was built to do.** A project can be tracked from the panel and exported to its reviewable mirror, and git sees the result — proven end to end against the real fixture project in a real repository: **11 files written in 16.7 s**, the direction lock taken and persisted, and a second export correctly refused by the hash gate.

**Both directions now run from the panel.** A project can be tracked, exported to its reviewable mirror, edited as text, and imported back into the `.project` — proven end to end against the real fixture with real CODESYS. The unfinished half is the rest of the assembly: change detection is not yet connected to the orchestrator, and the commit gate is not wired to a commit action because there is not one yet.

```
  CODESYS pipeline    ████████████████████  proven on a real project
  Domain core         ████████████████████  644 tests, 290 ms
  Infrastructure      ████████████████████  drives real CODESYS + real git
  Desktop shell       █████████████████░░░  adopt, export and import wired end to end
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

### 1. Wire the shell to the sync engine — *in progress*

Everything below it exists; this is assembly, not invention.

**Done.** The PLC sync panel is real. It probes the toolchain (install + profile, reporting
absence rather than throwing), scans the repository for PLC projects, classifies each against
`.versio/repo.json`, and lists orphan findings that need attention. Templates are excluded so
they cannot be adopted by accident. `RepoScanner` and `RepoConfigStore` back it; the vendor
seam it goes through is `IPlcProjectFormat`, so a second brand plugs in without touching it.

**Done — adopt a project.** *Track project* runs `Adoption.Plan` (pure, seven refusals, unit
tested), writes the `.gitattributes` rules the project needs, then writes the binding. The
attribute rules come from `GitAttributes.Plan`, which never rewrites a rule the repository
already has and **only writes `filter=lfs` when `git lfs` actually runs here** — an LFS rule
in a repository without LFS breaks every later checkout for everyone, and the person who hits
it did not choose it.

**Done — manual export.** *Export now* drives `SyncOrchestrator` as a synchronous job, which
bypasses the coalescer and the 30-second throttle but **not** the circuit breaker, the hash
gate or the direction lock: a manual trigger is not a reason to trust the request more than
the state machine. The panel is composed even with no CODESYS installed —
`UnavailableToolchainRunner` fails the job with the reason — so the lock, its persistence and
its recovery still work on a machine that cannot export.

**Done — the lock in plain language.** `SyncNarrative` is the single place the wording lives,
shared by the panel and the log, and tested for coverage and for naming a remedy: every state
that blocks somebody says how to clear it. The heartbeat is wired too, so a 17–31 s export
shows elapsed time instead of nothing.

**Done — the changed-files panel follows the repository.** It used to show whatever git said
at open time and never move again; every screenshot of it was stale. It now watches the
working tree and refreshes itself, and the status bar states how fresh it is and whether
watching is live. Three pieces, and the split matters:

| Piece | Where | Why it is there |
|---|---|---|
| `RepositoryChangeFilter` | `Core/Watching` | Pure string work: is this event worth a `git status`? Both failure directions are silent bugs, so it is unit tested rather than reasoned about |
| `StatusRefreshPolicy` | `Core/Watching` | Coalesce, throttle, and break the echo. Pure, clock-injected, 18 tests including a 60-second storm |
| `RepositoryWatcher` | `Infrastructure/Watching` | The OS half. Separate from `FileChangeWatcher`, which tracks *which* file changed for the sync engine; this one only asks whether git is worth re-reading |

Three things had to hold at once, and each was a real failure mode:

- **`git status` writes to `.git/index`** when it refreshes the stat cache. Refreshing
  because of that write is a loop that never idles, so metadata events are ignored for a
  second after every refresh. Working-tree events are *never* suppressed — Versio does not
  write there while refreshing, so those are always somebody else.
- **Windows raises a directory-level event for `.git` itself** whenever anything inside it is
  written, including the lock files git creates around every invocation. Treating that as a
  metadata change made lock-file churn alone wake the watcher. Caught by the integration
  suite, not by reasoning.
- **Rows are reconciled in place, not rebuilt.** Clear-and-refill was fine on a button press
  but drops the selection and resets the diff pane — several times a minute that is worse
  than the staleness it fixes.

Build output is deliberately **not** filtered by name. Whether `bin/` is ignored is a
property of the repository's `.gitignore`, not of the folder's name, and guessing would
reintroduce exactly this bug in the repositories that track one. Volume is handled by
debouncing instead: 50 writes settle into one refresh, measured.

**Done — the log is a real artefact.** Logging was well designed and barely connected: the
ring buffer, correlation ids and the heartbeat all worked, while the parts most worth
recording said nothing at all. Three findings, worst first:

| Finding | Consequence |
|---|---|
| **`RollingFileSink` was never constructed by anything** | Nothing was ever written to disk. Every line lived in a 10,000-entry ring buffer that dies with the process — so the entries written immediately before a crash, the only ones that matter for a crash, were exactly the ones guaranteed to be lost |
| **`SyncOrchestrator` logged nothing** | The component owning the direction lock, circuit breaker, hash gate and commit gate. A refusal is invisible by design — the user saves and nothing happens — so an unlogged one left them with no way to find out why |
| **`Action<string>` log seams** in the git client, the CODESYS runner and both watchers | Level, category, exception and correlation id were all discarded at the boundary. The git client logged the command line and never the exit code, so a failed push was indistinguishable from one that never ran |

The log now lives at **`%LOCALAPPDATA%\Versio\logs\versio-YYYY-MM-DD.log`**, one file per
day, **five days kept**, pruned at launch *and* on each day-roll — Versio is left open for
days at a time, and pruning only at startup would let the folder grow for the length of the
session. *Settings ▸ Open logs folder* reveals it in Explorer.

Under `%LOCALAPPDATA%` rather than the repository, for three reasons in order of what they
cost when ignored: **no repository is open during startup**, so a crash before one is chosen
would never be recorded anywhere; a repository can be **read-only, on a network share, or
deleted** while the log is the only evidence of what happened to it; and one machine-wide
location means **one place to look** rather than one per repository. Local rather than
Roaming because roaming profiles copy `%APPDATA%` at every logon, and machine-specific
diagnostics are precisely what should not travel.

Two properties are load-bearing and pinned by tests: every refusal is built by a `Refuse()`
helper that logs on the way out, so a branch added later **cannot** be a silent one; and
remote URLs are redacted before they reach the file, because `https://user:token@host` is an
ordinary remote and log files get pasted into bug reports.

Verified end to end rather than asserted — the app was launched against this repository and
the resulting file read back: banner, git commands with durations, the CODESYS probe and the
project scan, all correlated by operation id, UTF-8 without a BOM.

**Remaining:**

- **A diff preview inside the confirmation**, rather than a file list. The panel says which
  files change; it does not yet show what changes in them
- Import backup + swap, and the round-trip equivalence check (Phase 3 proper)
- Template generation — needs a new `CodesysOperation` and a new script on both sides, so it
  is not assembly like the rest of this list. Adopt and export do not depend on it
- Last export time / object counts, and `_coverage.md`
- Commit gate blocking a stale export, with a blocking progress dialog
- `FileChangeWatcher` → `SyncOrchestrator` wiring
- Profile choice when several are installed — currently reported and refused, not offered
- A **log level control** in settings; the file is written at `Debug` and nothing lowers it yet
- **Sanitising paths** in the log for sites that treat project names as sensitive

### Fixed — the text side of the hash gate could not see text edits

Found while wiring import, and it had to be fixed before import could work at all.

`SyncOrchestrator` answered "did anything really change?" for `ChangeSource.ExportedText` by
hashing **`_manifest.json`** — a file written by an export and by nothing else. An engineer
editing `Simulation.st`, the entire purpose of the mirror, left it byte-identical, so the gate
returned `NoChange` and the import was refused before the direction lock was ever consulted.

**The 24 scenario tests could not catch it**, because they simulated a text edit by rewriting
`_manifest.json` (`h.SetManifest("edited-by-hand")`) — the one file in the tree no human ever
touches. Both sides agreed about a signal that does not occur in the field. That is this
project's own documented failure mode, and it was the fifth instance of it.

**What changed:**

| | |
|---|---|
| `ContentHash.OfTree` | Combines per-file path and content hash into one tree hash. Sorted internally, because directory order is an OS detail and a hash that depended on it would differ between machines. Paths are hashed as well as contents, or a rename would move no bytes and never be imported |
| `SyncOrchestrator.ExportTreeHashAsync` | Replaces `ManifestHashAsync`; hashes what the tree actually contains. Logs file count, byte count and the short hash at `Debug`, because *"the gate says nothing changed but I edited a file"* is otherwise unanswerable |
| `SyncedState.MirrorHash`, `SyncBaseline.MirrorHash` | Renamed from `ManifestHash`. The old name described the proxy, not the thing, and a misleading name on a load-bearing lock field is how the bug got its blast radius. Lock schema bumped 2 → 3 |
| `FakeFileSystem.EnumerateFiles` | Was a bare prefix match, so `plc/A.project` enumerated as part of its own export tree `plc/A`. A test written against that would have proven the opposite of what it claimed |
| The scenario suite | A text edit now writes a real `.st` file. All 24 scenarios still hold — the difference is that they are now capable of not holding |

**Proven able to fail.** The old manifest-only hash was temporarily restored and the suite
re-run: four tests failed, including the two new ones. The first draft of
`An_edited_source_file_is_detected_although_the_manifest_is_untouched` **passed** against the
broken gate — a fresh binding has never recorded its mirror, so it reports a change whatever
the gate does. It now starts from a synced harness. Worth recording because it is the same
vacuous pass one level up, found only by deliberately breaking the code.

**Cost.** A SHA-256 over the export tree instead of one small read: measured at tens of
milliseconds for the fixture, against a 17–31 s CODESYS launch. The original reasoning about
walking megabytes of visualisation XML was sound about cost and wrong about correctness.

### Done — import from the panel

*Import now* proposes; *Confirm import* applies. The confirmation is **the orchestrator's own
`RequireImportConfirmation` gate**, not a second one written in the view model: the unconfirmed
pass runs through the circuit breaker, the hash gate and the direction lock exactly as the real
thing would, and only reaches `AwaitingConfirmation` when everything else has already agreed.
A dialog in the view model would have had to duplicate all of that reasoning and would drift
out of step with it.

The proposal lists the changed files **from `git status`**, not from a diff of our own — the
export tree is committed history, so git already knows, and it is the same answer the
changed-files panel shows. Two lists that disagree at the moment somebody is deciding whether
to overwrite a project file would be worse than no list.

Changing the selection cancels a pending proposal, so nobody confirms an import against one
project while reading a file list describing another.

Verified against a real project: the unconfirmed pass leaves the `.project` **byte-identical**
(otherwise "propose" is already "apply" and the confirmation is theatre), and the confirmed
pass changes it and takes the text direction.

### Done — the diff view tells the truth about new files

A file git has never seen has nothing to be compared against, so plain `git diff` says nothing
about it — and the panel dutifully reported *"no textual diff for this file"*. That is the
wrong answer to the wrong question. **A new file has not got *no* diff; it is *entirely* new**,
and every line of it is exactly what wants reading before it is committed. New files are the
normal case immediately after a first export, which is the worst possible moment to show the
user a blank pane.

`GitCliClient.DiffAsync` now falls back to `git diff --no-index -- /dev/null <path>`, which
produces an ordinary unified diff carrying a real `new file mode` header — so the existing
parser and the existing green highlighting handle it with no special case of their own.

**Only for paths git does not track**, checked with `ls-files --error-unmatch`. Falling back
whenever the primary diff came back empty would render an unmodified tracked file as though
every line had just been added: the same wrong answer in the opposite direction, and a far
more alarming one. Both directions are pinned by a test, and the pair was proven able to fail
by disabling the fallback and re-running.

The catch-all message it replaced blamed untracked files for a gap that was ours. What is left
says what is actually true: git reports this file as binary, so there are no lines to compare.

### Done — *Show full file* in the diff header

Three lines of context is plenty for reviewing a typo and nowhere near enough for a POU, where
whether an edit is safe depends on declarations well above it and the rest of the routine
below. The checkbox asks git for an enormous `-U` rather than reading the file from disk, so
**the result is still a diff**: added and removed lines stay marked and coloured. Loading the
raw text would have shown every line and lost the only thing the reviewer opened it for.

### Done — the window opens maximized

`WindowState="Maximized"` on the window itself. The declared height and width remain as the
restore size, so un-maximizing gives a sensible window rather than a sliver.

### Investigated — "Open logs folder" reporting *Location is not available*

Not a Versio defect, and worth writing down because the first diagnosis was wrong and the
first fix was aimed at the wrong thing.

The dialog named `%LOCALAPPDATA%\Versio\logs`, a folder that plainly existed and had that
day's log in it. The cause was **filesystem virtualization**: the app had been launched from
inside an MSIX/AppContainer, where writes under `%LOCALAPPDATA%` are silently redirected into
that package's `LocalCache`. Versio wrote its log to a genuine folder; Explorer, which runs
outside the container, was handed a path that does not exist from where it stands.

Two lessons, both cheap to repeat:

- **The check that "confirmed" the first diagnosis was vacuous.** `Start-Process explorer.exe`
  reported success because Explorer *launched* — Explorer then failed on its own and put up
  its own dialog. Process-started is not folder-opened, and the difference is the entire
  question being asked.
- **Launch context is part of the environment**, alongside the per-user .NET install already
  recorded here. An app started by a tool inherits that tool's container.

Kept from the attempted fix, on its own merits rather than as a repair: Explorer is launched
by name with `/select,` so today's file is highlighted instead of leaving the user in a folder
of dated logs, and the button opens **where the sink actually writes**, passed in from the
composition root rather than recomputed from the default. The remarks on `OpenLogsFolder` now
name virtualization as the first thing to suspect if the dialog ever returns.

### Done — the log console starts collapsed

It is a diagnostic, not the main event. Opening onto a wall of scrolling startup lines pushed
the diff and the panels into the top half of the window and read as though something was
wrong. The header, its count badge and the chevron stay visible, so it is one click away.

### Fixed — the UI froze on a large exported file

Selecting `Live_Visu.vis.xml` from the changed-files list locked the window. It is **2.5 MB and
33,644 lines** — not a pathological input, but the ordinary output of exporting the fixture
project, so this was on the path of the tool's main workflow.

`DiffView` is a virtualized `ListBox`, and its own class comment claimed rows were built on a
background thread. **Neither was doing anything.** `Rebuild` eagerly constructed a `UIElement`
per line — a `Grid`, two gutters and a syntax-tokenized `TextBlock` — for every one of the
33,644 lines, on the UI thread, before the list ever saw them. The panel then virtualized
containers over a collection whose contents had already been built in full.

Two changes:

- **Items are now diff lines, not built rows**, and the row is constructed in
  `PrepareContainerForItemOverride`. That is what makes the virtualization real: the cost is
  paid only for lines somebody can see, and recycling rebuilds them as the view scrolls.
- **Parsing moved off the dispatcher**, with a generation counter so a slow parse cannot land
  after the user has clicked on to a different file and replace what they are looking at.

Made worse by an earlier change in the same session, which is worth stating plainly: before
untracked files produced a diff at all, this file rendered nothing and returned instantly. The
new-file fix turned a blank pane into 33,644 rows and exposed the eager build behind it.

`LargeDiffTests` pins the parse against a diff of exactly that size.

### Fixed — CODESYS opened a visible window during export

`CreateNoWindow` was already set, and is not sufficient: it sets `CREATE_NO_WINDOW`, which
suppresses the console a **console-subsystem** child would allocate. `CODESYS.exe` is a GUI
application, and under `--noUI` it still created a top-level window — so an export flashed a
CODESYS shell up and left it there for the twenty seconds nobody asked to watch.

`WindowStyle = ProcessWindowStyle.Hidden` makes .NET pass `STARTF_USESHOWWINDOW` with
`SW_HIDE` in the `STARTUPINFO`, which is the request a GUI child honours for its first window.
Both flags are now set deliberately: they cover different subsystems and neither replaces the
other.

### Done — one progress bar for the whole application

Long operations here are genuinely long — 17–31 s for a CODESYS run, unbounded for a clone —
and without a visible signal the window just sits there, where "nothing is happening" and
"everything is happening" look identical. That is how somebody presses export twice and
launches a second toolchain run.

`BusyTracker` is shared by the shell and the sync panel rather than duplicated, so a git
refresh and an export report through the same place and cannot disagree while they overlap.
Three decisions worth keeping:

| | |
|---|---|
| **Counted, not a boolean** | A status refresh can overlap an export; whichever finished first would clear a flag while the other still ran. The bar leaves when the last operation ends |
| **`Begin` returns an `IDisposable`** | Paired begin/end calls get stranded by an early return or an exception. `using` ends it on every path out, including the unconsidered ones |
| **Indeterminate, with a caption** | None of these can honestly report a percentage — CODESYS reports nothing until it is done — and a bar that invents one is worse than one admitting it does not know. The caption beside it says what is running |

Automatic refreshes deliberately do **not** raise it. They fire whenever the working tree
moves, and a bar blinking on every keystroke in the user's editor trains them to ignore the
one signal that matters.

### Done — tracked projects are watched, and staleness is stated

`FileChangeWatcher` is wired to the sync panel: every tracked project's `.project` and its
export tree are watched, and when a file settles the panel re-reads how stale things are.

**It deliberately exports nothing.** The default sync mode is `On git action`, decided by
measurement — a CODESYS cold start is 17–31 s, so exporting after every settled save would run
the toolchain most of the working day for changes nobody looks at until they commit. What the
watcher buys is *awareness*: the panel now says **"2 projects have changes that have not been
exported"** instead of leaving silence that reads as "in sync", which the plan calls out by
name as the thing the status must not do.

Staleness is answered by `EvaluateCommitGateAsync` rather than by a second opinion written in
the view model, so the panel and the thing that will eventually block a commit cannot disagree
about which projects need exporting. No toolchain, no process — a hash per binding.

Nothing depends on the watcher being reliable. It drops events, and the commit gate re-checks
from scratch regardless; a completely dead watcher degrades this to "the panel tells me at
commit time rather than at save time" — annoying, never wrong.

`ShellViewModel.Dispose` now releases the sync panel's watcher as well as its own. Each holds
an OS handle on a directory, and a leaked one keeps the repository folder locked against
rename or delete after the window closes.

**Remaining on this path:** the commit gate has nothing to gate until there is a commit
action, which is the git side panel.

### Done — the git side panel, and the commit gate it makes possible

Staging, the commit message box, and fetch / pull / push. The client already did all of it;
only the panel was missing. What is new beyond the buttons is the rule they enforce.

**The commit gate now has something to gate.** Pressing Commit evaluates every enabled
binding, and where the mirror is stale it **exports first and stages the result**, then
commits. It exports rather than merely refusing, because with `On git action` as the default
sync mode this is the primary path by which the export tree is written at all — refusing here
and leaving the user to find the export button would make the default mode unusable.

Staging the export afterwards is not a detail. A commit recording the `.project` without the
mirror it exports to is the exact state the gate exists to prevent, and exporting and then
committing only what happened to be staged beforehand would produce it silently. The export
directory is staged **as a directory**, because an export can delete an object as well as
write one and staging only the writes would leave the deletion out of the commit.

A blocked gate — a binding in the failed state, which an export will not clear — says so and
commits nothing.

**Push releases the direction locks, commit does not.** Push is repo-wide, so it releases
every binding, and `HeadCommitAsync` supplies the commit the released baseline records: the
lock records what the world has seen, and that is a commit rather than a moment. The panel
says this in plain text under the buttons, because it is the most surprising rule in the tool
and finding it out afterwards is worse than reading it.

### One operation at a time

Every git button consults one shared flag and they all go dead together. `AsyncCommand`
already refused to double-invoke *itself*, which is not the same thing — it would have left
Push live during a Pull.

The flag is the shared `BusyTracker`, not a git-only one, so a 17–31 s CODESYS export also
holds the git buttons. Those two overlapping is precisely the case worth preventing: the
export writes the very files the commit is about to stage. Git serialises some of this itself
with index and ref locks, but not a push racing a commit, and not a commit landing while the
gate's export is still running.

Automatic status refreshes deliberately do not raise the tracker, so the buttons do not
flicker every time the user saves a file in their editor.

**Not built yet:** stash, per-hunk staging, and the recent-commits list. Staging is
whole-list rather than per-file, because partial staging belongs with per-hunk staging and
needs the diff view to own a selection first — half-offering it would be worse than not
offering it.

### Done — the project tree, built from the manifest

The sync panel now shows the project as CODESYS structures it, not as paths on disk. A flat
list of `plc/Machine/Device/Plc Logic/Application/Main.st` was a regression from what the
engineer looks at all day.

`ProjectTreeBuilder` is pure — manifest and git status in, an immutable snapshot out — which
is what lets the roll-up rules be tested without an export, a repository or a UI. Those rules
are the part most likely to be quietly wrong: a folder that under-reports its descendants
hides a change behind a collapsed node, and that looks exactly like no change at all.

| Decision | Why |
|---|---|
| **The node is the object, not the file** | A GVL exports to both `.gvl.st` and `.gvl.xml`. Two rows would invite the question of which one is the real object; they are layers of one node, and the diff opens the readable one |
| **An object is changed when any layer is** | Editing the `.st` and leaving the `.xml` alone still means the object changed. A node reporting the quieter of its two files would hide it |
| **Badges count descendants, not children** | So a change is never hidden behind a collapsed folder |
| **Roll-up strength is by attention deserved** | A conflict outranks everything because nothing proceeds until it is resolved; a deletion, an addition or a move outranks an edit, because those are the ones somebody might not have meant |
| **Changed-only prunes empty folders** | Keeping them would rebuild the full tree one level at a time and defeat the filter |
| **Built off the UI thread** | The plan names this as the most likely source of jank, and the 5,002-node spike is why that is believed rather than assumed |

The `TreeView` carries all three virtualization attributes and a `VirtualizingStackPanel`
items panel — 94.7 ms against 10,635 ms at the WPF defaults, measured.

**Selecting an object opens its diff** in the same pane the changed-files list uses. That
exposed a latent bug: the diff header read `SelectedChange.Path`, which only the changed-files
list ever sets, so an object picked in the tree would have shown its diff under the previously
selected file's name. There is now one `SelectedDiffPath` regardless of how the file was
reached.

**A repository with one tracked project no longer opens on an empty tree.** Nothing was
selected until somebody clicked the single row, and an empty panel reads as broken rather than
as unselected.

#### Finding — `objectCount` in the manifest is a file count

`versio_export.py` writes `len(records)`, and there is one record per *file*. The field
therefore equals `objects.Count`, not the number of distinct objects, and the two differ
exactly when an object exports to more than one layer — which the fixture does, via its GVL.

Found by an assertion written to trust the name. Nothing depends on the field today and it is
not worth a schema change on its own, but the integration test now asserts against distinct
GUIDs and says why, so the next person does not trust it either.

### Reversed by decision — sync is automatic in both directions

**This overturns a decision the plan reversed by measurement, deliberately and on the owner's
instruction.** §7.4.6 made `On git action` the default because a CODESYS cold start measures
17–31 s, so exporting after every settled save runs the toolchain most of the working day. The
cost is real and unchanged. The judgement that it is worth paying is the product owner's, and
the machinery to make it bearable was already built: the coalescer's 5-second cancel window and
30-second minimum interval mean a save storm produces one export, not one per keystroke.

What that changes:

| Was | Now |
|---|---|
| Adoption offered, never performed (§5.3, R28) | Every `.project` found is tracked automatically. Templates are still refused — a binding whose project is a template exports the template into its own tree, which is incoherent rather than merely unwanted |
| Export at the commit gate | A settled change to a `.project` exports, unattended |
| Import detected, applied only on confirmation | A settled change under an export tree imports, unattended, when no lock forbids it |

**The locks stop being defence in depth and become the defence.** With nobody present to
refuse anything, the direction lock, the hash gate, the self-write suppressor and the circuit
breaker are the only things between a save and an unwanted overwrite. Four scenarios now cover
exactly that, and the most important of them is that an export's own output — which lands in
the tree the text watcher is watching — does not come back as an import. With both directions
automatic, a version that acted on that echo would sync forever.

### Done — the project is copied aside before an import

`import_native` had no backup, and the plan listed one for Phase 3. Unattended import made it
required rather than desirable: an export can always be regenerated from the project, while a
project damaged by an import is gone unless git happens to hold it — and it will not, if the
damage lands on top of changes never committed.

The copy goes under `.versio/local/backups/<binding>/`, which is gitignored: this is a local
undo, not history, and committing megabytes of binary snapshots would be its own problem. The
last ten survive per binding — enough to walk back past a bad import, bounded so a busy
afternoon does not fill a disk.

**A failed backup fails the import**, and is pinned by a test. Running the one operation that
can destroy the project having skipped the step that makes it recoverable is the worst of both
outcomes.

### Fixed — the vendor seam was documented but not true

The README has always said that adding a vendor must not require changing `Sync/`, `Git/`,
`Locking/`, `Watching/` or `Diff/`, and that if it does, *the leak is the bug*. It was not
true. `SyncOrchestrator` — the whole neutral engine — was built directly on `ICodesysRunner`,
`CodesysJob`, `CodesysOperation`, `CodesysResult` and `CodesysProgress`. Rockwell, Siemens or
TwinCAT would have meant rewriting the engine, which is exactly what the rule existed to
prevent.

`Versio.Core/Abstractions/` also carried `ICodesysRunner` and `ICodesysLocator`: a folder whose
entire purpose is to be vendor-neutral, holding a brand name.

**What the seam is now:**

| Contract | Meaning |
|---|---|
| `IPlcRunner` | Drives a vendor's tool. `PlcJob` in, `PlcResult` out |
| `IPlcProjectFormat` | Recognises that vendor's project files and its export convention |
| `IPlcToolchain` | Whether the tool is installed, and what it can do |
| `PlcFormats` | The registry of everything Versio can recognise |

`PlcResult` is deliberately small — succeeded, why not, duration, changed paths, warnings.
That is everything the sync engine needs to decide what happens next and nothing that would
require it to understand a vendor's output format. The CODESYS JSON-lines protocol now stops
at `LocalCodesysRunner`, which translates in both directions: object events become plain
paths, warning events become sentences. `Abstractions/` holds three files and none of them
names a brand.

#### Accommodations for the vendors that do not exist yet

- **`ProjectBinding.Vendor`**, defaulting to CODESYS so existing bindings read as what they
  are. On the binding rather than the repository, because a systems integrator's repository
  can hold a CODESYS machine beside a TwinCAT one.
- **`RepoScanner` scans for every registered format**, not one. A mixed repository is two
  tracked projects rather than one and a blind spot.
- **Adoption resolves the format from the path**, so each binding records the right vendor.
- **`.gitattributes` covers every registered vendor's extensions**, not just the one being
  adopted. Writing them one vendor at a time would leave a TwinCAT project line-mergeable
  until somebody happened to adopt one.
- **A format may be registered without a runner.** Its projects are then detected, listed and
  explained as unsupported rather than silently ignored — which is how somebody concludes the
  tool is broken.

#### The seam is now enforced, not described

`VendorSeamTests` reads the assembly's own metadata and fails the build if any neutral
namespace declares a vendor-named type or references one in a field, property, parameter or
return. **Proven able to fail**: adding a `CodesysInstall` property to `SyncJob` turns it red,
naming the exact leak.

It also asserts that `PlcVendor` still lists TiaPortal, Studio5000 and TwinCat. Those cost
nothing today and are the entire point — the abstraction is judged against real second vendors
rather than a hypothetical one, and whoever adds TwinCAT should find a failing test before
they get far enough to regret the design.

A rule nothing checks is a rule that decays, and this one had already decayed silently.

### Verifying the UI, not just the engine

`Versio.exe --trace-bindings <file>` writes every WPF data-binding failure to a file. A broken
binding in WPF is otherwise **silent** — a mistyped path or a missing converter produces an
empty control and no error anywhere — which made it the one class of failure in this codebase
that was not loud. The sync panel is verified clean through it, including the row template,
and the instrument itself was proven able to fail before its clean result was believed.

### 2. Complete the git surface — *mostly done*

- ~~Git side panel: stage/unstage, commit message box, pull/push/fetch~~ — done above
- Stash, and the recent-commits list with click-to-diff
- Branch creation, clone-with-verification dialog
- Per-hunk staging

### 3. Project tree — *built*

The builder, the virtualized `TreeView`, roll-up badges, the changed-only filter, search, and selection driving the diff. Remaining: parsing `config/*.yaml` back into device nodes so a changed EtherCAT cycle time appears under the device that owns it, a layer switcher in the diff pane (`ST | native`), the context menu, and expansion state persisted per repository.

### 4. Diff view — *largely done*

Gutter with both sides' line numbers, per-row tinting and syntax highlighting for eleven languages, driven by a table-driven tokenizer in `Versio.Core` rather than AvalonEdit (§8.3 records why). Remaining: **side-by-side mode** and **word-level intra-line diff**.

### 5. Packaging

- ~~Application icon~~ — **done.** `assets/versio.ico`, 8 sizes, generated by
  `tools/New-VersioIcon.ps1` and embedded via `<ApplicationIcon>`. The installer and any
  shortcuts will read the same file.
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

**A silent failure is a bug even when the code is correct.** The rolling file sink was
written, reviewed and tested by eye — and never instantiated, so for the whole life of the
project nothing reached disk while the log panel made it look as though logging worked. It
now has tests that assert on real bytes in a real file, which is the only version of that
test that would have caught it.

**Findings are written down with their evidence.** `docs/PHASE0-FINDINGS.md` records the numbers, the failures and the dead ends — including the ones that reversed a plan decision.
