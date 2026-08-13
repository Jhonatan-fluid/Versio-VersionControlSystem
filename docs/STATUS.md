# Versio — status and route to completion

**Date:** 2026-08-12 · **Commits:** 30 · **Tests:** 799 unit + 45 UI + 101 integration, 0 skipped

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
  Domain core         ████████████████████  721 tests, 286 ms
  Vendor seam         ████████████████████  enforced from assembly metadata
  Infrastructure      ████████████████████  drives real CODESYS + real git
  Desktop shell       ███████████████████░  one project view, full git surface, diff
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

### Known limitations, carried deliberately

- **Library references are export-only.** They are exported now — that gap is closed — but the
  file is written for review and never applied back: adding a library through the scripting API
  is a different operation from importing an object, and getting it wrong rewires what the
  machine compiles against. Editing `Library Manager.libraries.yaml` in the mirror changes
  nothing in the project. The coverage report says so in its own section, and the importer
  skips the file by suffix rather than feeding YAML to a toolchain that expects XML.
  The edit is not undone either: it sits in the working tree until the next export rewrites
  the file from the project. Worth knowing before somebody stages one — **and the panel does
  not say so yet**, because the coverage sentence only appears when there are gaps and this is
  not one. The row-level lock glyph is the fix, and it is tree work (§4 below).
- **How a library reference *resolves* is deliberately not recorded.** The file carries what
  the project asks for — placeholder, default resolution, redirection, parameters — and not
  what it resolved to on the exporting machine. Two engineers with different libraries
  installed would otherwise produce different bytes from the same project, and neither would
  be wrong.
- **Managed (fixed-version) library references are untested.** All 34 references in the fixture
  are placeholders, so the branch that reads `version`, `company` and `title` off a managed
  library is written from the API's description and has never run against a real one.
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
| **Git panel** | Staging, commit message with a 50/72 guide, Ctrl+Enter, fetch / pull / push, stash, recent commits with click-to-diff, **discard**. One operation at a time across every button — a shared counted flag, so a 17–31 s export also holds them |
| **PLC state** | **No card, and no buttons.** Toolchain, version and profile were environment rather than repository state and moved to Settings → Help → System information. "Export now" is gone because a stale mirror needs no decision — it is exported when the repository opens. "Acknowledge failure" is gone as a panel-wide control: a failure belongs to one project, so it is reported on that project's row with the reason and a *Try again* beside it. What is left in the panel is transient and hides itself: the coverage warning and the live heartbeat |
| **Branch and clone** | Create a branch from HEAD and switch to it, with the name checked against git's rules while it is typed; clone a repository after verifying the remote, then open it. Both are modal dialogs built from the one stylesheet, and both are covered by tests that construct them against the real theme |
| **Commit gate** | A stale mirror is exported *and staged* before the commit. Push releases the direction locks; commit does not, and the panel says so |
| **Diff view** | Eleven languages, new files rendered entirely green, *Show full file*, rows built only when realised, **per-hunk stage and unstage** |
| **Open in the IDE** | Export runs anyway, from a copy; import waits and names who has the project. Neither marks the binding failed, and the breaker is never fed |
| **Adoption writes `.gitignore`** | Created when absent, appended when present, never overriding an existing decision. Covers `.versio/local/` — locks, backups, scratch copies — and the toolchain's `-AllUsers.opt` and `-<user>-<machine>.opt`, the second of which carries a username and machine name in its filename |
| **PLC state** | Toolchain, direction lock, staleness and heartbeat, folded into the project panel as facts rather than controls. Two buttons survive: export what is stale, and acknowledge a failure |
| **Export coverage** | What the export does *not* carry, said out loud: `_coverage.md` in the tree, every skip in the manifest with a stable reason, and a sentence in the panel naming what cannot be relied on. A gap is distinguished from a deliberate omission, from a file that is exported one way, and an old manifest reports "unknown" rather than "complete" |
| **Library references** | One `<manager>.libraries.yaml` per library manager, beside the object that owns it — what the project asks for, never how it resolved here. Hand-written YAML on both sides, no dependency added to either. **Export-only**, and stated where a reader will meet it: the file's own header, `_coverage.md`, and the manifest's `form`. **Not yet in the panel** — `CoverageSummary` speaks only when there are gaps, deliberately, and the row-level 🔒 glyph the plan asks for (§9.3) is part of the unbuilt tree work |
| **Chrome** | One app-wide progress bar with a caption, maximized on open, log console collapsed, heartbeat during long runs |
| **Logging** | Ring buffer, correlation ids, rolling file from startup, crash handlers on all three .NET channels. The export narrates itself: the directory swap with both file counts, every skip with its reason, the summary, and a warning when a killed run left a staging folder behind |

---

## What remains, in dependency order

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

- ~~Stash~~, ~~recent commits with click-to-diff~~, ~~per-hunk staging~~ — **done**
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

### 3. Retry automatically when the IDE closes

Export and import both handle an open project now. The one piece left is the trigger: the
vendor's lock file disappearing is a filesystem event the watcher could see, which would make
closing CODESYS the retry and need no button at all. Today the next save retries and
*Export now* covers the gap.

### 4. Finish the project tree

- **One node per library**, under its manager, parsed from the exported YAML. `LibraryCatalog`
  already reads the file; what is missing is the tree builder taking a catalog and the shell
  handing it one. This is the first piece of the plan's "config nodes parsed from YAML", and
  the only one whose data now exists
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

`Versio.exe --trace-bindings <file> C:epo` never opened the repository. The startup code
took "the first argument that does not start with `--`" as the repository path, which is the
trace file.

The cost was not the missing repository — it is logged — but what it did to the instrument.
That flag exists to verify the UI, and every clean binding trace taken with it was a clean
trace over an **empty** window: no repository, no project rows, no git panel contents, so most
bindings had never been evaluated at all. A verification that quietly checks nothing is worse
than no verification, because it gets reported as a pass. Both of this session's earlier
"clean trace" runs were exactly that, and were re-run properly afterwards.

Flags that consume a value are listed rather than inferred: "skip whatever follows a flag"
breaks on `--spike C:epo`, which is how the spike is actually run.

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

Not built, and deliberately: **automatic retry when the IDE closes.** The `.~u` disappearing is a filesystem event the watcher could see, which would make closing CODESYS the trigger and need no button. Today the next save retries, and *Export now* is there meanwhile. Worth doing, not worth blocking this on.

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

Two instruments, covering different halves of the problem.

**`Versio.App.Tests` constructs the real windows against the real stylesheet.** A WPF test
project on an STA thread with `Theme.xaml` loaded by pack URI from the app assembly, so a style
renamed in the stylesheet and not in a dialog fails the build rather than the first click. It
covers what the tracer cannot: a modal nobody has opened has never been parsed. 24 tests over
the two dialogs, and a deliberately broken style reference fails every one of them.

**`Versio.exe --trace-bindings <file>`** writes every WPF data-binding failure to a file. A
broken binding in WPF is otherwise **silent** — a mistyped path or a missing converter produces an
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

**Green results are distrusted until they can fail.** Twelve vacuous passes have been caught
this way — counting the determinism test that compared the one file with no GUIDs in it, and
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
